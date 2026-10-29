using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Ketras.Utilities;

/// <summary>
/// Attached behaviour for field rows: a text box runs its commit command on Enter and when it loses focus, so a value
/// is stored once the operator is done with it rather than on every keystroke. Escape runs the cancel command.
/// </summary>
public static class Commit
{
    /// <summary>Defines the command run on Enter and on focus loss.</summary>
    public static readonly AttachedProperty<ICommand?> CommandProperty = AvaloniaProperty.RegisterAttached<TextBox, ICommand?>("Command", typeof(Commit));

    /// <summary>Defines the command run on Escape.</summary>
    public static readonly AttachedProperty<ICommand?> CancelCommandProperty = AvaloniaProperty.RegisterAttached<TextBox, ICommand?>("CancelCommand", typeof(Commit));

    static Commit()
    {
        CommandProperty.Changed.AddClassHandler<TextBox>((box, e) =>
        {
            box.LostFocus -= OnLostFocus;
            box.KeyDown -= OnKeyDown;
            if (e.NewValue is not null)
            {
                box.LostFocus += OnLostFocus;
                box.KeyDown += OnKeyDown;
            }
        });
    }

    /// <summary>The commit command of a text box.</summary>
    public static ICommand? GetCommand(TextBox box) => box.GetValue(CommandProperty);

    /// <summary>Sets the commit command of a text box.</summary>
    public static void SetCommand(TextBox box, ICommand? value) => box.SetValue(CommandProperty, value);

    /// <summary>The cancel command of a text box.</summary>
    public static ICommand? GetCancelCommand(TextBox box) => box.GetValue(CancelCommandProperty);

    /// <summary>Sets the cancel command of a text box.</summary>
    public static void SetCancelCommand(TextBox box, ICommand? value) => box.SetValue(CancelCommandProperty, value);

    private static void OnLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Run(sender as TextBox, CommandProperty);

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Run(sender as TextBox, CommandProperty);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Run(sender as TextBox, CancelCommandProperty);
            e.Handled = true;
        }
    }

    private static void Run(TextBox? box, AttachedProperty<ICommand?> property)
    {
        if (box?.GetValue(property) is { } command && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }
}
