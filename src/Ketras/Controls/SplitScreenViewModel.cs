using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.Utilities;

namespace Ketras.Controls;

/// <summary>One page of a split screen's page list; <see cref="NeedsAttention"/> marks it in the list.</summary>
/// <param name="title">Its title.</param>
/// <param name="icon">Lucide icon name.</param>
/// <param name="content">The page's ViewModel.</param>
public sealed partial class ScreenPage(string title, string icon, object content) : ObservableObject
{
    /// <summary>Its title.</summary>
    public string Title { get; } = title;

    /// <summary>Lucide icon name.</summary>
    public string Icon { get; } = icon;

    /// <summary>The page's ViewModel.</summary>
    public object Content { get; } = content;

    /// <summary>Something on the page still needs the operator (a dot in the list).</summary>
    [ObservableProperty] public partial bool NeedsAttention { get; set; }
}

/// <summary>
/// Template method base of the work-area screens with a page list, a page and a log (docs/UI_RULES.md "Settings and log
/// split"); one view, <c>SplitScreenView</c>, draws them all. A screen supplies its titles, pages and an optional banner
/// over the page.
/// </summary>
public abstract partial class SplitScreenViewModel(TimeProvider time) : ObservableObject
{
    /// <summary>The page list's title.</summary>
    public abstract string NavTitle { get; }

    /// <summary>The log's title.</summary>
    public abstract string LogTitle { get; }

    /// <summary>What the empty log says.</summary>
    public abstract string LogEmptyText { get; }

    /// <summary>The pages.</summary>
    public abstract IReadOnlyList<ScreenPage> Pages { get; }

    /// <summary>An action in the page list's header (e.g. read everything again); null for none.</summary>
    public virtual System.Windows.Input.ICommand? NavCommand => null;

    /// <summary>The header action's Lucide icon.</summary>
    public virtual string NavIcon => string.Empty;

    /// <summary>The header action's tooltip.</summary>
    public virtual string NavTip => string.Empty;

    /// <summary>A ViewModel shown over the page (a state banner, an apply bar); null for none.</summary>
    public virtual object? Banner => null;

    /// <summary>The page shown.</summary>
    [ObservableProperty] public partial ScreenPage? SelectedPage { get; set; }

    /// <summary>The screen's log.</summary>
    public ScreenLogViewModel Log { get; } = new(time);

    /// <summary>The log column is shown.</summary>
    [ObservableProperty] public partial bool IsLogVisible { get; set; } = true;

    /// <summary>The page shown changed from <paramref name="from"/>.</summary>
    protected virtual void OnPageChanged(ScreenPage? from)
    {
    }

    partial void OnSelectedPageChanged(ScreenPage? oldValue, ScreenPage? newValue) => OnPageChanged(oldValue);

    /// <summary>Hides the log.</summary>
    [RelayCommand]
    private void HideLog() => IsLogVisible = false;

    /// <summary>Shows the log.</summary>
    [RelayCommand]
    private void ShowLog() => IsLogVisible = true;
}
