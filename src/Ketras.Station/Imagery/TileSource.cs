namespace Ketras.Station.Imagery;

/// <summary>
/// An XYZ map tile server: its URL template, the credit its terms require and the cache folder its tiles share with the
/// planning map. The built-in sources are the ones <c>Ketras.Controls.Map</c> offers.
/// </summary>
/// <param name="Name">Cache folder name under the tile cache.</param>
/// <param name="UrlTemplate">URL with <c>{z}</c>, <c>{x}</c> and <c>{y}</c>.</param>
/// <param name="Attribution">Text the source's licence requires next to anything drawn from it.</param>
/// <param name="MaxZoom">Highest zoom it serves.</param>
public sealed record TileSource(string Name, string UrlTemplate, string Attribution, int MaxZoom = 19)
{
    /// <summary>Esri World Imagery.</summary>
    public static TileSource Satellite { get; } = new(
        "satellite",
        "https://services.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}",
        "Esri, Maxar, Earthstar Geographics, and the GIS User Community");

    /// <summary>OpenStreetMap.</summary>
    public static TileSource Street { get; } = new("street", "https://tile.openstreetmap.org/{z}/{x}/{y}.png", "© OpenStreetMap contributors");

    /// <summary>A source the operator supplies.</summary>
    public static TileSource Custom(string urlTemplate, string attribution) => new("custom", urlTemplate, attribution);

    /// <summary>The URL of one tile.</summary>
    public Uri TileUrl(int zoom, int x, int y) => new(UrlTemplate
        .Replace("{z}", zoom.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("{x}", x.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("{y}", y.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
}
