using System.Globalization;
using System.IO;

namespace OView.App.Diagnostics;

/// <summary>
/// The diagnostic log for a windowed app with no console. Never records tokens,
/// credentials, or conversation content — refresh telemetry, cadence changes, resource
/// counts and failures only (CLAUDE.md rule 3).
///
/// <para><b>On by default, which is the whole point of it.</b> It used to be created only
/// when <c>--log</c> was passed, so in the field <c>_log</c> was always null and every
/// <c>_log?.Write</c> call site in the engine produced nothing. That instrumentation was
/// therefore written, maintained, and unavailable at exactly the moment it was needed: a
/// machine whose poll loop had stopped for five days, with a support bundle that said
/// <c>status : Ok</c> and no way to tell which call had failed. A log nobody can turn on
/// after the fact is not a log.</para>
///
/// <para><b>Bounded, because it now runs forever on every install.</b> The live file is
/// capped at <see cref="DefaultMaxBytes"/> and rolls to <c>.1</c> and <c>.2</c>, so the whole
/// thing is bounded at three files regardless of how long the app runs. Rolling rather than
/// truncating matters: a stall is diagnosed from what happened <i>before</i> it, and
/// truncation throws away precisely that.</para>
///
/// <para><b>It never throws.</b> A diagnostic that can take down the thing it is diagnosing
/// is worse than no diagnostic — and this is called from the poll's failure paths, where an
/// exception would replace a recorded failure with an unrecorded one.</para>
/// </summary>
public sealed class FileLog : IAppLog
{
    /// <summary>
    /// Size at which the live file rolls. Two megabytes is a few days of ordinary polling at
    /// the 60 s cadence, and roughly an hour of a poll failing on every tick — the case this
    /// exists for, and the one that produces the most lines per minute.
    /// </summary>
    public const long DefaultMaxBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Rolled generations kept beside the live file, so the ceiling is
    /// <c>(KeepGenerations + 1) * DefaultMaxBytes</c> — 6 MB.
    /// </summary>
    public const int KeepGenerations = 2;

    /// <summary>
    /// Default location: <c>%LOCALAPPDATA%\O-view\logs\oview.log</c>, and the XDG equivalent
    /// on Linux. Beside the rollup store and the weekly-reset log rather than in the install
    /// directory, which on Windows is per-user and replaced wholesale by every update.
    /// </summary>
    public static string DefaultPath => Path.Combine(DefaultDirectory, "oview.log");

    /// <summary>The folder holding the live file and its rolled generations.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "O-view",
        "logs");

    private readonly string _path;
    private readonly long _maxBytes;
    private readonly Lock _gate = new();

    /// <param name="path">Where to write; null uses <see cref="DefaultPath"/>.</param>
    /// <param name="maxBytes">Size at which the live file rolls.</param>
    public FileLog(string? path = null, long maxBytes = DefaultMaxBytes)
    {
        _path = path is { Length: > 0 } p ? p : DefaultPath;
        _maxBytes = maxBytes > 0 ? maxBytes : DefaultMaxBytes;
    }

    /// <summary>
    /// The file this instance is writing to, so diagnostics can name it. Deliberately not
    /// called <c>Path</c>: an instance member of that name shadows <see cref="System.IO.Path"/>
    /// inside every static member here, which turns each <c>Path.Combine</c> into a
    /// compile error at a distance.
    /// </summary>
    public string FilePath => _path;

    /// <summary>
    /// Marks the start of a run. In a rolling log that outlives the process, "which lines
    /// belong to this launch" is otherwise guesswork — and a stall that survives a restart
    /// looks identical to one that does not until the restarts are visible.
    /// </summary>
    public void WriteSessionHeader(string version, string installKind) =>
        Write($"──── session start · v{version} · {installKind} · pid {Environment.ProcessId} ────");

    public void Write(string message)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                RollIfOversized();

                var stamp = DateTimeOffset.UtcNow.ToString(
                    "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                File.AppendAllText(_path, $"{stamp}Z {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or ArgumentException or NotSupportedException)
            {
                // See the class remarks: a failure to record must never become a failure.
            }
        }
    }

    /// <summary>
    /// The most recent <paramref name="lines"/> lines of the live file, oldest first, for the
    /// support bundle. Empty when there is no log yet or it cannot be read — both of which are
    /// ordinary, and neither of which may fail the bundle.
    ///
    /// <para>Reads with <see cref="FileShare.ReadWrite"/> because the running instance holds
    /// the file open for appending; a bundle produced by <c>--diagnose</c> against a live
    /// instance is the case this is for.</para>
    /// </summary>
    public static IReadOnlyList<string> Tail(string? path = null, int lines = 30)
    {
        var target = path is { Length: > 0 } p ? p : DefaultPath;

        try
        {
            if (!File.Exists(target) || lines <= 0)
            {
                return [];
            }

            using var stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            // Ring buffer rather than reading the file into a list: the file is capped at
            // megabytes and the bundle wants tens of lines.
            var ring = new string[lines];
            var count = 0;
            while (reader.ReadLine() is { } line)
            {
                ring[count++ % lines] = line;
            }

            if (count == 0)
            {
                return [];
            }

            var take = Math.Min(count, lines);
            var start = count - take;
            var result = new List<string>(take);
            for (var i = 0; i < take; i++)
            {
                result.Add(ring[(start + i) % lines]);
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>
    /// Shifts <c>oview.log</c> to <c>oview.1.log</c>, <c>.1</c> to <c>.2</c>, and drops what
    /// falls off the end. Called under the write lock, before appending.
    /// </summary>
    private void RollIfOversized()
    {
        if (!File.Exists(_path) || new FileInfo(_path).Length < _maxBytes)
        {
            return;
        }

        // Oldest first, so nothing is overwritten before it has been moved.
        for (var generation = KeepGenerations; generation >= 1; generation--)
        {
            var source = generation == 1 ? _path : Generation(generation - 1);
            if (File.Exists(source))
            {
                File.Move(source, Generation(generation), overwrite: true);
            }
        }
    }

    /// <summary><c>oview.log</c> → <c>oview.1.log</c> for generation 1.</summary>
    private string Generation(int generation)
    {
        var directory = Path.GetDirectoryName(_path)!;
        var name = Path.GetFileNameWithoutExtension(_path);
        var extension = Path.GetExtension(_path);
        return Path.Combine(directory, $"{name}.{generation}{extension}");
    }
}
