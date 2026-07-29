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
        // True on Windows, false on Linux. Nothing CA1416 can see — exactly the class of
        // break only a real Linux run catches.
        Assert.Equal('\', Path.DirectorySeparatorChar);
    }
}
