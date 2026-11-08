using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using OView.App.Rendering;
using OView.Presentation.Panel;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;
using Size = System.Windows.Size;

namespace OView.Tray.Popup;

/// <summary>
/// Draws the plan block's title and its closing off-plan row, and spaces its meters. The meters
/// themselves are filled through <see cref="PopupMeter"/>, as they always were.
///
/// <para><b>Flat on the panel, on purpose.</b> The block has no fill and no side padding: on a
/// surface where everything was grey the grey said nothing, so what remains to separate the
/// block from its neighbour is space and one soft line.</para>
///
/// <para>Every gap follows the density's <see cref="PanelDensity.SpacingScale"/> rather than a
/// compact figure of its own, so the block tightens by the proportion the rest of the panel
/// does.</para>
/// </summary>
internal sealed class PopupPlanBlock(
    FrameworkElement owner, Border block, LetterSpacedText title,
    FrameworkElement sessionMeter, FrameworkElement weeklyMeter,
    FrameworkElement offPlanRow, TextBlock rowTitle, TextBlock rowBadge, TextBlock rowCaption,
    TextBlock rowValue, TextBlock rowCoverage)
{
    /// <summary>Space above the block, from whatever sits over it: the header or a banner.</summary>
    private const double GapAboveDip = 8;

    public void Populate(PanelPlanBlockView plan)
    {
        title.Text = plan.Title.ToUpperInvariant();
        AutomationProperties.SetName(title, plan.Title);

        offPlanRow.Visibility = plan.OffPlanRow is null ? Visibility.Collapsed : Visibility.Visible;

        if (plan.OffPlanRow is { } row)
        {
            rowTitle.Text = row.Title;
            rowBadge.Text = row.Badge;
            rowCaption.Text = row.Caption;
            rowValue.Text = row.Value;

            // Stays on screen: it qualifies the figure directly above it, and only the
            // explanatory prose goes behind the hover (rule 6).
            PopupText.SetLine(rowCoverage, row.Coverage);

            offPlanRow.ToolTip = HoverCard.Text(owner, row.Hint);
            HoverCard.ApplyTiming(offPlanRow);
        }
    }

    public void ApplyDensity(PanelDensity density)
    {
        var scale = density.SpacingScale;
        var padding = PanelMetrics.BlockPaddingYDip * scale;

        block.Margin = new Thickness(0, GapAboveDip * scale, 0, 0);
        block.Padding = new Thickness(0, padding, 0, padding);

        sessionMeter.Margin = MeterMargin(density);
        weeklyMeter.Margin = MeterMargin(density);
    }

    /// <summary>The space around one meter, shared with the rows <see cref="PopupModelMeters"/> builds.</summary>
    public static Thickness MeterMargin(PanelDensity density) => new(
        0, PanelMetrics.MeterGapAboveDip * density.SpacingScale,
        0, PanelMetrics.MeterGapBelowDip * density.SpacingScale);
}

/// <summary>
/// A caption with letter spacing, which a WPF <c>TextBlock</c> cannot do: it has no tracking
/// setting at all.
///
/// <para>One <see cref="FormattedText"/> per letter rather than a <c>GlyphRun</c> with stretched
/// advances, so each letter keeps the panel's Display-mode hinting and matches the type beside
/// it. Kerning is the price, and at this size and spacing it does not show.</para>
///
/// <para>Spacing adds room after every letter including the last, so the first letter is
/// indented by the same amount for the caption to sit centred in whatever holds it.</para>
/// </summary>
public sealed class LetterSpacedText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(LetterSpacedText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner(typeof(LetterSpacedText));

    public static readonly DependencyProperty FontWeightProperty =
        TextElement.FontWeightProperty.AddOwner(typeof(LetterSpacedText));

    public static readonly DependencyProperty FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner(typeof(LetterSpacedText));

    public static readonly DependencyProperty ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner(typeof(LetterSpacedText));

    private FormattedText[] _letters = [];

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => (FontWeight)GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    private double Spacing => FontSize * PanelMetrics.LetterSpacingEm;

    private double Indent => FontSize * PanelMetrics.LetterSpacedIndentEm;

    protected override Size MeasureOverride(Size availableSize)
    {
        var typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal);
        var mode = TextOptions.GetTextFormattingMode(this);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        _letters = [.. Text.Select(letter => new FormattedText(
            letter.ToString(), CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
            typeface, FontSize, Foreground, null, mode, pixelsPerDip))];

        return new Size(
            Indent + _letters.Sum(letter => letter.WidthIncludingTrailingWhitespace + Spacing),
            _letters.Length == 0 ? 0 : _letters.Max(letter => letter.Height));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var x = Indent;
        foreach (var letter in _letters)
        {
            // Set again here rather than only at measure: a theme change repaints without
            // measuring, and DynamicResource delivers the new brush to this property alone.
            letter.SetForegroundBrush(Foreground);
            drawingContext.DrawText(letter, new System.Windows.Point(x, 0));
            x += letter.WidthIncludingTrailingWhitespace + Spacing;
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}
