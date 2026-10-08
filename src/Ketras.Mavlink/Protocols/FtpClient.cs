using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Ketras.Mavlink.Messages;

namespace Ketras.Mavlink.Protocols;

/// <summary>MAVLink FTP failure, with the vehicle's error code when it sent one.</summary>
public sealed class FtpException(string message, int? errorCode = null) : Exception(message)
{
    /// <summary>MAVLink FTP error code from a NAK (e.g. 10 = file not found); null for timeouts.</summary>
    public int? ErrorCode { get; } = errorCode;
}

/// <summary>
/// MAVLink FTP client for reading files. Header layout, opcodes and error codes follow PX4's
/// mavlink_ftp.h; a request is resent unchanged (same sequence number) on timeout, which the server
/// answers with its previous reply. Sources in docs/reference.
/// </summary>
public sealed partial class FtpClient
{
    private const int PayloadLength = 251;
    private const int HeaderLength = 12;
    private const int DataMax = PayloadLength - HeaderLength;
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(1);
    private const int MaxRetries = 5;

    private const byte OpTerminateSession = 1;
    private const byte OpOpenFileRo = 4;
    private const byte OpReadFile = 5;
    private const byte RspAck = 128;
    private const byte RspNak = 129;
    private const byte ErrEof = 6;

    private readonly Func<IMavlinkMessage, ValueTask> _send;
    private readonly byte _targetSystem;
    private readonly byte _defaultComponent;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Channel<byte[]>? _inbox;
    private ushort _sequence;

    internal FtpClient(Func<IMavlinkMessage, ValueTask> send, byte targetSystem, byte defaultComponent)
    {
        _send = send;
        _targetSystem = targetSystem;
        _defaultComponent = defaultComponent;
    }

    /// <summary>Reads a whole file. Accepts "mftp://path", "mftp://[;comp=N]path" or a plain "/path".</summary>
    public async Task<byte[]> ReadFileAsync(string uri, IProgress<(long Read, long Size)>? progress = null, CancellationToken cancellation = default)
    {
        var (path, component) = ParseUri(uri, _defaultComponent);

        await _gate.WaitAsync(cancellation);
        _inbox = Channel.CreateUnbounded<byte[]>();
        byte session = 0;
        var opened = false;
        try
        {
            var pathBytes = Encoding.ASCII.GetBytes(path);
            var open = await RequestAsync(component, OpOpenFileRo, 0, 0, pathBytes, cancellation);
            session = open[2];
            opened = true;
            var size = BinaryPrimitives.ReadUInt32LittleEndian(open.AsSpan(HeaderLength, 4));

            var content = new MemoryStream((int)size);
            while (content.Length < size)
            {
                byte[] reply;
                try
                {
                    reply = await RequestAsync(component, OpReadFile, session, (uint)content.Length, [], cancellation, DataMax);
                }
                catch (FtpException nak) when (nak.ErrorCode == ErrEof)
                {
                    break;
                }

                int read = reply[4];
                if (read == 0)
                {
                    break;
                }

                content.Write(reply, HeaderLength, read);
                progress?.Report((content.Length, size));
            }

            return content.ToArray();
        }
        finally
        {
            if (opened)
            {
                await TerminateAsync(component, session);
            }

            _inbox = null;
            _gate.Release();
        }
    }

    internal void Handle(FileTransferProtocolMessage message)
    {
        _inbox?.Writer.TryWrite(message.Payload);
    }

    private async Task<byte[]> RequestAsync(byte component, byte opcode, byte session, uint offset, byte[] data, CancellationToken cancellation, int size = -1)
    {
        var sequence = ++_sequence;
        var payload = new byte[PayloadLength];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), sequence);
        payload[2] = session;
        payload[3] = opcode;
        payload[4] = (byte)(size >= 0 ? size : data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), offset);
        data.CopyTo(payload, HeaderLength);

        var request = new FileTransferProtocolMessage
        {
            TargetNetwork = 0,
            TargetSystem = _targetSystem,
            TargetComponent = component,
            Payload = payload
        };

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            await _send(request);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(ReplyTimeout);
            try
            {
                while (true)
                {
                    var reply = await _inbox!.Reader.ReadAsync(timeout.Token);
                    var replySequence = BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(0, 2));
                    if (replySequence != (ushort)(sequence + 1) || reply[5] != opcode)
                    {
                        continue;
                    }

                    if (reply[3] == RspAck)
                    {
                        return reply;
                    }

                    if (reply[3] == RspNak)
                    {
                        var code = reply[4] > 0 ? reply[HeaderLength] : 1;
                        throw new FtpException($"FTP opcode {opcode} refused with error {code}", code);
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
            }
        }

        throw new FtpException($"no FTP reply to opcode {opcode}");
    }

    private async Task TerminateAsync(byte component, byte session)
    {
        try
        {
            await RequestAsync(component, OpTerminateSession, session, 0, [], CancellationToken.None);
        }
        catch (FtpException)
        {
            // The server times out idle sessions on its own.
        }
    }

    /// <summary>"mftp://etc/x" becomes "/etc/x"; an optional "[;comp=N]" selects the component.</summary>
    internal static (string Path, byte Component) ParseUri(string uri, byte defaultComponent)
    {
        var path = uri.StartsWith("mftp://", StringComparison.OrdinalIgnoreCase) ? "/" + uri["mftp://".Length..] : uri;
        var component = defaultComponent;

        var match = ComponentPrefix().Match(path);
        if (match.Success)
        {
            component = byte.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            path = ComponentTag().Replace(path, string.Empty);
        }

        return (path, component);
    }

    [GeneratedRegex(@"^/??\[;comp=(\d+)\]")]
    private static partial Regex ComponentPrefix();

    [GeneratedRegex(@"\[;comp=\d+\]")]
    private static partial Regex ComponentTag();
}
