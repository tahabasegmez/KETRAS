using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace Ketras.App.Common;

/// <summary>
/// A section with a 22 px header (docs/UI_RULES.md "Section header"): a chevron, the title and actions on the right,
/// over its content; pressing the header folds the content away. Its look is the planner styles' control theme.
/// </summary>
public sealed class CollapsibleSection : ContentControl
{
    /// <summary>Defines <see cref="Title"/>.</summary>
    public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<CollapsibleSection, string>(nameof(Title), string.Empty);

    /// <summary>Defines <see cref="Actions"/>.</summary>
    public static readonly StyledProperty<object?> ActionsProperty = AvaloniaProperty.Register<CollapsibleSection, object?>(nameof(Actions));

    /// <summary>Defines <see cref="IsExpanded"/>.</summary>
    public static readonly StyledProperty<bool> IsExpandedProperty = AvaloniaProperty.Register<CollapsibleSection, bool>(nameof(IsExpanded), true);

    private Control? _header;

    /// <summary>The header text (upper case).</summary>
    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Row actions at the header's right end.</summary>
    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    /// <summary>The content shows.</summary>
    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    /// <inheritdoc />
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_header is not null)
        {
            _header.PointerPressed -= OnHeaderPressed;
        }

        _header = e.NameScope.Find<Control>("PART_Header");
        if (_header is not null)
        {
            _header.PointerPressed += OnHeaderPressed;
        }
    }

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            IsExpanded = !IsExpanded;
        }
    }
}
