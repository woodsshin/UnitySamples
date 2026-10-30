// Copyright 2017-present WoodsShin, Inc. All Rights Reserved.

using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace WoodsShin.SimpleUPNP.Samples
{
    /// <summary>The pieces of router information that a <see cref="UpnpInfoField"/> can show.</summary>
    public enum UpnpInfoKey
    {
        // Manufacturer tab
        Manufacturer = 0,
        FriendlyName = 1,
        ManufacturerUrl = 2,
        ModelName = 3,
        ModelDescription = 4,
        SpecVersion = 5,
        ModelNumber = 6,
        SerialNumber = 7,
        ModelUrl = 8,

        // Device Status tab: connection status and connection type
        ExternalIp = 20,
        InternalIp = 21,
        LastConnectionError = 22,
        ConnectionStatus = 23,
        Uptime = 24,
        ConnectionType = 25,
        PossibleConnectionTypes = 26,

        // Device Status tab: common link properties
        WanAccessType = 40,
        PhysicalLinkStatus = 41,
        UpstreamMaxBitRate = 42,
        DownstreamMaxBitRate = 43,
    }

    /// <summary>
    /// Put this component on a <c>Text</c> to show one piece of information of the selected router in it. Choose the
    /// information in the Inspector, <see cref="UpnpSettingsPanel"/> fills the text. To remove a line from the window,
    /// delete its object. To add one, duplicate a line and change its key.
    /// </summary>
    [RequireComponent(typeof(Text))]
    [AddComponentMenu("Simple UPNP/Samples/UPNP Info Field")]
    public sealed class UpnpInfoField : MonoBehaviour
    {
        [Tooltip("The information of the router that this text shows.")]
        [SerializeField] private UpnpInfoKey key = UpnpInfoKey.Manufacturer;

        private Text _text;

        /// <summary>The information that this field shows.</summary>
        public UpnpInfoKey Key
        {
            get { return key; }
        }

        /// <summary>Replaces the shown text.</summary>
        public void SetText(string value)
        {
            if (_text == null)
            {
                _text = GetComponent<Text>();
            }

            if (_text != null)
            {
                _text.text = value ?? string.Empty;
            }
        }

        /// <summary>Shows the information of <paramref name="gateway"/>, or "N/A" when it is null.</summary>
        public void Show(UpnpGateway gateway)
        {
            SetText(Describe(key, gateway));
        }

        /// <summary>Returns the text that a field with <paramref name="key"/> shows for <paramref name="gateway"/>.</summary>
        public static string Describe(UpnpInfoKey key, UpnpGateway gateway)
        {
            if (gateway == null)
            {
                return UpnpConstants.NotAvailable;
            }

            ManufacturerInfo maker = gateway.Manufacturer;
            DeviceStatusInfo status = gateway.Status;
            CommonLinkProperties link = gateway.LinkProperties;

            switch (key)
            {
                case UpnpInfoKey.Manufacturer:
                    return maker == null ? UpnpConstants.NotAvailable : Clean(maker.Manufacturer);
                case UpnpInfoKey.FriendlyName:
                    return maker == null ? UpnpConstants.NotAvailable : Clean(maker.FriendlyName);
                case UpnpInfoKey.ManufacturerUrl:
                    return maker == null ? UpnpConstants.NotAvailable : Clean(maker.ManufacturerUrl);
                case UpnpInfoKey.ModelName:
                    return maker == null ? UpnpConstants.NotAvailable : Clean(maker.ModelName);
                case UpnpInfoKey.ModelDescription:
                    return maker == null ? UpnpConstants.NotAvailable : Clean(maker.ModelDescription);
                case UpnpInfoKey.SpecVersion:
                    return maker == null ? UpnpConstants.NotAvailable : Clean(maker.SpecVersion);
                case UpnpInfoKey.ModelNumber:
                    return maker == null ? UpnpConstants.NotAvailable : Clean(maker.ModelNumber);
                case UpnpInfoKey.SerialNumber:
                    return maker == null ? UpnpConstants.NotAvailable : Clean(maker.SerialNumber);
                case UpnpInfoKey.ModelUrl:
                    return maker == null ? UpnpConstants.NotAvailable : Clean(maker.ModelUrl);

                case UpnpInfoKey.ExternalIp:
                    return status == null ? UpnpConstants.NotAvailable : Clean(status.ExternalIPAddress);
                case UpnpInfoKey.InternalIp:
                    return status == null ? UpnpConstants.NotAvailable : Clean(status.LocalIPAddress);
                case UpnpInfoKey.LastConnectionError:
                    return status == null ? UpnpConstants.NotAvailable : Clean(status.LastConnectionError);
                case UpnpInfoKey.ConnectionStatus:
                    return status == null ? UpnpConstants.NotAvailable : Clean(status.ConnectionStatus);
                case UpnpInfoKey.Uptime:
                    // 0 seconds is a valid uptime. "Not read yet" shows as N/A, like the other status values.
                    return status == null || Clean(status.ConnectionStatus) == UpnpConstants.NotAvailable
                        ? UpnpConstants.NotAvailable
                        : FormatUptime(status.Uptime);
                case UpnpInfoKey.ConnectionType:
                    return status == null ? UpnpConstants.NotAvailable : Clean(status.ConnectionType);
                case UpnpInfoKey.PossibleConnectionTypes:
                    return status == null ? UpnpConstants.NotAvailable : Clean(status.PossibleConnectionTypes);

                case UpnpInfoKey.WanAccessType:
                    return link == null ? UpnpConstants.NotAvailable : Clean(link.WanAccessType);
                case UpnpInfoKey.PhysicalLinkStatus:
                    return link == null ? UpnpConstants.NotAvailable : Clean(link.PhysicalLinkStatus);
                case UpnpInfoKey.UpstreamMaxBitRate:
                    return link == null ? UpnpConstants.NotAvailable : FormatBitRate(link.Layer1UpstreamMaxBitRate);
                case UpnpInfoKey.DownstreamMaxBitRate:
                    return link == null ? UpnpConstants.NotAvailable : FormatBitRate(link.Layer1DownstreamMaxBitRate);
                default:
                    return UpnpConstants.NotAvailable;
            }
        }

        /// <summary>Formats seconds as <c>hh:mm:ss</c>, or <c>3d 09:41:10</c> when the connection is up for a day or longer.</summary>
        public static string FormatUptime(int seconds)
        {
            long total = Math.Max(0, seconds);
            long days = total / 86400;
            long hours = (total % 86400) / 3600;
            long minutes = (total % 3600) / 60;
            long secs = total % 60;

            string clock = hours.ToString("00", CultureInfo.InvariantCulture) + ":" +
                           minutes.ToString("00", CultureInfo.InvariantCulture) + ":" +
                           secs.ToString("00", CultureInfo.InvariantCulture);
            return days > 0 ? days.ToString(CultureInfo.InvariantCulture) + "d " + clock : clock;
        }

        /// <summary>
        /// Formats a speed in bits per second, as routers report it, in a form that is easier to read: <c>100000000</c>
        /// becomes <c>100 Mbps</c>. Text that is not a number is returned unchanged.
        /// </summary>
        public static string FormatBitRate(string bitsPerSecond)
        {
            long bits;
            if (!long.TryParse(bitsPerSecond, NumberStyles.None, CultureInfo.InvariantCulture, out bits))
            {
                return Clean(bitsPerSecond);
            }

            if (bits >= 1000000000L)
            {
                return Scaled(bits, 1000000000d) + " Gbps";
            }

            if (bits >= 1000000L)
            {
                return Scaled(bits, 1000000d) + " Mbps";
            }

            if (bits >= 1000L)
            {
                return Scaled(bits, 1000d) + " Kbps";
            }

            return bits.ToString(CultureInfo.InvariantCulture) + " bps";
        }

        private static string Scaled(long bits, double unit)
        {
            // At most two decimals, no trailing zeros: 100 Mbps, 54.5 Mbps, 1.25 Gbps.
            return Math.Round(bits / unit, 2).ToString("0.##", CultureInfo.InvariantCulture);
        }

        /// <summary>Returns the trimmed text, or "N/A" when it is empty.</summary>
        private static string Clean(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return UpnpConstants.NotAvailable;
            }

            return value.Trim();
        }
    }
}
