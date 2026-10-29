using Ketras.Mavlink.Messages;

namespace Ketras.Vehicles;

/// <summary>
/// Joins STATUSTEXT chunks into whole texts (MAVLink 2: one <c>id</c> per long text, <c>chunk_seq</c> from 0, the last
/// chunk shorter than 50 characters). PX4 splits every text longer than 50 characters this way
/// (mavlink/streams/STATUSTEXT.hpp). A text whose length is a multiple of 50 has no short chunk, so a pending text is
/// also finished by a chunk with another id or after <see cref="Timeout"/>. Not thread-safe.
/// </summary>
internal sealed class StatusTextAssembler
{
    /// <summary>A pending text older than this is finished as it is.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);

    private const int ChunkLength = 50;

    private ushort _id;
    private MavSeverity _severity;
    private int _nextChunk;
    private DateTime _startedUtc;
    private System.Text.StringBuilder? _pending;

    /// <summary>Takes a chunk; returns the texts it finishes (a pending one first).</summary>
    public IReadOnlyList<(MavSeverity Severity, string Text)> Add(StatustextMessage message, DateTime nowUtc)
    {
        var done = new List<(MavSeverity, string)>();
        var severity = (MavSeverity)message.Severity;
        if (_pending is not null && (message.Id != _id || message.ChunkSeq != _nextChunk))
        {
            done.Add(Finish());
        }

        if (message.Id == 0 || (_pending is null && message.ChunkSeq != 0))
        {
            done.Add((severity, message.Text));
            return done;
        }

        if (_pending is null)
        {
            _pending = new System.Text.StringBuilder();
            _id = message.Id;
            _severity = severity;
            _startedUtc = nowUtc;
            _nextChunk = 0;
        }

        _pending.Append(message.Text);
        _nextChunk++;
        if (message.Text.Length < ChunkLength)
        {
            done.Add(Finish());
        }

        return done;
    }

    /// <summary>Finishes a pending text that waited longer than <see cref="Timeout"/>.</summary>
    public (MavSeverity Severity, string Text)? Expire(DateTime nowUtc) =>
        _pending is not null && nowUtc - _startedUtc > Timeout ? Finish() : null;

    private (MavSeverity, string) Finish()
    {
        var text = _pending!.ToString();
        _pending = null;
        return (_severity, text);
    }
}
