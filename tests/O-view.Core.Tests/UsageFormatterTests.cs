using System.Globalization;
using OView.Core.Models;

namespace OView.Core.Tests;

/// <summary>
/// The panel's most user-visible formatting had no test at all while it lived in a Window
/// code-behind (GitHub issue #55). The K/M boundaries in particular were untested in both
/// directions, and they decide what every tile and hover card reads.
/// </summary>
public class UsageFormatterTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(1, "1")]
    [InlineData(999, "999")]              // last value below the K boundary
    [InlineData(1_000, "1.0K")]           // first value at it
    [InlineData(1_500, "1.5K")]
    [InlineData(999_999, "1000.0K")]      // last value below the M boundary
    [InlineData(1_000_000, "1.0M")]       // first value at it
    [InlineData(12_700_000, "12.7M")]
    [InlineData(684_600_000, "684.6M")]
    public void Tokens_AbbreviatesAtThousandsAndMillions(long tokens, string expected)
    {
        Assert.Equal(expected, UsageFormatter.Tokens(tokens));
    }

    [Theory]
    [InlineData(0, "$0.00")]
    [InlineData(9.36, "$9.36")]
    [InlineData(492.52, "$492.52")]
    [InlineData(0.004, "$0.00")]          // rounds, never renders as "unknown"
    public void Usd_AlwaysTwoDecimals(decimal usd, string expected)
    {
        Assert.Equal(expected, UsageFormatter.Usd(usd));
    }

    [Fact]
    public void Usd_Null_IsUnknown_NotZero()
    {
        // An unpriced model has an unknown value, not a zero one. "$0.00" would read as
        // "this cost nothing" (CLAUDE.md rule 6).
        Assert.Equal("unknown", UsageFormatter.Usd(null));
    }

    [Fact]
    public void Formatting_IsInvariant_NotTheMachineCulture()
    {
        // A comma decimal separator would break both figures on a European machine. The
        // app pins its own presentation rather than inheriting the OS setting.
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

            Assert.Equal("$1234.50", UsageFormatter.Usd(1234.50m));
            Assert.Equal("1.5K", UsageFormatter.Tokens(1_500));
            Assert.Equal("12.7M", UsageFormatter.Tokens(12_700_000));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
