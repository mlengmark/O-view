using OView.Core.Updates;

namespace OView.Core.Tests;

/// <summary>
/// A rate-limited update check used to be indistinguishable from a dead network: both became
/// <see cref="UpdateOutcome.Unknown"/>, the reset header was ignored, and the next check
/// retried straight back into the limit (GitHub issue #176).
///
/// <para>These pin the rule that tells them apart. The limit is 60 requests an hour <b>per
/// IP</b> for an unauthenticated caller, so this is not an edge case on a shared network —
/// and conditional requests buy no exemption without an Authorization header, which rule 3
/// forbids this app from holding.</para>
/// </summary>
public class RateLimitResponseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private static bool IsLimited(
        int status,
        string? remaining,
        string? reset,
        string? retryAfter,
        out DateTimeOffset? retryAt) =>
        RateLimitResponse.IsRateLimited(status, remaining, reset, retryAfter, Now, out retryAt);

    // ── recognising a throttle ──────────────────────────────────────────────────────

    /// <summary>The shape GitHub actually sends: 403 with the budget spent.</summary>
    [Fact]
    public void A403WithNoRemainingBudgetIsRateLimited()
    {
        var resetsAt = Now.AddMinutes(37);

        Assert.True(IsLimited(403, "0", resetsAt.ToUnixTimeSeconds().ToString(), null, out var retryAt));
        Assert.Equal(resetsAt, retryAt);
    }

    [Fact]
    public void A429IsRateLimitedWithoutNeedingCorroboration()
    {
        Assert.True(IsLimited(429, null, null, null, out var retryAt));
        Assert.Null(retryAt);   // throttled, duration unknown — still more than "offline"
    }

    /// <summary>
    /// Not every 403 is a throttle. GitHub uses it for causes a cooldown would not fix, and
    /// excusing those as "try again in an hour" would bury a real fault behind a wait.
    /// </summary>
    [Fact]
    public void A403WithoutRateLimitHeadersIsNotATrottle()
    {
        Assert.False(IsLimited(403, null, null, null, out _));
        Assert.False(IsLimited(403, "42", null, null, out _));   // budget remains: something else failed
    }

    [Theory]
    [InlineData(200)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(503)]
    public void OtherStatusesAreNeverRateLimited(int status)
    {
        Assert.False(IsLimited(status, "0", "1787501288", "60", out _));
    }

    // ── reading the time ────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>x-ratelimit-reset</c> is epoch seconds; <c>retry-after</c> is a delta. Reading the
    /// first as a delta — which its name invites, sitting beside the second — would put the
    /// retry decades away and pin the app out of checking permanently.
    /// </summary>
    [Fact]
    public void TheResetHeaderIsReadAsAnInstantAndRetryAfterAsADelta()
    {
        var resetsAt = Now.AddMinutes(20);

        Assert.True(IsLimited(403, "0", resetsAt.ToUnixTimeSeconds().ToString(), null, out var fromReset));
        Assert.Equal(resetsAt, fromReset);

        Assert.True(IsLimited(429, null, null, "600", out var fromDelta));
        Assert.Equal(Now.AddSeconds(600), fromDelta);
    }

    /// <summary>The reset header wins: it is an absolute answer where the delta is relative.</summary>
    [Fact]
    public void TheResetHeaderIsPreferredOverRetryAfter()
    {
        var resetsAt = Now.AddMinutes(5);

        Assert.True(IsLimited(429, "0", resetsAt.ToUnixTimeSeconds().ToString(), "3600", out var retryAt));
        Assert.Equal(resetsAt, retryAt);
    }

    /// <summary>
    /// A reset already in the past is no answer. Taking it would mean a cooldown that never
    /// expires on a machine whose clock runs ahead.
    /// </summary>
    [Fact]
    public void AResetInThePastIsIgnoredRatherThanTrusted()
    {
        Assert.True(IsLimited(403, "0", Now.AddHours(-1).ToUnixTimeSeconds().ToString(), null, out var retryAt));
        Assert.Null(retryAt);
    }

    [Fact]
    public void UnparseableHeadersLeaveTheTimeUnknownWithoutThrowing()
    {
        Assert.True(IsLimited(429, "not-a-number", "soon", "later", out var retryAt));
        Assert.Null(retryAt);
    }

    /// <summary>
    /// Still a throttle when the duration is unreadable. The caller falls back to its own
    /// cooldown; what it must not do is conclude the network is down.
    /// </summary>
    [Fact]
    public void AThrottleWithNoUsableTimeIsStillAThrottle()
    {
        Assert.True(IsLimited(403, "0", null, null, out var retryAt));
        Assert.Null(retryAt);
    }
}
