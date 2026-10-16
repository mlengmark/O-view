using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;

namespace OView.Linux.Panel;

/// <summary>
/// Every gesture that moves the capped panel's content, and the thumb that says where in it
/// the reader is: the wheel, a drag of the thumb, and a click on the track above or below it.
///
/// <para>Separate from <see cref="PanelContent"/> only because that file is held under 600
/// lines; the panel still builds the viewport, the track and the thumb and hands them here.</para>
/// </summary>
internal sealed class PanelScroll
{
    /// <summary>Shortest the thumb may get. A thumb kept strictly proportional to very tall
    /// content shrinks to a few pixels, which is neither visible nor grabbable.</summary>
    internal const double MinThumbHeight = 24;

    /// <summary>
    /// How far one notch of the wheel moves the content. This is Avalonia's own figure for a
    /// viewport whose child does not scroll by logical item, and it is matched rather than
    /// chosen so that a turn this class handles and a turn the viewport handles itself move
    /// the content by the same amount — the user cannot tell which of the two answered, and
    /// that is the point.
    /// </summary>
    private const double WheelStepDip = 50;

    private readonly ScrollContentPresenter _viewport;
    private readonly Border _track;
    private readonly Border _thumb;

    /// <summary>Where a thumb drag began: the pointer's Y on the track, and the offset then.</summary>
    private double _dragFromY;
    private double _dragFromOffset;

    internal PanelScroll(Control panel, ScrollContentPresenter viewport, Border track, Border thumb)
    {
        _viewport = viewport;
        _track = track;
        _thumb = thumb;

        WireWheel(panel);
        WireBar();
    }

    /// <summary>
    /// The wheel, handled on the panel's outermost border rather than left to the viewport.
    ///
    /// <para><b>Why it cannot be left to the viewport, which is what this code used to say.</b>
    /// <see cref="ScrollContentPresenter"/> does answer a wheel turn, and it answers one raised
    /// anywhere inside itself — which is why the first wheel test passed and why this was
    /// believed to be working. But a real turn is not raised inside the viewport unless the
    /// pointer is over something that <i>paints</i>: Avalonia hit-tests a point against drawn
    /// content, and of everything between this panel's border and its sections — the frame grid,
    /// the viewport, the sections' stack — not one has a background. So a turn over the gap
    /// between two tiles, over a row of the Breakdown table, or over the panel's own padding
    /// lands on the panel's border, which is the viewport's <i>parent</i>; the event then bubbles
    /// outward, away from the viewport, and nothing moves. Only a turn that happened to be over
    /// a tile's fill or a line of glyphs ever reached it, which is why the panel read as having
    /// no wheel at all (Maximilian's report of 2026-10-16).</para>
    ///
    /// <para>Handled here, on the one element every point inside the panel does reach. A turn the
    /// viewport has already answered arrives marked handled and is left alone, so the two never
    /// both move the content.</para>
    /// </summary>
    private void WireWheel(Control panel) =>
        panel.AddHandler(InputElement.PointerWheelChangedEvent, (object? _, PointerWheelEventArgs e) =>
        {
            // Nothing out of sight means nothing to scroll — and a panel that fits has no
            // viewport at all, so a turn over it must stay the compositor's business.
            if (Hidden() <= 0)
            {
                return;
            }

            // Away from the user is a positive delta and moves the content's start back up,
            // so the offset falls. Clamped through the same step the thumb drags through, which
            // is what makes the wheel stop at the top and at the foot exactly where it does.
            ScrollTo(_viewport.Offset.Y - (e.Delta.Y * WheelStepDip));
            e.Handled = true;
        });

    /// <summary>
    /// The gestures the bar itself answers: dragging the thumb, and clicking the track above
    /// or below it to page. These two are the routes for a user whose pointer has no wheel.
    ///
    /// <para>Hand-drawn rather than Avalonia's <c>ScrollBar</c> because this head registers no
    /// Avalonia theme: a templated control arrives here with no template, so the toolkit's own
    /// bar would not draw at all, and every other piece of this panel is built from the same
    /// borders and text blocks.</para>
    /// </summary>
    private void WireBar()
    {
        _thumb.PointerPressed += (_, e) =>
        {
            _dragFromY = e.GetPosition(_track).Y;
            _dragFromOffset = _viewport.Offset.Y;
            e.Pointer.Capture(_thumb);
            e.Handled = true;        // not a page click as well
        };

        _thumb.PointerMoved += (_, e) =>
        {
            if (!ReferenceEquals(e.Pointer.Captured, _thumb))
            {
                return;
            }

            var travel = _track.Bounds.Height - _thumb.Bounds.Height;
            if (travel > 0)
            {
                ScrollTo(_dragFromOffset
                    + ((e.GetPosition(_track).Y - _dragFromY) * Hidden() / travel));
            }
        };

        _thumb.PointerReleased += (_, e) => e.Pointer.Capture(null);

        _track.PointerPressed += (_, e) =>
        {
            var above = e.GetPosition(_track).Y < _thumb.Bounds.Top;
            ScrollTo(_viewport.Offset.Y + (above ? -1 : 1) * _viewport.Viewport.Height);
        };
    }

    /// <summary>How much of the content is out of sight; zero when everything fits.</summary>
    private double Hidden() => Math.Max(0, _viewport.Extent.Height - _viewport.Viewport.Height);

    private void ScrollTo(double offsetY) =>
        _viewport.Offset = _viewport.Offset.WithY(Math.Clamp(offsetY, 0, Hidden()));

    /// <summary>
    /// Shows or hides the bar and puts the thumb where the offset says, after any change to
    /// what is on screen.
    ///
    /// <para>Making the bar visible narrows the content by its own width, which can reflow a
    /// wrapped line and make the content taller still — so this can run twice for one scroll.
    /// It cannot oscillate: a narrower column never shortens the content, so a bar that has
    /// appeared never has cause to go away again.</para>
    /// </summary>
    internal void UpdateBar()
    {
        var viewport = _viewport.Viewport.Height;
        var hidden = Hidden();

        _track.IsVisible = viewport > 0 && hidden > 0.5;
        if (!_track.IsVisible)
        {
            return;
        }

        var thumb = Math.Min(viewport, Math.Max(MinThumbHeight, viewport * viewport / _viewport.Extent.Height));
        var travel = Math.Max(0, viewport - thumb);

        _thumb.Height = thumb;
        _thumb.Margin = new Thickness(0, travel * Math.Clamp(_viewport.Offset.Y / hidden, 0, 1), 0, 0);
    }
}
