using System.Reflection;
using OView.Core.Models;
using OView.Core.Pricing;

namespace OView.Core.Tests;

/// <summary>
/// Pins the positional order of the records that are constructed positionally.
///
/// <para><b>Why this exists.</b> Issue #248 removed the seventh parameter of
/// <see cref="UsageSnapshot"/>. Two provider call sites failed to build, which is the outcome
/// anyone would expect, and two tests <b>compiled silently</b> with their argument landing in the
/// wrong property, one of which went on passing. A test that keeps passing while asserting the
/// wrong thing is the worst result available, and nothing in the language prevents it: a trailing
/// optional parameter that is removed simply shifts everything after it.</para>
///
/// <para>These tests do not restrict how anyone writes code. They make a change to the
/// <i>shape</i> fail in one obvious place with an explanation, instead of scattering into
/// bindings that still compile.</para>
///
/// <para><b>When one of these fails, that is the test working.</b> Read the diff, confirm every
/// positional construction still means what it did, the compiler will not, and then update the
/// list here in the same commit.</para>
/// </summary>
public class RecordShapeTests
{
    /// <summary>
    /// The primary constructor's parameter names, in order.
    ///
    /// <para>A positional record also carries a copy constructor, so the one with the most
    /// parameters is the primary, matching on count would break the moment a record gained one.</para>
    /// </summary>
    private static string[] PositionalOrder(Type record) =>
        [.. record.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .First()
            .GetParameters()
            .Select(p => p.Name!)];

    private static void AssertOrder(Type record, params string[] expected)
    {
        var actual = PositionalOrder(record);

        Assert.True(
            expected.SequenceEqual(actual, StringComparer.Ordinal),
            $"{record.Name}'s positional order changed.\n" +
            $"  expected: {string.Join(", ", expected)}\n" +
            $"  actual:   {string.Join(", ", actual)}\n\n" +
            "Every positional construction of this record now binds differently, and the compiler " +
            "will only catch the ones whose types stopped matching. Check each call site, then " +
            "update this list in the same commit.");
    }

    /// <summary>
    /// Eight parameters. The two trailing <see cref="TimeSpan"/>? are still the hazard #248
    /// found: both optional, both the same type, so removing or reordering either rebinds the
    /// other with no type change to notice.
    ///
    /// <para>The five readings in the middle are now each their own value-plus-status type, so
    /// the two <see cref="UsagePercent"/> arguments can be swapped for each other and the three
    /// <see cref="UsageInstant"/> ones for each other, fewer ways to get it wrong than the
    /// plain nullables allowed, and still ways.</para>
    /// </summary>
    [Fact]
    public void UsageSnapshotPositionalOrderIsPinned() =>
        AssertOrder(
            typeof(UsageSnapshot),
            "Source",
            "SessionPercentValue",
            "WeeklyPercentValue",
            "SessionResetAtUtcValue",
            "CapturedAtUtcValue",
            "WeeklyResetAtUtcValue",
            "WeeklyResetPeriod",
            "SessionResetUncertainty");

    /// <summary>
    /// Six positional <c>long</c>s, and the most dangerous shape in the codebase (issue #255).
    ///
    /// <para>Nothing in the language stops <c>new TokenSplit(input, w1h, w5m, …)</c> from
    /// compiling, and the two cache-write columns bill at 1.25× and 2×, so swapping them is a
    /// money error of exactly the kind this whole type was added to fix, arriving with no
    /// compiler diagnostic and no test failure anywhere the value is not asserted.</para>
    /// </summary>
    [Fact]
    public void TokenSplitPositionalOrderIsPinned() =>
        AssertOrder(
            typeof(TokenSplit),
            "Input",
            "CacheWrite5m",
            "CacheWrite1h",
            "CacheWriteTtlUnrecorded",
            "CacheRead",
            "Output");

    /// <summary>
    /// Five positional <c>decimal</c>s, and the published table lists its columns in a different
    /// order, input, 5m write, 1h write, cache hit, output. Transcribing a row off the page
    /// positionally therefore swaps output with the two writes, silently and with no type change
    /// to notice. The optional sixth is fast mode, and dropping it would rebind nothing.
    /// </summary>
    [Fact]
    public void ModelRatesPositionalOrderIsPinned() =>
        AssertOrder(
            typeof(ModelRates),
            "InputPerMTok",
            "OutputPerMTok",
            "CacheWrite5mPerMTok",
            "CacheWrite1hPerMTok",
            "CacheReadPerMTok",
            "Fast");

    /// <summary>
    /// The more dangerous of the two, and it has never bitten, yet.
    ///
    /// <para>It opens <c>long, long</c>: today's tokens, then the 31-day tokens. Swapping them
    /// compiles, passes type checking, and puts a month's figure where the day's belongs, a
    /// wrong number rendered confidently, which is the failure CLAUDE.md rule 6 is written
    /// against. The four Est. figures that used to interleave with them left the positional
    /// list when they became <see cref="EstimatedUsd"/>, which halved the shape without making
    /// the remaining pair any safer to transpose.</para>
    /// </summary>
    [Fact]
    public void PanelStatisticsPositionalOrderIsPinned() =>
        AssertOrder(
            typeof(PanelStatistics),
            "TokensToday",
            "Tokens31Days",
            "RecordedDays",
            "WindowDays",
            "DailySeries",
            "CreditTokens31Days",
            "Divergence");
}
