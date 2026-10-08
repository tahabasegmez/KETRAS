using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;

namespace Ketras.App.Common;

/// <summary>View of every <see cref="SplitScreenViewModel"/>; it keeps the log column's width while the log is hidden.</summary>
public sealed partial class SplitScreenView : UserControl
{
    private SplitScreenViewModel? _model;
    private GridLength _logWidth = new(2, GridUnitType.Star);

    /// <summary>Creates the view.</summary>
    public SplitScreenView() => InitializeComponent();

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
        }

        _model = DataContext as SplitScreenViewModel;
        if (_model is not null)
        {
            _model.PropertyChanged += OnModelChanged;
            ApplyLogColumn(_model.IsLogVisible);
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SplitScreenViewModel.IsLogVisible) && _model is not null)
        {
            ApplyLogColumn(_model.IsLogVisible);
        }
    }

    private void ApplyLogColumn(bool visible)
    {
        var column = Split.ColumnDefinitions[2];
        if (!visible && column.Width.Value > 0)
        {
            _logWidth = column.Width;
        }

        column.Width = visible ? _logWidth : new GridLength(0);
        column.MinWidth = visible ? 220 : 0;
        Page.Margin = new Thickness(28, 20, visible ? 12 : 36, 0);
    }
}
