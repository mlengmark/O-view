using OView.App;

namespace OView.TestSupport;

/// <summary>Captures log lines so a test can assert what was recorded without a file.</summary>
public sealed class ListLog : IAppLog
{
    private readonly List<string> _lines = [];

    /// <summary>
    /// A snapshot, taken under the lock. The engine writes from the thread pool while the
    /// test reads from its own thread, so handing out the live list would be a data race,
    /// and one that shows up as an intermittent failure rather than an error.
    /// </summary>
    public IReadOnlyList<string> Lines
    {
        get { lock (_lines) { return [.. _lines]; } }
    }

    public void Write(string message)
    {
        lock (_lines) { _lines.Add(message); }
    }
}
