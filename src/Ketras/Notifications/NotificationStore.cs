namespace Ketras.Notifications;

/// <summary>How much attention a notification needs.</summary>
public enum NotificationSeverity
{
    /// <summary>Nothing to do.</summary>
    Info,

    /// <summary>Something the operator started completed.</summary>
    Success,

    /// <summary>It will not go as expected unless the operator acts.</summary>
    Warning,

    /// <summary>Something failed.</summary>
    Error
}

/// <summary>A moment or a state; see docs/UI_RULES.md "Notifications and live conditions".</summary>
public enum NotificationKind
{
    /// <summary>Happened once: shown as a toast and kept in the history.</summary>
    Event,

    /// <summary>Holds until withdrawn: shown as a live indicator; its start is kept in the history.</summary>
    Condition
}

/// <summary>One message for the operator.</summary>
/// <param name="Source">Who raised it, e.g. "Link" or a plugin's name.</param>
/// <param name="Severity">How much attention it needs.</param>
/// <param name="Title">Short headline.</param>
/// <param name="Detail">Longer text, if any.</param>
/// <param name="Kind">Event or condition.</param>
/// <param name="Key">A condition's key, unique within its source.</param>
/// <param name="TimeUtc">When it was raised.</param>
public sealed record Notification(
    string Source,
    NotificationSeverity Severity,
    string Title,
    string? Detail,
    NotificationKind Kind,
    string? Key,
    DateTime TimeUtc);

/// <summary>
/// Store of the operator's notifications: events, the conditions that hold now, and a bounded history. Thread-safe;
/// events are raised on the caller's thread.
/// </summary>
public sealed class NotificationStore(TimeProvider time)
{
    /// <summary>History entries kept.</summary>
    public const int HistoryLimit = 500;

    private readonly object _lock = new();
    private readonly Dictionary<(string Source, string Key), Notification> _conditions = [];
    private readonly LinkedList<Notification> _history = new();

    /// <summary>A notification was raised, or a condition updated.</summary>
    public event Action<Notification>? Raised;

    /// <summary>A condition ended: its source and key.</summary>
    public event Action<string, string>? Withdrawn;

    /// <summary>Conditions that hold now.</summary>
    public IReadOnlyList<Notification> Conditions
    {
        get
        {
            lock (_lock)
            {
                return [.. _conditions.Values];
            }
        }
    }

    /// <summary>History, newest first.</summary>
    public IReadOnlyList<Notification> History
    {
        get
        {
            lock (_lock)
            {
                return [.. _history];
            }
        }
    }

    /// <summary>Raises a one-off event.</summary>
    public void Notify(string source, NotificationSeverity severity, string title, string? detail = null)
    {
        var notification = new Notification(source, severity, title, detail, NotificationKind.Event, null, time.GetUtcNow().UtcDateTime);
        lock (_lock)
        {
            Remember(notification);
        }

        Raised?.Invoke(notification);
    }

    /// <summary>Raises a condition, or updates the one with this key; the history records only its start.</summary>
    public void SetCondition(string source, string key, NotificationSeverity severity, string title, string? detail = null)
    {
        var notification = new Notification(source, severity, title, detail, NotificationKind.Condition, key, time.GetUtcNow().UtcDateTime);
        lock (_lock)
        {
            if (_conditions.TryGetValue((source, key), out var current) && current with { TimeUtc = notification.TimeUtc } == notification)
            {
                return;
            }

            if (current is null)
            {
                Remember(notification);
            }

            _conditions[(source, key)] = notification;
        }

        Raised?.Invoke(notification);
    }

    /// <summary>Ends a condition; nothing happens when it does not hold.</summary>
    public void ClearCondition(string source, string key)
    {
        lock (_lock)
        {
            if (!_conditions.Remove((source, key)))
            {
                return;
            }
        }

        Withdrawn?.Invoke(source, key);
    }

    /// <summary>Ends every condition of <paramref name="source"/> whose key is not in <paramref name="keep"/>.</summary>
    public void ClearConditionsExcept(string source, IReadOnlySet<string> keep)
    {
        List<string> ended;
        lock (_lock)
        {
            ended = _conditions.Keys.Where(k => k.Source == source && !keep.Contains(k.Key)).Select(k => k.Key).ToList();
            foreach (var key in ended)
            {
                _conditions.Remove((source, key));
            }
        }

        foreach (var key in ended)
        {
            Withdrawn?.Invoke(source, key);
        }
    }

    /// <summary>Empties the history; conditions that hold stay.</summary>
    public void ClearHistory()
    {
        lock (_lock)
        {
            _history.Clear();
        }
    }

    private void Remember(Notification notification)
    {
        _history.AddFirst(notification);
        if (_history.Count > HistoryLimit)
        {
            _history.RemoveLast();
        }
    }
}
