namespace OView.Presentation.Panel;

/// <summary>
/// The local block's model face: two doughnuts and the table of every model behind them.
/// Output and Overall differ in the figures, never in the shape, so one record serves both.
///
/// <para><b>Which models, in which order, in which colour is decided in
/// <see cref="PanelSections.ModelFace"/> and nowhere else.</b> The doughnut sections and the
/// table rows are the same models in the same order, so a head cannot draw one sequence and
/// label it with another.</para>
///
/// <para>Unlike the tiles, nothing is folded into "other": the rollup store keeps every model
/// and so does this face. The tiles' chart colour order is not used either; this face has its
/// own, because it spends a third chromatic slot where the tiles reserve it for the remainder.</para>
/// </summary>
/// <param name="Subtitle">The line under the block's title: <c>by model · output tokens</c>.</param>
/// <param name="FlipLabel">What the flip tag reads while this face shows: <c>totals</c>.</param>
/// <param name="Today">Today's doughnut.</param>
/// <param name="Window">The 31-day doughnut.</param>
/// <param name="Table">The models, one row each, with both windows' figures.</param>
/// <param name="GreyCount">
/// How many greys the models after the third need: the rows' <see cref="PanelModelRowView.GreyStep"/>
/// run 0 to one less than this. Carried so a head can space the greys without counting.
/// </param>
/// <param name="WindowCaveat">
/// What qualifies the window's figures, as on the totals face: partial history, unpriced
/// models, assumed cache-write durations, stale rates. Empty when none applies.
/// </param>
public sealed record PanelModelFaceView(
    string Subtitle,
    string FlipLabel,
    PanelModelDoughnutView Today,
    PanelModelDoughnutView Window,
    PanelModelTableView Table,
    int GreyCount,
    string WindowCaveat);

/// <summary>
/// One doughnut: the total in its centre, the caption under it, and a section per model that
/// has a share of it, clockwise from the top in the table's order.
/// </summary>
/// <param name="Total">The window's total, formatted, from the same figure the totals face states.</param>
/// <param name="Unit">Beneath the total in the centre: <c>output</c>, or <c>tokens</c> in Overall.</param>
/// <param name="Caption">Under the ring: <c>TODAY</c> or <c>LAST 31 DAYS</c> as words, the head sets the case.</param>
/// <param name="Sections">
/// Only models with something in this window. A model that did nothing today still has its row
/// in the table, and a zero-width section would be invisible with a hover card of its own.
/// </param>
public sealed record PanelModelDoughnutView(
    string Total,
    string Unit,
    string Caption,
    IReadOnlyList<PanelModelSectionView> Sections);

/// <summary>One section of a doughnut and the hover card behind it.</summary>
/// <param name="Row">Which table row this is, so the section takes that row's colour.</param>
/// <param name="Share">This model's fraction of the doughnut's total, 0–1, for the arc. Turning it into degrees and the gap between sections is the head's.</param>
/// <param name="Figure">The card's headline: the model's tokens in this window.</param>
/// <param name="ShareLine">The card's second line: <c>output · 37% of 31 days</c>.</param>
/// <param name="ValueLine">The card's last line: <c>Est. $12.40</c>, or <c>unpriced</c>.</param>
public sealed record PanelModelSectionView(
    int Row,
    double Share,
    string Figure,
    string ShareLine,
    string ValueLine);

/// <summary>The table: its group headings, and a row per model.</summary>
/// <param name="Headers">
/// The five column headings, <c>Model | Tokens | Est. value | Tokens | Est. value</c>.
/// </param>
/// <param name="TodayGroup">The group heading over the first pair of columns.</param>
/// <param name="WindowGroup">The group heading over the second pair.</param>
public sealed record PanelModelTableView(
    IReadOnlyList<string> Headers,
    string TodayGroup,
    string WindowGroup,
    IReadOnlyList<PanelModelRowView> Rows);

/// <summary>
/// One model's row, and the colour its chip, its sections and its card share.
///
/// <para><b>The slot, never the colour</b>, as in <see cref="PanelTileSegmentView.Slot"/>. The
/// three largest models take slots 0 to 2; every model after them is a grey, told apart by
/// <see cref="GreyStep"/>, stepping towards the surface, and by the legend.</para>
/// </summary>
/// <param name="Name">
/// The model's published name, or its raw id when it is not recognised. Carried whole: a long
/// one is cut where it is drawn, by the head's own ellipsis, and <see cref="Id"/> is what the
/// row's tooltip and the hover cards show in full.
/// </param>
/// <param name="Id">The raw model id as recorded.</param>
/// <param name="Slot">0–2 for a chromatic model, or -1 for a grey.</param>
/// <param name="GreyStep">0 for the first grey, counting up; -1 for a chromatic model.</param>
/// <param name="Today">Today's two figures.</param>
/// <param name="Window">The 31-day figures.</param>
public sealed record PanelModelRowView(
    string Name,
    string Id,
    int Slot,
    int GreyStep,
    PanelModelFigureView Today,
    PanelModelFigureView Window);

/// <summary>A model's tokens and estimated value in one window, already worded.</summary>
/// <param name="Tokens">The tokens, or <c>unknown</c> when the store holds no count for them.</param>
/// <param name="Value">The estimated value, or <c>unpriced</c>; never blank and never a zero for a model with no rate.</param>
/// <param name="ValueKnown">False when <paramref name="Value"/> is the word standing in for a missing price, which the head paints muted.</param>
public sealed record PanelModelFigureView(string Tokens, string Value, bool ValueKnown);
