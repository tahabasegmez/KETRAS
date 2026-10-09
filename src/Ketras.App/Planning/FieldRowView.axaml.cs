using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace Ketras.App.Planning;

/// <summary>View of <see cref="FieldViewModel"/>; tells it while its box has the keyboard focus.</summary>
public sealed partial class FieldRowView : UserControl
{
    /// <summary>An empty text as no value, so an empty tooltip does not open.</summary>
    public static FuncValueConverter<string?, string?> EmptyToNull { get; } = new(text => string.IsNullOrEmpty(text) ? null : text);

    /// <summary>Creates the view.</summary>
    public FieldRowView()
    {
        InitializeComponent();
        Box.GotFocus += (_, _) => SetEditing(true);
        Box.LostFocus += (_, _) => SetEditing(false);
    }

    private void SetEditing(bool editing)
    {
        if (DataContext is FieldViewModel field)
        {
            field.IsEditing = editing;
        }
    }
}
