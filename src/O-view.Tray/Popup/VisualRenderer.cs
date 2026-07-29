using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Size = System.Windows.Size;

namespace OView.Tray.Popup;

/// <summary>
/// Lays out a detached visual and renders it to a bitmap.
///
/// <para>This existed three times over — once each in <see cref="PopupWindow"/>,
/// <see cref="MenuWindow"/> and <see cref="DialogWindow"/>, the latter two byte-identical
/// — plus five more open-coded <see cref="RenderTargetBitmap"/> constructions in the
/// sample renderers (GitHub issue #53). The <c>Measure</c> → <c>Arrange</c> →
/// <c>UpdateLayout</c> order is load-bearing rather than incidental, and only one of the
/// three copies said so, which is exactly the shape of duplication that drifts.</para>
/// </summary>
internal static class VisualRenderer
{
    /// <summary>Verification renders are all taken at 2×, so text is legible when zoomed.</summary>
    public const double DefaultScale = 2.0;

    /// <summary>
    /// Renders a window's CONTENT, laid out to the window's declared width.
    ///
    /// <para>The content rather than the window itself: a <c>Window</c> that has never been
    /// shown has no meaningful rendered size of its own, and all three surfaces are a single
    /// rounded <c>Border</c> whose measured size is the thing worth capturing.</para>
    ///
    /// <para><paramref name="betweenPasses"/> runs after the first layout pass and is
    /// followed by a second. The detail panel needs it and the other two do not: its chart
    /// is a <c>Canvas</c>, so its <c>ActualWidth</c> is unknown until the tree has been laid
    /// out once, and drawing the graph before that silently falls back to a hard-coded
    /// default width and puts the week gridlines in the wrong places — which is the very
    /// fault the offscreen render exists to catch.</para>
    /// </summary>
    public static BitmapSource RenderContent(
        Window window, double scale = DefaultScale, Action? betweenPasses = null) =>
        Render((FrameworkElement)window.Content, scale, window.Width, betweenPasses);

    /// <summary>
    /// Renders any laid-out-on-demand element. Used for the sample surfaces that are not
    /// windows — a bare <c>Border</c> of stat tiles, or a <c>ToolTip</c>, which cannot be
    /// given a parent and so has to be measured standalone.
    /// </summary>
    public static BitmapSource Render(
        FrameworkElement root,
        double scale = DefaultScale,
        double availableWidth = double.PositiveInfinity,
        Action? betweenPasses = null)
    {
        void Layout()
        {
            root.Measure(new Size(availableWidth, double.PositiveInfinity));
            root.Arrange(new Rect(root.DesiredSize));
            root.UpdateLayout();
        }

        Layout();
        if (betweenPasses is not null)
        {
            betweenPasses();
            Layout();
        }

        return ToBitmap(root, root.ActualWidth, root.ActualHeight, scale);
    }

    /// <summary>
    /// Renders an already-sized visual. Separate from the two above because the backdrop
    /// composites build a <see cref="DrawingVisual"/>, which has no layout pass to run and
    /// no <c>ActualWidth</c> to read.
    /// </summary>
    public static BitmapSource ToBitmap(Visual visual, double width, double height, double scale)
    {
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width * scale),
            (int)Math.Ceiling(height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }
}
