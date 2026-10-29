using System.Text;

namespace Ketras.Mavlink.Messages;

/// <summary>MAVLink char[] fields: zero-padded, not necessarily zero-terminated when full.</summary>
internal static class MavlinkText
{
    /// <summary>Text up to the first zero byte, or the whole field when it is full.</summary>
    public static string Read(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? field : field[..end]);
    }

    /// <summary>Writes as much of the text as fits; the rest of the field stays zero.</summary>
    public static void Write(string? text, Span<byte> field)
    {
        field.Clear();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        bytes.AsSpan(0, Math.Min(bytes.Length, field.Length)).CopyTo(field);
    }
}
