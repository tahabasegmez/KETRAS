using Avalonia;
using Avalonia.Media;

namespace Ketras.Controls.Scene3D.Rendering;

/// <summary>
/// The sky, horizon haze and ground beyond the terrain data, shared by <see cref="TerrainView"/> and
/// <see cref="AirspaceViewport"/>: <see cref="Night"/> is the neutral dark scene of docs/UI_RULES.md for grey relief,
/// <see cref="Day"/> a blue sky fading to a pale horizon over map imagery.
/// </summary>
internal sealed class ScenePalette
{
    private ScenePalette(Color skyTop, Color skyMid, Color haze, Color groundNear, Color ground)
    {
        SkyTop = skyTop;
        SkyMid = skyMid;
        Haze = haze;
        Ground = ground;
        SkyBrush = Gradient((skyTop, 0), (skyMid, 0.55), (haze, 1));

        // Stops are bunched toward the top: a flat plane's distance falls off sharply near the horizon.
        GroundBrush = Gradient((haze, 0), (groundNear, 0.06), (ground, 0.20), (ground, 1));
    }

    /// <summary>Grey relief: the scene as it has always been.</summary>
    public static ScenePalette Night { get; } = new(
        Color.FromRgb(0x16, 0x17, 0x18), Color.FromRgb(0x1B, 0x1D, 0x1F), Color.FromRgb(0x23, 0x25, 0x27),
        Color.FromRgb(0x1F, 0x21, 0x23), Color.FromRgb(0x1B, 0x1D, 0x1F));

    /// <summary>Map imagery: blue sky, pale horizon, a muted earth tone beyond the data.</summary>
    public static ScenePalette Day { get; } = new(
        Color.FromRgb(0x2F, 0x6D, 0xB5), Color.FromRgb(0x6F, 0xA3, 0xD6), Color.FromRgb(0xCF, 0xDF, 0xEC),
        Color.FromRgb(0x9A, 0x9C, 0x92), Color.FromRgb(0x6B, 0x6A, 0x5E));

    /// <summary>The palette for a ground style.</summary>
    public static ScenePalette For(SceneGround ground) => ground == SceneGround.Imagery ? Day : Night;

    /// <summary>Top of the sky.</summary>
    public Color SkyTop { get; }

    /// <summary>The sky halfway up.</summary>
    public Color SkyMid { get; }

    /// <summary>The horizon, and the fog everything fades into.</summary>
    public Color Haze { get; }

    /// <summary>The ground beyond the terrain data.</summary>
    public Color Ground { get; }

    /// <summary>The flat backdrop's sky, top to horizon.</summary>
    public IBrush SkyBrush { get; }

    /// <summary>The flat backdrop's ground, horizon to bottom.</summary>
    public IBrush GroundBrush { get; }

    private static LinearGradientBrush Gradient(params (Color Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative)
        };
        foreach (var (color, offset) in stops)
        {
            brush.GradientStops.Add(new GradientStop(color, offset));
        }

        return brush;
    }
}
