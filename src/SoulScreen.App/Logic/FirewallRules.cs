using System.Globalization;
using System.IO;

namespace SoulScreen.App.Logic;

/// <summary>One Windows Firewall rule, reduced to what decides whether a phone gets through.</summary>
/// <param name="Profiles">Bit mask: 1 domain, 2 private, 4 public.</param>
/// <param name="Protocol">6 for TCP, 17 for UDP, 256 for any.</param>
/// <param name="LocalPorts">As the firewall spells it: "*", "7000", "5000-5010,7000".</param>
/// <param name="ScopedToPackageOrService">The rule is for a Store app or a Windows service. Such a
/// rule names no program, yet it is not about this one.</param>
internal readonly record struct FirewallRule(
    string? Name, bool Inbound, bool Allow, bool Enabled, int Profiles, int Protocol, string? LocalPorts, string? ApplicationName,
    bool ScopedToPackageOrService = false);

/// <summary>What the firewall will do with a phone trying to reach this app.</summary>
public enum FirewallVerdict
{
    /// <summary>The firewall is switched off on every network this PC is on.</summary>
    Off,
    /// <summary>Both TCP and UDP are let in: control, video and sound.</summary>
    Allowed,
    /// <summary>One of the two is let in and the other is not.</summary>
    PartlyAllowed,
    /// <summary>A rule turns the phone away.</summary>
    Blocked,
    /// <summary>Nothing mentions this app, so the default - blocking - applies.</summary>
    NoRule,
}

/// <summary>
/// Works out, from the firewall's rules, whether an iPhone can reach this receiver.
/// <para>
/// Mirroring needs TCP for the control channel and the picture and UDP for discovery and sound.
/// Block rules win over allow rules, as they do in Windows Firewall itself, and the rule most
/// worth finding is the one Windows writes when its "allow access?" prompt is dismissed: a
/// block on this very program.
/// </para>
/// <para>
/// A rule that names no program counts only where it names the control port. Windows is full of
/// rules that open every port for a Store app or a service, which carry no program path either;
/// read as being about SoulScreen, they made a PC with no rule for it look half open.
/// </para>
/// Deliberately free of COM and WPF so it can be tested on its own.
/// </summary>
internal static class FirewallRules
{
    public const int ProtocolTcp = 6;
    public const int ProtocolUdp = 17;
    public const int ProtocolAny = 256;

    public const int ProfileDomain = 1;
    public const int ProfilePrivate = 2;
    public const int ProfilePublic = 4;

    public static FirewallVerdict Evaluate(
        IEnumerable<FirewallRule> rules, int activeProfiles, bool enabledOnActiveProfiles, string applicationPath, int controlPort)
    {
        if (!enabledOnActiveProfiles) return FirewallVerdict.Off;

        bool tcp = false, udp = false, blocked = false;
        foreach (var rule in rules)
        {
            if (!rule.Enabled || !rule.Inbound || (rule.Profiles & activeProfiles) == 0) continue;
            if (rule.ScopedToPackageOrService) continue;

            var forThisApp = SamePath(rule.ApplicationName, applicationPath);
            var forAnyApp = string.IsNullOrWhiteSpace(rule.ApplicationName);
            if (!forThisApp && !forAnyApp) continue;

            var allPorts = CoversAllPorts(rule.LocalPorts);
            var namesControlPort = !allPorts && PortListed(rule.LocalPorts, controlPort);
            var coversTcp = rule.Protocol is ProtocolTcp or ProtocolAny;
            var coversUdp = rule.Protocol is ProtocolUdp or ProtocolAny;

            if (!rule.Allow)
            {
                // Every port closed for every program would be the user's own blanket policy, and
                // reading it as "SoulScreen is blocked" would be a guess; a rule naming the control
                // port, or this program, is not.
                if (forThisApp && allPorts) blocked = true;
                else if (namesControlPort && coversTcp) blocked = true;
                continue;
            }

            if (forThisApp && allPorts)
            {
                tcp |= coversTcp;
                udp |= coversUdp;
            }
            else if (namesControlPort && coversTcp)
            {
                tcp = true;
            }
        }

        if (blocked) return FirewallVerdict.Blocked;
        if (tcp && udp) return FirewallVerdict.Allowed;
        if (tcp || udp) return FirewallVerdict.PartlyAllowed;
        return FirewallVerdict.NoRule;
    }

    /// <summary>The profile names a firewall rule should list to cover <paramref name="mask"/>.</summary>
    public static string ProfileNames(int mask)
    {
        var names = new List<string>(3);
        if ((mask & ProfileDomain) != 0) names.Add("domain");
        if ((mask & ProfilePrivate) != 0) names.Add("private");
        if ((mask & ProfilePublic) != 0) names.Add("public");
        return names.Count == 0 ? "private" : string.Join(",", names);
    }

    public static bool CoversAllPorts(string? ports)
    {
        if (string.IsNullOrWhiteSpace(ports)) return true;
        var trimmed = ports.Trim();
        return trimmed == "*" || trimmed.Equals("Any", StringComparison.OrdinalIgnoreCase);
    }

    public static bool PortListed(string? ports, int port)
    {
        if (CoversAllPorts(ports)) return true;
        foreach (var part in ports!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-');
            if (dash > 0
                && int.TryParse(part[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var low)
                && int.TryParse(part[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var high))
            {
                if (port >= low && port <= high) return true;
            }
            else if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var single) && single == port)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Paths compared as Windows compares them: case aside, after expanding variables
    /// such as %ProgramFiles% that rules are often written with.</summary>
    public static bool SamePath(string? rulePath, string? applicationPath)
    {
        if (string.IsNullOrWhiteSpace(rulePath) || string.IsNullOrWhiteSpace(applicationPath)) return false;
        return string.Equals(Canonical(rulePath), Canonical(applicationPath), StringComparison.OrdinalIgnoreCase);
    }

    private static string Canonical(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        try
        {
            return Path.GetFullPath(expanded).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return expanded;
        }
    }
}
