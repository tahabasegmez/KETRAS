using System.Buffers.Binary;

namespace Ketras.Comms;

/// <summary>
/// Writes a telemetry log (.tlog): each record is an 8-byte big-endian timestamp in
/// microseconds since the Unix epoch, followed by one MAVLink frame. Readable by common
/// ground stations, MAVExplorer and PlotJuggler. Thread-safe.
/// </summary>
public sealed class TlogWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>Writes to <paramref name="stream"/>; it is disposed with the writer when <paramref name="ownsStream"/>.</summary>
    public TlogWriter(Stream stream, bool ownsStream = true)
    {
        _stream = stream;
        _ownsStream = ownsStream;
    }

    /// <summary>Creates the file (and its directory) and writes to it.</summary>
    public static TlogWriter Create(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return new TlogWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024));
    }

    /// <summary>Records written so far.</summary>
    public long Records { get; private set; }

    /// <summary>Appends one frame with its time. Throws <see cref="IOException"/> when the stream cannot be written.</summary>
    public void Write(DateTime timeUtc, ReadOnlySpan<byte> frame)
    {
        Span<byte> stamp = stackalloc byte[8];
        var micros = (ulong)(timeUtc - DateTime.UnixEpoch).Ticks / 10;
        BinaryPrimitives.WriteUInt64BigEndian(stamp, micros);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _stream.Write(stamp);
            _stream.Write(frame);
            Records++;
        }
    }

    /// <summary>Flushes and closes the log.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stream.Flush();
            if (_ownsStream)
            {
                _stream.Dispose();
            }
        }
    }
}

/// <summary>One record of a telemetry log.</summary>
/// <param name="TimeUtc">When the frame was recorded.</param>
/// <param name="Frame">The MAVLink frame bytes.</param>
public sealed record TlogRecord(DateTime TimeUtc, byte[] Frame);

/// <summary>Reads a telemetry log in the .tlog format (see <see cref="TlogWriter"/>).</summary>
public static class TlogReader
{
    private const int HeaderV2 = 10;
    private const int HeaderV1 = 6;
    private const int Checksum = 2;
    private const int Signature = 13;

    /// <summary>Every complete record in <paramref name="stream"/>; a truncated last record is ignored.</summary>
    public static IEnumerable<TlogRecord> Read(Stream stream)
    {
        var stamp = new byte[8];
        var header = new byte[HeaderV2];
        while (ReadExactly(stream, stamp) && ReadExactly(stream, header.AsSpan(0, 2)))
        {
            var time = DateTime.UnixEpoch.AddTicks((long)BinaryPrimitives.ReadUInt64BigEndian(stamp) * 10);
            var v2 = header[0] == 0xFD;
            var headerLength = v2 ? HeaderV2 : HeaderV1;
            if (!ReadExactly(stream, header.AsSpan(2, headerLength - 2)))
            {
                yield break;
            }

            var signed = v2 && (header[2] & 0x01) != 0;
            var frame = new byte[headerLength + header[1] + Checksum + (signed ? Signature : 0)];
            header.AsSpan(0, headerLength).CopyTo(frame);
            if (!ReadExactly(stream, frame.AsSpan(headerLength)))
            {
                yield break;
            }

            yield return new TlogRecord(time, frame);
        }
    }

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer[read..]);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }
}
