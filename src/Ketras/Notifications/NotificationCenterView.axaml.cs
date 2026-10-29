using Avalonia.Controls;

namespace Ketras.Notifications;

/// <summary>View of <see cref="NotificationCenterViewModel"/>.</summary>
public sealed partial class NotificationCenterView : UserControl
{
    /// <summary>Creates the view.</summary>
    public NotificationCenterView() => InitializeComponent();

    private void OnHistoryOpened(object? sender, EventArgs e)
    {
        if (DataContext is NotificationCenterViewModel center)
        {
            center.MarkReadCommand.Execute(null);
        }
    }

    private void OnHistoryClosed(object? sender, EventArgs e)
    {
        if (DataContext is NotificationCenterViewModel center)
        {
            center.HistoryClosedCommand.Execute(null);
        }
    }
}
