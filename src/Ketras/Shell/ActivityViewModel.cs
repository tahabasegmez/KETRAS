using CommunityToolkit.Mvvm.ComponentModel;

namespace Ketras.Shell;

/// <summary>One activity of the activity bar: what fills the work area while it is selected.</summary>
/// <param name="id">Stable identity, e.g. "link".</param>
/// <param name="title">Tooltip text.</param>
/// <param name="icon">Lucide icon name.</param>
/// <param name="content">The screen's ViewModel.</param>
public sealed partial class ActivityViewModel(string id, string title, string icon, object content) : ObservableObject
{
    /// <summary>Stable identity.</summary>
    public string Id { get; } = id;

    /// <summary>Tooltip text.</summary>
    public string Title { get; } = title;

    /// <summary>Lucide icon name.</summary>
    public string Icon { get; } = icon;

    /// <summary>The screen's ViewModel.</summary>
    public object Content { get; } = content;

    /// <summary>It fills the work area now.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>A screen that is not built yet; it says so instead of showing an empty area.</summary>
/// <param name="Title">The screen's name.</param>
public sealed record PendingActivityViewModel(string Title);
