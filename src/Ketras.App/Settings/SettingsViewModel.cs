using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.App.Common;
using Ketras.App.Resources;
using Ketras.Station.Settings;

namespace Ketras.App.Settings;

/// <summary>The operator's own settings, kept in the station's settings file.</summary>
public static class AppSettings
{
    /// <summary>A tile URL template ({z}, {x}, {y}) that replaces the built-in base maps; empty for none.</summary>
    public static SettingDefinition<string> CustomTileUrl { get; } = new("map.custom-url", string.Empty);

    /// <summary>The custom tile source's credit, as its licence requires.</summary>
    public static SettingDefinition<string> CustomTileAttribution { get; } = new("map.custom-attribution", string.Empty);
}

/// <summary>One choice in a settings list.</summary>
/// <param name="Value">The value stored.</param>
/// <param name="Label">The text shown.</param>
public sealed record SettingChoice<T>(T Value, string Label);

/// <summary>
/// The Settings activity: the operator's display choices (stored at once), the folders the station uses, and what
/// this build is. Theme and units offer one choice each until more exist.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _settings;

    /// <summary>Reads the current choices.</summary>
    public SettingsViewModel(SettingsStore settings, StationPaths paths, UnitService units)
    {
        _settings = settings;
        ConfigFolder = paths.ConfigDirectory;
        DataFolder = paths.DataDirectory;
        Units = [new SettingChoice<UnitSystem>(units.System, Strings.UnitsMetric)];
        SelectedUnits = Units[0];
        TileUrl = settings.Get(AppSettings.CustomTileUrl);
        TileAttribution = settings.Get(AppSettings.CustomTileAttribution);
    }

    /// <summary>The custom tile URL being edited; stored on commit when it is a valid template (or empty).</summary>
    [ObservableProperty] public partial string TileUrl { get; set; } = string.Empty;

    /// <summary>The custom tile source's credit.</summary>
    [ObservableProperty] public partial string TileAttribution { get; set; } = string.Empty;

    /// <summary>Why the URL was not stored; empty otherwise.</summary>
    [ObservableProperty] public partial string TileProblem { get; private set; } = string.Empty;

    /// <summary>Themes to choose from.</summary>
    public IReadOnlyList<string> Themes { get; } = [Strings.ThemeDark];

    /// <summary>Unit systems to choose from.</summary>
    public IReadOnlyList<SettingChoice<UnitSystem>> Units { get; }

    /// <summary>The unit system in use.</summary>
    public SettingChoice<UnitSystem> SelectedUnits { get; }

    /// <summary>Where settings and saved links are kept.</summary>
    public string ConfigFolder { get; }

    /// <summary>Where logs, telemetry logs and terrain data are kept.</summary>
    public string DataFolder { get; }

    /// <summary>This build's version.</summary>
    public string Version { get; } = typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    /// <summary>The license.</summary>
    public string License => "GPL-3.0-or-later";

    /// <summary>A URL template the map can use: http(s) with {z}, {x} and {y}.</summary>
    public static bool IsTileTemplate(string url) =>
        Uri.TryCreate(url.Replace("{z}", "0", StringComparison.Ordinal).Replace("{x}", "0", StringComparison.Ordinal).Replace("{y}", "0", StringComparison.Ordinal),
            UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && url.Contains("{z}", StringComparison.Ordinal) && url.Contains("{x}", StringComparison.Ordinal) && url.Contains("{y}", StringComparison.Ordinal);

    [RelayCommand]
    private void CommitTiles()
    {
        var url = TileUrl.Trim();
        if (url.Length > 0 && !IsTileTemplate(url))
        {
            TileProblem = Strings.SettingsTileUrlInvalid;
            return;
        }

        TileProblem = string.Empty;
        _settings.Set(AppSettings.CustomTileUrl, url);
        _settings.Set(AppSettings.CustomTileAttribution, TileAttribution.Trim());
    }

    [RelayCommand]
    private void CancelTiles()
    {
        TileProblem = string.Empty;
        TileUrl = _settings.Get(AppSettings.CustomTileUrl);
        TileAttribution = _settings.Get(AppSettings.CustomTileAttribution);
    }
}
