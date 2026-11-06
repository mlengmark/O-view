using System.Text.Json;

namespace OView.Core.Providers.CachedUsage;

/// <summary>What the read made of the <c>spend</c> block, so a missing amount can say why.</summary>
public enum SpendReadState
{
    /// <summary>No <c>spend</c> in the block, or JSON <c>null</c>: the file did not say.</summary>
    Absent,

    /// <summary>Present, but not in a shape this reads: a wrong type, a negative amount, an absurd exponent.</summary>
    Unreadable,

    /// <summary>Read: <see cref="ReportedSpend"/> is set.</summary>
    Read,
}

/// <summary>
/// What Claude reports having been spent past the plan, from
/// <c>cachedUsageUtilization.utilization.spend</c>.
///
/// <para><b>Only the zero shape has been observed.</b> <c>used</c> was
/// <c>{"amount_minor": 0, "currency": "USD", "exponent": 2}</c> with <c>enabled</c> false. A
/// non-zero amount is read from the field names: <c>amount_minor</c> is the amount in the
/// currency's minor unit and <c>exponent</c> the number of decimal places, so the amount is
/// <c>amount_minor / 10^exponent</c>. Nothing in the block names a period either, which is why
/// nothing derived from it may claim one.</para>
/// </summary>
/// <param name="Amount">The amount used, in the currency's major unit.</param>
/// <param name="Currency">ISO currency code as written, e.g. <c>USD</c>.</param>
/// <param name="Enabled">Whether spending past the plan is switched on, <c>spend.enabled</c>.</param>
public sealed record ReportedSpend(decimal Amount, string Currency, bool Enabled);

/// <summary>The outcome of reading <c>spend</c>: a state, and the figure only when <see cref="SpendReadState.Read"/>.</summary>
/// <param name="State">Absent, unreadable or read.</param>
/// <param name="Spend">The figure; null unless <paramref name="State"/> is <see cref="SpendReadState.Read"/>.</param>
public sealed record SpendRead(SpendReadState State, ReportedSpend? Spend)
{
    /// <summary>Property inside <c>utilization</c> that holds the block.</summary>
    public const string PropertyName = "spend";

    /// <summary>Largest scale <see cref="decimal"/> can carry.</summary>
    private const int MaxExponent = 28;

    /// <summary>The default everywhere: the file told us nothing.</summary>
    public static readonly SpendRead Absent = new(SpendReadState.Absent, null);

    private static readonly SpendRead Unreadable = new(SpendReadState.Unreadable, null);

    /// <summary>
    /// Reads the block out of a <c>utilization</c> object. Never throws: it is
    /// <see cref="Absent"/> when there is no <c>spend</c> or it is null, and
    /// <see cref="SpendReadState.Unreadable"/> for anything present that is not an amount with
    /// a currency beside a boolean <c>enabled</c>.
    /// </summary>
    public static SpendRead Read(JsonElement utilization)
    {
        if (utilization.ValueKind != JsonValueKind.Object ||
            !utilization.TryGetProperty(PropertyName, out var spend) ||
            spend.ValueKind == JsonValueKind.Null)
        {
            return Absent;
        }

        if (spend.ValueKind != JsonValueKind.Object ||
            !spend.TryGetProperty("enabled", out var enabled) ||
            enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !spend.TryGetProperty("used", out var used) ||
            used.ValueKind != JsonValueKind.Object ||
            !used.TryGetProperty("amount_minor", out var minor) ||
            minor.ValueKind != JsonValueKind.Number ||
            !minor.TryGetInt64(out var amountMinor) || amountMinor < 0 ||
            !used.TryGetProperty("exponent", out var scale) ||
            scale.ValueKind != JsonValueKind.Number ||
            !scale.TryGetInt32(out var exponent) || exponent is < 0 or > MaxExponent ||
            JsonFields.NonEmptyString(used, "currency") is not { } currency)
        {
            return Unreadable;
        }

        // Built from its bits rather than divided by a power of ten: a non-negative long
        // always fits the low 64 bits, and the scale does the division exactly.
        var amount = new decimal((int)amountMinor, (int)(amountMinor >> 32), 0, false, (byte)exponent);

        return new SpendRead(
            SpendReadState.Read,
            new ReportedSpend(amount, currency, enabled.ValueKind == JsonValueKind.True));
    }
}
