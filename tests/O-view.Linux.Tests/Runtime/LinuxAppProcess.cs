using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Tmds.DBus.Protocol;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace OView.Linux.Tests.Runtime;

/// <summary>
/// The shipped Linux head, started as its own process the way a desktop starts it, so a test
/// can watch what it does on the bus and on the display.
///
/// <para><b>Why the real executable rather than the classes behind it.</b> Everything that has
/// gone wrong with this head went wrong in the wiring between the toolkit, the dispatcher and
/// the bus — the first click deadlocking (#124), the menu taking tens of seconds (#125). A
/// test that constructs <c>LinuxApp</c> in-process has the test runner's dispatcher and the
/// test runner's bus connections, which is precisely the part not under test. This starts the
/// binary the .deb carries.</para>
///
/// <para><b>It never runs against a real home.</b> <c>HOME</c>, and the XDG variables .NET
/// derives from it, point at a writable copy of <c>tests/fixtures/claude-home</c>: the app
/// finds real-shaped data to ingest, and nothing it writes — the log, the rollup store, the
/// autostart entry — can land in the account running the test (CLAUDE.md rule 4). The
/// single-instance lock is redirected too, so one test's app cannot be mistaken for another's
/// and silently exit.</para>
///
/// <para><b>Every wait failure carries the app's own output.</b> "The icon never arrived" says
/// nothing; the same failure with the app's log underneath it usually says which bus call did
/// not come back. For the same reason <see cref="DisposeAsync"/> always reports the log,
/// passing or failing, and saves it where the <c>linux-runtime</c> job collects artifacts.</para>
///
/// <para><b>And what it held on the bus.</b> The toolkit's tray backend logs its own failures
/// through a logger the app installs no sink for, so a registration that never happens says
/// nothing anywhere. What the bus can still be asked is which names the app's own connections
/// owned, and that separates the two ways this goes wrong: a
/// <c>org.kde.StatusNotifierItem-…</c> name with no registration behind it means the item was
/// published and the call to the watcher is what failed, while no name at all means the
/// backend never got that far.</para>
/// </summary>
public sealed class LinuxAppProcess : IAsyncDisposable
{
    /// <summary>The app's file name on disk — <c>AssemblyName</c> in O-view.Linux.csproj.</summary>
    public const string ExecutableName = "o-view";

    /// <summary>
    /// The line <c>Program.Main</c> writes once it is past the two bus round trips it makes
    /// before the toolkit starts. Waiting for it is how a test says "started", as opposed to
    /// "launched": the process exists well before this.
    /// </summary>
    public const string StartupLogLine = "startup desktop=";

    /// <summary>How long the app is given to die once asked. It is killed, so this is only a bound on the wait.</summary>
    private static readonly TimeSpan ShutdownPatience = TimeSpan.FromSeconds(5);

    /// <summary>How often a log file is re-read while waiting for a line to appear in it.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Log lines carried in a failure message. Enough for a startup, not the whole run.</summary>
    private const int ReportedLogLines = 80;

    /// <summary>
    /// How long the bus is given to say what the app owns while a failure is being built.
    /// Short on purpose: this is evidence for a test that has already failed, and a diagnostic
    /// that hangs would replace the real failure with a timeout of its own.
    /// </summary>
    private static readonly TimeSpan BusReadTimeout = TimeSpan.FromSeconds(2);

    private readonly Process _process;
    private readonly ITestOutputHelper? _output;
    private readonly string _label;
    private readonly string _root;
    private readonly string _logPath;
    private readonly StringBuilder _console = new();
    private readonly Lock _consoleGate = new();

    private bool _started;
    private bool _disposed;
    private string _busNames = "(not read)";

    private LinuxAppProcess(Process process, ITestOutputHelper? output, string label, string root, string logPath)
    {
        _process = process;
        _output = output;
        _label = label;
        _root = root;
        _logPath = logPath;
    }

    /// <summary>Where the app is writing its log, which is inside this instance's temporary folder.</summary>
    public string LogPath => _logPath;

    /// <summary>The app's process id, for a test that needs to act on the process itself.</summary>
    public int ProcessId => _process.Id;

    /// <summary>The home the app was given: a copy of the fixture, and the only home it can see.</summary>
    public string Home => Path.Combine(_root, "home");

    /// <summary>Whether the app has gone. A test asserts this is false to show the app survived what it was asked to do.</summary>
    public bool HasExited => !_started || _process.HasExited;

    /// <summary>
    /// Starts the app with its log in a temporary folder and its home pointed at a copy of the
    /// fixture. The display and the session bus are inherited, because those are the ones the
    /// job set up and the ones the stand-in host is on.
    /// </summary>
    /// <param name="output">Where the log is reported on dispose. Null reports nowhere.</param>
    /// <param name="label">Names the temporary folder and the saved log, so two tests' apps are told apart.</param>
    public static async Task<LinuxAppProcess> StartAsync(ITestOutputHelper? output = null, string? label = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"oview-{label ?? "app"}-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var logPath = Path.Combine(root, "oview.log");

        CopyTree(FixtureHome(), home);
        Directory.CreateDirectory(Path.Combine(root, "run"));

        var start = new ProcessStartInfo(Executable())
        {
            // On by default in the app, so this only redirects it — into a folder this
            // instance owns, which is what makes reading it back unambiguous.
            ArgumentList = { "--log", logPath },
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // Set rather than cleared: .NET falls back from an unset XDG variable to a path under
        // HOME, but an inherited one would win, and on a desktop-shaped runner that means the
        // real account's folders. Naming each one leaves nothing to fall back to.
        start.Environment["HOME"] = home;
        start.Environment["XDG_DATA_HOME"] = Path.Combine(home, ".local", "share");
        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(home, ".config");
        start.Environment["XDG_CACHE_HOME"] = Path.Combine(home, ".cache");
        start.Environment["XDG_STATE_HOME"] = Path.Combine(home, ".local", "state");

        // The single-instance lock lives here (FileLockSingleInstanceGuard.DefaultPath). Per
        // instance on purpose: with a shared one, the second app to start would take the guard's
        // early exit and the test would be watching a process that had already decided to stop.
        start.Environment["XDG_RUNTIME_DIR"] = Path.Combine(root, "run");

        var app = new LinuxAppProcess(new Process { StartInfo = start }, output, label ?? "app", root, logPath);

        try
        {
            app._process.OutputDataReceived += app.CaptureConsole;
            app._process.ErrorDataReceived += app.CaptureConsole;

            app._process.Start();
            app._started = true;
            app._process.BeginOutputReadLine();
            app._process.BeginErrorReadLine();

            // Returned started, not merely launched: a caller that got a handle back has
            // something whose log it can read, and a process that fell over on its first bus
            // call has already failed here rather than three waits later.
            await app.WaitForLogAsync(StartupLogLine, TimeSpan.FromSeconds(20));
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Waits for a line containing <paramref name="containing"/> to appear in the app's log,
    /// and returns it. Fails with the app's own output on timeout, and at once if the app exits
    /// before the line arrives.
    /// </summary>
    public async Task<string> WaitForLogAsync(string containing, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            if (MatchingLogLine(containing) is { } line)
            {
                return line;
            }

            // Read once more after it has gone: the line may have been written between the
            // read above and the exit, and a log that answers the question is a pass, not a
            // race lost by a few milliseconds.
            if (HasExited)
            {
                return MatchingLogLine(containing)
                    ?? throw await FailureAsync($"waiting for a log line containing \"{containing}\"");
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw await FailureAsync(
                    $"waiting {timeout.TotalSeconds:0.#} s for a log line containing \"{containing}\"");
            }

            await Task.Delay(PollInterval);
        }
    }

    /// <summary>
    /// Waits for something happening elsewhere — a registration reaching the stand-in host, a
    /// panel appearing — with the same two guarantees as <see cref="WaitForLogAsync"/>: the
    /// app's output on any failure, and no waiting out the timeout once the app is gone.
    /// </summary>
    /// <param name="work">The wait to run. Its own result is returned untouched.</param>
    /// <param name="what">What is being waited for, in plain English, for the failure message.</param>
    public async Task<T> WaitForAsync<T>(Task<T> work, TimeSpan timeout, string what)
    {
        using var cancel = new CancellationTokenSource();
        var exited = _process.WaitForExitAsync(cancel.Token);

        try
        {
            var finished = await Task.WhenAny(work, exited, Task.Delay(timeout, cancel.Token));

            if (finished == work)
            {
                // Awaited rather than read off .Result so the wait's own failure — a
                // TimeoutException from the host, say — arrives with the app's log attached.
                try
                {
                    return await work;
                }
                catch (Exception ex)
                {
                    throw await FailureAsync($"waiting for {what}", ex);
                }
            }

            throw await FailureAsync(finished == exited
                ? $"waiting for {what} — the app exited first"
                : $"waiting {timeout.TotalSeconds:0.#} s for {what}");
        }
        finally
        {
            await cancel.CancelAsync();
        }
    }

    /// <summary>
    /// Kills the app and reports its log — always, whether the test passed or failed. A
    /// surviving o-view would hold the bus name and the display for the rest of the run, and
    /// an unreported log is a failure nobody can explain afterwards.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_started && !_process.HasExited)
            {
                // The whole tree: the app starts claude for the usage-cache refresh (ADR-0015),
                // and an orphan of that holds the temporary home open.
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(ShutdownPatience);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException
                                       or NotSupportedException or Win32Exception)
        {
            // Nothing here may fail a passing test: this is cleanup, and the report below is
            // the part with diagnostic value.
        }

        Report();
        SaveLogToArtifacts();

        _process.Dispose();
        TryDelete(_root);
    }

    /// <summary>
    /// A failure, with the bus asked what the app owns before the message is built. Every
    /// throwing path goes through here rather than through <see cref="Failure"/> directly,
    /// because the answer is only available while the app is still on the bus — once it has
    /// been killed on dispose there is nothing left to ask.
    /// </summary>
    private async Task<XunitException> FailureAsync(string what, Exception? cause = null)
    {
        _busNames = await ReadOwnedBusNamesAsync();
        return Failure(what, cause);
    }

    /// <summary>
    /// A failure message with everything known about the app underneath it: how it was
    /// started, how it ended, its log and its console output.
    /// </summary>
    private XunitException Failure(string what, Exception? cause = null)
    {
        var message = $"{ExecutableName} ({_label}): failed {what}.{Environment.NewLine}{Describe()}";
        return cause is null ? new XunitException(message) : new XunitException(message, cause);
    }

    private string Describe()
    {
        var report = new StringBuilder();

        report.AppendLine($"state       : {State()}");
        report.AppendLine($"home        : {Home}");
        report.AppendLine($"display     : {Value("DISPLAY")}");
        report.AppendLine($"session bus : {Value("DBUS_SESSION_BUS_ADDRESS")}");
        report.AppendLine($"its names   : {_busNames}");

        report.AppendLine($"──── its log ({_logPath}) ────");
        var lines = LogLines();
        if (lines.Count == 0)
        {
            report.AppendLine("(nothing written yet)");
        }
        else
        {
            foreach (var line in lines.TakeLast(ReportedLogLines))
            {
                report.AppendLine(line);
            }
        }

        report.AppendLine("──── its console output ────");
        lock (_consoleGate)
        {
            report.Append(_console.Length == 0 ? "(nothing)" : _console.ToString());
        }

        return report.ToString();

        // Never fabricate a number (CLAUDE.md rule 6): an exit code is only readable once the
        // process has actually ended, so the state is described rather than always numbered.
        string State()
        {
            if (!_started) return "never started";
            if (!_process.HasExited) return "still running";

            try
            {
                return $"exited with code {_process.ExitCode}";
            }
            catch (InvalidOperationException)
            {
                return "exited";
            }
        }

        static string Value(string variable) =>
            Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : "(unset)";
    }

    private void Report()
    {
        if (_output is null) return;

        _output.WriteLine($"{ExecutableName} ({_label}){Environment.NewLine}{Describe()}");
    }

    /// <summary>
    /// Copies the log to where the <c>linux-runtime</c> job collects what the runtime tests
    /// capture, so a CI failure can be read after the fact rather than only in the test
    /// runner's output. Does nothing outside the job, which does not set the variable.
    /// </summary>
    private void SaveLogToArtifacts()
    {
        if (Environment.GetEnvironmentVariable("OVIEW_RUNTIME_ARTIFACTS") is not { Length: > 0 } directory)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            File.Copy(_logPath, Path.Combine(directory, $"{_label}.log"), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            // Saving evidence must not become the failure; it is already reported above.
        }
    }

    /// <summary>
    /// Which names on the session bus belong to the app, asked of the bus itself: every name
    /// whose owning connection reports the app's process id. The unique names count too — how
    /// many connections the app made is what tells a tray backend that never connected apart
    /// from one that connected and then published nothing.
    ///
    /// <para>Never throws, and catches broadly to make sure of it. This runs only while a
    /// failure message is being built, so an exception escaping here would replace the failure
    /// the test actually found with one from reading about it.</para>
    /// </summary>
    private async Task<string> ReadOwnedBusNamesAsync()
    {
        if (!_started) return "(never started)";

        if (Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") is not { Length: > 0 } address)
        {
            return "(no session bus)";
        }

        try
        {
            // Its own connection, dropped on the way out. DBusConnection.Session is a shared
            // static that the app's own code and the other runtime tests use, and closing that
            // one would take the bus away from the rest of the run.
            using var connection = new DBusConnection(address);
            await connection.ConnectAsync().AsTask().WaitAsync(BusReadTimeout);

            var names = await CallBusAsync(
                connection, "ListNames", null, static (m, _) => ReadStrings(m));

            var mine = new List<string>();
            foreach (var name in names)
            {
                if (await OwnerProcessIdAsync(connection, name) == _process.Id)
                {
                    mine.Add(name);
                }
            }

            return mine.Count > 0
                ? string.Join(", ", mine)
                : "(none: the app owns no name on this bus)";
        }
        catch (Exception ex)
        {
            return $"(could not be read: {ex.GetType().Name}: {ex.Message})";
        }
    }

    /// <summary>
    /// The process behind a name, or null when the bus will not say — a name can be gone by
    /// the time it is asked about, which is ordinary and not worth failing over.
    /// </summary>
    private static async Task<int?> OwnerProcessIdAsync(DBusConnection connection, string name)
    {
        try
        {
            return (int)await CallBusAsync(
                connection,
                "GetConnectionUnixProcessID",
                writer => writer.WriteString(name),
                static (m, _) => m.GetBodyReader().ReadUInt32());
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>The <c>as</c> a reply carries, read off the body a name at a time.</summary>
    private static List<string> ReadStrings(Message message)
    {
        var reader = message.GetBodyReader();
        var end = reader.ReadArrayStart(DBusType.String);

        var strings = new List<string>();
        while (reader.HasNext(end))
        {
            strings.Add(reader.ReadString());
        }

        return strings;
    }

    /// <summary>Calls the bus's own object, which is where both questions above are answered.</summary>
    private static Task<T> CallBusAsync<T>(
        DBusConnection connection,
        string member,
        Action<MessageWriter>? argument,
        MessageValueReader<T> read)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: "org.freedesktop.DBus",
            path: "/org/freedesktop/DBus",
            @interface: "org.freedesktop.DBus",
            member: member,
            signature: argument is null ? null : "s",
            flags: MessageFlags.None);
        argument?.Invoke(writer);

        return connection.CallMethodAsync(writer.CreateMessage(), read).WaitAsync(BusReadTimeout);
    }

    private void CaptureConsole(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;

        lock (_consoleGate)
        {
            _console.AppendLine(e.Data);
        }
    }

    private string? MatchingLogLine(string containing) =>
        LogLines().FirstOrDefault(line => line.Contains(containing, StringComparison.Ordinal));

    /// <summary>
    /// The log as it stands. Shared read because the app holds the file open for appending —
    /// the same reason <c>FileLog.Tail</c> gives — and an empty list for "not written yet",
    /// which is the ordinary state for the first few hundred milliseconds.
    /// </summary>
    private IReadOnlyList<string> LogLines()
    {
        try
        {
            using var stream = new FileStream(
                _logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The built app under <c>src/O-view.Linux/bin</c>. Prefers the configuration this test
    /// assembly was itself built in — the job builds both in Release — and otherwise the most
    /// recently built one, so a developer who built Debug last gets the app they just changed.
    /// </summary>
    private static string Executable()
    {
        var binaries = Path.Combine(RepositoryRoot(), "src", "O-view.Linux", "bin");

        if (!Directory.Exists(binaries))
        {
            throw new XunitException(
                $"{binaries} does not exist: build the app first (dotnet build tests/O-view.Linux.Tests).");
        }

        var configuration = Path.DirectorySeparatorChar + Configuration() + Path.DirectorySeparatorChar;
        var candidates = Directory
            .EnumerateFiles(binaries, ExecutableName, SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).Length == 0)
            .OrderByDescending(path => path.Contains(configuration, StringComparison.Ordinal))
            .ThenByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        return candidates.Count > 0
            ? candidates[0]
            : throw new XunitException(
                $"no {ExecutableName} under {binaries}: build the app first (dotnet build tests/O-view.Linux.Tests).");
    }

    /// <summary>The configuration this assembly was built in, read off its own output path.</summary>
    private static string Configuration()
    {
        var parts = AppContext.BaseDirectory.Split(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var bin = Array.LastIndexOf(parts, "bin");

        return bin >= 0 && bin + 1 < parts.Length ? parts[bin + 1] : "Release";
    }

    /// <summary>
    /// The committed Claude home the app is given a copy of. Real-shaped data, so the engine
    /// has something to ingest and the panel has numbers — the same fixture the smoke test
    /// runs the engine against.
    /// </summary>
    private static string FixtureHome() =>
        Path.Combine(RepositoryRoot(), "tests", "fixtures", "claude-home");

    /// <summary>From this assembly's output up to the folder holding the solution file, as the smoke test does.</summary>
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "O-view.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new XunitException(
            $"no O-view.slnx above {AppContext.BaseDirectory}: the repository root cannot be found.");
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);

        foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temporary folder left behind is untidy, not a failure.
        }
    }
}
