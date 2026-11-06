using System.Globalization;
using OView.App;
using OView.Core.Providers;
using OView.Linux.Tray;
using OView.TestSupport;
using Xunit.Abstractions;

namespace OView.Linux.Tests.Runtime;

/// <summary>
/// What a notification area is offered when it reads the icon as soon as it hears of it, asked
/// of the real app by the stand-in host.
///
/// <para><b>Why this exists.</b> On Ubuntu's GNOME the icon was reported staying on the grey
/// no-data ring for up to a minute after launch, although the log said a reading had been drawn
/// 0.3 s in. The head used to publish the ring and repaint it moments later, so a host reading
/// in between was offered grey, and all that could correct it was one <c>NewIcon</c> sent while
/// the host was still setting the item up, which GLib's proxy drops
/// (<see cref="FirstReadingHold"/> has the detail). The poll after that is a minute away.</para>
///
/// <para><b>Why the read is the host's first and not a later one.</b> A later read would find
/// the reading whether or not a host that had already read grey ever heard of it, which is the
/// defect passing unseen. So <see cref="StandInTrayHost.WaitForFirstOfferAsync"/> asks while
/// the item's registration is still unanswered, and this test never subscribes to a signal:
/// it plays a host that gets the one read.</para>
///
/// <para><b>Why the reading comes with a long history.</b> On a CI runner the first poll lands
/// about 40 ms after registration, little enough that an unlucky run could let the read fall
/// behind it and pass with the defect back. A few thousand transcript records put the first
/// reading well after registration on every run, which is the state this is about, and is what
/// the same launch looks like on a machine with real history.</para>
///
/// <para>Runs only in the <c>linux-runtime</c> job, which provides the display, the bus and
/// <c>OVIEW_LINUX_RUNTIME=1</c>. Elsewhere there is no bus to ask, so it returns early the way
/// the other runtime tests do. What it read is saved beside the app's log, because a passing
/// test's output goes nowhere.</para>
/// </summary>
[Trait("Category", "LinuxRuntime")]
public class TrayIconFirstReadingRuntimeTests(ITestOutputHelper output)
{
    private static bool InRuntimeJob => Environment.GetEnvironmentVariable("OVIEW_LINUX_RUNTIME") == "1";

    /// <summary>A bound on each wait: nothing here measures a duration, it only refuses to hang.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>In the green band, so the reading's colour cannot be mistaken for the no-data neutral.</summary>
    private const int SessionPercent = 37;

    private const int HistoryRecords = 4000;

    /// <summary>Further apart than antialiasing moves a neutral grey: this far means a colour.</summary>
    private const int ColourSpread = 40;

    /// <summary>Faint antialiased edges say nothing about the colour the icon was drawn in.</summary>
    private const byte SolidAlpha = 128;

    [Fact]
    public async Task AHostThatReadsTheIconOnArrivalIsOfferedTheFirstReadingNotTheGreyRing()
    {
        if (!InRuntimeJob) return;

        var label = nameof(AHostThatReadsTheIconOnArrivalIsOfferedTheFirstReadingNotTheGreyRing);
        var configDirectory = Path.Combine(Path.GetTempPath(), $"oview-reading-{Guid.NewGuid():N}");
        var inherited = Environment.GetEnvironmentVariable(ClaudeConfigDir.OverrideVariable);

        WriteReading(configDirectory);
        WriteLongHistory(configDirectory);

        // The app inherits this; the runtime tests run one at a time, so nothing else sees it.
        Environment.SetEnvironmentVariable(ClaudeConfigDir.OverrideVariable, configDirectory);

        try
        {
            await using var traffic = await BusTraffic.StartAsync(label, output);
            await using var host = await StandInTrayHost.StartAsync();
            await using var app = await LinuxAppProcess.StartAsync(output, label);

            var offered = await app.WaitForAsync(
                host.WaitForFirstOfferAsync(Patience), Patience, "the icon to reach the stand-in host");
            var rendered = await app.WaitForLogAsync(LinuxAppProcess.IconRenderedLogLine, Patience);

            Save(label, offered, rendered);

            // That the app read this test's figure at all, or the colour below proves nothing.
            Assert.Contains($"session={SessionPercent}", rendered, StringComparison.Ordinal);

            Assert.NotNull(offered);
            var pixmap = Assert.Single(offered);
            Assert.True(
                ColouredPixels(pixmap) > 0,
                $"a host reading the icon on arrival was offered the grey ring ({Describe(pixmap)}), " +
                "and a host that then misses the next NewIcon shows it until the next poll");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ClaudeConfigDir.OverrideVariable, inherited);
            Directory.Delete(configDirectory, recursive: true);
        }
    }

    /// <summary>
    /// The fixture has transcripts and no reading, so the first poll has nothing authoritative
    /// to draw. Holding the icon back for a reading must not turn that into a fill, or into
    /// no icon: "unknown shows as unknown" is the rule the grey ring exists for.
    /// </summary>
    [Fact]
    public async Task WithNoDataTheIconAHostIsOfferedIsStillTheGreyRing()
    {
        if (!InRuntimeJob) return;

        var label = nameof(WithNoDataTheIconAHostIsOfferedIsStillTheGreyRing);

        await using var host = await StandInTrayHost.StartAsync();
        await using var app = await LinuxAppProcess.StartAsync(output, label);

        var offered = await app.WaitForAsync(
            host.WaitForFirstOfferAsync(Patience), Patience, "the icon to reach the stand-in host");
        var rendered = await app.WaitForLogAsync(LinuxAppProcess.IconRenderedLogLine, Patience);

        Save(label, offered, rendered);

        Assert.Contains("session=unavailable", rendered, StringComparison.Ordinal);

        Assert.NotNull(offered);
        var pixmap = Assert.Single(offered);

        Assert.True(PaintedPixels(pixmap) > 0, "the item was published with nothing drawn in its icon");
        Assert.Equal(0, ColouredPixels(pixmap));
    }

    /// <summary>
    /// The block <c>CachedUtilizationProvider</c> reads, fresh, so the first poll has an
    /// authoritative session figure to draw. Written where <c>CLAUDE_CONFIG_DIR</c> will send
    /// the app, this test's own copy, never a real <c>.claude.json</c>.
    /// </summary>
    private static void WriteReading(string configDirectory)
    {
        Directory.CreateDirectory(configDirectory);

        var now = DateTimeOffset.UtcNow;
        var resets = now.AddHours(3).ToString("O", CultureInfo.InvariantCulture);

        File.WriteAllText(
            Path.Combine(configDirectory, ".claude.json"),
            $$"""
            {
              "cachedUsageUtilization": {
                "fetchedAtMs": {{now.ToUnixTimeMilliseconds()}},
                "utilization": {
                  "five_hour": { "utilization": {{SessionPercent}}, "resets_at": "{{resets}}" },
                  "seven_day": { "utilization": 12, "resets_at": "{{resets}}" }
                }
              }
            }
            """);
    }

    private static void WriteLongHistory(string configDirectory)
    {
        var project = Path.Combine(configDirectory, "projects", "-tmp-long-history");
        Directory.CreateDirectory(project);

        var at = DateTimeOffset.UtcNow.AddHours(-1).ToString("O", CultureInfo.InvariantCulture);

        File.WriteAllLines(
            Path.Combine(project, "history.jsonl"),
            Enumerable.Range(0, HistoryRecords).Select(i =>
                $$$$"""{"type":"assistant","requestId":"req_h{{{{i}}}}","timestamp":"{{{{at}}}}","message":{"model":"claude-opus-5","usage":{"input_tokens":2,"cache_creation_input_tokens":10,"cache_read_input_tokens":20,"output_tokens":15}}}"""));
    }

    private static int PaintedPixels(TrayIconPixmap pixmap) =>
        Pixels(pixmap).Count(pixel => pixel.A > 0);

    /// <summary>Pixels drawn in a colour rather than a shade of grey. The no-data ring has none.</summary>
    private static int ColouredPixels(TrayIconPixmap pixmap) =>
        Pixels(pixmap).Count(pixel =>
            pixel.A >= SolidAlpha
            && Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) - Math.Min(pixel.R, Math.Min(pixel.G, pixel.B)) > ColourSpread);

    private static IEnumerable<(byte A, byte R, byte G, byte B)> Pixels(TrayIconPixmap pixmap)
    {
        for (var i = 0; i + 3 < pixmap.Argb32.Length; i += 4)
        {
            yield return (pixmap.Argb32[i], pixmap.Argb32[i + 1], pixmap.Argb32[i + 2], pixmap.Argb32[i + 3]);
        }
    }

    private static string Describe(TrayIconPixmap pixmap) =>
        $"{pixmap.Width}x{pixmap.Height}, {PaintedPixels(pixmap)} painted, {ColouredPixels(pixmap)} coloured";

    /// <summary>
    /// Writes what was read to where the job collects artifacts. A run that went red and a run
    /// that went green differ only in this, and the job's log carries neither.
    /// </summary>
    private void Save(string label, IReadOnlyList<TrayIconPixmap>? offered, string rendered)
    {
        var report =
            $"offered on arrival : {(offered is null ? "(could not be read)" : string.Join("; ", offered.Select(Describe)))}"
            + $"{Environment.NewLine}first render       : {rendered}";

        output.WriteLine(report);

        if (Environment.GetEnvironmentVariable("OVIEW_RUNTIME_ARTIFACTS") is not { Length: > 0 } directory)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"{label}.first-offer.txt"), report);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Saving evidence must not become the failure; it is already in the test's output.
        }
    }
}

/// <summary>
/// When the icon goes onto the bus, without one: the two ways the wait can end, and that
/// however it ends the icon is published exactly once. A second publish would be a second
/// registration, and a notification area draws those as two icons.
/// </summary>
public class FirstReadingHoldTests
{
    private readonly ManualTimers _timers = new();
    private readonly ListLog _log = new();
    private int _published;

    private FirstReadingHold StartedHold()
    {
        var hold = new FirstReadingHold(() => _published++, _timers, _log);
        hold.Start();
        return hold;
    }

    [Fact]
    public void TheIconWaitsForTheFirstReading()
    {
        StartedHold();

        Assert.Equal(0, _published);
    }

    [Fact]
    public void TheFirstReadingPublishesTheIconAndEndsTheWait()
    {
        var hold = StartedHold();

        hold.ReadingArrived();

        Assert.Equal(1, _published);
        Assert.False(_timers.Timer.Running);
        Assert.Contains(_log.Lines, line => line.Contains("first reading", StringComparison.Ordinal));
    }

    [Fact]
    public void LaterReadingsPublishNothing()
    {
        var hold = StartedHold();

        hold.ReadingArrived();
        hold.ReadingArrived();

        Assert.Equal(1, _published);
    }

    [Fact]
    public void WithNoReadingTheIconIsPublishedOnceThePatienceRunsOut()
    {
        StartedHold();

        _timers.Timer.Tick();

        Assert.Equal(1, _published);
        Assert.False(_timers.Timer.Running);
        Assert.Contains(_log.Lines, line => line.Contains("no reading after 5s", StringComparison.Ordinal));
    }

    [Fact]
    public void AReadingThatLandsAfterThePatienceRanOutPublishesNothingMore()
    {
        var hold = StartedHold();

        _timers.Timer.Tick();
        hold.ReadingArrived();

        Assert.Equal(1, _published);
    }

    [Fact]
    public void ThePatienceIsTheHeadsRealWait()
    {
        var hold = StartedHold();

        Assert.Equal(FirstReadingHold.DefaultPatience, hold.Patience);
        Assert.Equal(FirstReadingHold.DefaultPatience, _timers.Timer.Interval);
        Assert.True(_timers.Timer.Running);
    }

    private sealed class ManualTimers : ITimerFactory
    {
        public ManualTimer Timer { get; private set; } = null!;

        public IAppTimer Create(TimeSpan interval, Action onTick) => Timer = new ManualTimer(interval, onTick);
    }

    private sealed class ManualTimer(TimeSpan interval, Action onTick) : IAppTimer
    {
        public TimeSpan Interval { get; set; } = interval;

        public bool Running { get; private set; }

        public void Start() => Running = true;

        public void Stop() => Running = false;

        public void Dispose() => Running = false;

        public void Tick() => onTick();
    }
}
