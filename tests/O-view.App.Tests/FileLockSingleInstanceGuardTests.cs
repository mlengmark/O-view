using OView.App.Platform;

namespace OView.App.Tests;

/// <summary>
/// The Linux single-instance mechanism. The property that matters is the one a named
/// mutex on Unix does not give: the claim is released when the process dies, however it
/// dies. That cannot be tested without killing a process, so what is tested here is
/// everything around it — and the release-on-dispose behaviour that stands in for it.
/// </summary>
public class FileLockSingleInstanceGuardTests
{
    [Fact]
    public void FirstInstanceAcquires()
    {
        using var dir = new TempDir();
        using var guard = new FileLockSingleInstanceGuard(dir.File("o-view.lock"));

        Assert.True(guard.TryAcquire());
    }

    [Fact]
    public void SecondInstanceIsRefusedWhileTheFirstHolds()
    {
        using var dir = new TempDir();
        var path = dir.File("o-view.lock");

        using var first = new FileLockSingleInstanceGuard(path);
        Assert.True(first.TryAcquire());

        using var second = new FileLockSingleInstanceGuard(path);
        Assert.False(second.TryAcquire());
    }

    /// <summary>
    /// Releasing must let the next instance in. This is the behaviour that makes the lock
    /// file the right mechanism: a named mutex on Unix can outlive its owner, and an app
    /// that then refuses to start has no cure the user can find.
    /// </summary>
    [Fact]
    public void ReleasingLetsTheNextInstanceIn()
    {
        using var dir = new TempDir();
        var path = dir.File("o-view.lock");

        var first = new FileLockSingleInstanceGuard(path);
        Assert.True(first.TryAcquire());
        first.Dispose();

        using var second = new FileLockSingleInstanceGuard(path);
        Assert.True(second.TryAcquire());
    }

    /// <summary>
    /// A lock file left behind by a killed process must not, on its own, look like a live
    /// instance — the file's existence means nothing, only the lock does.
    /// </summary>
    [Fact]
    public void AStaleFileWithNoHolderDoesNotBlockStartup()
    {
        using var dir = new TempDir();
        var path = dir.File("o-view.lock");
        File.WriteAllText(path, "999999");   // a pid that is not running

        using var guard = new FileLockSingleInstanceGuard(path);

        Assert.True(guard.TryAcquire());
    }

    [Fact]
    public void AcquiringTwiceFromTheSameGuardIsNotASecondInstance()
    {
        using var dir = new TempDir();
        using var guard = new FileLockSingleInstanceGuard(dir.File("o-view.lock"));

        Assert.True(guard.TryAcquire());
        Assert.True(guard.TryAcquire());
    }

    [Fact]
    public void CreatesTheLockDirectoryWhenAbsent()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "run", "user", "1000", "o-view.lock");

        using var guard = new FileLockSingleInstanceGuard(path);

        Assert.True(guard.TryAcquire());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void DisposingWithoutAcquiringIsSafe()
    {
        using var dir = new TempDir();
        // The losing instance shuts down through the same path as the winner, so Dispose
        // has to tolerate never having held anything.
        var guard = new FileLockSingleInstanceGuard(dir.File("o-view.lock"));
        guard.Dispose();
    }

    [Fact]
    public void DefaultPathPrefersXdgRuntimeDir()
    {
        var original = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        try
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", Path.Combine(Path.GetTempPath(), "xdg-probe"));
            Assert.Contains("xdg-probe", FileLockSingleInstanceGuard.DefaultPath, StringComparison.Ordinal);

            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", null);
            Assert.EndsWith("o-view.lock", FileLockSingleInstanceGuard.DefaultPath, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", original);
        }
    }
}
