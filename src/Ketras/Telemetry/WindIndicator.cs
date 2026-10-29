using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Ketras.Telemetry;

/// <summary>
/// A small compass with the wind as a flow arrow. <see cref="FromDegrees"/> is the meteorological direction the wind
/// comes from; the arrow points where it blows to. The arrow is neutral below a strong breeze (Beaufort 6,
/// 10.8 m/s), Warning from there and Error from a near gale (Beaufort 7, 13.9 m/s).
/// </summary>
public sealed class WindIndicator : Control
{
    /// <summary>Defines <see cref="FromDegrees"/>.</summary>
    public static readonly StyledProperty<double?> FromDegreesProperty = AvaloniaProperty.Register<WindIndicator, double?>(nameof(FromDegrees));

    /// <summary>Defines <see cref="SpeedMps"/>.</summary>
    public static readonly StyledProperty<double?> SpeedMpsProperty = AvaloniaProperty.Register<WindIndicator, double?>(nameof(SpeedMps));

    static WindIndicator() => AffectsRender<WindIndicator>(FromDegreesProperty, SpeedMpsProperty);

    /// <summary>Direction the wind comes from, 0…360; null when unknown.</summary>
    public double? FromDegrees
    {
        get => GetValue(FromDegreesProperty);
        set => SetValue(FromDegreesProperty, value);
    }

    /// <summary>Wind speed, m/s; null when unknown.</summary>
    public double? SpeedMps
    {
        get => GetValue(SpeedMpsProperty);
        set => SetValue(SpeedMpsProperty, value);
    }

    /// <summary>The colour token of the arrow for a wind speed.</summary>
    internal static string ArrowToken(double speed) => speed switch
    {
        <= 0 => "Faint",
        < 10.8 => "Text",
        < 13.9 => "Warning",
        _ => "Error"
    };

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Math.Min(double.IsInfinity(availableSize.Width) ? 72 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 72 : availableSize.Height);
        return new Size(side, side);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
        {
            return;
        }

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = (size / 2) - 2;
        var ring = new Pen(Brush("BorderStrong"), 1);
        context.DrawEllipse(null, ring, center, radius, radius);
        context.DrawEllipse(null, new Pen(Brush("BorderStrong"), 0.6), center, radius * 0.55, radius * 0.55);
        for (var angle = 0; angle < 360; angle += 30)
        {
            var cardinal = angle % 90 == 0;
            var (x1, y1) = DialScale.PointAt(center.X, center.Y, radius - 1, angle);
            var (x2, y2) = DialScale.PointAt(center.X, center.Y, radius - (cardinal ? 6 : 4), angle);
            context.DrawLine(new Pen(Brush("HoverBorder"), cardinal ? 1.2 : 0.7), new Point(x1, y1), new Point(x2, y2));
        }

        foreach (var (label, angle) in new[] { ("N", 0.0), ("E", 90), ("S", 180), ("W", 270) })
        {
            var text = Text(label, 11, "Faint", mono: false);
            var (x, y) = DialScale.PointAt(center.X, center.Y, radius - Math.Max(10, radius * 0.24), angle);
            context.DrawText(text, new Point(x - (text.Width / 2), y - (text.Height / 2)));
        }

        if (FromDegrees is not { } from || SpeedMps is not { } speed)
        {
            var none = Text("--", 11, "Faint", mono: true);
            context.DrawText(none, new Point(center.X - (none.Width / 2), center.Y - (none.Height / 2)));
            return;
        }

        var flow = (from + 180) % 360;
        var brush = Brush(ArrowToken(speed));
        var (hx, hy) = DialScale.PointAt(center.X, center.Y, radius - 6, flow);
        var (tx, ty) = DialScale.PointAt(center.X, center.Y, radius - 6, flow + 180);
        context.DrawLine(new Pen(brush, Math.Max(1.5, size * 0.03), lineCap: PenLineCap.Round), new Point(tx, ty), new Point(hx, hy));
        var head = Math.Max(size * 0.14, 6);
        var (lx, ly) = DialScale.PointAt(hx, hy, head, flow + 180 - 25);
        var (rx, ry) = DialScale.PointAt(hx, hy, head, flow + 180 + 25);
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(hx, hy), true);
            path.LineTo(new Point(lx, ly));
            path.LineTo(new Point(rx, ry));
            path.EndFigure(true);
        }

        context.DrawGeometry(brush, null, geometry);
        context.DrawEllipse(Brush("Overlay"), new Pen(brush, 1), center, size * 0.05, size * 0.05);
    }

    private IBrush Brush(string token) =>
        this.TryFindResource($"Brush.{token}", ActualThemeVariant, out var resource) && resource is IBrush brush ? brush : Brushes.Gray;

    private FormattedText Text(string text, double size, string token, bool mono)
    {
        var family = this.TryFindResource(mono ? "Font.Mono" : "Font.Ui", out var resource) && resource is FontFamily found ? found : FontFamily.Default;
        return new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(family), size, Brush(token));
    }
}
