using OView.App;
using OView.App.Updates;

namespace OView.App.Tests;

/// <summary>
/// The update cadence and its jitter (ADR-0009 as amended 2026-08-23).
///
/// <para>Both numbers here are constrained from outside the app. GitHub allows an
/// unauthenticated caller 60 requests an hour <b>per IP address</b>, shared with everyone
/// behind the same NAT, and conditional requests earn no exemption without an
/// <c>Authorization</c> header — which rule 3 forbids this app from holding. So the interval
/// cannot drop to minutes, and instances that start together must not stay in step.</para>
/// </summary>
public class UpdateScheduleTests
{
    // ── the interval ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Six hours: four requests a day per instance. The floor is what stops a future edit
    /// quietly turning this into a per-minute poll on a shared address; the ceiling is the
    /// gap that prompted the change, an app running for days taking a day to notice.
    /// </summary>
    [Fact]
    public void TheDefaultIntervalIsHoursNotMinutes()
    {
        var interval = new UsageEngineOptions().UpdateCheckInterval;

        Assert.Equal(TimeSpan.FromHours(6), interval);
        Assert.True(interval >= TimeSpan.FromHours(1),
            "an interval under an hour spends a rate-limit budget shared per IP address");
        Assert.True(interval <= TimeSpan.FromHours(24));
    }

    /// <summary>Even at the worst jitter, the cadence stays inside a sane band.</summary>
    [Fact]
    public void JitterNeverPushesTheIntervalOutOfBand()
    {
        var random = new Random(20260823);
        var baseline = new UsageEngineOptions().UpdateCheckInterval;

        for (var i = 0; i < 2_000; i++)
        {
            var jittered = UpdateSchedule.Jittered(baseline, random);

            Assert.InRange(
                jittered,
                baseline * (1 - UpdateSchedule.JitterFraction),
                baseline * (1 + UpdateSchedule.JitterFraction));

            // Six per hour is the shape that would matter on a shared IP; nothing this
            // produces may approach it.
            Assert.True(jittered > TimeSpan.FromHours(1));
        }
    }

    // ── the jitter ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The point of the whole thing: two instances starting in the same second must not keep
    /// arriving at GitHub in the same second for as long as they run.
    /// </summary>
    [Fact]
    public void TwoInstancesStartingTogetherDoNotShareAnInterval()
    {
        var interval = TimeSpan.FromHours(6);

        var intervals = Enumerable.Range(0, 50)
            .Select(seed => UpdateSchedule.Jittered(interval, new Random(seed)))
            .ToList();

        // Not merely "some differ" — a implementation that jittered only occasionally would
        // pass that. Nearly all of them must be distinct.
        Assert.True(intervals.Distinct().Count() >= 45,
            $"only {intervals.Distinct().Count()} of 50 instances got a distinct interval");
    }

    /// <summary>Jitter moves it in both directions, not just one.</summary>
    [Fact]
    public void JitterGoesBothWaysAroundTheBaseInterval()
    {
        var random = new Random(7);
        var baseline = TimeSpan.FromHours(6);

        var samples = Enumerable.Range(0, 500)
            .Select(_ => UpdateSchedule.Jittered(baseline, random))
            .ToList();

        Assert.Contains(samples, s => s < baseline);
        Assert.Contains(samples, s => s > baseline);
    }

    /// <summary>
    /// A zero or negative interval must never come back as a timer that spins. It cannot
    /// arise from the shipped configuration; it can arise from a test or a future edit, and
    /// the failure would present as a busy loop rather than an exception.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveIntervalIsReturnedUnchangedRatherThanScaled(int hours)
    {
        var interval = TimeSpan.FromHours(hours);

        Assert.Equal(interval, UpdateSchedule.Jittered(interval, new Random(1)));
    }

    /// <summary>The launch check is unchanged — it is what catches a release cut while you were off.</summary>
    [Fact]
    public void TheFirstCheckStillRunsShortlyAfterLaunch()
    {
        var delay = new UsageEngineOptions().FirstUpdateCheckDelay;

        Assert.Equal(TimeSpan.FromSeconds(30), delay);
    }
}
