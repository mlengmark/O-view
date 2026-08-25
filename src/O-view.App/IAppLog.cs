namespace OView.App;

/// <summary>
/// Diagnostic log sink. <b>Active by default</b> — see <see cref="Diagnostics.FileLog"/> for
/// why it stopped being opt-in, and for the bound that makes always-on affordable.
///
/// <para><b>Never log tokens, credentials, or conversation content</b> — refresh telemetry,
/// cadence changes and resource counts only (CLAUDE.md rule 3). That constraint is what lets
/// this run unattended on every install and lets the tail ship inside a support bundle
/// destined for a public issue.</para>
/// </summary>
public interface IAppLog
{
    void Write(string message);
}
