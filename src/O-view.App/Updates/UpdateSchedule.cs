namespace OView.App.Updates;

/// <summary>
/// How often the background update check runs.
///
/// <para><b>Why jitter matters more than the interval.</b> The rate limit GitHub applies to
/// an unauthenticated caller is 60 requests per hour <b>per IP address</b>, shared with every
/// other unauthenticated caller behind the same NAT. A fixed interval means instances that
/// start together — a mass reboot after Patch Tuesday, a lab of machines, a fleet behind one
/// VPN exit — stay synchronised for as long as they run, and arrive at GitHub in the same
/// second every time. Spreading them costs nothing and removes the only way this app could
/// plausibly exhaust that budget on its own.</para>
///
/// <para>Applied <b>once at startup</b> rather than per tick. A single offset de-synchronises
/// instances permanently, which is the whole objective; re-rolling every tick would add
/// variance nobody benefits from and make the schedule harder to reason about.</para>
/// </summary>
public static class UpdateSchedule
{
    /// <summary>
    /// How far the interval may move, as a fraction — ±15%. Wide enough to scatter a fleet
    /// across a couple of hours at the default interval, narrow enough that the cadence still
    /// means what the ADR says it means.
    /// </summary>
    public const double JitterFraction = 0.15;

    /// <summary>
    /// The interval this instance will actually use: <paramref name="interval"/> scaled by a
    /// random factor in [1 - <see cref="JitterFraction"/>, 1 + <see cref="JitterFraction"/>].
    ///
    /// <para>Never returns zero or negative, whatever it is handed — a zero-interval timer is
    /// a busy loop, and this runs in an app designed to sit in a tray for days.</para>
    /// </summary>
    public static TimeSpan Jittered(TimeSpan interval, Random random)
    {
        if (interval <= TimeSpan.Zero)
        {
            return interval;
        }

        var factor = 1.0 + ((random.NextDouble() * 2.0) - 1.0) * JitterFraction;
        var jittered = interval * factor;

        // Defensive floor. The arithmetic above cannot reach zero for a positive interval,
        // but a caller that later widens JitterFraction past 1.0 would make it negative, and
        // that failure would present as a spin rather than as an exception.
        return jittered > TimeSpan.Zero ? jittered : interval;
    }
}
