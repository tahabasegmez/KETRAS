using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.Notifications;

namespace Ketras.Utilities;

/// <summary>One line of a screen's log.</summary>
/// <param name="Time">Local time, HH:mm:ss.</param>
/// <param name="Text">What happened.</param>
/// <param name="Level">Its kind; it sets the row's icon and tint.</param>
public sealed record LogRow(string Time, string Text, NotificationSeverity Level);

/// <summary>A work-area screen's log (docs/UI_RULES.md "Settings and log split"): newest first, the latest <see cref="Capacity"/> lines.</summary>
public sealed partial class ScreenLogViewModel(TimeProvider time) : ObservableObject
{
    /// <summary>Lines kept.</summary>
    public const int Capacity = 500;

    /// <summary>The lines, newest first.</summary>
    public ObservableCollection<LogRow> Rows { get; } = [];

    /// <summary>True while the log is empty.</summary>
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>The number of lines, empty when none.</summary>
    public string Count => Rows.Count > 0 ? Rows.Count.ToString(CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>Adds a line stamped now; call on the UI thread.</summary>
    public void Append(string text, NotificationSeverity level = NotificationSeverity.Info) => Append(text, level, time.GetUtcNow().UtcDateTime);

    /// <summary>Adds a line stamped at <paramref name="atUtc"/>; call on the UI thread.</summary>
    public void Append(string text, NotificationSeverity level, DateTime atUtc)
    {
        var at = TimeZoneInfo.ConvertTimeFromUtc(atUtc, time.LocalTimeZone);
        Rows.Insert(0, new LogRow(at.ToString("HH:mm:ss", CultureInfo.InvariantCulture), text, level));
        while (Rows.Count > Capacity)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }

        Changed();
    }

    /// <summary>Empties the log.</summary>
    [RelayCommand]
    private void Clear()
    {
        Rows.Clear();
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Count));
    }
}
