using System.Globalization;
using System.IO;

namespace OView.Tray.Diagnostics;

/// <summary>
/// Minimal diagnostic log for a WinExe with no console. Only active when --log is
/// passed; never logs tokens, credentials, or conversation content — refresh
/// telemetry and GDI counts only.
/// </summary>
public sealed class FileLog(string path)
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
