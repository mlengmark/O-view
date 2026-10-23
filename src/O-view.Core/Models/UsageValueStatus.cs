namespace OView.Core.Models;

/// <summary>
/// How trustworthy a usage value is. A skin never guesses this, Core states it alongside
/// the value itself, so "unknown" can always be rendered as unknown rather than as zero.
/// </summary>
public enum UsageValueStatus
{
    /// <summary>Measured directly from a vendor source.</summary>
    Real,

    /// <summary>Derived or modelled rather than measured, for example, priced from token counts.</summary>
    Estimated,

    /// <summary>No source produced a value. Rendered as an explicit gap, never as zero or blank.</summary>
    Unavailable,
}
