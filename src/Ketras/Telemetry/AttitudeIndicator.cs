using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Ketras.Telemetry;

/// <summary>
/// Attitude: a square artificial horizon with rounded corners. The horizon moves with pitch (<see cref="DegreesShown"/>
/// from the centre to the edge) and turns with roll under a fixed aircraft symbol; a bank scale with a roll pointer
/// sits along the top and the heading in a small window at the bottom. Colours and fonts come from Ketras.Theme; without
/// an attitude the face is plain.
/// </summary>
public sealed class AttitudeIndicator : Control
{
    /// <summary>Defines <see cref="Roll"/>.</summary>
    public static readonly StyledProperty<double?> RollProperty = AvaloniaProperty.Register<AttitudeIndicator, double?>(nameof(Roll));

    /// <summary>Defines <see cref="Pitch"/>.</summary>
    public static readonly StyledProperty<double?> PitchProperty = AvaloniaProperty.Register<AttitudeIndicator, double?>(nameof(Pitch));

    /// <summary>Defines <see cref="Heading"/>.</summary>
    public static readonly StyledProperty<double?> HeadingProperty = AvaloniaProperty.Register<AttitudeIndicator, double?>(nameof(Heading));

    /// <summary>Pitch from the centre to the edge of the horizon, degrees.</summary>
    public const double DegreesShown = 30;

    private const double DefaultSize = 120;

    static AttitudeIndicator() => AffectsRender<AttitudeIndicator>(RollProperty, PitchProperty, HeadingProperty);

    /// <summary>Roll, degrees, positive right wing down; null when unknown.</summary>
    public double? Roll
    {
        get => GetValue(RollProperty);
        set => SetValue(RollProperty, value);
    }

    /// <summary>Pitch, degrees, positive nose up; null when unknown.</summary>
    public double? Pitch
    {
        get => GetValue(PitchProperty);
        set => SetValue(PitchProperty, value);
    }

    /// <summary>Heading, 0…360 degrees; null when unknown.</summary>
    public double? Heading
    {
        get => GetValue(HeadingProperty);
        set => SetValue(HeadingProperty, value);
    }

    /// <summary>How far the horizon line sits below the centre for a pitch, as a share of the half height (clamped at ±1.5).</summary>
    internal static double HorizonOffset(double pitch) => Math.Clamp(pitch / DegreesShown, -1.5, 1.5);

    /// <summary>The heading window's text: three digits and a degree sign, or "---°".</summary>
    internal static string HeadingText(double? heading) =>
        heading is { } h ? string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Round(((h % 360) + 360) % 360) % 360:000}°") : "---°";

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? DefaultSize : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? width : availableSize.Height;
        var side = Math.Min(width, height);
        return new Size(side, side);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var side = Math.Min(Bounds.Width, Bounds.Height);
        if (side <= 0)
        {
            return;
        }

        var frame = new Rect((Bounds.Width - side) / 2, (Bounds.Height - side) / 2, side, side);
        var center = frame.Center;
        var radius = side / 2;
        using (context.PushClip(new RoundedRect(frame, 4)))
        {
            DrawHorizon(context, center, radius);
            DrawBankScale(context, center, radius);
            DrawSymbol(context, center, radius);
            DrawHeading(context, frame, radius);
        }

        context.DrawRectangle(null, new Pen(Brush("Border"), 1), frame.Deflate(0.5), 4, 4);
    }

    private void DrawHorizon(DrawingContext context, Point center, double radius)
    {
        if (Roll is not { } roll || Pitch is not { } pitch)
        {
            context.DrawRectangle(Brush("Input"), null, new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2));
            return;
        }

        var matrix = Matrix.CreateTranslation(-center.X, -center.Y) * Matrix.CreateRotation(-roll * Math.PI / 180) * Matrix.CreateTranslation(center.X, center.Y);
        using (context.PushTransform(matrix))
        {
            var horizon = center.Y + (HorizonOffset(pitch) * radius);
            var span = radius * 4;
            context.DrawRectangle(Brush("Sky"), null, new Rect(center.X - span, horizon - span, span * 2, span));
            context.DrawRectangle(Brush("Ground"), null, new Rect(center.X - span, horizon, span * 2, span));
            context.DrawLine(new Pen(Brush("TextStrong"), Math.Max(1, radius * 0.02)), new Point(center.X - span, horizon), new Point(center.X + span, horizon));

            // Pitch ladder every 10°, labelled at ±20°.
            var ladder = new Pen(Brush("Text"), 1);
            for (var mark = -20; mark <= 20; mark += 10)
            {
                if (mark == 0)
                {
                    continue;
                }

                var y = horizon - (mark / DegreesShown * radius);
                var half = radius * (mark % 20 == 0 ? 0.26 : 0.14);
                context.DrawLine(ladder, new Point(center.X - half, y), new Point(center.X + half, y));
                if (mark % 20 == 0)
                {
                    var label = Text(Math.Abs(mark).ToString(CultureInfo.InvariantCulture), Math.Max(7, radius * 0.13), "Text");
                    context.DrawText(label, new Point(center.X + half + 3, y - (label.Height / 2)));
                }
            }
        }
    }

    private void DrawBankScale(DrawingContext context, Point center, double radius)
    {
        var arc = radius * 0.86;
        var pen = new Pen(Brush("TextStrong"), 1);
        foreach (var bank in new[] { -60.0, -45, -30, -20, -10, 0, 10, 20, 30, 45, 60 })
        {
            var length = bank % 30 == 0 ? 0.1 : 0.05;
            var (x1, y1) = DialScale.PointAt(center.X, center.Y, arc, bank);
            var (x2, y2) = DialScale.PointAt(center.X, center.Y, arc * (1 - length), bank);
            context.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));
        }

        if (Roll is not { } roll)
        {
            return;
        }

        // The pointer turns with the horizon: it marks the sky's zero against the fixed scale.
        var (tx, ty) = DialScale.PointAt(center.X, center.Y, arc * 0.9, -roll);
        var (lx, ly) = DialScale.PointAt(center.X, center.Y, arc * 0.78, -roll - 4);
        var (rx, ry) = DialScale.PointAt(center.X, center.Y, arc * 0.78, -roll + 4);
        var pointer = new StreamGeometry();
        using (var path = pointer.Open())
        {
            path.BeginFigure(new Point(tx, ty), true);
            path.LineTo(new Point(lx, ly));
            path.LineTo(new Point(rx, ry));
            path.EndFigure(true);
        }

        context.DrawGeometry(Brush("Warning"), null, pointer);
    }

    private void DrawSymbol(DrawingContext context, Point center, double radius)
    {
        var pen = new Pen(Brush("Warning"), Math.Max(2, radius * 0.045), lineCap: PenLineCap.Round);
        context.DrawLine(pen, new Point(center.X - (radius * 0.5), center.Y), new Point(center.X - (radius * 0.16), center.Y));
        context.DrawLine(pen, new Point(center.X - (radius * 0.16), center.Y), new Point(center.X - (radius * 0.08), center.Y + (radius * 0.08)));
        context.DrawLine(pen, new Point(center.X + (radius * 0.16), center.Y), new Point(center.X + (radius * 0.08), center.Y + (radius * 0.08)));
        context.DrawLine(pen, new Point(center.X + (radius * 0.16), center.Y), new Point(center.X + (radius * 0.5), center.Y));
        context.DrawEllipse(Brush("Warning"), null, center, radius * 0.035, radius * 0.035);
    }

    private void DrawHeading(DrawingContext context, Rect frame, double radius)
    {
        var text = Text(HeadingText(Heading), Math.Max(8, radius * 0.17), "TextStrong", FontWeight.SemiBold);
        const double padX = 5;
        var box = new Rect(frame.Center.X - (text.Width / 2) - padX, frame.Bottom - text.Height - 6, text.Width + (2 * padX), text.Height + 2);
        context.DrawRectangle(Brush("Overlay"), new Pen(Brush("BorderStrong"), 1), box, 2, 2);
        context.DrawText(text, new Point(box.X + padX, box.Y + 1));
    }

    private IBrush Brush(string token) =>
        this.TryFindResource($"Brush.{token}", ActualThemeVariant, out var resource) && resource is IBrush brush ? brush : Brushes.Gray;

    private FormattedText Text(string text, double size, string colourToken, FontWeight weight = FontWeight.Normal)
    {
        var family = this.TryFindResource("Font.Mono", out var resource) && resource is FontFamily found ? found : FontFamily.Default;
        return new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(family, FontStyle.Normal, weight), size, Brush(colourToken));
    }
}
