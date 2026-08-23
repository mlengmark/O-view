using OView.Core.Providers;
using OView.Core.Providers.Jsonl;

namespace OView.Core.Tests;

/// <summary>
/// Where Claude Code keeps its configuration, and therefore where O-view looks for
/// transcripts. Anthropic documents exactly two cases:
///
/// <blockquote>"On Windows, <c>~/.claude</c> resolves to <c>%USERPROFILE%\.claude</c>. If you
/// set <c>CLAUDE_CONFIG_DIR</c>, every <c>~/.claude</c> path on this page lives under that
/// directory instead."</blockquote>
///
/// <para>O-view honoured only the first, so anyone who had relocated their configuration got
/// no transcripts found and a token tile reading zero, with nothing to explain it — the same
/// silent-empty-tile failure as issues #44 and #58 through a third door.</para>
/// </summary>
public class ClaudeConfigDirTests
{
    private static string ProfileDefault => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    [Fact]
    public void WithNoOverrideItIsTheDocumentedProfilePath()
    {
        Assert.Equal(ProfileDefault, ClaudeConfigDir.Resolve(null));
    }

    [Fact]
    public void AnOverrideReplacesTheWholePath()
    {
        Assert.Equal(@"D:\claude-config", ClaudeConfigDir.Resolve(@"D:\claude-config"));
    }

    /// <summary>
    /// An exported-but-blank variable is a shell accident, not an instruction to read the
    /// filesystem root. Honouring it literally would point the scan somewhere with no
    /// transcripts and no explanation — which is the failure this whole change fixes.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void AnEmptyOrBlankOverrideIsTreatedAsUnset(string value)
    {
        Assert.Equal(ProfileDefault, ClaudeConfigDir.Resolve(value));
    }

    /// <summary>Surrounding whitespace is a copy-paste artefact, not part of the path.</summary>
    [Fact]
    public void SurroundingWhitespaceIsTrimmed()
    {
        Assert.Equal(@"D:\claude-config", ClaudeConfigDir.Resolve("  D:\\claude-config  "));
    }

    /// <summary>
    /// The point of the whole change: the transcript root follows the override, because
    /// that is where the projects directory actually is.
    /// </summary>
    [Fact]
    public void TheTranscriptRootSitsUnderWhicheverDirectoryIsInEffect()
    {
        var original = Environment.GetEnvironmentVariable(ClaudeConfigDir.OverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(ClaudeConfigDir.OverrideVariable, @"D:\claude-config");
            Assert.Equal(Path.Combine(@"D:\claude-config", "projects"), ClaudeProjectsLocator.DefaultRoot);

            Environment.SetEnvironmentVariable(ClaudeConfigDir.OverrideVariable, null);
            Assert.Equal(Path.Combine(ProfileDefault, "projects"), ClaudeProjectsLocator.DefaultRoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ClaudeConfigDir.OverrideVariable, original);
        }
    }

    /// <summary>
    /// Read on every call, not cached: O-view runs for days, and a cached value would also
    /// survive a change made while it runs.
    /// </summary>
    [Fact]
    public void TheOverrideIsReReadRatherThanCached()
    {
        var original = Environment.GetEnvironmentVariable(ClaudeConfigDir.OverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(ClaudeConfigDir.OverrideVariable, @"D:\first");
            var first = ClaudeConfigDir.Path;

            Environment.SetEnvironmentVariable(ClaudeConfigDir.OverrideVariable, @"D:\second");
            var second = ClaudeConfigDir.Path;

            Assert.Equal(@"D:\first", first);
            Assert.Equal(@"D:\second", second);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ClaudeConfigDir.OverrideVariable, original);
        }
    }
}
