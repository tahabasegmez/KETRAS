using System.Buffers.Binary;
using System.IO.Compression;

namespace Ketras.Tests.Support;

/// <summary>
/// Writes a small GeoTIFF laid out like a Copernicus DEM cell: classic little-endian TIFF, float32, internal tiles,
/// DEFLATE with the floating-point predictor (3), PixelIsPoint tiepoint at the north-west pixel centre.
/// </summary>
internal static class SyntheticGeoTiff
{
    public static void Write(string path, int width, int height, int tile, double northLatitude, double westLongitude, double step, Func<int, int, float> heightAt)
    {
        var tilesAcross = (width + tile - 1) / tile;
        var tilesDown = (height + tile - 1) / tile;
        var tiles = new List<byte[]>();
        for (var ty = 0; ty < tilesDown; ty++)
        {
            for (var tx = 0; tx < tilesAcross; tx++)
            {
                tiles.Add(Compress(EncodeTile(tile, (r, c) =>
                {
                    var row = (ty * tile) + r;
                    var column = (tx * tile) + c;
                    return row < height && column < width ? heightAt(row, column) : 0;
                })));
            }
        }

        using var file = new MemoryStream();
        var writer = new BinaryWriter(file);
        writer.Write("II"u8);
        writer.Write((ushort)42);
        writer.Write(0u); // IFD offset, patched below

        var tileOffsets = new List<uint>();
        foreach (var data in tiles)
        {
            tileOffsets.Add((uint)file.Position);
            writer.Write(data);
        }

        var scaleOffset = (uint)file.Position;
        foreach (var value in new[] { step, step, 0.0 })
        {
            writer.Write(value);
        }

        var tieOffset = (uint)file.Position;
        foreach (var value in new[] { 0.0, 0.0, 0.0, westLongitude, northLatitude, 0.0 })
        {
            writer.Write(value);
        }

        var offsetsOffset = (uint)file.Position;
        tileOffsets.ForEach(writer.Write);
        var countsOffset = (uint)file.Position;
        tiles.ForEach(t => writer.Write((uint)t.Length));

        var ifd = (uint)file.Position;
        var entries = new List<(ushort Tag, ushort Type, uint Count, uint Value)>
        {
            (256, 4, 1, (uint)width),
            (257, 4, 1, (uint)height),
            (258, 3, 1, 32),
            (259, 3, 1, 8),
            (317, 3, 1, 3),
            (322, 4, 1, (uint)tile),
            (323, 4, 1, (uint)tile),
            (324, 4, (uint)tiles.Count, tiles.Count == 1 ? tileOffsets[0] : offsetsOffset),
            (325, 4, (uint)tiles.Count, tiles.Count == 1 ? (uint)tiles[0].Length : countsOffset),
            (339, 3, 1, 3),
            (33550, 12, 3, scaleOffset),
            (33922, 12, 6, tieOffset)
        };
        writer.Write((ushort)entries.Count);
        foreach (var (tag, type, count, value) in entries)
        {
            writer.Write(tag);
            writer.Write(type);
            writer.Write(count);
            writer.Write(value);
        }

        writer.Write(0u);
        writer.Flush();
        var bytes = file.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), ifd);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Floating-point predictor: per row, sample bytes split into planes (most significant first), then differenced.</summary>
    private static byte[] EncodeTile(int tile, Func<int, int, float> value)
    {
        var rowBytes = tile * 4;
        var raw = new byte[rowBytes * tile];
        var sample = new byte[4];
        for (var r = 0; r < tile; r++)
        {
            var row = raw.AsSpan(r * rowBytes, rowBytes);
            for (var c = 0; c < tile; c++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(sample, value(r, c));
                for (var k = 0; k < 4; k++)
                {
                    row[(k * tile) + c] = sample[3 - k];
                }
            }

            for (var i = rowBytes - 1; i > 0; i--)
            {
                row[i] = (byte)(row[i] - row[i - 1]);
            }
        }

        return raw;
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }
}
