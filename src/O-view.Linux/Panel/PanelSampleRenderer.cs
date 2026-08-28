using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using OView.Core.Models;

namespace OView.Linux.Panel;

/// <summary>
/// Renders the panel offscreen to PNGs — the Linux counterpart of the WPF head's
/// <c>--popup-samples</c>.
///
/// <para>It exists because the panel is otherwise only inspectable by running it on a
/// desktop none of its authors has. Rendering to a file means legibility and layout can be
/// judged from images, by someone who is not the tester, before anyone installs anything —
/// and it reaches states real data on any one machine may never produce.</para>
/// </summary>
public static class PanelSampleRenderer
{
    /// <summary>Fixed clock, so a sample is reproducible rather than carrying the time it was taken.</summary>
    private static readonly DateTimeOffset At = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    public static void RenderAll(string directory)
    {
        Directory.CreateDirectory(directory);

        (string Name, UsageSnapshot Snapshot, PanelStatistics Stats)[] cases =
        [
            ("typical", Live(25, 3), Stats(recordedDays: 31)),
            ("partial-history", Live(47, 61), Stats(recordedDays: 10)),
            ("unpriced-model", Live(58, 44), Stats(recordedDays: 31, unpriced: ["claude-brandnew-9"])),
            ("high-usage", Live(91, 80), Stats(recordedDays: 31)),
            ("estimate-only", new UsageSnapshot(DataSource.Estimate, null, null, null, At), Stats(recordedDays: 12)),
            ("no-data", UsageSnapshot.None, Stats(recordedDays: 0)),
        ];

        var written = 0;
        foreach (var light in new[] { false, true })
        {
            foreach (var (name, snapshot, stats) in cases)
            {
                // A FRESH control per sample. Reusing one silently produced eleven copies
                // of the first image: layout is cached against the tree, so re-populating
                // and re-rendering returns the size and content already arranged.
                var content = new PanelContent(new LinuxPanelTheme(light));
                content.Populate(snapshot, stats, Account, null, null, At);

                Save(content, Path.Combine(directory, $"panel-{name}-{(light ? "light" : "dark")}.png"));
                written++;
            }
        }

        Console.WriteLine($"wrote {written} panel PNGs to {Path.GetFullPath(directory)}");
    }

    private static void Save(Control content, string path)
    {
        // Two passes: the tree must be measured before its desired size is known, and
        // arranged at that size before it will render anything but blank.
        content.Measure(Size.Infinity);
        content.Arrange(new Rect(content.DesiredSize));

        var pixel = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(content.DesiredSize.Width)),
            Math.Max(1, (int)Math.Ceiling(content.DesiredSize.Height)));

        using var bitmap = new RenderTargetBitmap(pixel, new Vector(96, 96));
        bitmap.Render(content);
        bitmap.Save(path, new PngBitmapEncoderOptions());
    }

    private static UsageSnapshot Live(int session, int weekly) => new(
        DataSource.Live, session, weekly,
        SessionResetAtUtc: At.AddHours(2).AddMinutes(14),
        CapturedAtUtc: At,
        WeeklyResetAtUtc: At.AddDays(3).AddHours(4),
        // Wide enough to be approximate, so the ~ marker appears in a sample rather than
        // only in a state nobody looks at.
        WeeklyResetUncertainty: TimeSpan.FromHours(9));

    private static readonly ClaudeAccount Account = new("Sample User", "sample@example.com", "claude_pro", "org-uuid");

    private static PanelStatistics Stats(int recordedDays, string[]? unpriced = null)
    {
        var series = Enumerable.Range(0, 31)
            .Select(i => new DayUsage(
                DateOnly.FromDateTime(At.UtcDateTime).AddDays(i - 30),
                // Days before the store existed have NO data, not zero data.
                i < 31 - recordedDays ? 0 : 8_000_000 + (i * 900_000 % 17_000_000),
                PreInstall: i < 31 - recordedDays))
            .ToList();

        return new PanelStatistics(
            TokensToday: 12_700_000,
            EstTodayUsd: 9.36m,
            Tokens31Days: 684_600_000,
            Est31DaysUsd: 492.52m,
            RecordedDays: recordedDays,
            WindowDays: 31,
            DailySeries: series,
            CreditTokens31Days: 0,
            EstCredit31DaysUsd: null)
        {
            UnpricedModels = unpriced ?? [],
        };
    }
}
