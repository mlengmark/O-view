namespace OView.Presentation.Panel;

/// <summary>
/// Everything the panel says about usage that is not drawing from the plan: the live banner
/// for the current session window, the standing 31-day spend section, and the relabel the
/// value tile takes while the two are true (docs/ui-spec.md §2, GitHub issues #3, #259).
///
/// <para><b>Why one view model for two registers.</b> They are independent signals, the
/// 31-day figure stands while the current window is on-plan, and the banner appears before
/// any credit spend has accrued, but they make one claim between them, and the Windows panel
/// was the only place that knew how the halves fit together. The Linux panel showed neither,
/// so the one thing a reader needs when a meter stops moving existed on one face only.</para>
///
/// <para>The banner's three billing wordings are the part that must not be rebuilt per head.
/// What the panel may assert about the reader's money follows an account setting O-view reads
/// from another application's cache, and a second face deciding that for itself is how a
/// panel ends up telling someone with extra usage switched off that they are being charged
/// (issue #259). It is decided here, once, and stamped with when the cache was read.</para>
/// </summary>
/// <param name="Banner">
/// The live banner, or null when this window is drawing from the plan as expected, which is
/// the usual state and draws nothing at all.
/// </param>
/// <param name="Heading">
/// The standing section's heading, naming the window its figure covers. Always present: the
/// section is shown whether or not anything was recorded, because a section that vanishes
/// leaves the reader unable to tell no off-plan usage from O-view not having looked.
/// </param>
/// <param name="Badge">Whether credit-billed usage was recorded in the window, in two words.</param>
/// <param name="SpendLabel">The label on the figure.</param>
/// <param name="SpendValue">The figure, a true zero when nothing was recorded, not a blank.</param>
/// <param name="Coverage">
/// The caveat qualifying the figure directly, or the empty string when the window is fully
/// covered. <b>This one belongs on screen</b>, unlike <see cref="Hint"/>: "30 of 31 days
/// recorded" is the difference between a real total and a partial one (rule 6).
/// </param>
/// <param name="Hint">
/// What the figure is and is not, in two clauses, an estimate at published rates, and not
/// the charge. Shown on hover where a head has one (issue #181); a head with nowhere to put
/// a card still has somewhere to put a sentence, because the caveat is the point of it.
/// </param>
/// <param name="ValueTileLabel">
/// The value tile's name while this window is off-plan. The "not money charged" framing the
/// tile normally carries is only true of plan usage, so the label flips with the state rather
/// than being decided beside the tile (<see cref="PanelSections.Tiles"/>).
/// </param>
/// <param name="ValueTileNote">The caveat under that tile naming the mixture, or empty.</param>
public sealed record PanelOffPlanView(
    PanelOffPlanBannerView? Banner,
    string Heading,
    string Badge,
    string SpendLabel,
    string SpendValue,
    string Coverage,
    string Hint,
    string ValueTileLabel,
    string ValueTileNote);

/// <summary>
/// The live banner: what was observed, what the account setting allows the panel to say about
/// it, and where the reader can find the figure O-view will not state.
/// </summary>
/// <param name="Title">
/// The heading, which asserts a charge only where the account setting supports one, see
/// <see cref="Text.OffPlanText.OffPlanTitle"/> for the three states and why they are three
/// headings rather than one hedge.
/// </param>
/// <param name="Detail">
/// The line underneath: the observation, the setting, and when Claude Code last read it.
/// </param>
/// <param name="LinkLabel">
/// What to call the link on a head that can navigate. Every wording above ends by sending the
/// reader to Claude for the figure, so the instruction is the thing you click.
/// </param>
/// <param name="LinkUrl">
/// Where it goes. Carried beside the label rather than left to each head, and <b>shown
/// outright by a head that cannot open it</b>, a label with no affordance behind it is a dead
/// end, and the address is the whole of what it was offering.
/// </param>
public sealed record PanelOffPlanBannerView(
    string Title,
    string Detail,
    string LinkLabel,
    string LinkUrl);
