using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.Resources;

namespace Ketras.Notifications;

/// <summary>One notification as a row or a toast shows it; a row opens its detail and source when pressed.</summary>
public sealed partial class NotificationRowViewModel(Notification notification, bool isUnread = false) : ObservableObject
{
    /// <summary>The notification.</summary>
    public Notification Notification { get; } = notification;

    /// <summary>What happened.</summary>
    public string Title => Notification.Title;

    /// <summary>More detail; empty when none.</summary>
    public string Detail => Notification.Detail ?? string.Empty;

    /// <summary>True when there is a detail.</summary>
    public bool HasDetail => !string.IsNullOrEmpty(Notification.Detail);

    /// <summary>"Source: …".</summary>
    public string Source => string.Format(CultureInfo.CurrentCulture, Strings.NotificationSource, Notification.Source);

    /// <summary>How serious it is.</summary>
    public NotificationSeverity Severity => Notification.Severity;

    /// <summary>Local time, HH:mm:ss.</summary>
    public string Time => Notification.TimeUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Not seen before the history was opened this time; tinted until the history closes.</summary>
    [ObservableProperty] public partial bool IsUnread { get; set; } = isUnread;

    /// <summary>The detail and source show under the title.</summary>
    [ObservableProperty] public partial bool IsExpanded { get; set; }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}
