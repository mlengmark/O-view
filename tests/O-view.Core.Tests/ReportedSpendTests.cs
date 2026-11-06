using System.Text.Json;
using OView.Core.Providers.CachedUsage;

namespace OView.Core.Tests;

/// <summary>
/// The spend Claude reports, from the cache. Only the zero shape has been seen on a real
/// account (2026-11-06, <c>enabled</c> false); the non-zero cases here pin the reading taken
/// from the field names, and would be the first thing to correct if a real one differs.
/// </summary>
public class ReportedSpendTests
{
    private static SpendRead Read(string utilization) =>
        SpendRead.Read(JsonDocument.Parse(utilization).RootElement);

    private static string Spend(string used, string enabled = "false") =>
        $$$"""{"spend": {"used": {{{used}}}, "limit": null, "enabled": {{{enabled}}}}}""";

    private static string Used(string minor, string currency = "\"USD\"", string exponent = "2") =>
        $$$"""{"amount_minor": {{{minor}}}, "currency": {{{currency}}}, "exponent": {{{exponent}}}}""";

    [Fact]
    public void TheMeasuredZeroReadsAsAnAmountAndACurrency()
    {
        var read = Read(Spend(Used("0")));

        Assert.Equal(SpendReadState.Read, read.State);
        Assert.Equal(new ReportedSpend(0m, "USD", Enabled: false), read.Spend);
    }

    [Theory]
    [InlineData("1234", 2, "12.34")]
    [InlineData("5", 2, "0.05")]
    [InlineData("1500", 0, "1500")]
    [InlineData("2500", 3, "2.500")]
    [InlineData("9223372036854775807", 2, "92233720368547758.07")]
    public void TheAmountIsTheMinorUnitsOverTenToTheExponent(string minor, int exponent, string expected)
    {
        var read = Read(Spend(Used(minor, exponent: exponent.ToString()), enabled: "true"));

        Assert.Equal(SpendReadState.Read, read.State);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), read.Spend!.Amount);
        Assert.True(read.Spend.Enabled);
    }

    [Fact]
    public void TheLargestExponentADecimalCarriesStillReads()
    {
        var read = Read(Spend(Used("1", exponent: "28")));

        Assert.Equal(SpendReadState.Read, read.State);
        Assert.Equal(0.0000000000000000000000000001m, read.Spend!.Amount);
    }

    [Theory]
    [InlineData("""{"amount_minor": -1, "currency": "USD", "exponent": 2}""")]  // negative
    [InlineData("""{"amount_minor": 1, "currency": "USD", "exponent": 29}""")]   // would overflow a decimal's scale
    [InlineData("""{"amount_minor": 1, "currency": "USD", "exponent": 1000}""")] // absurd
    [InlineData("""{"amount_minor": 1, "currency": "USD", "exponent": -1}""")]
    [InlineData("""{"amount_minor": 1.5, "currency": "USD", "exponent": 2}""")]  // not whole minor units
    [InlineData("""{"amount_minor": 1e30, "currency": "USD", "exponent": 2}""")] // beyond a long
    [InlineData("""{"amount_minor": "12", "currency": "USD", "exponent": 2}""")]
    [InlineData("""{"amount_minor": 12, "currency": "", "exponent": 2}""")]
    [InlineData("""{"amount_minor": 12, "currency": null, "exponent": 2}""")]
    [InlineData("""{"amount_minor": 12, "exponent": 2}""")]
    [InlineData("""{"amount_minor": 12, "currency": "USD"}""")]
    [InlineData("""{"currency": "USD", "exponent": 2}""")]
    [InlineData("""null""")]
    [InlineData("""12""")]
    public void AnAmountItCannotReadIsUnreadableAndNeverThrows(string used)
    {
        var read = Read(Spend(used));

        Assert.Equal(SpendReadState.Unreadable, read.State);
        Assert.Null(read.Spend);
    }

    /// <summary>
    /// Unknown beside the amount is not "off": the way <see cref="ExtraUsageStatus"/> refuses
    /// to read a missing flag as disabled, a spend without its <c>enabled</c> is unreadable.
    /// </summary>
    [Theory]
    [InlineData("""{"spend": {"used": {"amount_minor": 0, "currency": "USD", "exponent": 2}}}""")]
    [InlineData("""{"spend": {"used": {"amount_minor": 0, "currency": "USD", "exponent": 2}, "enabled": "no"}}""")]
    [InlineData("""{"spend": {"used": {"amount_minor": 0, "currency": "USD", "exponent": 2}, "enabled": null}}""")]
    public void ASpendWithoutABooleanEnabledIsUnreadable(string utilization)
    {
        Assert.Equal(SpendReadState.Unreadable, Read(utilization).State);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"spend": null}""")]
    public void NoSpendIsAbsent(string utilization)
    {
        var read = Read(utilization);

        Assert.Equal(SpendReadState.Absent, read.State);
        Assert.Null(read.Spend);
    }

    [Theory]
    [InlineData("""{"spend": 7}""")]
    [InlineData("""{"spend": "free"}""")]
    [InlineData("""{"spend": []}""")]
    [InlineData("""{"spend": {}}""")]
    [InlineData("""{"spend": {"used": null, "enabled": true}}""")]
    public void ASpendOfTheWrongShapeIsUnreadable(string utilization)
    {
        Assert.Equal(SpendReadState.Unreadable, Read(utilization).State);
    }

    [Fact]
    public void ANonObjectUtilizationIsAbsent()
    {
        Assert.Equal(SpendRead.Absent, SpendRead.Read(default));
        Assert.Equal(SpendRead.Absent, Read("[]"));
    }
}
