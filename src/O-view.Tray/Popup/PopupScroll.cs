using System.Windows;
using System.Windows.Controls;

namespace OView.Tray.Popup;

/// <summary>
/// Moves the panel's sections into a scrolling viewport, and back out again — the response to
/// a display too short for them even packed tight.
///
/// <para>Asked for exactly when <see cref="OView.App.Rendering.PanelFit"/> has had to cap the
/// window at the work area, so the sections past the cap can still be reached rather than
/// being clipped off the bottom of the screen. The Linux panel answers the same question with
/// <c>PanelContent.ScrollContent</c>; the bar itself is this window's
/// <c>PanelScrollViewer</c> style, drawn from the shared palette so the two heads show one
/// bar.</para>
///
/// <para><b>Why a panel with room does not simply always have one.</b> A viewport clips to its
/// own bounds and takes its own measure of what is inside it, so a panel laid out through one
/// is not pixel for pixel the panel laid out without one — and that would be a change to how
/// the panel looks on every display that already fits, arriving inside a change about the ones
/// that do not. The Linux head found the same thing one golden at a time. So a panel with room
/// keeps exactly the tree it has always had, and every existing render is unmoved.</para>
/// </summary>
internal sealed class PopupScroll
{
    private readonly Border _root;
    private readonly UIElement _body;
    private readonly ScrollViewer _viewport;

    /// <param name="root">The panel's outer border, whose single child the sections are.</param>
    /// <param name="body">The sections, as laid out when everything fits.</param>
    public PopupScroll(Border root, UIElement body)
    {
        _root = root;
        _body = body;

        // Built here rather than in the markup for the reason above: it must be possible for
        // the sections not to be inside it. The style is the window's, so the bar is themed
        // by the same dictionary PanelTheme writes the palette into.
        _viewport = new ScrollViewer { Style = (Style)root.FindResource("PanelScrollViewer") };
    }

    /// <summary>Whether the sections are currently in the viewport rather than straight in
    /// the border.</summary>
    public bool Scrolls => ReferenceEquals(_root.Child, _viewport);

    /// <summary>
    /// Puts the sections in the viewport, or takes them straight back into the border.
    ///
    /// <para>The mouse wheel comes with the viewport and needs no wiring: a
    /// <see cref="ScrollViewer"/> handles the wheel itself, and the sections contain nothing
    /// else that scrolls for it to be swallowed by.</para>
    /// </summary>
    public void ScrollContent(bool scrolls)
    {
        if (scrolls == Scrolls)
        {
            return;
        }

        // Detached from the old parent first: WPF refuses a child that still has one.
        _root.Child = null;

        if (scrolls)
        {
            _viewport.Content = _body;
            _root.Child = _viewport;
        }
        else
        {
            _viewport.Content = null;
            _root.Child = _body;
        }
    }

    /// <summary>
    /// Back to the top. A re-populate is a fresh open or a re-layout at a new density, and
    /// either way the header is where the panel should start — not wherever the reader had
    /// scrolled to the last time it was open.
    /// </summary>
    public void ScrollToTop() => _viewport.ScrollToTop();
}
