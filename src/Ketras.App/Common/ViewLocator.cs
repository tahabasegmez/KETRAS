using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Ketras.App.Common;

/// <summary>
/// Strategy (data template): the view for a ViewModel, from an explicit map built at start. No reflection, so a
/// missing view is a visible placeholder naming the ViewModel rather than a silent blank. A kept view is built once per
/// ViewModel instance and shown again on its next visit, so a screen keeps its state (the map's viewport, the 3D camera).
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    private readonly Dictionary<Type, Func<Control>> _views = [];
    private readonly HashSet<Type> _kept = [];
    private readonly ConditionalWeakTable<object, Control> _built = [];

    /// <summary>Shows <typeparamref name="TViewModel"/> with a new <typeparamref name="TView"/>.</summary>
    public ViewLocator Register<TViewModel, TView>() where TView : Control, new()
    {
        _views[typeof(TViewModel)] = () => new TView();
        return this;
    }

    /// <summary>Shows <typeparamref name="TViewModel"/> with one <typeparamref name="TView"/> per ViewModel instance, kept between visits.</summary>
    public ViewLocator Keep<TViewModel, TView>() where TView : Control, new()
    {
        _kept.Add(typeof(TViewModel));
        return Register<TViewModel, TView>();
    }

    /// <inheritdoc />
    public Control Build(object? param)
    {
        if (param is null || !_views.TryGetValue(param.GetType(), out var create))
        {
            return new TextBlock { Text = $"No view for {param?.GetType().Name}" };
        }

        return _kept.Contains(param.GetType()) ? _built.GetValue(param, _ => create()) : create();
    }

    /// <inheritdoc />
    public bool Match(object? data) => data is not null && _views.ContainsKey(data.GetType());
}
