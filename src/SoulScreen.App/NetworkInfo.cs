using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SoulScreen.App;

/// <summary>One network the PC is reachable on.</summary>
/// <param name="Address">Its IPv4 address on that network.</param>
/// <param name="AdapterName">The adapter's friendly name, e.g. "Wi-Fi".</param>
/// <param name="IsWireless">True for an 802.11 adapter.</param>
public readonly record struct NetworkEndpoint(string Address, string AdapterName, bool IsWireless);

/// <summary>
/// Reads the networks this PC is on. Shown on the idle screen because "is the phone on the
/// same network?" is the first question when a receiver does not appear in Control Center,
/// and an address on screen answers it without opening a terminal.
/// </summary>
internal static class NetworkInfo
{
    /// <summary>Every up, non-virtual adapter with an IPv4 address, wireless first.</summary>
    public static IReadOnlyList<NetworkEndpoint> ActiveEndpoints()
    {
        var endpoints = new List<NetworkEndpoint>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (IsVirtual(adapter)) continue;

                var properties = adapter.GetIPProperties();
                // Only adapters with a gateway are on a network a phone could share.
                if (properties.GatewayAddresses.Count == 0) continue;

                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    endpoints.Add(new NetworkEndpoint(
                        unicast.Address.ToString(),
                        adapter.Name,
                        adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211));
                    break;
                }
            }
        }
        catch (Exception)
        {
            // Network enumeration can fail on a machine mid-way through a driver change;
            // an empty list is the honest answer.
        }

        return endpoints.OrderByDescending(e => e.IsWireless).ThenBy(e => e.AdapterName).ToList();
    }

    /// <summary>
    /// Filters out the adapters that exist on every developer PC but that no phone is on:
    /// Hyper-V and WSL switches, VPN and VM host adapters.
    /// </summary>
    private static bool IsVirtual(NetworkInterface adapter)
    {
        var description = adapter.Description;
        return description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
            || description.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
            || description.Contains("VMware", StringComparison.OrdinalIgnoreCase)
            || description.Contains("WSL", StringComparison.OrdinalIgnoreCase)
            || description.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase)
            || description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase);
    }
}
