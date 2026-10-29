using Avalonia.Controls;

namespace Ketras.Shell;

/// <summary>View of <see cref="ShellViewModel"/>.</summary>
public sealed partial class MainWindow : Window
{
    /// <summary>Creates the window; its DataContext is the shell.</summary>
    public MainWindow() => InitializeComponent();
}
