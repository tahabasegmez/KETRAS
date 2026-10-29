using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Ketras.Geo;

namespace Ketras.Environment;

/// <summary>
/// Parser: zones and points of interest from GeoJSON (RFC 7946) or KML 2.2. Polygons become zones (outer ring only),
/// points with a <c>radius</c> property become circular zones, other points become points of interest. Properties (GeoJSON
/// <c>properties</c>, KML <c>ExtendedData/Data</c>): <c>name</c>, <c>kind</c> (keep-out, stay-in, advisory),
/// <c>floor</c> and <c>ceiling</c> (metres AMSL), <c>validFrom</c> and <c>validUntil</c> (ISO 8601), <c>geofence</c>
/// (true to upload as a geofence zone), <c>radius</c> (metres) and <c>category</c>.
/// </summary>
public static class ZoneFileParser
{
    /// <summary>What a file held.</summary>
    /// <param name="Zones">Zones read.</param>
    /// <param name="Points">Points of interest read.</param>
    /// <param name="Skipped">Features that could not be read, in operator-facing English.</param>
    public sealed record Result(IReadOnlyList<Zone> Zones, IReadOnlyList<PointOfInterest> Points, IReadOnlyList<string> Skipped);

    /// <summary>Reads a file by its extension (.geojson / .json or .kml). Throws <see cref="FormatException"/> for anything else.</summary>
    public static Result ReadFile(string path, string source) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".geojson" or ".json" => ReadGeoJson(File.ReadAllText(path), source),
        ".kml" => ReadKml(File.ReadAllText(path), source),
        var extension => throw new FormatException($"{Path.GetFileName(path)}: '{extension}' is not a GeoJSON or KML file")
    };

    /// <summary>Reads GeoJSON text: a FeatureCollection, a Feature or a bare geometry.</summary>
    public static Result ReadGeoJson(string text, string source)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException exception)
        {
            throw new FormatException($"not valid JSON: {exception.Message}", exception);
        }

        var features = (string?)root?["type"] switch
        {
            "FeatureCollection" => (root!["features"] as JsonArray ?? []).OfType<JsonObject>().ToList(),
            "Feature" => [(JsonObject)root!],
            _ when root is JsonObject geometry => [new JsonObject { ["type"] = "Feature", ["geometry"] = geometry.DeepClone() }],
            _ => throw new FormatException("not a GeoJSON object")
        };

        var builder = new Builder(source);
        for (var i = 0; i < features.Count; i++)
        {
            var feature = features[i];
            var properties = (feature["properties"] as JsonObject ?? []).ToDictionary(p => p.Key.ToLowerInvariant(), p => p.Value?.ToString());
            var id = feature["id"]?.ToString() ?? properties.GetValueOrDefault("id") ?? $"feature-{i + 1}";
            var geometry = feature["geometry"] as JsonObject;
            switch ((string?)geometry?["type"])
            {
                case "Polygon" when geometry!["coordinates"] is JsonArray rings && rings.FirstOrDefault() is JsonArray outer:
                    builder.Polygon(id, Ring(outer), properties);
                    break;
                case "MultiPolygon" when geometry!["coordinates"] is JsonArray polygons:
                    var part = 1;
                    foreach (var polygon in polygons.OfType<JsonArray>())
                    {
                        if (polygon.FirstOrDefault() is JsonArray ring)
                        {
                            builder.Polygon($"{id}-{part++}", Ring(ring), properties);
                        }
                    }

                    break;
                case "Point" when geometry!["coordinates"] is JsonArray point && point.Count >= 2:
                    builder.Point(id, new GeoPoint(Number(point[1]), Number(point[0])), point.Count > 2 ? Number(point[2]) : null, properties);
                    break;
                default:
                    builder.Skip($"{id}: geometry '{(string?)geometry?["type"]}' is not supported");
                    break;
            }
        }

        return builder.Build();
    }

    /// <summary>Reads KML text: every Placemark with a Polygon or a Point.</summary>
    public static Result ReadKml(string text, string source)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(text);
        }
        catch (System.Xml.XmlException exception)
        {
            throw new FormatException($"not valid KML: {exception.Message}", exception);
        }

        var builder = new Builder(source);
        var index = 0;
        foreach (var placemark in document.Descendants().Where(e => e.Name.LocalName == "Placemark"))
        {
            index++;
            var properties = placemark.Descendants().Where(e => e.Name.LocalName == "Data")
                .Where(e => e.Attribute("name") is not null)
                .ToDictionary(e => e.Attribute("name")!.Value.ToLowerInvariant(), e => (string?)e.Elements().FirstOrDefault(v => v.Name.LocalName == "value")?.Value);
            if (Child(placemark, "name") is { } name)
            {
                properties.TryAdd("name", name);
            }

            var id = placemark.Attribute("id")?.Value ?? $"placemark-{index}";
            var polygon = placemark.Descendants().FirstOrDefault(e => e.Name.LocalName == "Polygon");
            var point = placemark.Descendants().FirstOrDefault(e => e.Name.LocalName == "Point");
            if (polygon?.Descendants().FirstOrDefault(e => e.Name.LocalName == "outerBoundaryIs")?
                    .Descendants().FirstOrDefault(e => e.Name.LocalName == "coordinates")?.Value is { } coordinates)
            {
                builder.Polygon(id, KmlCoordinates(coordinates).Select(c => c.Point).ToList(), properties);
            }
            else if (point?.Descendants().FirstOrDefault(e => e.Name.LocalName == "coordinates")?.Value is { } at
                     && KmlCoordinates(at).FirstOrDefault() is { } first)
            {
                builder.Point(id, first.Point, first.Altitude, properties);
            }
            else
            {
                builder.Skip($"{id}: no polygon or point");
            }
        }

        return builder.Build();
    }

    private sealed class Builder(string source)
    {
        private readonly List<Zone> _zones = [];
        private readonly List<PointOfInterest> _points = [];
        private readonly List<string> _skipped = [];

        public void Polygon(string id, IReadOnlyList<GeoPoint> ring, Dictionary<string, string?> properties)
        {
            var vertices = ring.Count > 1 && ring[0] == ring[^1] ? ring.Take(ring.Count - 1).ToList() : [.. ring];
            if (vertices.Count < 3)
            {
                Skip($"{id}: a polygon needs at least three corners");
                return;
            }

            _zones.Add(MakeZone(id, new PolygonArea([.. vertices]), properties));
        }

        public void Point(string id, GeoPoint location, double? altitude, Dictionary<string, string?> properties)
        {
            if (Double(properties, "radius") is { } radius and > 0)
            {
                _zones.Add(MakeZone(id, new CircleArea(location, radius), properties));
                return;
            }

            _points.Add(new PointOfInterest
            {
                Id = id,
                Source = source,
                Name = properties.GetValueOrDefault("name"),
                Location = location,
                AltitudeMeters = altitude,
                Category = properties.GetValueOrDefault("category"),
                Description = properties.GetValueOrDefault("description")
            });
        }

        public void Skip(string reason) => _skipped.Add(reason);

        public Result Build() => new(_zones, _points, _skipped);

        private Zone MakeZone(string id, Area area, Dictionary<string, string?> properties) => new()
        {
            Id = id,
            Source = source,
            Name = properties.GetValueOrDefault("name"),
            Area = area,
            Kind = properties.GetValueOrDefault("kind")?.Trim().ToLowerInvariant() switch
            {
                "stay-in" or "stayin" or "inclusion" => ZoneKind.StayIn,
                "advisory" or "info" => ZoneKind.Advisory,
                _ => ZoneKind.KeepOut
            },
            FloorMeters = Double(properties, "floor"),
            CeilingMeters = Double(properties, "ceiling"),
            ValidFromUtc = Time(properties, "validfrom"),
            ValidUntilUtc = Time(properties, "validuntil"),
            IncludeInGeofence = bool.TryParse(properties.GetValueOrDefault("geofence"), out var geofence) && geofence
        };
    }

    private static List<GeoPoint> Ring(JsonArray ring) =>
        [.. ring.OfType<JsonArray>().Where(p => p.Count >= 2).Select(p => new GeoPoint(Number(p[1]), Number(p[0])))];

    /// <summary>KML coordinates: "lon,lat[,alt]" tuples separated by white space.</summary>
    private static IEnumerable<(GeoPoint Point, double? Altitude)> KmlCoordinates(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(tuple => tuple.Split(','))
            .Where(parts => parts.Length >= 2)
            .Select(parts => (new GeoPoint(Parse(parts[1]), Parse(parts[0])), parts.Length > 2 ? Parse(parts[2]) : (double?)null));

    private static string? Child(XElement element, string name) => element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    private static double Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) ? number : double.NaN;

    private static double Parse(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static double? Double(Dictionary<string, string?> properties, string key) =>
        double.TryParse(properties.GetValueOrDefault(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static DateTime? Time(Dictionary<string, string?> properties, string key) =>
        DateTime.TryParse(properties.GetValueOrDefault(key), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time) ? time : null;
}
