using System.Buffers.Binary;
using System.IO.Compression;

namespace Ketras.Terrain;

/// <summary>
/// Reads one Copernicus DEM GeoTIFF: classic little-endian TIFF, float32, DEFLATE with the
/// floating-point predictor (3), square internal tiles, geographic tiepoint and pixel scale.
/// Only the full-resolution image (first IFD) is read, and only the internal tiles a window needs.
/// Anything else is rejected with <see cref="InvalidDataException"/>.
/// </summary>
public sealed class GeoTiffTileReader : IDisposable
{
    private const ushort TagImageWidth = 256;
    private const ushort TagImageLength = 257;
    private const ushort TagBitsPerSample = 258;
    private const ushort TagCompression = 259;
    private const ushort TagPredictor = 317;
    private const ushort TagTileWidth = 322;
    private const ushort TagTileLength = 323;
    private const ushort TagTileOffsets = 324;
    private const ushort TagTileByteCounts = 325;
    private const ushort TagSampleFormat = 339;
    private const ushort TagModelPixelScale = 33550;
    private const ushort TagModelTiepoint = 33922;

    private readonly FileStream _file;
    private readonly long[] _tileOffsets;
    private readonly long[] _tileByteCounts;
    private readonly Dictionary<int, float[]> _decoded = [];

    private GeoTiffTileReader(FileStream file, Dictionary<ushort, double[]> tags)
    {
        _file = file;
        Width = (int)Single(tags, TagImageWidth);
        Height = (int)Single(tags, TagImageLength);
        TileWidth = (int)Single(tags, TagTileWidth);
        TileHeight = (int)Single(tags, TagTileLength);

        if (Single(tags, TagBitsPerSample) != 32 || Single(tags, TagSampleFormat) != 3)
        {
            throw new InvalidDataException("expected 32-bit float samples");
        }

        if (Single(tags, TagCompression) is not (8 or 32946) || Single(tags, TagPredictor, 1) != 3)
        {
            throw new InvalidDataException("expected DEFLATE with the floating-point predictor");
        }

        var scale = tags[TagModelPixelScale];
        var tie = tags[TagModelTiepoint];
        LongitudeStep = scale[0];
        LatitudeStep = scale[1];
        // Copernicus files are RasterPixelIsPoint: the tiepoint is the centre of pixel (0,0).
        OriginLongitude = tie[3] - (tie[0] * LongitudeStep);
        OriginLatitude = tie[4] + (tie[1] * LatitudeStep);

        _tileOffsets = tags[TagTileOffsets].Select(v => (long)v).ToArray();
        _tileByteCounts = tags[TagTileByteCounts].Select(v => (long)v).ToArray();
    }

    /// <summary>Image width in pixels.</summary>
    public int Width { get; }

    /// <summary>Image height in pixels.</summary>
    public int Height { get; }

    /// <summary>Internal tile width.</summary>
    public int TileWidth { get; }

    /// <summary>Internal tile height.</summary>
    public int TileHeight { get; }

    /// <summary>Longitude of pixel column 0.</summary>
    public double OriginLongitude { get; }

    /// <summary>Latitude of pixel row 0 (the northern row).</summary>
    public double OriginLatitude { get; }

    /// <summary>Degrees between columns.</summary>
    public double LongitudeStep { get; }

    /// <summary>Degrees between rows.</summary>
    public double LatitudeStep { get; }

    /// <summary>Opens a file and reads its first IFD.</summary>
    public static GeoTiffTileReader Open(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var header = ReadBytes(file, 0, 8);
            if (header[0] != (byte)'I' || header[1] != (byte)'I' || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)) != 42)
            {
                throw new InvalidDataException($"{Path.GetFileName(path)}: not a little-endian classic TIFF");
            }

            var ifd = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            return new GeoTiffTileReader(file, ReadIfd(file, ifd));
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Height of pixel (row, column), row 0 north; NaN outside the image.</summary>
    public float Pixel(int row, int column)
    {
        if (row < 0 || column < 0 || row >= Height || column >= Width)
        {
            return float.NaN;
        }

        var tilesAcross = (Width + TileWidth - 1) / TileWidth;
        var index = ((row / TileHeight) * tilesAcross) + (column / TileWidth);
        if (!_decoded.TryGetValue(index, out var tile))
        {
            tile = DecodeTile(index);
            _decoded[index] = tile;
        }

        return tile[((row % TileHeight) * TileWidth) + (column % TileWidth)];
    }

    /// <summary>Nearest pixel's height; null when the point is more than half a pixel outside the image.</summary>
    public float? NearestHeight(double latitude, double longitude)
    {
        var row = (int)Math.Round((OriginLatitude - latitude) / LatitudeStep);
        var column = (int)Math.Round((longitude - OriginLongitude) / LongitudeStep);
        return row < 0 || column < 0 || row >= Height || column >= Width ? null : Pixel(row, column);
    }

    /// <summary>Bilinear height at a point; points up to one pixel beyond the last row or column (the gap
    /// to the neighbouring cell) use the edge pixels. Null further outside.</summary>
    public double? HeightAt(double latitude, double longitude)
    {
        var y = (OriginLatitude - latitude) / LatitudeStep;
        var x = (longitude - OriginLongitude) / LongitudeStep;
        if (y < 0 || x < 0 || y >= Height || x >= Width)
        {
            return null;
        }

        y = Math.Min(y, Height - 1);
        x = Math.Min(x, Width - 1);

        var r0 = Math.Min((int)y, Height - 2);
        var c0 = Math.Min((int)x, Width - 2);
        var fy = y - r0;
        var fx = x - c0;
        double h00 = Pixel(r0, c0), h01 = Pixel(r0, c0 + 1), h10 = Pixel(r0 + 1, c0), h11 = Pixel(r0 + 1, c0 + 1);
        return ((h00 * (1 - fx)) + (h01 * fx)) * (1 - fy) + ((h10 * (1 - fx)) + (h11 * fx)) * fy;
    }

    /// <summary>Closes the file.</summary>
    public void Dispose() => _file.Dispose();

    private float[] DecodeTile(int index)
    {
        var compressed = ReadBytes(_file, _tileOffsets[index], (int)_tileByteCounts[index]);
        var rowBytes = TileWidth * 4;
        var raw = new byte[rowBytes * TileHeight];
        using (var zlib = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress))
        {
            zlib.ReadExactly(raw);
        }

        // Floating-point predictor (TIFF Technical Note 3): each row holds the bytes of its samples split
        // into planes, most significant byte first, and horizontally differenced.
        var values = new float[TileWidth * TileHeight];
        var sample = new byte[4];
        for (var r = 0; r < TileHeight; r++)
        {
            var row = raw.AsSpan(r * rowBytes, rowBytes);
            for (var i = 1; i < rowBytes; i++)
            {
                row[i] = (byte)(row[i] + row[i - 1]);
            }

            for (var c = 0; c < TileWidth; c++)
            {
                for (var k = 0; k < 4; k++)
                {
                    sample[3 - k] = row[(k * TileWidth) + c];
                }

                values[(r * TileWidth) + c] = BinaryPrimitives.ReadSingleLittleEndian(sample);
            }
        }

        return values;
    }

    private static Dictionary<ushort, double[]> ReadIfd(FileStream file, long offset)
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian(ReadBytes(file, offset, 2));
        var entries = ReadBytes(file, offset + 2, count * 12);
        var tags = new Dictionary<ushort, double[]>();
        for (var i = 0; i < count; i++)
        {
            var entry = entries.AsSpan(i * 12, 12);
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(entry);
            var type = BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]);
            var n = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            var size = type switch { 3 => 2, 4 => 4, 12 => 8, 16 => 8, _ => 0 };
            if (size == 0)
            {
                continue;
            }

            var bytes = size * n <= 4
                ? entry.Slice(8, 4).ToArray()
                : ReadBytes(file, BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]), size * n);
            var values = new double[n];
            for (var v = 0; v < n; v++)
            {
                var span = bytes.AsSpan(v * size);
                values[v] = type switch
                {
                    3 => BinaryPrimitives.ReadUInt16LittleEndian(span),
                    4 => BinaryPrimitives.ReadUInt32LittleEndian(span),
                    12 => BinaryPrimitives.ReadDoubleLittleEndian(span),
                    _ => BinaryPrimitives.ReadUInt64LittleEndian(span)
                };
            }

            tags[tag] = values;
        }

        return tags;
    }

    private static double Single(Dictionary<ushort, double[]> tags, ushort tag, double? fallback = null) =>
        tags.TryGetValue(tag, out var values) && values.Length > 0 ? values[0]
        : fallback ?? throw new InvalidDataException($"TIFF tag {tag} missing");

    private static byte[] ReadBytes(FileStream file, long offset, int length)
    {
        var buffer = new byte[length];
        file.Seek(offset, SeekOrigin.Begin);
        file.ReadExactly(buffer);
        return buffer;
    }
}
