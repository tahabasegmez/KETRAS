using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Ketras.App.Resources;

namespace Ketras.App.Common;

/// <summary>A "?" whose <see cref="Text"/> explains the control next to it, shown as a tooltip on hover.</summary>
public sealed partial class HelpMark : UserControl
{
    /// <summary>Defines <see cref="Text"/>.</summary>
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<HelpMark, string?>(nameof(Text));

    static HelpMark()
    {
        TextProperty.Changed.AddClassHandler<HelpMark>((mark, e) => ToolTip.SetTip(mark, e.NewValue));
    }

    /// <summary>Creates the mark.</summary>
    public HelpMark()
    {
        InitializeComponent();
        AutomationProperties.SetName(this, Strings.HelpMarkName);
    }

    /// <summary>What the control next to it does.</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}
