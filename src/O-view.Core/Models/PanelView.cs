namespace OView.Core.Models;

/// <summary>
/// What the panel's figures and chart count. Named in Core because the setting that
/// remembers the choice and the Presentation that applies it must agree on the two values
/// without either referencing the other.
/// </summary>
public enum PanelMeasure
{
    /// <summary>
    /// Output tokens only, priced at the output rate only. The default for a fresh install:
    /// it answers "what did I produce".
    /// </summary>
    Output,

    /// <summary>
    /// Everything billed, priced in full, which is what "Est. value" has always meant. It
    /// answers "what did this cost to run", cached prompt re-reads included.
    /// </summary>
    Overall,
}

/// <summary>Which side of the local block is showing.</summary>
public enum PanelFace
{
    /// <summary>The chart and the totals table.</summary>
    Totals,

    /// <summary>The doughnuts and the table of every model.</summary>
    Models,
}
