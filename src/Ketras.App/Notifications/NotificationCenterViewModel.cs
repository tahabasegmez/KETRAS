using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.App.Common;
using Ketras.Station.Notifications;

namespace Ketras.App.Notifications;

/// <summary>
/// The right end of the status bar: the conditions that hold now, and the bell with the history. Events count as
/// unread until the history is opened; they stay tinted while it is open, and what arrives while it is open is read at
/// once. A condition's start is in the history but never unread, since it shows live. A row keeps its opened state
/// while the lists refresh.
/// </summary>
public sealed partial class NotificationCenterViewModel : ObservableObject, IDisposable
{
    private readonly NotificationStore _store;
    private readonly IUiDispatcher _ui;
    private DateTime _readUntilUtc = DateTime.MinValue;
    private bool _historyOpen;

    /// <summary>Follows <paramref name="store"/>.</summary>
    public NotificationCenterViewModel(NotificationStore store, IUiDispatcher ui)
    {
        _store = store;
        _ui = ui;
        _store.Raised += OnChanged;
        _store.Withdrawn += OnWithdrawn;
        Refresh();
    }

    /// <summary>The history, newest first, filtered by <see cref="OnlyProblems"/>.</summary>
    public ObservableCollection<NotificationRowViewModel> History { get; } = [];

    /// <summary>The conditions that hold now, most severe first.</summary>
    public ObservableCollection<NotificationRowViewModel> Conditions { get; } = [];

    /// <summary>Unread events.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    public partial int UnreadCount { get; private set; }

    /// <summary>The most severe unread event's severity.</summary>
    [ObservableProperty]
    public partial NotificationSeverity UnreadSeverity { get; private set; }

    /// <summary>The most severe condition's severity.</summary>
    [ObservableProperty]
    public partial NotificationSeverity ConditionSeverity { get; private set; }

    /// <summary>Show only warnings and errors in the history.</summary>
    [ObservableProperty]
    public partial bool OnlyProblems { get; set; }

    /// <summary>True while there are unread events.</summary>
    public bool HasUnread => UnreadCount > 0;

    /// <summary>True while a condition holds.</summary>
    public bool HasConditions => Conditions.Count > 0;

    /// <summary>True when the history shows nothing.</summary>
    public bool IsHistoryEmpty => History.Count == 0;

    /// <summary>The history was opened: everything in it is read, and stays tinted until it closes.</summary>
    [RelayCommand]
    private void MarkRead()
    {
        _historyOpen = true;
        Refresh();
    }

    /// <summary>The history closed: the unread tint goes.</summary>
    [RelayCommand]
    private void HistoryClosed()
    {
        _historyOpen = false;
        foreach (var row in History)
        {
            row.IsUnread = false;
        }
    }

    /// <summary>Empties the history; conditions that hold stay.</summary>
    [RelayCommand]
    private void ClearAll()
    {
        _store.ClearHistory();
        Refresh();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _store.Raised -= OnChanged;
        _store.Withdrawn -= OnWithdrawn;
    }

    partial void OnOnlyProblemsChanged(bool value) => Refresh();

    private void OnChanged(Notification notification) => _ui.Post(Refresh);

    private void OnWithdrawn(string source, string key) => _ui.Post(Refresh);

    private void Refresh()
    {
        var history = _store.History;
        var readBefore = _readUntilUtc;
        if (_historyOpen)
        {
            _readUntilUtc = history.FirstOrDefault()?.TimeUtc ?? _readUntilUtc;
        }

        // Rows already shown keep their state; a new row is unread when it came after the last look.
        var shown = History.ToDictionary<NotificationRowViewModel, Notification>(row => row.Notification, ReferenceEqualityComparer.Instance);
        Replace(History, history
            .Where(n => !OnlyProblems || n.Severity >= NotificationSeverity.Warning)
            .Select(n => shown.TryGetValue(n, out var row) ? row : new NotificationRowViewModel(n, n.Kind == NotificationKind.Event && n.TimeUtc > readBefore && !_historyOpen)));

        var expanded = Conditions.Where(row => row.IsExpanded).Select(row => (row.Notification.Source, row.Notification.Key)).ToHashSet();
        var conditions = _store.Conditions.OrderByDescending(n => n.Severity).ThenBy(n => n.TimeUtc).ToList();
        Replace(Conditions, conditions.Select(n => new NotificationRowViewModel(n) { IsExpanded = expanded.Contains((n.Source, n.Key)) }));

        var unread = history.Where(n => n.Kind == NotificationKind.Event && n.TimeUtc > _readUntilUtc).ToList();
        UnreadCount = unread.Count;
        UnreadSeverity = unread.Count > 0 ? unread.Max(n => n.Severity) : NotificationSeverity.Info;
        ConditionSeverity = conditions.Count > 0 ? conditions[0].Severity : NotificationSeverity.Info;
        OnPropertyChanged(nameof(HasConditions));
        OnPropertyChanged(nameof(IsHistoryEmpty));
    }

    private static void Replace(ObservableCollection<NotificationRowViewModel> target, IEnumerable<NotificationRowViewModel> rows)
    {
        var list = rows.ToList();
        if (list.SequenceEqual(target))
        {
            return;
        }

        target.Clear();
        foreach (var row in list)
        {
            target.Add(row);
        }
    }
}
