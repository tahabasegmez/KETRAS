using System.Numerics;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Ketras.Scene3D.Rendering;

/// <summary>Turns colour + lighting + fog into a brush/pen and caches it by quantised inputs.</summary>
internal sealed class ShadeCache
{
    /// <summary>Number of discrete steps a 0..1 lighting or fog term is rounded to.</summary>
    private const int Steps = 20;

    private readonly record struct BrushKey(uint Color, byte Light, byte Fog, byte Alpha);
    private readonly record struct PenKey(uint Color, byte Light, byte Fog, byte Alpha, ushort Width);

    private readonly Dictionary<BrushKey, IImmutableBrush> _brushes = new();
    private readonly Dictionary<PenKey, IPen> _pens = new();

    private Color _fogColor = Color.FromRgb(0x23, 0x25, 0x27);
    private float _ambient = 0.42f;

    /// <summary>Colour distant geometry converges to, so it dissolves into the sky instead of a hard edge.</summary>
    public Color FogColor
    {
        get => _fogColor;
        set
        {
            if (value == _fogColor)
            {
                return;
            }

            _fogColor = value;
            Clear();
        }
    }

    /// <summary>Fraction of a surface's colour that survives with no direct light on it.</summary>
    public float Ambient
    {
        get => _ambient;
        set
        {
            var clamped = Math.Clamp(value, 0f, 1f);
            if (Math.Abs(clamped - _ambient) < 0.001f)
            {
                return;
            }

            _ambient = clamped;
            Clear();
        }
    }

    /// <summary>Direction the light travels *from*, in world space. Normalised on assignment.</summary>
    public Vector3 LightDirection { get; private set; } = Vector3.Normalize(new Vector3(-0.42f, 0.78f, -0.46f));

    public void SetLightDirection(Vector3 direction)
    {
        if (direction.LengthSquared() < 1e-6f)
        {
            return;
        }

        LightDirection = Vector3.Normalize(direction);
    }

    /// <summary>Half-Lambert term: keeps a little bounce light instead of going flat black.</summary>
    public float LambertFor(Vector3 normal)
    {
        var lambert = Vector3.Dot(normal, LightDirection);
        return Math.Clamp(lambert * 0.5f + 0.5f, 0f, 1f);
    }

    public IImmutableBrush Brush(Color baseColor, float light, float fog, byte alpha = 255)
    {
        var key = new BrushKey(baseColor.ToUInt32(), Quantise(light), Quantise(fog), alpha);
        if (_brushes.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var brush = new ImmutableSolidColorBrush(Resolve(baseColor, key.Light, key.Fog, alpha));
        _brushes[key] = brush;
        return brush;
    }

    public IPen Pen(Color baseColor, double thickness, float light, float fog, byte alpha = 255)
    {
        // Thickness is quantised to 1/8 px so animated widths cannot grow the cache without bound.
        var widthKey = (ushort)Math.Clamp((int)Math.Round(thickness * 8d), 1, ushort.MaxValue);
        var key = new PenKey(baseColor.ToUInt32(), Quantise(light), Quantise(fog), alpha, widthKey);
        if (_pens.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var pen = new ImmutablePen(
            new ImmutableSolidColorBrush(Resolve(baseColor, key.Light, key.Fog, alpha)),
            widthKey / 8d,
            lineCap: PenLineCap.Round,
            lineJoin: PenLineJoin.Round);

        _pens[key] = pen;
        return pen;
    }

    private Color Resolve(Color baseColor, byte lightStep, byte fogStep, byte alpha)
    {
        var light = lightStep / (float)Steps;
        var fog = fogStep / (float)Steps;

        var intensity = _ambient + (1f - _ambient) * light;
        var r = baseColor.R * intensity;
        var g = baseColor.G * intensity;
        var b = baseColor.B * intensity;

        r += (_fogColor.R - r) * fog;
        g += (_fogColor.G - g) * fog;
        b += (_fogColor.B - b) * fog;

        return Color.FromArgb(alpha, ToByte(r), ToByte(g), ToByte(b));
    }

    private static byte Quantise(float value) =>
        (byte)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0f, 1f) * Steps), 0, Steps);

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);

    private void Clear()
    {
        _brushes.Clear();
        _pens.Clear();
    }
}
