namespace OView.Presentation.Tests;

/// <summary>
/// <see cref="OView.Core.Models.DiagnosticsHint"/> is one process-wide setting, and the
/// diagnostics sentences, the banner and the panel's scope note all read it. A class that
/// sets it would otherwise change the wording under any of those readers xUnit runs beside
/// it, so the setter's collection runs with nothing else in flight. Readers need no
/// attribute: none of them sets the hint, they only need it not to move.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DiagnosticsHintCollection
{
    public const string Name = "DiagnosticsHint";
}
