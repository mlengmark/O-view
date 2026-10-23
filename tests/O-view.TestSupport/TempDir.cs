namespace OView.TestSupport;

/// <summary>
/// A disposable temp directory, so a test can point real storage at real files: the rollup
/// store is real SQLite and the settings, reset anchor and panel position are real JSON,
/// which is the point. These tests exercise the actual storage paths, not stand-ins.
/// </summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "oview-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    /// <summary>A path for a file inside the directory.</summary>
    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
