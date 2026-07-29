namespace OView.Core.Tests;

/// <summary>
/// THROWAWAY — proves the ubuntu-latest matrix leg actually runs the suite and can fail
/// independently of Windows. Delete this file and its branch once observed.
/// </summary>
public class PlatformCanaryTests
{
    [Fact]
    public void DirectorySeparatorIsBackslash()
    {
        // True on Windows, false on Linux, and invisible to CA1416 — exactly the class of
        // break only a real Linux run catches. It must fail at *assertion* time, not
        // compile time, or it says nothing about whether the tests actually ran.
        Assert.Equal('\\', Path.DirectorySeparatorChar);
    }

    [Fact]
    public void RunsOnWindows()
    {
        Assert.True(OperatingSystem.IsWindows());
    }
}
