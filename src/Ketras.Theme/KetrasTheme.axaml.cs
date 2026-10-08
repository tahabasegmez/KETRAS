using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Ketras.Theme;

/// <summary>
/// The Ketras look: Fluent's dark theme tuned to the tokens of docs/UI_RULES.md, and the shared patterns as style
/// classes. Add it to the application's styles and run the application in the Dark theme variant.
/// </summary>
public sealed class KetrasTheme : Styles
{
    /// <summary>Loads the theme.</summary>
    public KetrasTheme() => AvaloniaXamlLoader.Load(this);
}
