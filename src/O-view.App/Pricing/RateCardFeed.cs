using System.Net.Http;
using System.Net.Http.Headers;
using OView.Core.Pricing;

namespace OView.App.Pricing;

/// <summary>
/// Fetches Anthropic's published pricing table and reports where it differs from the bundled
/// rate card.
///
/// <para><b>It returns a difference list and installs nothing.</b> The bundled table stays
/// authoritative until a human confirms a change, and that asymmetry is the whole design: a
/// broken parser here produces a false "check pricing" line in the log, which is noisy and
/// harmless, where a parser that broke while <i>writing</i> rates would produce confident wrong
/// money (GitHub issue #257).</para>
///
/// <para><b>Modelled on <see cref="Updates.ReleaseFeed"/> deliberately.</b> Same client shape,
/// same short timeout, same swallow-and-report-unknown failure handling, same split — the IO
/// lives here and the comparison lives in Core's <see cref="PublishedRates"/>, so both heads
/// ask the same URL and neither grows the logic.</para>
///
/// <para><b>The network bar is the release feed's, not a new one.</b> This is an
/// unauthenticated GET of a public documentation page: no credential, no user data, nothing
/// sent about this machine. ADR-0016 records that as an amendment to ADR-0009 rather than a new
/// class of decision, and SECURITY.md names the host. Rule 3 is untouched — it governs
/// subscription credentials, and none is involved.</para>
/// </summary>
public sealed class RateCardFeed
{
    // One client, a real User-Agent, and the same 15-second timeout the release feed uses so a
    // stalled network never holds a background task open.
    private static readonly HttpClient Http = CreateClient();

    private readonly IAppLog? _log;
    private readonly Func<DateTimeOffset> _utcNow;

    public RateCardFeed(IAppLog? log = null, Func<DateTimeOffset>? utcNow = null)
    {
        _log = log;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Compares the published table against <paramref name="card"/>.
    ///
    /// <para>Null on any failure — offline, timeout, non-success status, or a page that did not
    /// parse. An honest "did not check", never a silent pass: reporting agreement because the
    /// request failed is the one outcome that would make this mechanism worse than not having
    /// it (CLAUDE.md rule 6).</para>
    ///
    /// <para>Nothing is persisted. The result is re-derivable in one request, and a
    /// last-checked timestamp buys nothing that the weekly timer does not already give —
    /// unlike an observed weekly reset, which costs a week to see again.</para>
    /// </summary>
    public async Task<RateCardDrift?> CheckAsync(RateCard card, CancellationToken cancellation = default)
    {
        try
        {
            using var response = await Http
                .GetAsync(PublishedRates.PricingUrl, cancellation)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();
            var markdown = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);

            var drift = PublishedRates.Compare(
                card, markdown, DateOnly.FromDateTime(_utcNow().UtcDateTime));

            _log?.Write(drift?.Describe() ?? "rate check failed — published table did not parse");
            return drift;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _log?.Write($"rate check failed {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("O-view", "1.0"));
        return client;
    }
}
