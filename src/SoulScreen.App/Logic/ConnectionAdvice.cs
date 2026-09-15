namespace SoulScreen.App.Logic;

/// <summary>
/// How long a network has to stay poor before advice is offered about it.
/// <para>
/// Wi-Fi drops a burst now and then on an otherwise good link; advice fired on the first poor
/// reading would be noise. The advice is also not repeated while the link stays bad - once is
/// enough per stretch of trouble - and a good stretch longer than the hush means the next run
/// of trouble is worth naming again.
/// </para>
/// Deliberately free of WPF so the decision can be tested on its own.
/// </summary>
public sealed class ConnectionAdvice
{
    /// <summary>Poor readings needed, in a row, before anything is said.</summary>
    public static readonly TimeSpan PoorStreakNeeded = TimeSpan.FromSeconds(6);

    /// <summary>How long after advice is given it will not be given again - even if the
    /// connection turns good in between, unless it stays good this long.</summary>
    public static readonly TimeSpan Hush = TimeSpan.FromMinutes(4);

    /// <summary>A good stretch longer than this clears the hush.</summary>
    public static readonly TimeSpan Recovery = TimeSpan.FromSeconds(45);

    private TimeSpan? _poorSince;
    private TimeSpan? _advisedAt;
    private TimeSpan? _goodSince;

    /// <summary>True once the streak has been long enough and advice has not been given
    /// for this stretch of trouble already.</summary>
    public bool ShouldAdvise(TimeSpan now, bool poor)
    {
        if (poor)
        {
            _goodSince = null;
            _poorSince ??= now;
            var streakLongEnough = now - _poorSince.Value >= PoorStreakNeeded;
            var hushed = _advisedAt is { } at && now - at < Hush && _goodSince is null;
            var notYetForThisStretch = _advisedAt is null || _advisedAt < _poorSince;
            if (streakLongEnough && !hushed && notYetForThisStretch)
            {
                _advisedAt = now;
                return true;
            }
            return false;
        }

        _poorSince = null;
        _goodSince ??= now;
        // Long enough recovered that the next run of trouble is fresh.
        if (_goodSince is { } good && now - good >= Recovery) _advisedAt = null;
        return false;
    }

    /// <summary>Forgets the session measured so far.</summary>
    public void Reset()
    {
        _poorSince = null;
        _advisedAt = null;
        _goodSince = null;
    }
}
