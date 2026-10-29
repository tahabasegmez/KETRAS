using System.Globalization;
using System.Text;
using System.Text.Json;
using Ketras.Mavlink.Messages;
using SharpCompress.Compressors.Xz;

namespace Ketras.Mavlink.Protocols;

/// <summary>A metadata file the vehicle names but does not serve on board: an internet address and the CRC it is cached by.</summary>
/// <param name="Uri">The http(s) address.</param>
/// <param name="Crc">The file's CRC32 from the general metadata; 0 when unknown.</param>
public sealed record MetadataFallback(string Uri, uint Crc);

/// <summary>
/// Metadata files a vehicle describes itself with; a file is null when the vehicle does not provide it on board, and
/// then its fallback (the general file's <c>uriFallback</c>, or its <c>uri</c> when that is not on the vehicle) says
/// where the host may download it.
/// </summary>
public sealed record ComponentMetadata(string? ParametersJson, string? ActuatorsJson, IReadOnlyList<string> Notes)
{
    /// <summary>Where the parameter metadata can be downloaded when it is not on board.</summary>
    public MetadataFallback? ParametersFallback { get; init; }

    /// <summary>Where the actuator metadata can be downloaded when it is not on board.</summary>
    public MetadataFallback? ActuatorsFallback { get; init; }

    /// <summary>The events metadata (libevents JSON); null when the vehicle does not provide it on board.</summary>
    public string? EventsJson { get; init; }

    /// <summary>Where the events metadata can be downloaded when it is not on board.</summary>
    public MetadataFallback? EventsFallback { get; init; }
}

/// <summary>
/// Component metadata: requests COMPONENT_METADATA (397), reads the general file it points to, then the
/// parameter (type 1), events (type 4) and actuator (type 5) files, over MAVLink FTP. Files are decompressed when xz and
/// cached by their CRC, so a known firmware loads from disk. Internet fallbacks are left to the host.
/// </summary>
public sealed class ComponentMetadataClient
{
    private const int TypeParameter = 1;
    private const int TypeEvents = 4;
    private const int TypeActuators = 5;
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(5);

    private readonly CommandClient _commands;
    private readonly FtpClient _ftp;
    private volatile TaskCompletionSource<ComponentMetadataMessage>? _waiting;

    internal ComponentMetadataClient(CommandClient commands, FtpClient ftp)
    {
        _commands = commands;
        _ftp = ftp;
    }

    /// <summary>Loads parameter and actuator metadata; <paramref name="cacheDirectory"/> is created if needed.</summary>
    public async Task<ComponentMetadata> LoadAsync(string cacheDirectory, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(cacheDirectory);
        var notes = new List<string>();

        var waiting = new TaskCompletionSource<ComponentMetadataMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting = waiting;
        var request = await _commands.RequestMessageAsync(ComponentMetadataMessage.WireId, cancellation: cancellation);
        if (!request.IsAccepted)
        {
            notes.Add($"COMPONENT_METADATA request: {request.Outcome} {request.Reason}".Trim());
        }

        var finished = await Task.WhenAny(waiting.Task, Task.Delay(MetadataTimeout, cancellation));
        _waiting = null;
        if (finished != waiting.Task)
        {
            notes.Add("no COMPONENT_METADATA from the vehicle");
            return new ComponentMetadata(null, null, notes);
        }

        var general = waiting.Task.Result;
        var generalJson = await FetchAsync(general.Uri, general.FileCrc, "general", cacheDirectory, notes, cancellation);
        if (generalJson is null)
        {
            return new ComponentMetadata(null, null, notes);
        }

        string? parameters = null, actuators = null, events = null;
        MetadataFallback? parametersFallback = null, actuatorsFallback = null, eventsFallback = null;
        using var document = JsonDocument.Parse(generalJson);
        if (document.RootElement.TryGetProperty("metadataTypes", out var types))
        {
            foreach (var type in types.EnumerateArray())
            {
                var kind = type.GetProperty("type").GetInt32();
                if (kind is not (TypeParameter or TypeEvents or TypeActuators) || !type.TryGetProperty("uri", out var uriElement))
                {
                    continue;
                }

                var uri = uriElement.GetString() ?? string.Empty;
                var crc = type.TryGetProperty("fileCrc", out var crcElement) ? crcElement.GetUInt32() : 0;
                var fallbackUri = type.TryGetProperty("uriFallback", out var fallbackElement) ? fallbackElement.GetString() ?? string.Empty : string.Empty;
                var fallbackCrc = type.TryGetProperty("fileCrcFallback", out var fallbackCrcElement) ? fallbackCrcElement.GetUInt32() : 0;
                var onBoard = uri.StartsWith("mftp://", StringComparison.OrdinalIgnoreCase);
                var fallback = !onBoard && IsWeb(uri) ? new MetadataFallback(uri, crc)
                    : IsWeb(fallbackUri) ? new MetadataFallback(fallbackUri, fallbackCrc)
                    : null;
                string? json = null;
                if (onBoard)
                {
                    json = await FetchAsync(uri, crc, kind.ToString(CultureInfo.InvariantCulture), cacheDirectory, notes, cancellation);
                }
                else
                {
                    notes.Add($"metadata type {kind} is not on the vehicle ({uri})");
                }

                switch (kind)
                {
                    case TypeParameter:
                        parameters = json;
                        parametersFallback = json is null ? fallback : null;
                        break;
                    case TypeEvents:
                        events = json;
                        eventsFallback = json is null ? fallback : null;
                        break;
                    default:
                        actuators = json;
                        actuatorsFallback = json is null ? fallback : null;
                        break;
                }
            }
        }

        return new ComponentMetadata(parameters, actuators, notes)
        {
            ParametersFallback = parametersFallback, ActuatorsFallback = actuatorsFallback, EventsJson = events, EventsFallback = eventsFallback
        };
    }

    private static bool IsWeb(string uri) =>
        uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    internal void Handle(ComponentMetadataMessage message) => _waiting?.TrySetResult(message);

    private async Task<string?> FetchAsync(string uri, uint crc, string kind, string cacheDirectory, List<string> notes, CancellationToken cancellation)
    {
        var cacheFile = Path.Combine(cacheDirectory, $"{kind}-{crc:x8}.json");
        if (crc != 0 && File.Exists(cacheFile))
        {
            return await File.ReadAllTextAsync(cacheFile, cancellation);
        }

        try
        {
            var bytes = await _ftp.ReadFileAsync(uri, cancellation: cancellation);
            var text = DecodeFile(uri, bytes);
            if (crc != 0)
            {
                await File.WriteAllTextAsync(cacheFile, text, cancellation);
            }

            return text;
        }
        catch (Exception exception) when (exception is FtpException or IOException or InvalidDataException)
        {
            notes.Add($"{uri}: {exception.Message}");
            return null;
        }
    }

    /// <summary>A metadata file's text: xz-compressed when its name ends in <c>.xz</c>, else UTF-8.</summary>
    public static string DecodeFile(string uri, byte[] bytes) =>
        uri.EndsWith(".xz", StringComparison.OrdinalIgnoreCase) ? DecompressXz(bytes) : Encoding.UTF8.GetString(bytes);

    private static string DecompressXz(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var xz = new XZStream(input);
        using var reader = new StreamReader(xz, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
