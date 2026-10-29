using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.Station.Notifications;
using Ketras.Utilities;

namespace Ketras.Notifications;

/// <summary>One toast: a notification row that can be dismissed.</summary>
public sealed partial class ToastViewModel(NotificationRowViewModel row, Action<ToastViewModel> dismiss)
{
    /// <summary>What it shows.</summary>
    public NotificationRowViewModel Row { get; } = row;

    /// <summary>Removes it before its time.</summary>
    [RelayCommand]
    private void Dismiss() => dismiss(this);
}

/// <summary>
/// The toasts over the work area: events only (conditions show live in the status bar), at most
/// <see cref="MaxVisible"/>, the oldest leaving first, each gone after its severity's time.
/// </summary>
public sealed class ToastsViewModel : IDisposable
{
    /// <summary>Most toasts shown at once.</summary>
    public const int MaxVisible = 3;

    private readonly NotificationStore _store;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly Dictionary<ToastViewModel, ITimer> _timers = [];

    /// <summary>Shows the events of <paramref name="store"/>.</summary>
    public ToastsViewModel(NotificationStore store, IUiDispatcher ui, TimeProvider time)
    {
        _store = store;
        _ui = ui;
        _time = time;
        _store.Raised += OnRaised;
    }

    /// <summary>The toasts shown, oldest first.</summary>
    public ObservableCollection<ToastViewModel> Toasts { get; } = [];

    /// <summary>How long a toast of this severity stays.</summary>
    public static TimeSpan DurationOf(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Success => TimeSpan.FromSeconds(3),
        NotificationSeverity.Warning => TimeSpan.FromSeconds(8),
        NotificationSeverity.Error => TimeSpan.FromSeconds(12),
        _ => TimeSpan.FromSeconds(4)
    };

    /// <inheritdoc />
    public void Dispose()
    {
        _store.Raised -= OnRaised;
        foreach (var timer in _timers.Values)
        {
            timer.Dispose();
        }

        _timers.Clear();
    }

    private void OnRaised(Notification notification)
    {
        if (notification.Kind == NotificationKind.Event)
        {
            _ui.Post(() => Show(notification));
        }
    }

    private void Show(Notification notification)
    {
        var toast = new ToastViewModel(new NotificationRowViewModel(notification), Remove);
        Toasts.Add(toast);
        _timers[toast] = _time.CreateTimer(_ => _ui.Post(() => Remove(toast)), null, DurationOf(notification.Severity), Timeout.InfiniteTimeSpan);
        while (Toasts.Count > MaxVisible)
        {
            Remove(Toasts[0]);
        }
    }

    private void Remove(ToastViewModel toast)
    {
        if (_timers.Remove(toast, out var timer))
        {
            timer.Dispose();
        }

        Toasts.Remove(toast);
    }
}
