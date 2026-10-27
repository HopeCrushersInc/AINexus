using System.Windows;
using System.Windows.Media;

namespace AINexus.App;

public enum NexusAppearanceMode { Standard, Lcars, Retro }

public sealed record NexusAppearancePalette(string Background, string Panel, string Button, string Hover,
    string Accent, string Secondary, string Text, string Muted, string Border, string Selected,
    string FontFamily, double CornerRadius);

/// <summary>Appearance only. Selecting a style never changes workload, approval or runtime state.</summary>
public static class NexusAppearance
{
    public static NexusAppearanceMode ResolveSelection(NexusAppearanceMode requested, bool shiftHeld)
    {
        if (!Enum.IsDefined(requested)) throw new ArgumentOutOfRangeException(nameof(requested));
        return requested == NexusAppearanceMode.Lcars && shiftHeld ? NexusAppearanceMode.Retro : requested;
    }

    public static NexusAppearancePalette PaletteFor(NexusAppearanceMode mode) => mode switch
    {
        NexusAppearanceMode.Standard => new("#070B13", "#DE122132", "#122B3E", "#244A61",
            "#66C0F4", "#B291F2", "#E6F3FA", "#A3B8C9", "#366079", "#24445D", "Segoe UI", 3),
        NexusAppearanceMode.Lcars => new("#09090D", "#EB17141D", "#3A293A", "#61445B",
            "#FFBC75", "#C7A3E7", "#FFF1DD", "#C6B4CB", "#BF8DAF", "#694961", "Segoe UI", 18),
        NexusAppearanceMode.Retro => new("#03050A", "#D9080614", "#0B061B", "#21103B",
            "#00FFF0", "#F100EB", "#C9FFF7", "#A6B7D4", "#00BFB6", "#22213F", "Consolas", 0),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}

/// <summary>
/// Static, resolution-independent space backdrop. It owns no animation timer, worker,
/// bitmap, download or external asset. Normal WPF invalidation is its only rendering trigger.
/// </summary>
public sealed class NexusBackdrop : FrameworkElement
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(nameof(Mode),
        typeof(NexusAppearanceMode), typeof(NexusBackdrop),
        new FrameworkPropertyMetadata(NexusAppearanceMode.Standard, FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is NexusAppearanceMode mode && Enum.IsDefined(mode));

    public NexusAppearanceMode Mode
    {
        get => (NexusAppearanceMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public NexusBackdrop()
    {
        IsHitTestVisible = false;
        Focusable = false;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var width = RenderSize.Width;
        var height = RenderSize.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return;

        var retro = Mode == NexusAppearanceMode.Retro;
        var palette = NexusAppearance.PaletteFor(Mode);
        drawing.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
        drawing.DrawRectangle(Solid(palette.Background), null, new Rect(0, 0, width, height));

        var haze = Glow(retro ? Color.FromArgb(48, 235, 0, 138) : Color.FromArgb(20, 123, 65, 170));
        drawing.DrawEllipse(haze, null, new Point(width * .10, height * .13), width * .36, height * .38);
        var coolHaze = Glow(retro ? Color.FromArgb(24, 0, 224, 221) : Color.FromArgb(13, 42, 122, 160));
        drawing.DrawEllipse(coolHaze, null, new Point(width * .87, height * .82), width * .35, height * .31);

        // A deterministic integer sequence keeps stars fixed across repaints and resizes.
        var starBrush = Solid(retro ? "#A8CEF4F3" : "#527E9AAB");
        var brightStar = Solid(retro ? "#C9FFFFFF" : "#798EB4CB");
        for (var index = 0; index < 110; index++)
        {
            var x = ((index * 367 + 53) % 997) / 997d * width;
            var y = ((index * 587 + 131) % 991) / 991d * height;
            var radius = index % 17 == 0 ? .95 : .45;
            drawing.DrawEllipse(index % 17 == 0 ? brightStar : starBrush, null, new Point(x, y), radius, radius);
        }

        var center = new Point(width * .82, height * .76);
        var radiusBase = Math.Min(width * .125, height * .17);
        drawing.PushOpacity(retro ? 1 : Mode == NexusAppearanceMode.Lcars ? .28 : .66);
        DrawBlackHole(drawing, center, radiusBase, retro);
        drawing.Pop();

        if (retro)
        {
            var scanLine = new Pen(Solid("#1600B8C9"), .6);
            scanLine.Freeze();
            for (double y = 1; y < height; y += 4)
                drawing.DrawLine(scanLine, new Point(0, y), new Point(width, y));
        }
        drawing.Pop();
    }

    private static void DrawBlackHole(DrawingContext drawing, Point center, double radius, bool retro)
    {
        // The broad disk sits behind the event horizon, as in the original Nexus1 reference.
        var disk = new RadialGradientBrush
        {
            Center = new Point(.5, .5), GradientOrigin = new Point(.5, .5), RadiusX = .5, RadiusY = .5,
            GradientStops = new GradientStopCollection
            {
                new(Color.FromArgb(0, 105, 0, 230), 0),
                new(Color.FromArgb(retro ? (byte)170 : (byte)100, 144, 38, 247), .33),
                new(Color.FromArgb(retro ? (byte)118 : (byte)70, 202, 41, 246), .66),
                new(Color.FromArgb(0, 70, 15, 170), 1)
            }
        };
        disk.Freeze();
        drawing.DrawEllipse(disk, null, center, radius * 2.65, radius * .76);

        for (var layer = 9; layer >= 0; layer--)
        {
            var pen = new Pen(Solid(Color.FromArgb((byte)(12 + layer * 2), 148, 55, 250)), .7);
            pen.Freeze();
            drawing.DrawEllipse(null, pen, center, radius * (1.42 + layer * .12), radius * (.34 + layer * .037));
        }

        drawing.DrawEllipse(Glow(Color.FromArgb(105, 105, 30, 240)), null, center, radius * 1.17, radius * 1.17);
        var violetRim = new Pen(Solid(retro ? "#D07B32FF" : "#8E6E51D2"), 2.3);
        var cyanRim = new Pen(Solid(retro ? "#F000F5EC" : "#A15DBFDA"), 1.2);
        violetRim.Freeze(); cyanRim.Freeze();
        drawing.DrawEllipse(Solid("#010207"), violetRim, center, radius * 1.015, radius * 1.015);
        drawing.DrawEllipse(null, cyanRim, new Point(center.X - .6, center.Y + .6), radius, radius);
    }

    private static SolidColorBrush Solid(string color) => Solid((Color)ColorConverter.ConvertFromString(color));
    private static SolidColorBrush Solid(Color color)
    {
        var brush = new SolidColorBrush(color); brush.Freeze(); return brush;
    }
    private static RadialGradientBrush Glow(Color color)
    {
        var brush = new RadialGradientBrush(color, Color.FromArgb(0, color.R, color.G, color.B));
        brush.Freeze(); return brush;
    }
}
