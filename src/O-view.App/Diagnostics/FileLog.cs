using System.Globalization;
using System.IO;

namespace OView.App.Diagnostics;

/// <summary>
/// Minimal diagnostic log for a windowed app with no console. Only active when --log is
/// passed; never logs tokens, credentials, or conversation content — refresh
/// telemetry and resource counts only.
/// </summary>
public sealed class FileLog(string path) : IAppLog
{
    private readonly object _gate = new();

    public void Write(string message)
    {
        lock (_gate)
        {
            File.AppendAllText(path,
                $"{DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}Z {message}{Environment.NewLine}");
        }
    }
}
