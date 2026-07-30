namespace OView.App.Platform;

/// <summary>
/// Single-instance via an exclusively-held lock file — the Linux mechanism.
///
/// <para><b>Why not a named <see cref="Mutex"/>, which .NET does implement on Unix?</b>
/// Because its lifetime is not tied to the process. Named mutexes on Unix are backed by
/// files in a shared-memory directory, and a hard-killed process can leave the name held —
/// which for an app designed to run for days means "O-view won't start any more", with a
/// cause the user has no way to guess. An advisory file lock is released by the kernel
/// however the process dies, including <c>kill -9</c>.</para>
///
/// <para>The lock lives in <c>$XDG_RUNTIME_DIR</c> where one exists: it is per-user,
/// per-session, and cleared on logout, which is exactly the lifetime wanted. Falling back
/// to the temp directory keeps it working where that variable is unset.</para>
///
/// <para>Nothing here is Linux-only — it is ordinary file IO, so it is exercised on both
/// CI platforms rather than only on the one it was written for.</para>
/// </summary>
public sealed class FileLockSingleInstanceGuard : ISingleInstanceGuard
{
    private readonly string _path;
    private FileStream? _held;

    /// <summary>Default lock location: <c>$XDG_RUNTIME_DIR/o-view.lock</c>, else the temp directory.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime
            ? runtime
            : Path.GetTempPath(),
        "o-view.lock");

    public FileLockSingleInstanceGuard(string? path = null) => _path = path ?? DefaultPath;

    /// <summary>Where this guard is holding, or would hold, its lock. Useful in diagnostics.</summary>
    public string LockPath => _path;

    public bool TryAcquire()
    {
        if (_held is not null)
        {
            return true;   // already ours; asking twice is not a second instance
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            // FileShare.None is the whole mechanism: the second opener is refused.
            _held = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            // The pid is written for diagnosis only — never read back to make a decision.
            // A stale pid in an abandoned file must not be mistaken for a live instance.
            var pid = System.Text.Encoding.UTF8.GetBytes(Environment.ProcessId.ToString());
            _held.SetLength(0);
            _held.Write(pid);
            _held.Flush();
            return true;
        }
        catch (IOException)
        {
            return false;   // someone else holds it — the normal "already running" path
        }
        catch (UnauthorizedAccessException)
        {
            // Cannot lock at all. Refusing to start would be worse than the double-icon
            // risk it guards against, so proceed and let the user have a working app.
            return true;
        }
    }

    public void Dispose()
    {
        _held?.Dispose();
        _held = null;

        try
        {
            File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leaving the file behind is harmless — it is the *lock*, not the file's
            // existence, that means "running".
        }
    }
}
