using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace Ketras.Commands;

/// <summary>View of <see cref="CommandBarViewModel"/>.</summary>
public sealed partial class CommandBarView : UserControl
{
    /// <summary>Creates the view.</summary>
    public CommandBarView() => InitializeComponent();

    /// <summary>
    /// Sends the command, then closes the flyout. A bound Command would not run: Button reads it after its Click
    /// handlers, and closing the flyout there has already cleared the binding.
    /// </summary>
    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: CommandButtonViewModel button })
        {
            button.ConfirmCommand.Execute(null);
        }

        OnCloseFlyout(sender, e);
    }

    /// <summary>Closes the confirmation flyout the pressed button sits in.</summary>
    private void OnCloseFlyout(object? sender, RoutedEventArgs e) =>
        (sender as ILogical)?.GetLogicalAncestors().OfType<Popup>().FirstOrDefault()?.Close();
}
