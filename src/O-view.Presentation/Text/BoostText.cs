using System.Globalization;
using OView.Core.Providers.CachedUsage;

namespace OView.Presentation.Text;

/// <summary>
/// The boost chip and its hover card: the panel's wording for a live promo (see
/// <see cref="PanelText"/> for why wording like this is shared rather than written per
/// platform).
/// </summary>
public static class BoostText
{
    /// <summary>
    /// The boost chip on a meter's label row: <c>50% Boosted · until 31 Aug · ends in 2w 4d 14h</c>.
    ///
    /// <para><b>The figure leads and the word follows it immediately.</b> The same row carries
    /// utilisation, a <i>level</i>, a hundred pixels to the right, and this is a <i>delta</i>.
    /// A bare <c>50%</c> adrift on that row would read as the same kind of number; <c>50%
    /// Boosted</c> reads as one phrase, so the two are never split.</para>
    ///
    /// <para><b>Every part is optional and drops out silently.</b> With neither figure parsed the
    /// chip is just <c>Boosted</c>, which is the floor this feature never falls below: the
    /// message itself is relayed verbatim in the hover card either way
    /// (<see cref="BoostCard"/>), and that path depends on no parsing at all.</para>
    ///
    /// <para><b>The month is abbreviated, and that is a layout constraint rather than a
    /// preference.</b> Measured at the panel's real width in the face the Windows head paints,
    /// the label row leaves 281px for this chip: <c>50% Boosted · until 31 Aug · ends in 14h</c>
    /// takes 220, the widest the payload could carry takes 263, and the same string with a full
    /// month name takes 289 and overflows. The row must never wrap, a wrap shifts the bar under
    /// it and changes the panel's height.</para>
    /// </summary>
    /// <param name="notice">The notice to describe. Its own bar decides where this is drawn.</param>
    /// <param name="utcNow">Now, for the countdown.</param>
    /// <param name="local">The reader's zone: a promo ends at the end of its last local day.</param>
    public static string BoostChip(BoostNotice notice, DateTimeOffset utcNow, TimeZoneInfo local)
    {
        var chip = notice.Percent is { } pct
            ? string.Create(CultureInfo.InvariantCulture, $"{pct}% Boosted")
            : "Boosted";

        if (notice.EndsOn is not { } last)
        {
            return chip;
        }

        var ends = EndOfDayUtc(last, local);
        return string.Create(CultureInfo.InvariantCulture,
            $"{chip} · until {last:d MMM} · ends in {BoostRemaining(ends - utcNow)}");
    }

    /// <summary>
    /// Time left on a promo, in the weeks/days/hours the chip asks for: <c>2w 4d 14h</c>,
    /// <c>4d 14h</c>, <c>14h</c>.
    ///
    /// <para><b>Empty leading units are dropped</b>, a promo ending tonight reads <c>14h</c>,
    /// not <c>0w 0d 14h</c>. Hours are the floor because the end is a <i>date</i>: the source
    /// says "Aug 31", so the last hour of that day is the finest thing anyone knows, and
    /// counting down in minutes would imply a precision the sentence never carried.</para>
    /// </summary>
    public static string BoostRemaining(TimeSpan left)
    {
        if (left <= TimeSpan.Zero)
        {
            return "under an hour";
        }

        var weeks = left.Days / 7;
        var days = left.Days % 7;
        var hours = left.Hours;

        var parts = new List<string>(3);
        if (weeks > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{weeks}w"));
        }

        if (days > 0 || parts.Count > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{days}d"));
        }

        if (hours > 0 || parts.Count > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{hours}h"));
        }

        return parts.Count > 0 ? string.Join(" ", parts) : "under an hour";
    }

    /// <summary>
    /// The instant a promo's last day ends, in UTC, the next local midnight after it.
    ///
    /// <para>Built from the zone's offset rather than
    /// <see cref="TimeZoneInfo.ConvertTimeToUtc(DateTime, TimeZoneInfo)"/>, which throws when the
    /// wall-clock time it is handed does not exist. Midnight is skipped outright in a handful of
    /// zones on their DST transition day, and a countdown must not be the thing that takes the
    /// panel down.</para>
    /// </summary>
    private static DateTimeOffset EndOfDayUtc(DateOnly lastDay, TimeZoneInfo local)
    {
        var midnight = lastDay.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(midnight, local.GetUtcOffset(midnight)).ToUniversalTime();
    }

    /// <summary>
    /// The hover card behind the chip: Claude's sentence, then who said it and when O-view read it.
    ///
    /// <para><b>The message is never edited, summarised or re-worded.</b> Whether this promo
    /// applies to this account is something O-view cannot check, the payload is a feature-flag
    /// cache, evaluated server-side, so the panel relays rather than asserts, and the provenance
    /// line is what makes that the honest reading rather than a hedge (rule 6).</para>
    /// </summary>
    public static string BoostCard(
        BoostNotice notice, DateTimeOffset fetchedAtUtc, TimeZoneInfo local)
    {
        var read = TimeZoneInfo.ConvertTime(fetchedAtUtc, local);
        var ends = notice.EndsOn is { } last
            ? string.Create(CultureInfo.InvariantCulture, $"Ends {last:ddd d MMM} · ")
            : "";

        return string.Create(CultureInfo.InvariantCulture,
            $"{notice.Text}\n\n{ends}reported by Claude Code, read {read:HH:mm}");
    }
}
