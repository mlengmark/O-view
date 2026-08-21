using System.Globalization;

namespace OView.Core.Storage;

/// <summary>
/// One quarantined generation: the <c>.db</c> and its <c>-wal</c>/<c>-shm</c> sidecars, all
/// carrying the same timestamp suffix.
/// </summary>
/// <param name="Stamp">The <c>yyyyMMdd-HHmmss</c> suffix shared by the generation's files.</param>
/// <param name="Files">Every file bearing that stamp. Kept or dropped together.</param>
/// <param name="Bytes">Total size of those files.</param>
public sealed record CorruptGeneration(string Stamp, IReadOnlyList<string> Files, long Bytes);

/// <summary>
/// What the quarantine currently holds. One line's worth of facts, for the diagnostics
/// bundle.
/// </summary>
/// <param name="Generations">How many corruption events have left files behind.</param>
/// <param name="NewestStamp">Timestamp of the most recent, or null when there are none.</param>
/// <param name="Bytes">Total size of everything retained.</param>
public sealed record CorruptBackupReport(int Generations, string? NewestStamp, long Bytes)
{
    public static CorruptBackupReport Empty { get; } = new(0, null, 0);
}

/// <summary>
/// Retention for the databases <see cref="RollupStore"/> moves aside when it finds one
/// malformed (issue #160).
///
/// <para><b>The problem was not the quarantine, it was that nothing bounded or read it.</b>
/// Each corruption renames the DB and its sidecars with a timestamp so two events a week
/// apart cannot overwrite each other — which is correct — but the directory then only ever
/// grows. Measured on one machine over about a month: seven generations, ~6 MB. Small, and far
/// smaller than the temp-installer leak filed alongside it (#159).</para>
///
/// <para><b>The reason to bound it anyway is that the files were not serving their stated
/// purpose.</b> They exist "so the corruption can still be examined", but nothing surfaced
/// them: <c>--diagnose</c> did not list them, the bundle did not mention them, and the panel
/// said nothing. A backup nobody is told about is not evidence, it is residue — and the oldest
/// generations are the least likely ever to be looked at. So the two halves here belong
/// together, and the second is what makes the first defensible: what is kept is now named in
/// the diagnostics bundle, which means the pruning trades away generations nobody could have
/// found rather than evidence someone might have used.</para>
///
/// <para>Worth recording: on the machine measured, the self-heal path behaved correctly every
/// time — quarantine, rebuild empty, carry on — and the corruption timestamps line up with
/// hard power-cycles rather than with anything the app did, which is the expected way to end
/// up with a half-written WAL. <c>weekly-resets.json</c> survived intact each time, which is
/// exactly what ADR-0011 moved it out of the store for.</para>
/// </summary>
public static class CorruptBackups
{
    /// <summary>The suffix <see cref="RollupStore"/> renames with, and the only place it appears besides there.</summary>
    public const string Marker = ".corrupt-";

    /// <summary>
    /// How many generations survive a prune.
    ///
    /// <para>Two, not one: the newest is the event that just happened, and the one before it is
    /// what a "has this been happening repeatedly?" question is answered from. Past that,
    /// another copy of a database nobody has opened adds nothing a bug report could use — the
    /// <i>count</i> of events is the part that carries information beyond two, and that is now
    /// reported rather than inferred from the file listing.</para>
    /// </summary>
    public const int KeepGenerations = 2;

    /// <summary>
    /// Every retained generation for <paramref name="dbPath"/>, newest first. Empty — never
    /// throwing — when the directory is unreadable or holds none.
    ///
    /// <para>Grouped by stamp because a generation is only examinable whole: a database
    /// separated from its WAL is not the state that was quarantined, so the three files are
    /// kept or dropped together.</para>
    /// </summary>
    public static IReadOnlyList<CorruptGeneration> Find(string? dbPath = null)
    {
        var path = dbPath ?? RollupStore.DefaultPath;
        var directory = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);

        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(name))
        {
            return [];
        }

        string[] files;
        try
        {
            // usage.db.corrupt-<stamp>, usage.db-wal.corrupt-<stamp>, usage.db-shm.corrupt-<stamp>
            files = Directory.GetFiles(directory, $"{name}*{Marker}*");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return files
            .Select(f => (File: f, Stamp: StampOf(f)))
            .Where(x => x.Stamp is { Length: > 0 })
            .GroupBy(x => x.Stamp!, StringComparer.Ordinal)
            // Ordinal descending is newest-first for yyyyMMdd-HHmmss, and stays deterministic
            // for a stamp that is not one — sorting by parsed date would drop those silently.
            .OrderByDescending(g => g.Key, StringComparer.Ordinal)
            .Select(g => new CorruptGeneration(
                g.Key,
                [.. g.Select(x => x.File).Order(StringComparer.Ordinal)],
                g.Sum(x => SizeOf(x.File))))
            .ToList();
    }

    /// <summary>
    /// The one-line summary the diagnostics bundle prints. Never throws — an unreadable
    /// directory reports nothing rather than failing the bundle, which is the one thing a
    /// user with a broken machine still has to be able to produce.
    /// </summary>
    public static CorruptBackupReport Inspect(string? dbPath = null)
    {
        var generations = Find(dbPath);
        return generations.Count == 0
            ? CorruptBackupReport.Empty
            : new CorruptBackupReport(generations.Count, generations[0].Stamp, generations.Sum(g => g.Bytes));
    }

    /// <summary>
    /// Deletes all but the newest <paramref name="keep"/> generations, returning how many were
    /// removed. Never throws.
    ///
    /// <para><b>Best-effort, exactly as the move it follows already is.</b> A file that cannot
    /// be deleted is skipped and the next corruption sweeps again. This runs on the rebuild
    /// path, and rule 7 / issue #16 requires that path to leave a usable empty database behind
    /// whatever else fails — housekeeping must not be the thing that makes a self-heal
    /// fatal.</para>
    /// </summary>
    /// <param name="dbPath">The live database path the backups are named after.</param>
    /// <param name="keep">Generations to retain, newest first. Clamped at zero.</param>
    /// <param name="delete">
    /// How to remove one file. The seam exists for the test that a failure here does not
    /// propagate — the real causes are OS- and state-specific and cannot be provoked the same
    /// way on Windows and Linux, whereas the guarantee under test is the same on both.
    /// </param>
    public static int Prune(string? dbPath = null, int keep = KeepGenerations, Action<string>? delete = null)
    {
        delete ??= File.Delete;
        var removed = 0;

        foreach (var generation in Find(dbPath).Skip(Math.Max(keep, 0)))
        {
            var whole = true;
            foreach (var file in generation.Files)
            {
                try
                {
                    delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    whole = false;
                }
            }

            if (whole)
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// The stamp a quarantined file carries, or null when the name does not end in one.
    /// Last occurrence, so a database path that itself contains the marker cannot confuse it.
    /// </summary>
    private static string? StampOf(string file)
    {
        var name = Path.GetFileName(file);
        var at = name.LastIndexOf(Marker, StringComparison.Ordinal);
        return at < 0 ? null : name[(at + Marker.Length)..];
    }

    private static long SizeOf(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Human-readable size for the diagnostics line — MB below a gigabyte, GB above.</summary>
    public static string DescribeBytes(long bytes) => bytes >= 1024L * 1024 * 1024
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.0} GB")
        : string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.0} MB");
}
