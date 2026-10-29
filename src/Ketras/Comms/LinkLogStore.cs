using Ketras.Notifications;

namespace Ketras.Comms;

/// <summary>One line of the vehicle link's log.</summary>
/// <param name="TimeUtc">When it happened.</param>
/// <param name="Severity">Its kind.</param>
/// <param name="Text">What happened.</param>
public sealed record LinkLogEntry(DateTime TimeUtc, NotificationSeverity Severity, string Text);

/// <summary>
/// Store: what the vehicle and the station said on the link that the operator should be able to read back: vehicle
/// messages (STATUSTEXT and events), health checks that block arming or a mode, and every command with its outcome.
/// Keeps the latest <see cref="Capacity"/> lines. Thread-safe; <see cref="Appended"/> is raised on the caller's thread.
/// </summary>
public sealed class LinkLogStore(TimeProvider time)
{
    /// <summary>Lines kept.</summary>
    public const int Capacity = 500;

    private readonly object _lock = new();
    private readonly LinkedList<LinkLogEntry> _entries = new();

    /// <summary>A line was added.</summary>
    public event Action<LinkLogEntry>? Appended;

    /// <summary>The lines, oldest first.</summary>
    public IReadOnlyList<LinkLogEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>Adds a line.</summary>
    public void Append(NotificationSeverity severity, string text)
    {
        var entry = new LinkLogEntry(time.GetUtcNow().UtcDateTime, severity, text);
        lock (_lock)
        {
            _entries.AddLast(entry);
            while (_entries.Count > Capacity)
            {
                _entries.RemoveFirst();
            }
        }

        Appended?.Invoke(entry);
    }
}
