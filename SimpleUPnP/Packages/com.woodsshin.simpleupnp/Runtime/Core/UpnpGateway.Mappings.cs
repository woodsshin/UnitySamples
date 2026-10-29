// Copyright 2017-present WoodsShin, Inc. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace WoodsShin.SimpleUPNP
{
    public sealed partial class UpnpGateway
    {
        /// <summary>Asks the router for its public address.</summary>
        public async Task<UpnpResult<string>> GetExternalIPAddressAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await EnsureWanServiceAsync(cancellationToken).ConfigureAwait(false);
                    SoapResponse r = await SoapClient.CallAsync(_wan, "GetExternalIPAddress", null, _options, cancellationToken).ConfigureAwait(false);
                    if (!r.Succeeded)
                    {
                        UpnpResult failure = Remember(Failure(r, UpnpErrorCode.GetExternalIpFailed, "GetExternalIPAddress"));
                        return new UpnpResult<string>(null, failure);
                    }

                    string address = Pick(r.Get("NewExternalIPAddress"));
                    DeviceStatusInfo current = _status;
                    _status = new DeviceStatusInfo
                    {
                        Uptime = current.Uptime,
                        ConnectionStatus = current.ConnectionStatus,
                        LastConnectionError = current.LastConnectionError,
                        ExternalIPAddress = address,
                        LocalIPAddress = current.LocalIPAddress,
                        DeviceLocation = current.DeviceLocation,
                        ConnectionType = current.ConnectionType,
                        PossibleConnectionTypes = current.PossibleConnectionTypes,
                    };

                    return new UpnpResult<string>(address, Remember(UpnpResult.Ok));
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return new UpnpResult<string>(null, Remember(new UpnpResult(UpnpErrorCode.Canceled, "The operation was canceled.")));
            }
        }

        /// <summary>
        /// Adds a port mapping. When <see cref="PortMappingInfo.InternalClient"/> is empty, the address of this
        /// computer on the network of the router is used. If the router only supports permanent mappings (error 725)
        /// the request is repeated with lease 0, see <see cref="UpnpClientOptions.FallbackToPermanentLease"/>.
        /// </summary>
        public async Task<UpnpResult> AddPortMappingAsync(PortMappingInfo mapping, CancellationToken cancellationToken = default)
        {
            if (mapping == null)
            {
                return Remember(new UpnpResult(UpnpErrorCode.InvalidArgument, "mapping is null."));
            }

            string problem = mapping.Validate();
            if (problem != null)
            {
                return Remember(new UpnpResult(UpnpErrorCode.InvalidArgument, problem));
            }

            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await EnsureWanServiceAsync(cancellationToken).ConfigureAwait(false);

                    string client = string.IsNullOrEmpty(mapping.InternalClient) ? DefaultInternalClient() : mapping.InternalClient;
                    if (string.IsNullOrEmpty(client))
                    {
                        return Remember(new UpnpResult(UpnpErrorCode.InvalidArgument,
                            "InternalClient is empty and the local address could not be determined. Set PortMappingInfo.InternalClient."));
                    }

                    UpnpResult result = await AddCoreAsync(mapping, client, mapping.LeaseDuration, cancellationToken).ConfigureAwait(false);

                    if (!result.Succeeded && result.UpnpFaultCode == 725 && mapping.LeaseDuration != 0 && _options.FallbackToPermanentLease)
                    {
                        Log(UpnpLogLevel.Warning, "The router only supports permanent mappings. Repeating the request with lease 0. " +
                                                  "Delete the mapping when it is no longer needed.");
                        result = await AddCoreAsync(mapping, client, 0, cancellationToken).ConfigureAwait(false);
                    }

                    return Remember(result);
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return Remember(new UpnpResult(UpnpErrorCode.Canceled, "The operation was canceled."));
            }
        }

        private async Task<UpnpResult> AddCoreAsync(PortMappingInfo mapping, string internalClient, int leaseDuration, CancellationToken ct)
        {
            // The order of the arguments is fixed by the WANIPConnection service definition.
            List<KeyValuePair<string, string>> arguments = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("NewRemoteHost", mapping.RemoteHost ?? string.Empty),
                new KeyValuePair<string, string>("NewExternalPort", mapping.ExternalPort.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("NewProtocol", mapping.Protocol.ToWireString()),
                new KeyValuePair<string, string>("NewInternalPort", mapping.InternalPort.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("NewInternalClient", internalClient),
                new KeyValuePair<string, string>("NewEnabled", mapping.Enabled ? "1" : "0"),
                new KeyValuePair<string, string>("NewPortMappingDescription", mapping.Description ?? string.Empty),
                new KeyValuePair<string, string>("NewLeaseDuration", leaseDuration.ToString(CultureInfo.InvariantCulture)),
            };

            SoapResponse r = await SoapClient.CallAsync(_wan, "AddPortMapping", arguments, _options, ct).ConfigureAwait(false);
            if (r.Succeeded)
            {
                Log(UpnpLogLevel.Info, "Added " + mapping.Protocol.ToWireString() + " port mapping " + mapping.ExternalPort + " -> " +
                                       internalClient + ":" + mapping.InternalPort + " on " + DisplayName);
                return UpnpResult.Ok;
            }

            return Failure(r, UpnpErrorCode.AddMappingFailed, "AddPortMapping");
        }

        /// <summary>Deletes the mapping described by <paramref name="mapping"/> (protocol, external port and remote host).</summary>
        public Task<UpnpResult> DeletePortMappingAsync(PortMappingInfo mapping, CancellationToken cancellationToken = default)
        {
            if (mapping == null)
            {
                return Task.FromResult(Remember(new UpnpResult(UpnpErrorCode.InvalidArgument, "mapping is null.")));
            }

            return DeletePortMappingAsync(mapping.Protocol, mapping.ExternalPort, mapping.RemoteHost, cancellationToken);
        }

        /// <summary>
        /// Deletes a port mapping. Deleting a mapping that does not exist counts as success, because the mapping is
        /// gone either way; <see cref="UpnpResult.UpnpFaultCode"/> is then 714.
        /// </summary>
        public async Task<UpnpResult> DeletePortMappingAsync(UpnpProtocol protocol, int externalPort, string remoteHost = "", CancellationToken cancellationToken = default)
        {
            if (externalPort < 1 || externalPort > 65535)
            {
                return Remember(new UpnpResult(UpnpErrorCode.InvalidArgument, "externalPort must be between 1 and 65535."));
            }

            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await EnsureWanServiceAsync(cancellationToken).ConfigureAwait(false);

                    SoapResponse r = await SoapClient.CallAsync(
                        _wan,
                        "DeletePortMapping",
                        new[]
                        {
                            new KeyValuePair<string, string>("NewRemoteHost", remoteHost ?? string.Empty),
                            new KeyValuePair<string, string>("NewExternalPort", externalPort.ToString(CultureInfo.InvariantCulture)),
                            new KeyValuePair<string, string>("NewProtocol", protocol.ToWireString()),
                        },
                        _options,
                        cancellationToken).ConfigureAwait(false);

                    if (r.Succeeded)
                    {
                        Log(UpnpLogLevel.Info, "Deleted " + protocol.ToWireString() + " port mapping " + externalPort + " on " + DisplayName);
                        return Remember(UpnpResult.Ok);
                    }

                    if (!r.TransportFailed && r.UpnpErrorCode == 714)
                    {
                        return Remember(new UpnpResult(UpnpErrorCode.Success, "There was no such port mapping.", 714, r.UpnpErrorDescription));
                    }

                    return Remember(Failure(r, UpnpErrorCode.DeleteMappingFailed, "DeletePortMapping"));
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return Remember(new UpnpResult(UpnpErrorCode.Canceled, "The operation was canceled."));
            }
        }

        /// <summary>
        /// Reads one port mapping. When the router has no such mapping the call succeeds and
        /// <see cref="UpnpResult{T}.Value"/> is null.
        /// </summary>
        public async Task<UpnpResult<PortMappingInfo>> GetPortMappingAsync(UpnpProtocol protocol, int externalPort, string remoteHost = "", CancellationToken cancellationToken = default)
        {
            if (externalPort < 1 || externalPort > 65535)
            {
                return new UpnpResult<PortMappingInfo>(null, Remember(new UpnpResult(UpnpErrorCode.InvalidArgument, "externalPort must be between 1 and 65535.")));
            }

            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await EnsureWanServiceAsync(cancellationToken).ConfigureAwait(false);

                    SoapResponse r = await SoapClient.CallAsync(
                        _wan,
                        "GetSpecificPortMappingEntry",
                        new[]
                        {
                            new KeyValuePair<string, string>("NewRemoteHost", remoteHost ?? string.Empty),
                            new KeyValuePair<string, string>("NewExternalPort", externalPort.ToString(CultureInfo.InvariantCulture)),
                            new KeyValuePair<string, string>("NewProtocol", protocol.ToWireString()),
                        },
                        _options,
                        cancellationToken).ConfigureAwait(false);

                    if (r.Succeeded)
                    {
                        PortMappingInfo mapping = ParseMapping(r, protocol, externalPort);
                        mapping.RemoteHost = remoteHost ?? string.Empty;
                        return new UpnpResult<PortMappingInfo>(mapping, Remember(UpnpResult.Ok));
                    }

                    // 714 (no such entry) and 713 (index invalid, used by some routers) both mean "not mapped".
                    if (!r.TransportFailed && (r.UpnpErrorCode == 714 || r.UpnpErrorCode == 713))
                    {
                        return new UpnpResult<PortMappingInfo>(null, Remember(UpnpResult.Ok));
                    }

                    return new UpnpResult<PortMappingInfo>(null, Remember(Failure(r, UpnpErrorCode.GetPortMappingEntryFailed, "GetSpecificPortMappingEntry")));
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return new UpnpResult<PortMappingInfo>(null, Remember(new UpnpResult(UpnpErrorCode.Canceled, "The operation was canceled.")));
            }
        }
    }
}
