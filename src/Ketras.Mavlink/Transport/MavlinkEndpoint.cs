using System.Globalization;

namespace Ketras.Mavlink.Transport;

/// <summary>
/// Parses a connection address into a transport:
/// <c>udp://:14550</c> (listen), <c>udp://host:port</c> (fixed peer), <c>tcp://host:port</c>,
/// <c>serial:COM5:57600</c>.
/// </summary>
internal static class MavlinkEndpoint
{
    /// <summary>Creates the transport an address names; throws <see cref="FormatException"/> on anything else.</summary>
    public static IMavlinkTransport Create(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var text = address.Trim();

        if (text.StartsWith("serial:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = text.Split(':');
            if (parts.Length != 3 || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var baud))
            {
                throw new FormatException($"Expected serial:<port>:<baud>, got '{address}'.");
            }

            return new SerialTransport(parts[1], baud);
        }

        var normalized = text.Replace("udp://:", "udp://0.0.0.0:", StringComparison.OrdinalIgnoreCase);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || uri.Port <= 0)
        {
            throw new FormatException($"Unrecognised MAVLink address '{address}'.");
        }

        return uri.Scheme.ToLowerInvariant() switch
        {
            "udp" when uri.Host is "0.0.0.0" => UdpTransport.Listen(uri.Port),
            "udp" => UdpTransport.Connect(uri.Host, uri.Port),
            "tcp" => new TcpTransport(uri.Host, uri.Port),
            _ => throw new FormatException($"Unsupported scheme in '{address}'.")
        };
    }
}
