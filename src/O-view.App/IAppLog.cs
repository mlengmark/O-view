namespace OView.App;

/// <summary>
/// Diagnostic log sink. Only ever active when the user passes <c>--log</c>.
///
/// <para><b>Never log tokens, credentials, or conversation content</b> — refresh telemetry,
/// cadence changes and resource counts only (CLAUDE.md rule 3).</para>
/// </summary>
public interface IAppLog
{
    void Write(string message);
}
