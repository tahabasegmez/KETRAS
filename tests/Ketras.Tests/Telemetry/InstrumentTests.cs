using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Ketras.Telemetry;

namespace Ketras.Tests.Telemetry;

[Collection(AvaloniaCollection.Name)]
public sealed class InstrumentTests
{
    [Fact]
    public void Values_map_onto_the_dial_and_stop_at_its_ends()
    {
        Assert.Equal(-150, DialScale.AngleFor(0, 0, 40, -150, 300));
        Assert.Equal(0, DialScale.AngleFor(20, 0, 40, -150, 300));
        Assert.Equal(150, DialScale.AngleFor(99, 0, 40, -150, 300));
        Assert.Equal(-150, DialScale.AngleFor(-5, 0, 40, -150, 300));
        Assert.Equal(0.25, DialScale.Fraction(-5, -10, 10));
        Assert.Equal([0.0, 5, 10, 15, 20], DialScale.Ticks(0, 20, 5));
        Assert.Equal(5, DialScale.NiceStep(40, 8));
        Assert.Equal(2, DialScale.NiceStep(10, 5));
        var (x, y) = DialScale.PointAt(0, 0, 10, 90);
        Assert.Equal((10, 0), (Math.Round(x, 9), Math.Round(y, 9)));
    }

    [Fact]
    public void Attitude_and_wind_follow_their_conventions()
    {
        Assert.Equal(0.5, AttitudeIndicator.HorizonOffset(15));
        Assert.Equal(-1.5, AttitudeIndicator.HorizonOffset(-80));
        Assert.Equal(("274°", "000°", "359°", "---°"), (AttitudeIndicator.HeadingText(273.6), AttitudeIndicator.HeadingText(359.7), AttitudeIndicator.HeadingText(-1), AttitudeIndicator.HeadingText(null)));
        Assert.Equal(("Faint", "Text", "Warning", "Error"), (WindIndicator.ArrowToken(0), WindIndicator.ArrowToken(6), WindIndicator.ArrowToken(12), WindIndicator.ArrowToken(20)));
    }

    [AvaloniaFact]
    public void The_horizon_is_square_and_draws_with_and_without_an_attitude()
    {
        var with = new AttitudeIndicator { Roll = 20, Pitch = -5, Heading = 90 };
        var without = new AttitudeIndicator();
        var wind = new WindIndicator { FromDegrees = 220, SpeedMps = 6 };
        var panel = new StackPanel { Width = 150, Children = { with, without, wind } };
        var window = new Window { Content = panel, Width = 300, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.Equal((150, 150), (with.Bounds.Width, with.Bounds.Height));

        // Drawing runs every Render path against the headless drawing context.
        foreach (var control in panel.Children)
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize(150, 150));
            bitmap.Render(control);
        }

        window.Close();
    }
}
