// Copyright 2017-present WoodsShin, Inc. All Rights Reserved.

namespace WoodsShin.SimpleUPNP
{
    /// <summary>Explains the UPnP error codes that routers return for the Internet Gateway Device actions.</summary>
    public static class UpnpErrors
    {
        /// <summary>
        /// Returns a short explanation for a UPnP error code such as 718, or an empty string when the code is unknown.
        /// </summary>
        public static string Describe(int upnpErrorCode)
        {
            switch (upnpErrorCode)
            {
                case 401:
                    return "The router does not support this action.";
                case 402:
                    return "The router rejected the arguments of the request.";
                case 501:
                    return "The router failed to carry out the action.";
                case 606:
                    return "The router does not allow this action (not authorized). Port mapping may be disabled in the router settings.";
                case 713:
                    return "The index is past the end of the port mapping table.";
                case 714:
                    return "There is no such port mapping on the router.";
                case 715:
                    return "The router does not accept a wildcard remote host.";
                case 716:
                    return "The router does not accept a wildcard external port.";
                case 718:
                    return "The external port is already mapped, probably to another computer. Remove that mapping first or use another port.";
                case 724:
                    return "The router requires the internal and external port to be the same.";
                case 725:
                    return "The router only supports permanent mappings (lease duration 0).";
                case 726:
                    return "The router only accepts mappings without a remote host.";
                case 727:
                    return "The router only accepts mappings with a wildcard external port.";
                case 728:
                    return "The router has no free port mapping slots left.";
                case 729:
                    return "The mapping conflicts with another mechanism of the router (for example a firewall rule).";
                default:
                    return string.Empty;
            }
        }
    }
}
