namespace Ketras.Settings;

/// <summary>
/// Where the station keeps its files: configuration (saved links, user settings) and data (logs, caches). On Windows
/// these are <c>%APPDATA%\Ketras</c> and <c>%LOCALAPPDATA%\Ketras</c>; on Linux <c>~/.config/Ketras</c> and
/// <c>~/.local/share/Ketras</c>.
/// </summary>
/// <param name="ConfigDirectory">Configuration directory.</param>
/// <param name="DataDirectory">Data directory.</param>
public sealed record StationPaths(string ConfigDirectory, string DataDirectory)
{
    /// <summary>
    /// The platform's standard directories. They are not checked for existence: on Linux GetFolderPath returns an empty
    /// string for a folder that does not exist yet (a fresh ~/.config), which would put the files in the working directory.
    /// </summary>
    public static StationPaths Default { get; } = new(
        Path.Combine(Folder(System.Environment.SpecialFolder.ApplicationData), "Ketras"),
        Path.Combine(Folder(System.Environment.SpecialFolder.LocalApplicationData), "Ketras"));

    /// <summary>A directory from the options, or <paramref name="fallback"/> under the data directory.</summary>
    public string DataPath(string? configured, string fallback) =>
        string.IsNullOrWhiteSpace(configured) ? Path.Combine(DataDirectory, fallback) : Path.GetFullPath(configured, DataDirectory);

    /// <summary>A file under the configuration directory.</summary>
    public string ConfigFile(string name) => Path.Combine(ConfigDirectory, name);

    private static string Folder(System.Environment.SpecialFolder folder) =>
        System.Environment.GetFolderPath(folder, System.Environment.SpecialFolderOption.DoNotVerify);
}
