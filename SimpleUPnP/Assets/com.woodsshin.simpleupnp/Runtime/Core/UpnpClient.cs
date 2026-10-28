// Copyright 2017-present WoodsShin, Inc. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace WoodsShin.SimpleUPNP
{
    /// <summary>What an <see cref="UpnpClient"/> is currently doing.</summary>
    public enum UpnpOperation
    {
        Idle = 0,
        DiscoveringGateways,
        RefreshingGateway,
        GettingPortMappings,
        AddingPortMapping,
        DeletingPortMapping,
        PerformingOnAllGateways,
    }

    /// <summary>
    /// Entry point of the pure C# API. Finds the routers on the local network and adds, deletes and lists port
    /// mappings. Every method is asynchronous and returns an <see cref="UpnpResult"/> instead of throwing;
    /// cancellation is reported as <see cref="UpnpErrorCode.Canceled"/>. When you <c>await</c> a method on Unity's
    /// main thread, the code after the <c>await</c> runs on the main thread again.
    /// </summary>
    public sealed class UpnpClient : IDisposable
    {
        private static readonly IReadOnlyList<UpnpGateway> NoGateways = new ReadOnlyCollection<UpnpGateway>(new List<UpnpGateway>());

        private readonly UpnpClientOptions _options;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly object _stateLock = new object();

        private volatile IReadOnlyList<UpnpGateway> _gateways = NoGateways;
        private volatile UpnpGateway _selected;
        private int _activeOperations;
        private UpnpOperation _currentOperation;
        private int _disposed;

        public UpnpClient() : this(null)
        {
        }

        public UpnpClient(UpnpClientOptions options)
        {
            _options = options ?? new UpnpClientOptions();
        }

        /// <summary>The settings of this client. Change them before the first operation.</summary>
        public UpnpClientOptions Options
        {
            get { return _options; }
        }

        /// <summary>The gateways found by the last discovery.</summary>
        public IReadOnlyList<UpnpGateway> Gateways
        {
            get { return _gateways; }
        }

        /// <summary>The gateway that <see cref="AddPortMappingAsync"/> and the other client level methods use. Null before a gateway was found.</summary>
        public UpnpGateway SelectedGateway
        {
            get { return _selected; }
        }

        /// <summary>True while an operation is running.</summary>
        public bool IsBusy
        {
            get
            {
                lock (_stateLock)
                {
                    return _activeOperations > 0;
                }
            }
        }

        /// <summary>The operation that started last and is still running, or <see cref="UpnpOperation.Idle"/>.</summary>
        public UpnpOperation CurrentOperation
        {
            get
            {
                lock (_stateLock)
                {
                    return _activeOperations > 0 ? _currentOperation : UpnpOperation.Idle;
                }
            }
        }

        /// <summary>Selects the gateway that the client level methods use. Returns false when it is not one of <see cref="Gateways"/>.</summary>
        public bool SelectGateway(UpnpGateway gateway)
        {
            if (gateway == null)
            {
                return false;
            }

            IReadOnlyList<UpnpGateway> gateways = _gateways;
            for (int i = 0; i < gateways.Count; i++)
            {
                if (ReferenceEquals(gateways[i], gateway))
                {
                    _selected = gateway;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Selects a gateway by its index in <see cref="Gateways"/>.</summary>
        public bool SelectGateway(int index)
        {
            IReadOnlyList<UpnpGateway> gateways = _gateways;
            if (index < 0 || index >= gateways.Count)
            {
                return false;
            }

            _selected = gateways[index];
            return true;
        }

        // ------------------------------------------------------------------------------------------------------
        // Discovery
        // ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Searches the local network for routers (SSDP) and reads the device description of every answer.
        /// </summary>
        /// <param name="refreshSelected">
        /// Also read status, public address, link properties and port mappings of the first gateway. The outcome of
        /// that step is available as <see cref="UpnpGateway.LastResult"/>; it does not change the result of the discovery.
        /// </param>
        public Task<UpnpResult<IReadOnlyList<UpnpGateway>>> DiscoverAsync(bool refreshSelected = true, CancellationToken cancellationToken = default)
        {
            return RunAsync<IReadOnlyList<UpnpGateway>>(UpnpOperation.DiscoveringGateways, cancellationToken, async ct =>
            {
                UpnpResult<IReadOnlyList<UpnpGateway>> discovered = await DiscoverCoreAsync(ct).ConfigureAwait(false);
                if (discovered.Succeeded && refreshSelected && _selected != null)
                {
                    SetOperation(UpnpOperation.RefreshingGateway);
                    await _selected.RefreshAsync(true, ct).ConfigureAwait(false);
                }

                return discovered;
            });
        }

        private async Task<UpnpResult<IReadOnlyList<UpnpGateway>>> DiscoverCoreAsync(CancellationToken ct)
        {
            List<SsdpDeviceInfo> answers;
            try
            {
                answers = await SsdpDiscovery.SearchAsync(_options, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Fail<IReadOnlyList<UpnpGateway>>(UpnpErrorCode.NetworkInitFailed, "The network search failed: " + ex.Message);
            }

            Dictionary<string, UpnpGateway> known = new Dictionary<string, UpnpGateway>(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<UpnpGateway> previous = _gateways;
            for (int i = 0; i < previous.Count; i++)
            {
                known[previous[i].Location.ToString()] = previous[i];
            }

            List<Task<UpnpResult<UpnpGateway>>> pending = new List<Task<UpnpResult<UpnpGateway>>>();
            for (int i = 0; i < answers.Count; i++)
            {
                Uri location;
                if (!Uri.TryCreate(answers[i].Location, UriKind.Absolute, out location))
                {
                    continue;
                }

                UpnpGateway existing;
                if (known.TryGetValue(location.ToString(), out existing))
                {
                    pending.Add(Task.FromResult(new UpnpResult<UpnpGateway>(existing, UpnpResult.Ok)));
                }
                else
                {
                    pending.Add(UpnpGateway.CreateAsync(location, answers[i], _options, ct));
                }
            }

            UpnpResult<UpnpGateway>[] created = await Task.WhenAll(pending).ConfigureAwait(false);

            List<UpnpGateway> gateways = new List<UpnpGateway>();
            for (int i = 0; i < created.Length; i++)
            {
                if (created[i].Succeeded && created[i].Value != null)
                {
                    gateways.Add(created[i].Value);
                }
                else
                {
                    Log(UpnpLogLevel.Verbose, created[i].Message);
                }
            }

            if (gateways.Count == 0)
            {
                string reason = answers.Count == 0
                    ? "No UPnP device answered the search. UPnP may be switched off in the router, or a firewall blocks multicast traffic."
                    : "UPnP devices answered, but none of them is an Internet Gateway Device (router).";
                _gateways = NoGateways;
                _selected = null;
                return Fail<IReadOnlyList<UpnpGateway>>(UpnpErrorCode.NoGatewayFound, reason);
            }

            ReadOnlyCollection<UpnpGateway> result = new ReadOnlyCollection<UpnpGateway>(gateways);
            _gateways = result;
            if (_selected == null || !gateways.Contains(_selected))
            {
                _selected = gateways[0];
            }

            Log(UpnpLogLevel.Info, "Found " + gateways.Count + " gateway(s): " + string.Join(", ", gateways.ConvertAll(g => g.DisplayName).ToArray()));
            return new UpnpResult<IReadOnlyList<UpnpGateway>>(result, UpnpResult.Ok);
        }

        /// <summary>
        /// Adds a gateway whose device description URL is already known, for example from a settings file, without
        /// an SSDP search. The URL must point to a local network address unless
        /// <see cref="UpnpClientOptions.AllowPublicGatewayAddresses"/> is set.
        /// </summary>
        public Task<UpnpResult<UpnpGateway>> AddGatewayAsync(Uri location, CancellationToken cancellationToken = default)
        {
            return RunAsync<UpnpGateway>(UpnpOperation.DiscoveringGateways, cancellationToken, async ct =>
            {
                if (location == null || !location.IsAbsoluteUri || !await UpnpNetwork.IsAllowedGatewayHostAsync(location, _options).ConfigureAwait(false))
                {
                    return Fail<UpnpGateway>(UpnpErrorCode.InvalidArgument,
                        "The location must be an absolute http:// URL on a local network address.");
                }

                UpnpResult<UpnpGateway> created = await UpnpGateway.CreateAsync(location, null, _options, ct).ConfigureAwait(false);
                if (!created.Succeeded)
                {
                    return created;
                }

                List<UpnpGateway> gateways = new List<UpnpGateway>(_gateways);
                gateways.RemoveAll(g => string.Equals(g.Location.ToString(), location.ToString(), StringComparison.OrdinalIgnoreCase));
                gateways.Add(created.Value);
                _gateways = new ReadOnlyCollection<UpnpGateway>(gateways);

                if (_selected == null)
                {
                    _selected = created.Value;
                }

                return created;
            });
        }

        // ------------------------------------------------------------------------------------------------------
        // Operations on the selected gateway
        // ------------------------------------------------------------------------------------------------------

        /// <summary>Refreshes status, public address, link properties and (optionally) port mappings of the selected gateway.</summary>
        public Task<UpnpResult> RefreshAsync(bool includePortMappings = true, CancellationToken cancellationToken = default)
        {
            return RunAsync(UpnpOperation.RefreshingGateway, cancellationToken, async ct =>
            {
                UpnpResult<UpnpGateway> gateway = await EnsureGatewayAsync(ct).ConfigureAwait(false);
                return gateway.Succeeded ? await gateway.Value.RefreshAsync(includePortMappings, ct).ConfigureAwait(false) : gateway.Result;
            });
        }

        /// <summary>Reads the port mapping table of the selected gateway.</summary>
        public Task<UpnpResult<IReadOnlyList<PortMappingInfo>>> GetPortMappingsAsync(CancellationToken cancellationToken = default)
        {
            return RunAsync<IReadOnlyList<PortMappingInfo>>(UpnpOperation.GettingPortMappings, cancellationToken, async ct =>
            {
                UpnpResult<UpnpGateway> gateway = await EnsureGatewayAsync(ct).ConfigureAwait(false);
                if (!gateway.Succeeded)
                {
                    return Fail<IReadOnlyList<PortMappingInfo>>(gateway.ErrorCode, gateway.Message);
                }

                return await gateway.Value.GetPortMappingsAsync(ct).ConfigureAwait(false);
            });
        }

        /// <summary>Adds a port mapping on the selected gateway. If no gateway is known yet, the network is searched first.</summary>
        public Task<UpnpResult> AddPortMappingAsync(PortMappingInfo mapping, CancellationToken cancellationToken = default)
        {
            return RunAsync(UpnpOperation.AddingPortMapping, cancellationToken, async ct =>
            {
                if (mapping == null)
                {
                    return new UpnpResult(UpnpErrorCode.InvalidArgument, "mapping is null.");
                }

                string problem = mapping.Validate();
                if (problem != null)
                {
                    return new UpnpResult(UpnpErrorCode.InvalidArgument, problem);
                }

                UpnpResult<UpnpGateway> gateway = await EnsureGatewayAsync(ct).ConfigureAwait(false);
                return gateway.Succeeded ? await gateway.Value.AddPortMappingAsync(mapping, ct).ConfigureAwait(false) : gateway.Result;
            });
        }

        /// <summary>Deletes a port mapping on the selected gateway. Deleting a mapping that does not exist counts as success.</summary>
        public Task<UpnpResult> DeletePortMappingAsync(UpnpProtocol protocol, int externalPort, CancellationToken cancellationToken = default)
        {
            return RunAsync(UpnpOperation.DeletingPortMapping, cancellationToken, async ct =>
            {
                UpnpResult<UpnpGateway> gateway = await EnsureGatewayAsync(ct).ConfigureAwait(false);
                return gateway.Succeeded ? await gateway.Value.DeletePortMappingAsync(protocol, externalPort, string.Empty, ct).ConfigureAwait(false) : gateway.Result;
            });
        }

        /// <summary>
        /// Deletes the mapping described by <paramref name="mapping"/> (protocol, external port and remote host) on the
        /// selected gateway. Deleting a mapping that does not exist counts as success.
        /// </summary>
        public Task<UpnpResult> DeletePortMappingAsync(PortMappingInfo mapping, CancellationToken cancellationToken = default)
        {
            return RunAsync(UpnpOperation.DeletingPortMapping, cancellationToken, async ct =>
            {
                if (mapping == null)
                {
                    return new UpnpResult(UpnpErrorCode.InvalidArgument, "mapping is null.");
                }

                UpnpResult<UpnpGateway> gateway = await EnsureGatewayAsync(ct).ConfigureAwait(false);
                return gateway.Succeeded ? await gateway.Value.DeletePortMappingAsync(mapping, ct).ConfigureAwait(false) : gateway.Result;
            });
        }

        /// <summary>
        /// Searches the network again and adds or deletes the mapping on every gateway that answers. Succeeds when at
        /// least one gateway accepted the request; the message tells how many did.
        /// </summary>
        public Task<UpnpResult> PerformAllGatewaysAsync(UpnpAction action, PortMappingInfo mapping, CancellationToken cancellationToken = default)
        {
            return RunAsync(UpnpOperation.PerformingOnAllGateways, cancellationToken, async ct =>
            {
                if (mapping == null)
                {
                    return new UpnpResult(UpnpErrorCode.InvalidArgument, "mapping is null.");
                }

                string problem = mapping.Validate();
                if (problem != null)
                {
                    return new UpnpResult(UpnpErrorCode.InvalidArgument, problem);
                }

                UpnpResult<IReadOnlyList<UpnpGateway>> discovered = await DiscoverCoreAsync(ct).ConfigureAwait(false);
                if (!discovered.Succeeded)
                {
                    return discovered.Result;
                }

                int succeeded = 0;
                UpnpResult lastFailure = default(UpnpResult);
                IReadOnlyList<UpnpGateway> gateways = discovered.Value;

                for (int i = 0; i < gateways.Count; i++)
                {
                    UpnpResult result = action == UpnpAction.AddPort
                        ? await gateways[i].AddPortMappingAsync(mapping, ct).ConfigureAwait(false)
                        : await gateways[i].DeletePortMappingAsync(mapping, ct).ConfigureAwait(false);

                    if (result.ErrorCode == UpnpErrorCode.Canceled)
                    {
                        return result;
                    }

                    if (result.Succeeded)
                    {
                        succeeded++;
                    }
                    else
                    {
                        lastFailure = result;
                    }
                }

                string summary = succeeded + " of " + gateways.Count + " gateway(s) accepted the request.";
                return succeeded > 0
                    ? new UpnpResult(UpnpErrorCode.Success, summary)
                    : new UpnpResult(lastFailure.ErrorCode, summary + " " + lastFailure.Message, lastFailure.UpnpFaultCode, lastFailure.UpnpFaultDescription);
            });
        }

        /// <summary>Returns the selected gateway, searching the network first when there is none.</summary>
        private async Task<UpnpResult<UpnpGateway>> EnsureGatewayAsync(CancellationToken ct)
        {
            UpnpGateway gateway = _selected;
            if (gateway != null)
            {
                return new UpnpResult<UpnpGateway>(gateway, UpnpResult.Ok);
            }

            SetOperation(UpnpOperation.DiscoveringGateways);
            UpnpResult<IReadOnlyList<UpnpGateway>> discovered = await DiscoverCoreAsync(ct).ConfigureAwait(false);
            if (!discovered.Succeeded)
            {
                return Fail<UpnpGateway>(discovered.ErrorCode, discovered.Message);
            }

            return new UpnpResult<UpnpGateway>(_selected, UpnpResult.Ok);
        }

        // ------------------------------------------------------------------------------------------------------
        // Plumbing
        // ------------------------------------------------------------------------------------------------------

        private async Task<UpnpResult> RunAsync(UpnpOperation operation, CancellationToken ct, Func<CancellationToken, Task<UpnpResult>> body)
        {
            CancellationTokenSource linked = null;
            try
            {
                linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
                using (BeginOperation(operation))
                {
                    return await body(linked.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return new UpnpResult(UpnpErrorCode.Canceled, "The operation was canceled.");
            }
            catch (ObjectDisposedException)
            {
                return new UpnpResult(UpnpErrorCode.Canceled, "The client was disposed.");
            }
            finally
            {
                if (linked != null)
                {
                    linked.Dispose();
                }
            }
        }

        private async Task<UpnpResult<T>> RunAsync<T>(UpnpOperation operation, CancellationToken ct, Func<CancellationToken, Task<UpnpResult<T>>> body)
        {
            CancellationTokenSource linked = null;
            try
            {
                linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
                using (BeginOperation(operation))
                {
                    return await body(linked.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return Fail<T>(UpnpErrorCode.Canceled, "The operation was canceled.");
            }
            catch (ObjectDisposedException)
            {
                return Fail<T>(UpnpErrorCode.Canceled, "The client was disposed.");
            }
            finally
            {
                if (linked != null)
                {
                    linked.Dispose();
                }
            }
        }

        private static UpnpResult<T> Fail<T>(UpnpErrorCode code, string message)
        {
            return new UpnpResult<T>(default(T), new UpnpResult(code, message));
        }

        private IDisposable BeginOperation(UpnpOperation operation)
        {
            lock (_stateLock)
            {
                _activeOperations++;
                _currentOperation = operation;
            }

            return new OperationScope(this);
        }

        private void SetOperation(UpnpOperation operation)
        {
            lock (_stateLock)
            {
                _currentOperation = operation;
            }
        }

        private void EndOperation()
        {
            lock (_stateLock)
            {
                if (_activeOperations > 0)
                {
                    _activeOperations--;
                }
            }
        }

        private void Log(UpnpLogLevel level, string message)
        {
            Action<UpnpLogLevel, string> logger = _options.Logger;
            if (logger != null)
            {
                logger(level, message);
            }
        }

        /// <summary>Cancels every running operation. The client cannot be used afterwards.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _lifetime.Cancel();
            }
            catch (AggregateException)
            {
                // A cancellation callback threw. Nothing to recover.
            }

            _lifetime.Dispose();
        }

        private sealed class OperationScope : IDisposable
        {
            private UpnpClient _owner;

            public OperationScope(UpnpClient owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                UpnpClient owner = Interlocked.Exchange(ref _owner, null);
                if (owner != null)
                {
                    owner.EndOperation();
                }
            }
        }
    }
}
