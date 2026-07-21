namespace OView.Core.Models;

/// <summary>
/// Provenance of a <see cref="UsageSnapshot"/>. The UI must always surface this
/// (ADR-0002: mandatory data-source labelling).
/// </summary>
public enum DataSource
{
    /// <summary>No data is available. Show a neutral state, never a fabricated number.</summary>
    None,

    /// <summary>Derived from local JSONL token counts. An estimate, labelled "local estimate".</summary>
    Estimate,

    /// <summary>Authoritative data, but older than the freshness threshold. Label with its age.</summary>
    Stale,

    /// <summary>Authoritative data within the freshness threshold.</summary>
    Live,
}
