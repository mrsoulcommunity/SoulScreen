using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SoulScreen.App.Logic;
using SoulScreen.Core.Logging;

namespace SoulScreen.App;

/// <summary>A network this PC is connected to, as Windows classifies it.</summary>
/// <param name="Category">0 public, 1 private, 2 domain.</param>
internal sealed record ConnectedNetwork(string Name, int Category)
{
    public bool IsPublic => Category == 0;
}

/// <summary>What the firewall's configuration looked like when it was read.</summary>
internal sealed record FirewallSnapshot(int ActiveProfiles, bool EnabledOnActiveProfiles, IReadOnlyList<FirewallRule> Rules);

/// <summary>
/// Reads the parts of Windows' configuration that decide whether a phone can reach the receiver:
/// how the network is classified, and what the firewall allows.
/// <para>
/// Both come from the system's own COM objects, late bound, so nothing needs installing and
/// nothing needs elevation to read. Either can fail - a group policy that hides the firewall,
/// a service that is stopped - and a null result means "could not tell", never "all is well".
/// Call from a background thread: the firewall holds hundreds of rules.
/// </para>
/// </summary>
internal static class SystemDiagnostics
{
    private static readonly ILogger Log_ = Log.For("diagnostics");

    private const string NetworkListManagerClsid = "DCB00C01-570F-4A9B-8D69-199FDBA5723B";
    private const int EnumConnectedNetworks = 1;

    public static IReadOnlyList<ConnectedNetwork>? ConnectedNetworks()
    {
        object? manager = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid(NetworkListManagerClsid), throwOnError: false);
            if (type is null) return null;
            manager = Activator.CreateInstance(type);
            if (manager is null) return null;

            var networks = new List<ConnectedNetwork>();
            dynamic list = ((dynamic)manager).GetNetworks(EnumConnectedNetworks);
            foreach (dynamic network in list)
            {
                try
                {
                    networks.Add(new ConnectedNetwork((string)network.GetName(), (int)network.GetCategory()));
                }
                catch (Exception ex)
                {
                    Log_.Debug($"skipped a network that could not be read: {ex.Message}");
                }
            }
            return networks;
        }
        catch (Exception ex)
        {
            Log_.Debug($"the network list is unavailable: {ex.Message}");
            return null;
        }
        finally
        {
            if (manager is not null && Marshal.IsComObject(manager)) Marshal.FinalReleaseComObject(manager);
        }
    }

    public static FirewallSnapshot? ReadFirewall()
    {
        object? policy = null;
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: false);
            if (type is null) return null;
            policy = Activator.CreateInstance(type);
            if (policy is null) return null;

            dynamic firewall = policy;
            int active = firewall.CurrentProfileTypes;
            var enabled = false;
            foreach (var profile in new[] { FirewallRules.ProfileDomain, FirewallRules.ProfilePrivate, FirewallRules.ProfilePublic })
            {
                if ((active & profile) != 0 && (bool)firewall.FirewallEnabled[profile]) enabled = true;
            }

            var rules = new List<FirewallRule>();
            foreach (dynamic rule in firewall.Rules)
            {
                try
                {
                    rules.Add(new FirewallRule(
                        (string?)rule.Name,
                        Inbound: (int)rule.Direction == 1,
                        Allow: (int)rule.Action == 1,
                        Enabled: (bool)rule.Enabled,
                        Profiles: (int)rule.Profiles,
                        Protocol: (int)rule.Protocol,
                        LocalPorts: (string?)rule.LocalPorts,
                        ApplicationName: (string?)rule.ApplicationName,
                        ScopedToPackageOrService: IsScoped(rule)));
                }
                catch (Exception)
                {
                    // Some built-in rules throw on a property that does not apply to them.
                }
            }

            return new FirewallSnapshot(active, enabled, rules);
        }
        catch (Exception ex)
        {
            Log_.Debug($"the firewall configuration is unavailable: {ex.Message}");
            return null;
        }
        finally
        {
            if (policy is not null && Marshal.IsComObject(policy)) Marshal.FinalReleaseComObject(policy);
        }
    }

    /// <summary>True for a rule written for a Store app or a Windows service: it names no program,
    /// and yet it is not about this one.</summary>
    private static bool IsScoped(dynamic rule)
    {
        return HasText(() => (string?)rule.serviceName) || HasText(() => (string?)rule.LocalAppPackageId);

        static bool HasText(Func<string?> read)
        {
            try { return !string.IsNullOrWhiteSpace(read()); }
            catch (Exception) { return false; }
        }
    }

    /// <summary>True if nothing on this PC is listening on <paramref name="port"/>.</summary>
    public static bool IsTcpPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, port) { ExclusiveAddressUse = true };
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
