namespace SoulScreen.App.Logic;

/// <summary>
/// Whether a mirroring session whose phone has just gone away is held open for that phone to
/// come back, and which phone is being waited for.
/// <para>
/// iOS tears a session down and sets a new one up for a sleep, a change of network or a moment
/// out of range, and to the person watching that is a flicker rather than the end of the
/// session. The rules about identity and time live here, apart from the window that carries
/// them out, so they can be tested without a phone, a socket or a dispatcher.
/// </para>
/// </summary>
internal sealed class ReconnectPolicy
{
    /// <summary>How long a session waits for the phone that dropped out of it.</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromMinutes(1);

    /// <param name="grace">How long to wait. Tests use a shorter one.</param>
    public ReconnectPolicy(TimeSpan? grace = null) => Grace = grace ?? DefaultGrace;

    /// <summary>How long a held session waits before it ends the way it would have at once.</summary>
    public TimeSpan Grace { get; }

    /// <summary>The phone being waited for, or null when nothing is held open.</summary>
    public string? DeviceName { get; private set; }

    /// <summary>True while a session is being held open for a phone that has gone.</summary>
    public bool IsHolding => DeviceName is not null;

    /// <summary>When the wait runs out. Only meaningful while a session is held.</summary>
    public DateTime DeadlineUtc { get; private set; }

    /// <summary>
    /// Begins waiting for a phone.
    /// </summary>
    /// <returns>False when the phone never named itself, which leaves nothing to match on.</returns>
    public bool Hold(string? name, string? model, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        DeviceName = name;
        _model = model;
        DeadlineUtc = nowUtc + Grace;
        return true;
    }

    /// <summary>True when a device introducing itself now is the phone being waited for.</summary>
    public bool IsReturning(string? name, string? model) =>
        IsHolding && SameDevice(DeviceName, _model, name, model);

    /// <summary>True once the grace has run out with no sign of the phone.</summary>
    public bool HasExpired(DateTime nowUtc) => IsHolding && nowUtc >= DeadlineUtc;

    /// <summary>Forgets the wait. Safe to call whatever happens next.</summary>
    public void Clear()
    {
        DeviceName = null;
        _model = null;
        DeadlineUtc = default;
    }

    private string? _model;

    /// <summary>
    /// Whether two introductions are the same phone.
    /// <para>
    /// The address is deliberately not compared. A phone that dropped and joined another
    /// network, or was handed a new lease, comes back from a different one - and that is often
    /// exactly why it dropped. What the phone calls itself, and the model it names, is what
    /// identifies it; a model that one side did not send is not held against it.
    /// </para>
    /// </summary>
    public static bool SameDevice(string? name, string? model, string? otherName, string? otherModel)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(otherName)) return false;
        if (!string.Equals(name, otherName, StringComparison.OrdinalIgnoreCase)) return false;

        // Either side staying quiet about the model still leaves the name to go on.
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(otherModel)) return true;
        return string.Equals(model, otherModel, StringComparison.OrdinalIgnoreCase);
    }
}
