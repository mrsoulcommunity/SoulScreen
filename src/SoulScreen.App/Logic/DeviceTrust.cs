namespace SoulScreen.App.Logic;

/// <summary>
/// A phone as it names itself to the receiver. Saved in the settings file, so it is a plain
/// class with setters rather than a record.
/// </summary>
public sealed class DeviceKey
{
    public string Name { get; set; } = string.Empty;
    public string? Model { get; set; }
}

/// <summary>What happens when a phone starts to mirror.</summary>
public enum ConnectDecision
{
    /// <summary>The picture and sound are shown straight away.</summary>
    Allow,
    /// <summary>Everything is held back until the user answers.</summary>
    Ask,
    /// <summary>The session is dropped as soon as it can be.</summary>
    Block,
}

/// <summary>
/// Decides whether a phone may mirror, from the allowed and blocked lists.
/// <para>
/// A phone is known by its name and model, because that is all AirPlay mirroring tells the
/// receiver: the only other identifier is the phone's address, which changes whenever the
/// router hands out a new lease. This is a courtesy gate - it stops a housemate's phone
/// taking over the screen by accident - not a security boundary, and the settings say so.
/// </para>
/// Deliberately free of WPF so it can be tested on its own.
/// </summary>
internal static class DeviceTrust
{
    public static ConnectDecision Decide(
        bool askFirst, IEnumerable<DeviceKey> allowed, IEnumerable<DeviceKey> blocked, string name, string? model)
    {
        // A block is an explicit choice, so it holds whether or not asking is switched on.
        if (Contains(blocked, name, model)) return ConnectDecision.Block;
        if (!askFirst || Contains(allowed, name, model)) return ConnectDecision.Allow;
        return ConnectDecision.Ask;
    }

    public static bool Contains(IEnumerable<DeviceKey> list, string name, string? model) =>
        list.Any(key => Matches(key, name, model));

    /// <summary>Names match exactly - "Kasra's iPhone" and "kasra's iphone" are two phones as
    /// far as the phone's owner is concerned - while models ignore case and a missing model
    /// matches only a missing model.</summary>
    public static bool Matches(DeviceKey? key, string name, string? model) =>
        key is not null
        && string.Equals((key.Name ?? string.Empty).Trim(), (name ?? string.Empty).Trim(), StringComparison.Ordinal)
        && string.Equals(NormaliseModel(key.Model), NormaliseModel(model), StringComparison.OrdinalIgnoreCase);

    /// <summary>Adds a phone to a list it is not already in. Returns false if nothing changed.</summary>
    public static bool Add(List<DeviceKey> list, string name, string? model)
    {
        if (string.IsNullOrWhiteSpace(name) || Contains(list, name, model)) return false;
        var normalisedModel = NormaliseModel(model);
        list.Add(new DeviceKey { Name = name.Trim(), Model = normalisedModel.Length == 0 ? null : normalisedModel });
        return true;
    }

    /// <summary>Removes every entry for a phone. Returns how many there were.</summary>
    public static int Remove(List<DeviceKey> list, string name, string? model) =>
        list.RemoveAll(key => Matches(key, name, model));

    /// <summary>Drops blank and repeated entries a hand-edited file could hold.</summary>
    public static List<DeviceKey> Sanitise(List<DeviceKey>? list)
    {
        var clean = new List<DeviceKey>();
        if (list is null) return clean;
        foreach (var key in list)
        {
            if (key is null || string.IsNullOrWhiteSpace(key.Name)) continue;
            Add(clean, key.Name, key.Model);
        }
        return clean;
    }

    private static string NormaliseModel(string? model) => string.IsNullOrWhiteSpace(model) ? string.Empty : model.Trim();
}
