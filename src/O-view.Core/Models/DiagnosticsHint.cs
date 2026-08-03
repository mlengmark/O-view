namespace OView.Core.Models;

/// <summary>
/// How the user of *this* build reaches the diagnostics report.
///
/// <para>The panel's explanatory copy tells a stuck user what to do next, and until v0.6.0
/// that instruction was hard-coded as <c>"Right-click the tray icon → Copy diagnostics"</c>
/// in five places. On Linux the tray menu carries <b>Exit only</b>, so every one of those
/// sentences directed the reader at a menu item that is not there — a claim about the user's
/// machine that O-view had not verified, which is CLAUDE.md rule 6 in the one place the app
/// is supposed to be at its most careful: the copy shown when something is already
/// wrong.</para>
///
/// <para><b>Why a settable phrase rather than a platform check.</b> Rule 1 resolves platform
/// differences by which implementation is constructed. Each head states its own affordance
/// once at startup, so <c>Core</c> holds no branch, knows nothing about menus or command
/// lines, and a head that grows a Copy-diagnostics item later changes one line rather than
/// five sentences. It is set once before any panel is shown and never again.</para>
///
/// <para>The default is deliberately true on <i>both</i> platforms: an unconfigured head, or
/// a test, gets a sentence that is vague rather than one that is wrong.</para>
/// </summary>
public static class DiagnosticsHint
{
    /// <summary>Correct anywhere, specific nowhere — the safe fallback.</summary>
    public const string Default = "Produce an O-view diagnostics report";

    /// <summary>
    /// An imperative phrase naming the affordance, with no trailing punctuation, so it
    /// composes into "<c>{Instruction} to report this.</c>" and its siblings.
    /// </summary>
    public static string Instruction { get; private set; } = Default;

    /// <summary>
    /// Called once by the head during startup. Blank input is ignored rather than allowed to
    /// produce a sentence with a hole in it.
    /// </summary>
    public static void Use(string instruction)
    {
        if (!string.IsNullOrWhiteSpace(instruction))
        {
            Instruction = instruction.TrimEnd(' ', '.', ':');
        }
    }

    /// <summary>Restores the default. For tests, which must not leak state into each other.</summary>
    public static void Reset() => Instruction = Default;
}
