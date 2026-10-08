using Avalonia.Controls;
using Avalonia.Input;

namespace Ketras.App.Notifications;

/// <summary>View of <see cref="ToastsViewModel"/>.</summary>
public sealed partial class ToastsView : UserControl
{
    /// <summary>Creates the view.</summary>
    public ToastsView() => InitializeComponent();

    private void OnCardTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: ToastViewModel toast })
        {
            toast.DismissCommand.Execute(null);
        }
    }
}
