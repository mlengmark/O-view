using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Tmds.DBus.Protocol;
using Xunit.Abstractions;

namespace OView.Linux.Tests.Runtime;

/// <summary>
/// Everything said on the session bus while a runtime test runs, written down as it happens and
/// saved where the <c>linux-runtime</c> job collects artifacts.
///
/// <para><b>Why a recording at all.</b> The toolkit's tray backend logs its own failures
/// through a logger the app installs no sink for, so a registration that never happens says
/// nothing anywhere — and asking the bus afterwards what names exist (<see
/// cref="LinuxAppProcess"/>) only shows the end state. The conversation itself says which side
/// gave up: whether the backend opened a bus connection of its own, whether it ever asked for
/// an <c>org.kde.StatusNotifierItem-…</c> name, and what the bus or the watcher answered.</para>
///
/// <para><b>Its own process, and nothing in the app changes to allow it.</b>
/// <c>dbus-monitor --session</c> asks the bus to let it see every message, which is a property
/// of the bus and not of anything being watched. An app instrumented to report its own bus
/// calls would be a different app from the one the .deb ships.</para>
///
/// <para><b>Saved on a pass as well as a failure.</b> <see cref="DisposeAsync"/> writes the
/// capture out either way, the same way the app's log is saved: the capture is the point of the
/// test that takes one, so a green run that discarded it would have proved nothing anybody can
/// read. Outside the job <c>OVIEW_RUNTIME_ARTIFACTS</c> is unset and nothing is written.</para>
/// </summary>
public sealed class BusTraffic : IAsyncDisposable
{
    /// <summary>The recorder. Part of the <c>dbus-bin</c> package, as <c>dbus-run-session</c> is.</summary>
    public const string Tool = "dbus-monitor";

    /// <summary>
    /// The start of the bus name the toolkit takes for a tray item once it publishes one. The
    /// telling fragment to look for in a capture: asked for and refused is one failure, never
    /// asked for at all is another.
    /// </summary>
    public const string ItemNamePrefix = "org.kde.StatusNotifierItem-";

    /// <summary>
    /// How long the recorder is given to prove it is seeing messages before the capture is
    /// written off as not running.
    /// </summary>
    private static readonly TimeSpan ReadyPatience = TimeSpan.FromSeconds(10);

    /// <summary>How often a marker is re-sent while waiting for the recorder to see one, and how often a capture is re-read.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>How long the recorder is given to die once asked. It is killed, so this only bounds the wait.</summary>
    private static readonly TimeSpan ShutdownPatience = TimeSpan.FromSeconds(5);

    /// <summary>The four words <c>dbus-monitor</c> begins a message with, each at the start of a line.</summary>
    private static readonly string[] MessageStarts = ["method call", "method return", "error", "signal"];

    private readonly Process? _monitor;
    private readonly ITestOutputHelper? _output;
    private readonly string _label;
    private readonly StringBuilder _capture = new();
    private readonly StringBuilder _errors = new();
    private readonly Lock _gate = new();

    private string _state;
    private bool _disposed;

    private BusTraffic(Process? monitor, ITestOutputHelper? output, string label, string state)
    {
        _monitor = monitor;
        _output = output;
        _label = label;
        _state = state;
    }

    /// <summary>
    /// Whether the recorder is known to be seeing messages — it was started, and a marker sent
    /// on the bus came back out of it. False means <see cref="State"/> says why, and the
    /// capture is not evidence of anything.
    /// </summary>
    public bool IsRecording { get; private set; }

    /// <summary>What the recorder is doing, or why it is not recording, in one plain sentence.</summary>
    public string State => _state;

    /// <summary>The capture as it stands. A snapshot: the recorder keeps writing.</summary>
    public string Text
    {
        get { lock (_gate) return _capture.ToString(); }
    }

    /// <summary>How many messages have been captured so far.</summary>
    public int MessageCount => Lines().Count(StartsMessage);

    /// <summary>
    /// Starts recording the session bus the test is running against. Returns a recorder that
    /// writes nothing — rather than throwing — when there is no bus or no <see cref="Tool"/>,
    /// so the ordinary test job and a developer's own machine are unaffected; <see
    /// cref="IsRecording"/> is then false and <see cref="State"/> names the reason.
    /// </summary>
    /// <param name="label">Names the saved capture, so two tests' recordings are told apart.</param>
    /// <param name="output">Where the capture is reported on dispose. Null reports nowhere.</param>
    public static async Task<BusTraffic> StartAsync(string label, ITestOutputHelper? output = null)
    {
        if (Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") is not { Length: > 0 } address)
        {
            return new BusTraffic(null, output, label, "not recording: there is no session bus to record");
        }

        if (OnPath(Tool) is null)
        {
            return new BusTraffic(null, output, label,
                $"not recording: {Tool} is not on PATH — the linux-runtime job's install step " +
                "must add the dbus-bin package that carries it");
        }

        var traffic = new BusTraffic(new Process { StartInfo = Launch() }, output, label, "not recording: never started");

        try
        {
            traffic._monitor!.OutputDataReceived += traffic.CaptureMessage;
            traffic._monitor.ErrorDataReceived += traffic.CaptureError;

            traffic._monitor.Start();
            traffic._monitor.BeginOutputReadLine();
            traffic._monitor.BeginErrorReadLine();

            // Returned recording, not merely started: a caller that got a handle back can send
            // the app on its way knowing the conversation is being written down from here.
            await traffic.WaitUntilRecordingAsync(address);
            return traffic;
        }
        catch
        {
            await traffic.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Waits for a captured message mentioning <paramref name="fragment"/>, and says whether
    /// one arrived. Both answers are results: a caller watching for a registration that may
    /// never be attempted wants the wait bounded, not a failure.
    /// </summary>
    public async Task<bool> WaitForMentionAsync(string fragment, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            if (Text.Contains(fragment, StringComparison.Ordinal))
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(PollInterval);
        }
    }

    /// <summary>
    /// Every captured message mentioning <paramref name="fragment"/> — the whole message, its
    /// header line and the body lines underneath it, as <see cref="Tool"/> printed them.
    /// </summary>
    public IReadOnlyList<string> Mentioning(string fragment) =>
        [.. Messages().Where(message => message.Contains(fragment, StringComparison.Ordinal))];

    /// <summary>
    /// Stops the recorder and saves what it captured — whether the test passed or failed. A
    /// surviving dbus-monitor would go on writing into the next test's capture, and an unsaved
    /// capture is evidence nobody can read after the run.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_monitor is not null)
        {
            try
            {
                if (!_monitor.HasExited)
                {
                    _monitor.Kill(entireProcessTree: true);
                    await _monitor.WaitForExitAsync().WaitAsync(ShutdownPatience);
                }

                // Synchronous and unbounded on purpose: this is the call that waits for the
                // output handlers to drain, so without it the last messages — the app being
                // killed, the names being released — are missing from the capture.
                _monitor.WaitForExit();
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException
                                           or NotSupportedException or Win32Exception)
            {
                // Nothing here may fail a passing test: this is cleanup, and the capture
                // written below is the part with diagnostic value.
            }

            _monitor.Dispose();
        }

        Report();
        SaveToArtifacts();
    }

    /// <summary>
    /// How the recorder is started: <c>dbus-monitor --session</c>, line-buffered.
    ///
    /// <para>Line-buffered through <c>stdbuf</c> because its output is read through a pipe, and
    /// a pipe makes the C library hold about four kilobytes back before writing any of it. The
    /// capture would still be complete by the end, but nothing in it could be read while the
    /// test ran — including the marker that proves the recorder is alive. <c>stdbuf</c> is
    /// coreutils, so it is on every runner; without it the recording still happens and only
    /// the waiting for it stops working, which <see cref="State"/> then says.</para>
    /// </summary>
    private static ProcessStartInfo Launch()
    {
        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (OnPath("stdbuf") is { } stdbuf)
        {
            start.FileName = stdbuf;
            start.ArgumentList.Add("-oL");
            start.ArgumentList.Add(Tool);
        }
        else
        {
            start.FileName = Tool;
        }

        start.ArgumentList.Add("--session");
        return start;
    }

    /// <summary>
    /// Sends a signal of its own on the bus until the recorder prints it back, which is the
    /// only honest way to know every message from here on is being seen. Sets <see
    /// cref="IsRecording"/> and <see cref="State"/>; never throws, because a recorder that
    /// could not be started is a capture to explain, not a test to fail here.
    /// </summary>
    private async Task WaitUntilRecordingAsync(string address)
    {
        var marker = $"oview-bus-traffic-{Guid.NewGuid():N}";
        var deadline = DateTime.UtcNow + ReadyPatience;

        try
        {
            // Its own connection, dropped on the way out: DBusConnection.Session is a shared
            // static the app's own code and the other runtime tests use, and closing that one
            // would take the bus away from the rest of the run.
            using var connection = new DBusConnection(address);
            await connection.ConnectAsync().AsTask().WaitAsync(ReadyPatience);

            while (true)
            {
                SendMarker(connection, marker);

                if (Text.Contains(marker, StringComparison.Ordinal))
                {
                    IsRecording = true;
                    _state = $"recording every message on {address}";
                    return;
                }

                if (_monitor!.HasExited)
                {
                    _state = $"not recording: {Tool} exited with code {_monitor.ExitCode} before it saw a marker";
                    return;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    _state = $"not recording: {Tool} did not report a marker within " +
                             $"{ReadyPatience.TotalSeconds:0.#} s";
                    return;
                }

                await Task.Delay(PollInterval);
            }
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException or TimeoutException
                                       or InvalidOperationException)
        {
            _state = $"not recording: the marker could not be sent ({ex.GetType().Name}: {ex.Message})";
        }
    }

    /// <summary>
    /// A broadcast signal carrying the marker. Nothing subscribes to it; the bus hands it to
    /// its monitors all the same, which is exactly what is being checked.
    /// </summary>
    private static void SendMarker(DBusConnection connection, string marker)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteSignalHeader(
            path: "/oview/BusTraffic",
            @interface: "oview.BusTraffic",
            member: "Marker",
            signature: "s");
        writer.WriteString(marker);

        // Best effort: a failed send is indistinguishable here from one nobody recorded, and
        // either way the loop above gives up with a state that says so.
        connection.TrySendMessage(writer.CreateMessage());
    }

    private void CaptureMessage(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;

        lock (_gate)
        {
            _capture.AppendLine(e.Data);
        }
    }

    private void CaptureError(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;

        lock (_gate)
        {
            _errors.AppendLine(e.Data);
        }
    }

    private void Report()
    {
        if (_output is null) return;

        _output.WriteLine($"{Tool} ({_label}){Environment.NewLine}{Describe()}");
    }

    /// <summary>The capture's own header: enough to tell a real recording from an empty one.</summary>
    private string Describe()
    {
        var report = new StringBuilder();

        report.AppendLine($"state    : {_state}");
        report.AppendLine($"messages : {MessageCount}");

        report.AppendLine("──── its own errors ────");
        lock (_gate)
        {
            report.AppendLine(_errors.Length == 0 ? "(nothing)" : _errors.ToString().TrimEnd());
        }

        return report.ToString();
    }

    /// <summary>
    /// Writes the capture to "{label}.dbus.txt" under <c>OVIEW_RUNTIME_ARTIFACTS</c>, which
    /// .github/workflows/ci.yml uploads whether the job passed or failed. Does nothing outside
    /// the job, which is the only place that sets the variable.
    /// </summary>
    private void SaveToArtifacts()
    {
        if (Environment.GetEnvironmentVariable("OVIEW_RUNTIME_ARTIFACTS") is not { Length: > 0 } directory)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, $"{_label}.dbus.txt"),
                $"{Tool} --session · {_label}{Environment.NewLine}{Describe()}" +
                $"──── the capture ────{Environment.NewLine}{Text}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            // Saving evidence must not become the failure; it is already reported above.
        }
    }

    /// <summary>
    /// The capture a message at a time: each header line <see cref="Tool"/> wrote, with the
    /// indented body lines that belong to it.
    /// </summary>
    private IEnumerable<string> Messages()
    {
        var message = new StringBuilder();

        foreach (var line in Lines())
        {
            if (StartsMessage(line) && message.Length > 0)
            {
                yield return message.ToString();
                message.Clear();
            }

            message.AppendLine(line);
        }

        if (message.Length > 0)
        {
            yield return message.ToString();
        }
    }

    private string[] Lines() => Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static bool StartsMessage(string line) =>
        MessageStarts.Any(start => line.StartsWith(start, StringComparison.Ordinal));

    /// <summary>The tool's full path, or null when it is not on PATH.</summary>
    private static string? OnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(directory => directory.Length > 0)
            .Select(directory => Path.Combine(directory, tool))
            .FirstOrDefault(File.Exists);
}
