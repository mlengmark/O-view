using System.Diagnostics;
using System.Globalization;
using System.Text;
using OView.Core.Providers;

namespace OView.Core.Providers.Jsonl;

/// <summary>A directory under a surveyed root, and when anything in it last changed.</summary>
public sealed record SurveyedChild(string Name, DateTimeOffset WrittenUtc);

/// <summary>One of Claude's directories, and what is actually being written inside it.</summary>
/// <param name="Label">What this root is — <c>config</c> or <c>data</c>.</param>
/// <param name="Transcripts">Every <c>*.jsonl</c> beneath it, newest first.</param>
/// <param name="Registries">Every <c>local_*.json</c> beneath it — Cowork's session register.</param>
/// <param name="Children">Immediate subdirectories by recency, which say what is live at all.</param>
/// <param name="Capped">The walk hit its directory ceiling, so the counts are a floor.</param>
public sealed record SurveyedRoot(
    string Label,
    string Path,
    bool Exists,
    IReadOnlyList<string> Transcripts,
    IReadOnlyList<string> Registries,
    IReadOnlyList<SurveyedChild> Children,
    bool Capped)
{
    /// <summary>
    /// This root shows the same files as an earlier one, so its detail is not repeated.
    ///
    /// <para>MSIX presents a package's store through the canonical path as well as its own, and
    /// neither is a link, so nothing about either path reveals that they are one tree. Printing
    /// both in full doubles the longest section of the bundle and invites the reader to add two
    /// numbers that are the same number.</para>
    /// </summary>
    public string? Mirrors { get; init; }

    /// <summary>The relative paths found here, which is what makes two roots comparable.</summary>
    internal IReadOnlyList<string> Signature(string root) =>
        [.. Transcripts.Concat(Registries).Select(f => System.IO.Path.GetRelativePath(root, f))];
}

/// <summary>
/// Where Claude is <b>actually</b> writing, as opposed to where O-view expects it to
/// (GitHub issue #218).
///
/// <para><b>Why the existing reports cannot answer this.</b> Every other scan in this app looks
/// in two known places — <c>~/.claude/projects</c> and each data root's
/// <c>local-agent-mode-sessions</c> — and reports what it found there. That is the right shape
/// for ingestion and the wrong shape for a fault where the files have moved: a machine actively
/// running Cowork whose transcripts appear nowhere produces the same report as a machine sitting
/// idle, and neither the transcript scan nor
/// <see cref="CoworkSessionReport"/> can distinguish them, because both only ever
/// look where they already believe the answer is. This sweeps the whole of Claude's own
/// directories and prints what it finds, so a relocated path shows up as a fresh file somewhere
/// unexpected rather than as an absence with no explanation.</para>
///
/// <para><b>It is the map, not the verdict.</b> It asserts nothing about why a file is where it
/// is; it reports paths, counts and ages. That is deliberate — the thing being diagnosed is by
/// definition a layout this build does not know about, so any interpretation it offered would be
/// a guess about a shape nobody has seen (rule 6).</para>
///
/// <para><b>Bounded, because it runs in Copy diagnostics.</b> Claude's data directory holds
/// browser-engine caches with a great many subdirectories, and this builds on the UI thread. The
/// walk is filtered by file pattern rather than enumerating everything, and stops after
/// <see cref="MaxDirectories"/> directories per root — saying so when it does, because a
/// truncated count that cannot admit it is worse than no count. Measured at ~150 ms across three
/// roots on the development machine.</para>
///
/// <para>Paths, sizes and timestamps only. No file is opened.</para>
/// </summary>
public sealed record ClaudeWriteSurvey(IReadOnlyList<SurveyedRoot> Roots, TimeSpan Elapsed)
{
    /// <summary>Directory ceiling per root. Generous against real layouts, fatal to a pathological one.</summary>
    public const int MaxDirectories = 4_000;

    /// <summary>Subdirectories listed per root, newest first.</summary>
    private const int ChildrenShown = 4;

    public static ClaudeWriteSurvey Empty { get; } = new([], TimeSpan.Zero);

    /// <summary>Surveys the real machine: Claude Code's config directory and every Claude data root.</summary>
    public static ClaudeWriteSurvey Inspect() =>
        Inspect([("config", ClaudeConfigDir.Path), .. ClaudeDataRoots.All().Select(r => ("data", r))]);

    /// <summary>
    /// Overload taking the roots explicitly, so a layout can be surveyed without depending on
    /// the running machine.
    ///
    /// <para>Roots are de-duplicated after link resolution: MSIX redirection can expose one
    /// tree through two paths, and surveying it twice would double every count in a report
    /// whose whole job is to be counted against the sections above it.</para>
    /// </summary>
    public static ClaudeWriteSurvey Inspect(IReadOnlyList<(string Label, string Path)> roots)
    {
        var clock = Stopwatch.StartNew();
        var seen = new HashSet<string>(PathIdentity.Comparer);

        var surveyed = new List<SurveyedRoot>();
        var signatures = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (label, path) in roots)
        {
            if (!seen.Add(Resolve(path)))
            {
                continue;
            }

            var root = Survey(label, path);

            // Two roots that turn up the same relative paths are one tree seen twice. Keyed on
            // the paths found rather than on the directory names, because MSIX gives no hint
            // from either name that they are the same store.
            if (root.Exists && root.Signature(path) is { Count: > 0 } signature)
            {
                var key = string.Join("\n", signature);
                if (signatures.TryGetValue(key, out var original))
                {
                    root = root with { Mirrors = original };
                }
                else
                {
                    signatures[key] = path;
                }
            }

            surveyed.Add(root);
        }

        return new ClaudeWriteSurvey(surveyed, clock.Elapsed);
    }

    private static SurveyedRoot Survey(string label, string path)
    {
        if (!Directory.Exists(path))
        {
            return new SurveyedRoot(label, path, false, [], [], [], false);
        }

        var transcripts = TranscriptFileScan.Find(path, "*.jsonl", MaxDirectories, out var cappedA);

        // Cowork's session register (CoworkSessionReport). Swept for here as well as read
        // there, because this is the report that answers "and if it is not in the place we
        // look, where is it?".
        var registries = TranscriptFileScan.Find(path, "local_*.json", MaxDirectories, out var cappedB);

        return new SurveyedRoot(
            label, path, true,
            ByRecency(transcripts), ByRecency(registries), RecentChildren(path), cappedA || cappedB);
    }

    /// <summary>
    /// Immediate subdirectories, newest first.
    ///
    /// <para>This is what separates "Claude is not running" from "Claude is running and writing
    /// no transcripts" — the second has a stale <c>projects</c> beside siblings touched
    /// minutes ago, and nothing else in the bundle shows that. One level only: a directory's
    /// own timestamp moves when its entries change, which is enough to rank them and cheap
    /// enough to take on the UI thread.</para>
    /// </summary>
    private static IReadOnlyList<SurveyedChild> RecentChildren(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path)
                .Select(d => new SurveyedChild(
                    System.IO.Path.GetFileName(d), new DateTimeOffset(Directory.GetLastWriteTimeUtc(d), TimeSpan.Zero)))
                .OrderByDescending(c => c.WrittenUtc)
                .Take(ChildrenShown)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> ByRecency(IReadOnlyList<string> files) =>
        files.OrderByDescending(WrittenUtc).ToList();

    private static DateTimeOffset WrittenUtc(string file)
    {
        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static string Resolve(string path)
    {
        try
        {
            return Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    public string ToClipboardText(DateTimeOffset utcNow)
    {
        var text = new StringBuilder();

        if (Roots.Count == 0)
        {
            text.AppendLine("  claude writes : not surveyed");
            return text.ToString();
        }

        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"  claude writes : {Roots.Count} root(s) swept in {Elapsed.TotalMilliseconds:0} ms"));

        foreach (var root in Roots)
        {
            text.AppendLine($"    {root.Label,-12}: {root.Path}{(root.Exists ? "  <-- exists" : "  <-- missing")}");

            if (!root.Exists)
            {
                continue;
            }

            if (root.Mirrors is { Length: > 0 } original)
            {
                text.AppendLine($"      (same files as {original})");
                continue;
            }

            text.AppendLine($"      *.jsonl     : {Describe(root.Transcripts, root.Path, utcNow)}");
            text.AppendLine($"      local_*.json: {Describe(root.Registries, root.Path, utcNow)}");

            if (root.Children.Count > 0)
            {
                text.AppendLine("      recent      : " + string.Join(" · ", root.Children.Select(
                    c => string.Create(CultureInfo.InvariantCulture,
                        $"{c.Name} {(utcNow - c.WrittenUtc).TotalHours:0.0}h"))));
            }

            if (root.Capped)
            {
                text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"      capped      : stopped after {MaxDirectories:N0} directories — counts are a floor"));
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// A pattern's tally and its freshest example. The newest file's <b>path</b> is the point:
    /// a count says whether anything is there, and only the path says whether it is somewhere
    /// this build knows to look.
    /// </summary>
    private static string Describe(IReadOnlyList<string> files, string root, DateTimeOffset utcNow)
    {
        if (files.Count == 0)
        {
            return "none";
        }

        var newest = files[0];
        var relative = Elide(System.IO.Path.GetRelativePath(root, newest));

        // Clamped: a file written while the sweep was running, or by a machine whose clock has
        // just been corrected, is otherwise reported as "-0.0 h old" — an age that cannot exist
        // and that reads as a rendering fault rather than as freshness.
        var age = utcNow - WrittenUtc(newest);
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"{files.Count} file(s), newest {age.TotalHours:0.0} h old — {relative}");
    }

    /// <summary>
    /// Keeps a path readable without losing what it is read for.
    ///
    /// <para>Claude Code encodes a whole working directory into one folder name, so a session
    /// whose cwd was itself deep inside Claude's tree produces a segment several hundred
    /// characters long — measured at 236 on the development machine.</para>
    ///
    /// <para><b>Trimmed per segment, never across the whole string, and the head of a segment is
    /// what survives.</b> Eliding the middle of the joined path was shorter but cut wherever the
    /// character count happened to land — including through the middle of an account name, which
    /// <see cref="App.Diagnostics.Redact"/> then could not match against either half. An encoded
    /// working directory carries the account name near its start (<c>C--Users-ada-work</c>), so
    /// keeping each segment's head keeps the name intact and redactable, and keeps the leading
    /// segments that say which subtree this is. The file name is its own short segment and
    /// survives whole.</para>
    /// </summary>
    private static string Elide(string relative)
    {
        const int maxSegment = 60;

        var separators = new[] { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar };
        var segments = relative.Split(separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Length <= maxSegment ? s : $"{s[..maxSegment]}…");

        return string.Join(System.IO.Path.DirectorySeparatorChar, segments);
    }
}
