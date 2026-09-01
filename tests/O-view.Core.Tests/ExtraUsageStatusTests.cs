using System.Text.Json;
using OView.Core.Providers.CachedUsage;

namespace OView.Core.Tests;

/// <summary>
/// Whether extra usage (auto-billing) is switched on for the account, read from Claude Code's
/// own cache — the field that decides whether O-view's off-plan banner may claim a charge at
/// all (issue #259).
///
/// <para><b>These fixtures are the real shape, measured 2026-09-01</b> on a <c>claude_pro</c>
/// account against a block fetched three minutes earlier. That matters because the findings had
/// this field recorded as empty even when fresh, and every wording built on that premise hedged
/// around an answer that was sitting in the file.</para>
/// </summary>
public class ExtraUsageStatusTests
{
    private static ExtraUsageStatus Read(string utilization) =>
        ExtraUsageStatus.Read(JsonDocument.Parse(utilization).RootElement);

    /// <summary>
    /// The measured block, verbatim but for indentation: four booleans carrying real values,
    /// and a money half that is null precisely BECAUSE extra usage is off and nothing has been
    /// spent. "Mostly nulls" was read as "empty"; it is neither.
    /// </summary>
    private const string Measured = """
    {
      "extra_usage": {
        "is_enabled": false, "monthly_limit": null, "used_credits": null,
        "utilization": null, "currency": null, "decimal_places": null,
        "disabled_reason": null, "user_disabled": true,
        "spend_limit_reached": false, "credits_ever_enabled": true,
        "daily": null, "weekly": null
      }
    }
    """;

    [Fact]
    public void TheMeasuredBlockReadsAsDisabledAndSaysWhy()
    {
        var status = Read(Measured);

        Assert.Equal(ExtraUsageState.Disabled, status.State);
        Assert.True(status.UserDisabled);
        Assert.False(status.SpendLimitReached);
        Assert.Null(status.DisabledReason);
    }

    [Fact]
    public void AnEnabledAccountReadsAsEnabled()
    {
        var status = Read("""
        {"extra_usage": {"is_enabled": true, "user_disabled": false, "monthly_limit": null}}
        """);

        Assert.Equal(ExtraUsageState.Enabled, status.State);
        Assert.False(status.UserDisabled);
    }

    /// <summary>
    /// <c>is_enabled</c> is the resolved answer and the only thing the state comes from. The
    /// other three explain it — an account barred by policy is disabled without the person
    /// having chosen it, and reading <c>user_disabled</c> as the answer would call that one
    /// enabled.
    /// </summary>
    [Fact]
    public void ThePolicyDisabledCaseIsStillDisabled()
    {
        var status = Read("""
        {"extra_usage": {"is_enabled": false, "user_disabled": false,
                         "disabled_reason": "org_policy"}}
        """);

        Assert.Equal(ExtraUsageState.Disabled, status.State);
        Assert.False(status.UserDisabled);
        Assert.Equal("org_policy", status.DisabledReason);
    }

    /// <summary>
    /// <c>spend_limit_reached</c> is carried, never branched on. It looks like a second off
    /// switch for an enabled account whose cap is spent, and it may well be — but nothing has
    /// been observed with it true, so acting on it would be reasoning from a field name
    /// (rule 6). If an account is ever found in that state, this is the test to change.
    /// </summary>
    [Fact]
    public void ASpentCapIsReportedButDoesNotSwitchTheStateOff()
    {
        var status = Read("""
        {"extra_usage": {"is_enabled": true, "spend_limit_reached": true}}
        """);

        Assert.Equal(ExtraUsageState.Enabled, status.State);
        Assert.True(status.SpendLimitReached);
    }

    /// <summary>
    /// Every way the file can decline to answer. None of them may become a guess: Unknown told
    /// as Disabled reassures a user who is being billed, and Unknown told as Enabled is exactly
    /// the false alarm this work removed.
    /// </summary>
    [Theory]
    [InlineData("""{}""")]                                             // no such property
    [InlineData("""{"extra_usage": null}""")]                          // the siblings' normal state
    [InlineData("""{"extra_usage": {}}""")]                            // present, says nothing
    [InlineData("""{"extra_usage": {"is_enabled": null}}""")]          // explicitly unknown
    [InlineData("""{"extra_usage": {"is_enabled": "false"}}""")]       // a shape change upstream
    [InlineData("""{"extra_usage": {"user_disabled": true}}""")]       // the why without the what
    public void AnythingElseIsUnknownRatherThanAGuess(string utilization) =>
        Assert.Equal(ExtraUsageState.Unknown, Read(utilization).State);

    /// <summary>A <c>utilization</c> that is not an object at all — the block's own absence.</summary>
    [Fact]
    public void AMissingUtilizationObjectIsUnknown() =>
        Assert.Equal(ExtraUsageState.Unknown, ExtraUsageStatus.Read(default(JsonElement)).State);

    /// <summary>
    /// It arrives on the parsed block, so a caller that already has the percentages has the
    /// setting too and does not read the file a second time to get it.
    /// </summary>
    [Fact]
    public void TheParsedBlockCarriesIt()
    {
        var parsed = CachedUtilization.Parse($$"""
        {
          "cachedUsageUtilization": {
            "fetchedAtMs": 1788246449647,
            "utilization": {
              "five_hour": {"utilization": 100},
              "extra_usage": {"is_enabled": true}
            }
          }
        }
        """);

        Assert.Equal(ExtraUsageState.Enabled, parsed!.ExtraUsage.State);
    }

    /// <summary>
    /// A block from before the field existed still parses, and reports Unknown rather than
    /// inheriting whatever the default of a bool would have been.
    /// </summary>
    [Fact]
    public void ABlockWithoutTheFieldStillParses()
    {
        var parsed = CachedUtilization.Parse("""
        {"cachedUsageUtilization": {"fetchedAtMs": 1788246449647,
                                    "utilization": {"five_hour": {"utilization": 12}}}}
        """);

        Assert.Equal(12, parsed!.FiveHour!.Percent);
        Assert.Equal(ExtraUsageState.Unknown, parsed.ExtraUsage.State);
    }
}
