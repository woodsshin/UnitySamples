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
    /// <summary>The parsed answer of one SOAP action.</summary>
    internal sealed class SoapResponse
    {
        /// <summary>True when the router answered with HTTP 2xx and no fault.</summary>
        public bool Succeeded;

        /// <summary>True when no HTTP answer was received (timeout, connection refused, ...).</summary>
        public bool TransportFailed;

        /// <summary>True when the failure was a timeout.</summary>
        public bool TimedOut;

        public int HttpStatus;

        /// <summary>UPnP error code from a fault, for example 718. 0 when there was none.</summary>
        public int UpnpErrorCode;

        public string UpnpErrorDescription = string.Empty;

        /// <summary>Transport error text or SOAP faultstring.</summary>
        public string ErrorMessage = string.Empty;

        /// <summary>Output arguments of the action, keyed by name (case-insensitive).</summary>
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Get(string name)
        {
            string value;
            return Values.TryGetValue(name, out value) ? value : null;
        }
    }

    /// <summary>Builds and sends the SOAP requests of the UPnP control protocol.</summary>
    internal static class SoapClient
    {
        public static string BuildSoapAction(string serviceType, string action)
        {
            return "\"" + serviceType + "#" + action + "\"";
        }

        public static string BuildEnvelope(string serviceType, string action, IList<KeyValuePair<string, string>> arguments)
        {
            StringBuilder body = new StringBuilder(512);
            body.Append("<?xml version=\"1.0\"?>\r\n");
            body.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">");
            body.Append("<s:Body><u:").Append(action).Append(" xmlns:u=\"").Append(LiteXml.Escape(serviceType)).Append("\">");

            if (arguments != null)
            {
                for (int i = 0; i < arguments.Count; i++)
                {
                    string name = arguments[i].Key;
                    body.Append('<').Append(name).Append('>');
                    body.Append(LiteXml.Escape(arguments[i].Value));
                    body.Append("</").Append(name).Append('>');
                }
            }

            body.Append("</u:").Append(action).Append("></s:Body></s:Envelope>");
            return body.ToString();
        }

        /// <summary>
        /// Calls an action. Transport problems are retried (<see cref="UpnpClientOptions.MaxRetries"/>); an answer
        /// from the router, including a fault, is returned as it is. Never throws except for cancellation.
        /// </summary>
        public static async Task<SoapResponse> CallAsync(
            UpnpService service,
            string action,
            IList<KeyValuePair<string, string>> arguments,
            UpnpClientOptions options,
            CancellationToken cancellationToken)
        {
            string envelope = BuildEnvelope(service.ServiceType, action, arguments);
            byte[] body = Encoding.UTF8.GetBytes(envelope);

            List<KeyValuePair<string, string>> headers = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Content-Type", "text/xml; charset=\"utf-8\""),
                new KeyValuePair<string, string>("SOAPAction", BuildSoapAction(service.ServiceType, action)),
                new KeyValuePair<string, string>("User-Agent", options.UserAgent),
                new KeyValuePair<string, string>("Cache-Control", "no-cache"),
                new KeyValuePair<string, string>("Pragma", "no-cache"),
            };

            Log(options, UpnpLogLevel.Verbose, "SOAP " + action + " -> " + service.ControlUrl);

            SoapResponse response = null;
            int attempts = Math.Max(0, options.MaxRetries) + 1;

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    HttpReply reply = await MiniHttp.SendAsync(
                        service.ControlUrl, "POST", headers, body, options.RequestTimeout, cancellationToken).ConfigureAwait(false);

                    response = ParseReply(reply, action);
                    Log(options, UpnpLogLevel.Verbose, "SOAP " + action + " <- HTTP " + reply.StatusCode + " " + Truncate(reply.BodyText, 600));
                    return response;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (TimeoutException ex)
                {
                    response = Transport(ex.Message, true);
                }
                catch (Exception ex) when (ex is SocketException || ex is IOException || ex is InvalidDataException || ex is ObjectDisposedException || ex is NotSupportedException)
                {
                    response = Transport(ex.Message, false);
                }
                catch (Exception ex)
                {
                    // Anything unexpected must not escape into the caller's async chain.
                    response = Transport(ex.GetType().Name + ": " + ex.Message, false);
                }

                Log(options, UpnpLogLevel.Warning, "SOAP " + action + " attempt " + attempt + "/" + attempts + " failed: " + response.ErrorMessage);

                if (attempt < attempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken).ConfigureAwait(false);
                }
            }

            return response;
        }

        private static SoapResponse Transport(string message, bool timedOut)
        {
            return new SoapResponse { TransportFailed = true, TimedOut = timedOut, ErrorMessage = message };
        }

        /// <summary>Turns an HTTP reply into a <see cref="SoapResponse"/>.</summary>
        internal static SoapResponse ParseReply(HttpReply reply, string action)
        {
            SoapResponse response = new SoapResponse { HttpStatus = reply.StatusCode };
            XmlNodeLite root = LiteXml.Parse(reply.BodyText);

            XmlNodeLite upnpError = root.Descendant("UPnPError");
            XmlNodeLite fault = root.Descendant("Fault");

            if (upnpError != null || fault != null)
            {
                if (upnpError != null)
                {
                    int code;
                    if (int.TryParse(upnpError.ChildText("errorCode"), NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
                    {
                        response.UpnpErrorCode = code;
                    }

                    response.UpnpErrorDescription = upnpError.ChildText("errorDescription") ?? string.Empty;
                }

                if (fault != null)
                {
                    response.ErrorMessage = fault.ChildText("faultstring") ?? string.Empty;
                }

                return response;
            }

            if (reply.StatusCode < 200 || reply.StatusCode > 299)
            {
                response.ErrorMessage = "HTTP " + reply.StatusCode + " " + reply.ReasonPhrase;
                return response;
            }

            // The output arguments are the children of <u:ActionResponse>.
            XmlNodeLite container = root.Descendant(action + "Response");
            if (container != null)
            {
                for (int i = 0; i < container.Children.Count; i++)
                {
                    response.Values[container.Children[i].Name] = container.Children[i].Text;
                }
            }
            else
            {
                // Some routers use another wrapper name. Collect every leaf element instead.
                CollectLeaves(root, response.Values);
            }

            response.Succeeded = true;
            return response;
        }

        private static void CollectLeaves(XmlNodeLite node, Dictionary<string, string> values)
        {
            for (int i = 0; i < node.Children.Count; i++)
            {
                XmlNodeLite child = node.Children[i];
                if (child.Children.Count == 0)
                {
                    values[child.Name] = child.Text;
                }
                else
                {
                    CollectLeaves(child, values);
                }
            }
        }

        private static string Truncate(string text, int length)
        {
            return text.Length <= length ? text : text.Substring(0, length) + "...";
        }

        private static void Log(UpnpClientOptions options, UpnpLogLevel level, string message)
        {
            Action<UpnpLogLevel, string> logger = options.Logger;
            if (logger != null)
            {
                logger(level, message);
            }
        }
    }
}
