// Copyright 2017-present WoodsShin, Inc. All Rights Reserved.

using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace WoodsShin.SimpleUPNP.Samples
{
    /// <summary>
    /// One line of the port mapping table of <see cref="UpnpSettingsPanel"/>: protocol, external port, internal address
    /// and port, description and a Remove button. The panel copies a hidden template row of this type for every mapping.
    /// </summary>
    [AddComponentMenu("Simple UPNP/Samples/UPNP Mapping Row")]
    public sealed class UpnpMappingRow : MonoBehaviour
    {
#pragma warning disable 0649 // The references are assigned in the Inspector.
        [SerializeField] private Image background;
        [SerializeField] private Text protocolText;
        [SerializeField] private Text externalPortText;
        [SerializeField] private Text internalText;
        [SerializeField] private Text descriptionText;
        [SerializeField] private Button removeButton;

        [Tooltip("Background colors of the even and the odd lines.")]
        [SerializeField] private Color evenColor = new Color(0.25f, 0.25f, 0.25f, 1f);
        [SerializeField] private Color oddColor = new Color(0.29f, 0.29f, 0.29f, 1f);

#pragma warning restore 0649

        private PortMappingInfo _mapping;
        private Action<PortMappingInfo> _onRemove;

        /// <summary>The mapping that this line shows, or null.</summary>
        public PortMappingInfo Mapping
        {
            get { return _mapping; }
        }

        /// <summary>Shows <paramref name="mapping"/>. <paramref name="onRemove"/> runs when the Remove button is pressed.</summary>
        public void Bind(PortMappingInfo mapping, int index, Action<PortMappingInfo> onRemove)
        {
            _mapping = mapping;
            _onRemove = onRemove;

            if (mapping == null)
            {
                return;
            }

            SetText(protocolText, mapping.Protocol.ToWireString());
            SetText(externalPortText, mapping.ExternalPort.ToString(CultureInfo.InvariantCulture));
            SetText(internalText, FormatInternal(mapping));
            SetText(descriptionText, FormatDescription(mapping));

            if (background != null)
            {
                background.color = (index & 1) == 0 ? evenColor : oddColor;
            }

            if (removeButton != null)
            {
                removeButton.onClick.RemoveAllListeners();
                removeButton.onClick.AddListener(OnRemoveClicked);
            }
        }

        /// <summary>Enables or disables the Remove button.</summary>
        public void SetInteractable(bool value)
        {
            if (removeButton != null)
            {
                removeButton.interactable = value;
            }
        }

        /// <summary>Internal address and port as the router reports them, for example <c>192.168.0.220:7777</c>.</summary>
        public static string FormatInternal(PortMappingInfo mapping)
        {
            string client = string.IsNullOrWhiteSpace(mapping.InternalClient) ? "?" : mapping.InternalClient.Trim();
            return client + ":" + mapping.InternalPort.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The description of the mapping. A mapping that the router keeps switched off is marked.</summary>
        public static string FormatDescription(PortMappingInfo mapping)
        {
            string text = mapping.Description ?? string.Empty;
            return mapping.Enabled ? text : text + " (disabled)";
        }

        private void OnRemoveClicked()
        {
            Action<PortMappingInfo> handler = _onRemove;
            PortMappingInfo mapping = _mapping;
            if (handler != null && mapping != null)
            {
                handler(mapping);
            }
        }

        private static void SetText(Text target, string value)
        {
            if (target != null)
            {
                target.text = value;
            }
        }
    }
}
