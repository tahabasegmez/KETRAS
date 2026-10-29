using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Ketras.Geo;

namespace Ketras.Scene3D.Rendering;

/// <summary>
/// Builder: decodes a block of map tiles into one RGBA texture, north row first, for <see cref="TerrainView"/> to drape
/// over the terrain. Missing or undecodable tiles stay transparent, so the grey relief shows there.
/// </summary>
internal sealed class ImageryTexture
{
    private ImageryTexture(byte[] pixels, int width, int height, TileRange range)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
        var n = (double)(1 << range.Zoom);
        MinX = range.MinX / n;
        MinY = range.MinY / n;
        MaxX = (range.MinX + range.Columns) / n;
        MaxY = (range.MinY + range.Rows) / n;
    }

    /// <summary>RGBA bytes, unpremultiplied, rows top-down.</summary>
    public byte[] Pixels { get; }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>West edge as a mercator x fraction (<see cref="WebMercatorRules.X"/>).</summary>
    public double MinX { get; }

    /// <summary>North edge as a mercator y fraction (<see cref="WebMercatorRules.Y"/>).</summary>
    public double MinY { get; }

    /// <summary>East edge as a mercator x fraction.</summary>
    public double MaxX { get; }

    /// <summary>South edge as a mercator y fraction.</summary>
    public double MaxY { get; }

    /// <summary>
    /// Decodes <paramref name="imagery"/> with tiles of at most <see cref="WebMercatorRules.TilePixels"/> a side, smaller
    /// when the block would exceed <paramref name="maxSize"/>.
    /// </summary>
    public static ImageryTexture Build(SceneImagery imagery, int maxSize = 4096) => Build(imagery, maxSize, Decode);

    /// <summary>
    /// As <see cref="Build(SceneImagery, int)"/> with a given decoder: encoded bytes and an edge length in, RGBA bytes
    /// of that square out, or null when the tile cannot be read.
    /// </summary>
    internal static ImageryTexture Build(SceneImagery imagery, int maxSize, Func<byte[], int, byte[]?> decode)
    {
        var range = imagery.Range;
        var tile = Math.Clamp(maxSize / Math.Max(range.Columns, range.Rows), 1, WebMercatorRules.TilePixels);
        var width = range.Columns * tile;
        var height = range.Rows * tile;
        var pixels = new byte[width * height * 4];
        var rowBytes = tile * 4;
        foreach (var source in imagery.Tiles)
        {
            var column = source.X - range.MinX;
            var row = source.Y - range.MinY;
            if (column < 0 || row < 0 || column >= range.Columns || row >= range.Rows || decode(source.Encoded, tile) is not { } decoded
                || decoded.Length != rowBytes * tile)
            {
                continue;
            }

            for (var y = 0; y < tile; y++)
            {
                Buffer.BlockCopy(decoded, y * rowBytes, pixels, ((((row * tile) + y) * width) + (column * tile)) * 4, rowBytes);
            }
        }

        return new ImageryTexture(pixels, width, height, range);
    }

    private static byte[]? Decode(byte[] encoded, int size)
    {
        try
        {
            using var stream = new MemoryStream(encoded, writable: false);
            using var decoded = Bitmap.DecodeToWidth(stream, size, BitmapInterpolationMode.MediumQuality);
            if (decoded.PixelSize.Width != size || decoded.PixelSize.Height != size)
            {
                return null;
            }

            using var target = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
            using var buffer = target.Lock();
            decoded.CopyPixels(buffer, AlphaFormat.Unpremul);
            var rowBytes = size * 4;
            var pixels = new byte[rowBytes * size];
            for (var y = 0; y < size; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(buffer.Address + (y * buffer.RowBytes), pixels, y * rowBytes, rowBytes);
            }

            return pixels;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or NotSupportedException)
        {
            return null;
        }
    }
}
