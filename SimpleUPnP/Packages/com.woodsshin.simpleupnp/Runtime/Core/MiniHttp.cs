// Copyright 2017-present WoodsShin, Inc. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WoodsShin.SimpleUPNP
{
    /// <summary>A parsed HTTP response.</summary>
    internal sealed class HttpReply
    {
        public int StatusCode;
        public string ReasonPhrase = string.Empty;
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = new byte[0];

        public string BodyText
        {
            get { return Encoding.UTF8.GetString(Body); }
        }
    }

    /// <summary>
    /// A very small HTTP/1.1 client over a plain TCP socket. UPnP routers only speak plain HTTP, and some of them
    /// answer with unusual headers, so this avoids the strict header checks of the framework HTTP stacks and
    /// behaves the same on every platform Unity supports.
    /// </summary>
    internal static class MiniHttp
    {
        private const int MaxLineLength = 16 * 1024;
        private const int MaxBodyBytes = 2 * 1024 * 1024;

        /// <exception cref="OperationCanceledException">The caller canceled the request.</exception>
        /// <exception cref="TimeoutException">The router did not answer in time.</exception>
        public static async Task<HttpReply> SendAsync(
            Uri uri,
            string method,
            IList<KeyValuePair<string, string>> headers,
            byte[] body,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (uri == null)
            {
                throw new ArgumentNullException("uri");
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException("Only http:// URLs are supported.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Build (and thereby validate) the request before any connection is made.
            byte[] request = BuildRequest(uri, method, headers, body);

            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (TcpClient client = new TcpClient())
            {
                linked.CancelAfter(timeout);

                // A canceled or timed out request is given up in two ways. Closing the client breaks a pending read or
                // write. That is not enough: a connect that is closed while it is pending is never completed on Mono (the
                // runtime of the Unity editor and of the Mono players), so the request would hang until the system gives
                // up on the connection. That is why the request is also abandoned explicitly through this task, whatever
                // the socket does afterwards.
                TaskCompletionSource<bool> abandoned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                CancellationToken token = linked.Token;

                using (token.Register(() =>
                {
                    abandoned.TrySetResult(true);
                    CloseQuietly(client);
                }))
                {
                    try
                    {
                        // On the thread pool, so that nothing the runtime does before its first await can block the caller.
                        Task<HttpReply> exchange = Task.Run(() => ExchangeAsync(client, uri, request, token));

                        Task first = await Task.WhenAny(exchange, abandoned.Task).ConfigureAwait(false);
                        if (!ReferenceEquals(first, exchange))
                        {
                            // The exchange may still be stuck in the socket. Whatever it ends with is of no interest anymore.
                            _ = exchange.ContinueWith(
                                ObserveFault,
                                CancellationToken.None,
                                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                                TaskScheduler.Default);

                            throw new OperationCanceledException(token);
                        }

                        return await exchange.ConfigureAwait(false);
                    }
                    catch (Exception) when (linked.IsCancellationRequested)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new TimeoutException("The request to " + uri.Authority + " timed out.");
                    }
                }
            }
        }

        private static async Task<HttpReply> ExchangeAsync(TcpClient client, Uri uri, byte[] request, CancellationToken token)
        {
            client.NoDelay = true;
            await client.ConnectAsync(uri.DnsSafeHost, uri.Port).ConfigureAwait(false);

            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(request, 0, request.Length, token).ConfigureAwait(false);

            return await ReadResponseAsync(stream, token).ConfigureAwait(false);
        }

        private static void ObserveFault(Task task)
        {
            // Reading the exception marks it as observed.
            Exception ignored = task.Exception;
        }

        private static void CloseQuietly(TcpClient client)
        {
            try
            {
                client.Close();
            }
            catch (Exception)
            {
                // The socket is already gone.
            }
        }

        private static byte[] BuildRequest(Uri uri, string method, IList<KeyValuePair<string, string>> headers, byte[] body)
        {
            StringBuilder head = new StringBuilder(256);
            head.Append(method).Append(' ').Append(uri.PathAndQuery).Append(" HTTP/1.1\r\n");
            head.Append("Host: ").Append(uri.Authority).Append("\r\n");
            head.Append("Connection: close\r\n");

            if (headers != null)
            {
                foreach (KeyValuePair<string, string> header in headers)
                {
                    if (ContainsLineBreak(header.Key) || ContainsLineBreak(header.Value))
                    {
                        throw new ArgumentException("HTTP header values must not contain line breaks.");
                    }

                    head.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
                }
            }

            int bodyLength = body == null ? 0 : body.Length;
            if (body != null)
            {
                head.Append("Content-Length: ").Append(bodyLength.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            }

            head.Append("\r\n");

            byte[] headBytes = Encoding.UTF8.GetBytes(head.ToString());
            byte[] request = new byte[headBytes.Length + bodyLength];
            Buffer.BlockCopy(headBytes, 0, request, 0, headBytes.Length);
            if (bodyLength > 0)
            {
                Buffer.BlockCopy(body, 0, request, headBytes.Length, bodyLength);
            }

            // Header and body leave in a single write: some routers read the request with one recv().
            return request;
        }

        private static bool ContainsLineBreak(string value)
        {
            return value != null && (value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0);
        }

        private static async Task<HttpReply> ReadResponseAsync(Stream stream, CancellationToken token)
        {
            ByteReader reader = new ByteReader(stream, token);
            HttpReply reply = new HttpReply();

            // Skip interim "100 Continue" style responses.
            while (true)
            {
                string statusLine = await reader.ReadLineAsync(MaxLineLength).ConfigureAwait(false);
                if (statusLine == null)
                {
                    throw new InvalidDataException("The router closed the connection without answering.");
                }

                ParseStatusLine(statusLine, reply);
                reply.Headers.Clear();

                while (true)
                {
                    string line = await reader.ReadLineAsync(MaxLineLength).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(line))
                    {
                        break;
                    }

                    int colon = line.IndexOf(':');
                    if (colon > 0)
                    {
                        reply.Headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
                    }
                }

                if (reply.StatusCode >= 100 && reply.StatusCode < 200)
                {
                    continue;
                }

                break;
            }

            if (reply.StatusCode == 204 || reply.StatusCode == 304)
            {
                return reply;
            }

            string transferEncoding;
            string contentLengthText;
            if (reply.Headers.TryGetValue("Transfer-Encoding", out transferEncoding) &&
                transferEncoding.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                reply.Body = await ReadChunkedAsync(reader).ConfigureAwait(false);
            }
            else if (reply.Headers.TryGetValue("Content-Length", out contentLengthText))
            {
                int length;
                if (!int.TryParse(contentLengthText, NumberStyles.None, CultureInfo.InvariantCulture, out length) || length < 0)
                {
                    throw new InvalidDataException("Invalid Content-Length header.");
                }

                if (length > MaxBodyBytes)
                {
                    throw new InvalidDataException("The response is larger than " + MaxBodyBytes + " bytes.");
                }

                reply.Body = await reader.ReadExactAsync(length).ConfigureAwait(false);
            }
            else
            {
                reply.Body = await reader.ReadToEndAsync(MaxBodyBytes).ConfigureAwait(false);
            }

            return reply;
        }

        private static void ParseStatusLine(string line, HttpReply reply)
        {
            // "HTTP/1.1 200 OK"
            string[] parts = line.Split(new[] { ' ' }, 3);
            int code;
            if (parts.Length < 2 ||
                !parts[0].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out code))
            {
                throw new InvalidDataException("Invalid HTTP status line: " + line);
            }

            reply.StatusCode = code;
            reply.ReasonPhrase = parts.Length > 2 ? parts[2] : string.Empty;
        }

        private static async Task<byte[]> ReadChunkedAsync(ByteReader reader)
        {
            using (MemoryStream body = new MemoryStream())
            {
                while (true)
                {
                    string sizeLine = await reader.ReadLineAsync(MaxLineLength).ConfigureAwait(false);
                    if (sizeLine == null)
                    {
                        break;
                    }

                    sizeLine = sizeLine.Trim();
                    if (sizeLine.Length == 0)
                    {
                        continue;
                    }

                    int semicolon = sizeLine.IndexOf(';');
                    if (semicolon >= 0)
                    {
                        sizeLine = sizeLine.Substring(0, semicolon);
                    }

                    int size;
                    if (!int.TryParse(sizeLine, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out size) || size < 0)
                    {
                        throw new InvalidDataException("Invalid chunk size: " + sizeLine);
                    }

                    if (size == 0)
                    {
                        // Skip the optional trailer.
                        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(MaxLineLength).ConfigureAwait(false)))
                        {
                        }

                        break;
                    }

                    if (body.Length + size > MaxBodyBytes)
                    {
                        throw new InvalidDataException("The response is larger than " + MaxBodyBytes + " bytes.");
                    }

                    byte[] chunk = await reader.ReadExactAsync(size).ConfigureAwait(false);
                    body.Write(chunk, 0, chunk.Length);
                    if (chunk.Length < size)
                    {
                        break;
                    }

                    // The CRLF that ends the chunk.
                    await reader.ReadLineAsync(MaxLineLength).ConfigureAwait(false);
                }

                return body.ToArray();
            }
        }

        /// <summary>Buffered reader on top of a stream that can read lines and blocks.</summary>
        private sealed class ByteReader
        {
            private readonly Stream _stream;
            private readonly CancellationToken _token;
            private readonly byte[] _buffer = new byte[8192];
            private int _position;
            private int _length;
            private bool _endOfStream;

            public ByteReader(Stream stream, CancellationToken token)
            {
                _stream = stream;
                _token = token;
            }

            private async Task<bool> FillAsync()
            {
                if (_endOfStream)
                {
                    return false;
                }

                int read = await _stream.ReadAsync(_buffer, 0, _buffer.Length, _token).ConfigureAwait(false);
                if (read <= 0)
                {
                    _endOfStream = true;
                    return false;
                }

                _position = 0;
                _length = read;
                return true;
            }

            /// <summary>Reads a line without its line break. Returns null at the end of the stream.</summary>
            public async Task<string> ReadLineAsync(int maxLength)
            {
                StringBuilder line = new StringBuilder();
                bool anyByte = false;

                while (true)
                {
                    if (_position >= _length && !await FillAsync().ConfigureAwait(false))
                    {
                        return anyByte ? line.ToString() : null;
                    }

                    while (_position < _length)
                    {
                        byte value = _buffer[_position++];
                        anyByte = true;

                        if (value == (byte)'\n')
                        {
                            if (line.Length > 0 && line[line.Length - 1] == '\r')
                            {
                                line.Length--;
                            }

                            return line.ToString();
                        }

                        if (line.Length >= maxLength)
                        {
                            throw new InvalidDataException("An HTTP line is too long.");
                        }

                        line.Append((char)value);
                    }
                }
            }

            /// <summary>Reads exactly <paramref name="count"/> bytes, or fewer when the stream ends early.</summary>
            public async Task<byte[]> ReadExactAsync(int count)
            {
                byte[] result = new byte[count];
                int total = 0;

                while (total < count)
                {
                    if (_position >= _length && !await FillAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    int take = Math.Min(count - total, _length - _position);
                    Buffer.BlockCopy(_buffer, _position, result, total, take);
                    _position += take;
                    total += take;
                }

                if (total < count)
                {
                    Array.Resize(ref result, total);
                }

                return result;
            }

            /// <summary>Reads until the router closes the connection.</summary>
            public async Task<byte[]> ReadToEndAsync(int maxBytes)
            {
                using (MemoryStream result = new MemoryStream())
                {
                    while (true)
                    {
                        if (_position >= _length && !await FillAsync().ConfigureAwait(false))
                        {
                            break;
                        }

                        int take = _length - _position;
                        if (result.Length + take > maxBytes)
                        {
                            throw new InvalidDataException("The response is larger than " + maxBytes + " bytes.");
                        }

                        result.Write(_buffer, _position, take);
                        _position = _length;
                    }

                    return result.ToArray();
                }
            }
        }
    }
}
