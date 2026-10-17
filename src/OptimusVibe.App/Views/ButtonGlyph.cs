using System.Globalization;
using System.Windows;
using System.Windows.Media;
using OptimusVibe.App.Input;

namespace OptimusVibe.App.Views;

/// <summary>
/// An Xbox-style controller button drawn as vector shapes (crisp at any DPI): coloured A / B / X / Y discs, dark LB / RB / LT / RT
/// pills, Menu, View, D-pad and stick marks. They are hardware colours, so they are the same in every theme. Each carries a subtle
/// darker 1px ring so it still reads on light or gold surfaces. Hidden unless a gamepad is connected (<see cref="PadState"/>),
/// unless <see cref="Always"/> is set.
/// </summary>
public sealed class ButtonGlyph : FrameworkElement
{
    // design box: 32 units high; discs 32 wide, pills 52 wide
    private const double Box = 32;
    private const double PillWidth = 52;

    private static readonly Brush Dark = Frozen(0x3A, 0x3D, 0x44);
    private static readonly Brush DarkRing = Frozen(0x0E, 0x0F, 0x12);
    private static readonly Brush White = Frozen(0xFF, 0xFF, 0xFF);
    private static readonly Brush Ink = Frozen(0x1B, 0x1B, 0x1B);
    private static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(PadButton), typeof(ButtonGlyph),
        new FrameworkPropertyMetadata(PadButton.A, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(GlyphSize), typeof(ButtonGlyph),
        new FrameworkPropertyMetadata(GlyphSize.Medium, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AlwaysProperty = DependencyProperty.Register(
        nameof(Always), typeof(bool), typeof(ButtonGlyph), new PropertyMetadata(false, (d, _) => d.CoerceValue(VisibilityProperty)));

    static ButtonGlyph()
    {
        VisibilityProperty.OverrideMetadata(typeof(ButtonGlyph), new FrameworkPropertyMetadata(Visibility.Visible, null, CoerceVisibility));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(ButtonGlyph), new UIPropertyMetadata(false));
        FocusableProperty.OverrideMetadata(typeof(ButtonGlyph), new UIPropertyMetadata(false));
        VerticalAlignmentProperty.OverrideMetadata(typeof(ButtonGlyph), new FrameworkPropertyMetadata(VerticalAlignment.Center));
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(ButtonGlyph), new FrameworkPropertyMetadata(false));
    }

    public PadButton Kind { get => (PadButton)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public GlyphSize Size { get => (GlyphSize)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    /// <summary>Show even without a connected gamepad (the post-game prompt).</summary>
    public bool Always { get => (bool)GetValue(AlwaysProperty); set => SetValue(AlwaysProperty, value); }

    private static object CoerceVisibility(DependencyObject d, object value)
    {
        var g = (ButtonGlyph)d;
        return g.Always || PadState.GetIsConnected(g) ? value : Visibility.Collapsed;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == PadState.IsConnectedProperty) CoerceValue(VisibilityProperty);
    }

    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        CoerceValue(VisibilityProperty);
    }

    private double GlyphHeight => Size switch { GlyphSize.Small => 20, GlyphSize.Large => 32, _ => 26 };
    private bool IsPill => Kind is PadButton.LB or PadButton.RB or PadButton.LT or PadButton.RT;

    protected override Size MeasureOverride(Size availableSize) =>
        new(GlyphHeight * (IsPill ? PillWidth / Box : 1), GlyphHeight);

    protected override void OnRender(DrawingContext dc)
    {
        double s = GlyphHeight / Box;
        double hair = 1 / s; // one device-independent pixel, in design units
        dc.PushTransform(new ScaleTransform(s, s));
        switch (Kind)
        {
            case PadButton.A: Disc(dc, Rgb(0x3B, 0xB1, 0x43), "A", White, hair); break;
            case PadButton.B: Disc(dc, Rgb(0xE3, 0x34, 0x2F), "B", White, hair); break;
            case PadButton.X: Disc(dc, Rgb(0x2D, 0x7F, 0xF9), "X", White, hair); break;
            case PadButton.Y: Disc(dc, Rgb(0xFF, 0xB9, 0x00), "Y", Ink, hair); break;
            case PadButton.LB: Pill(dc, "LB", hair); break;
            case PadButton.RB: Pill(dc, "RB", hair); break;
            case PadButton.LT: Pill(dc, "LT", hair); break;
            case PadButton.RT: Pill(dc, "RT", hair); break;
            case PadButton.Menu: Menu(dc, hair); break;
            case PadButton.View: View(dc, hair); break;
            case PadButton.DPad: DPad(dc, hair); break;
            case PadButton.LStick: Stick(dc, "L", hair); break;
            case PadButton.RStick: Stick(dc, "R", hair); break;
        }
        dc.Pop();
    }

    // ---------------------------------------------------------------- shapes (design units, 32 high)

    private static Point C => new(Box / 2, Box / 2);

    private void Disc(DrawingContext dc, Color fill, string letter, Brush ink, double hair)
    {
        var ring = new Pen(new SolidColorBrush(Darken(fill, 0.45)), hair);
        ring.Freeze();
        dc.DrawEllipse(new SolidColorBrush(fill), ring, C, Box / 2 - hair / 2, Box / 2 - hair / 2);
        Letter(dc, letter, 19, ink, C);
    }

    private void Pill(DrawingContext dc, string text, double hair)
    {
        var ring = new Pen(DarkRing, hair);
        ring.Freeze();
        dc.DrawRoundedRectangle(Dark, ring, new Rect(hair / 2, 3 + hair / 2, PillWidth - hair, 26 - hair), 10, 10);
        Letter(dc, text, 15, White, new Point(PillWidth / 2, Box / 2));
    }

    private static void Back(DrawingContext dc, double hair)
    {
        var ring = new Pen(DarkRing, hair);
        ring.Freeze();
        dc.DrawEllipse(Dark, ring, C, Box / 2 - hair / 2, Box / 2 - hair / 2);
    }

    private static void Menu(DrawingContext dc, double hair)
    {
        Back(dc, hair);
        var pen = new Pen(White, 2.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        foreach (var y in new[] { 10.5, 16, 21.5 }) dc.DrawLine(pen, new Point(9.5, y), new Point(22.5, y));
    }

    private static void View(DrawingContext dc, double hair)
    {
        Back(dc, hair);
        var pen = new Pen(White, 1.9) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawRoundedRectangle(null, pen, new Rect(9.5, 9.5, 9.5, 9.5), 1.8, 1.8);       // behind
        dc.DrawRoundedRectangle(Dark, pen, new Rect(13, 13, 9.5, 9.5), 1.8, 1.8);          // in front, hides the overlap
    }

    private static void DPad(DrawingContext dc, double hair)
    {
        Back(dc, hair);
        dc.DrawRoundedRectangle(White, null, new Rect(7.5, 13.2, 17, 5.6), 1.8, 1.8);
        dc.DrawRoundedRectangle(White, null, new Rect(13.2, 7.5, 5.6, 17), 1.8, 1.8);
        dc.DrawEllipse(Dark, null, C, 2.2, 2.2);
    }

    private void Stick(DrawingContext dc, string side, double hair)
    {
        Back(dc, hair);
        var ring = new Pen(Frozen(0xB9, 0xC0, 0xCB), 1.6);
        ring.Freeze();
        dc.DrawEllipse(null, ring, C, 11.2, 11.2);
        dc.DrawEllipse(White, null, C, 7.4, 7.4);
        Letter(dc, side, 11, Ink, C);
    }

    private void Letter(DrawingContext dc, string text, double size, Brush brush, Point center)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2 + 0.4));
    }


    // ---------------------------------------------------------------- colours

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static Color Darken(Color c, double amount) =>
        Color.FromRgb((byte)(c.R * (1 - amount)), (byte)(c.G * (1 - amount)), (byte)(c.B * (1 - amount)));

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
}
