using Ketras.Environment;
using Ketras.Model.Environment;
using Ketras.Model.Geo;
using Ketras.Model.Vehicles;
using Ketras.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace Ketras.Tests.Environment;

public sealed class EnvironmentTests
{
    private const string GeoJson = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","id":"ctr","properties":{"name":"Control zone","kind":"keep-out","ceiling":"900","geofence":"true"},
           "geometry":{"type":"Polygon","coordinates":[[[8.50,47.40],[8.52,47.40],[8.52,47.42],[8.50,47.42],[8.50,47.40]]]}},
          {"type":"Feature","id":"field","properties":{"kind":"stay-in","radius":"1500","geofence":"true"},
           "geometry":{"type":"Point","coordinates":[8.54,47.39]}},
          {"type":"Feature","id":"expired","properties":{"radius":"100","geofence":"true","validUntil":"2001-01-01T00:00:00Z"},
           "geometry":{"type":"Point","coordinates":[8.55,47.39]}},
          {"type":"Feature","id":"mast","properties":{"name":"Mast","category":"obstacle"},
           "geometry":{"type":"Point","coordinates":[8.53,47.41,612]}},
          {"type":"Feature","id":"road","geometry":{"type":"LineString","coordinates":[[8.5,47.4],[8.6,47.5]]}}]}
        """;

    private const string Kml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <kml xmlns="http://www.opengis.net/kml/2.2"><Document>
          <Placemark id="lake"><name>Lake</name>
            <ExtendedData><Data name="kind"><value>advisory</value></Data></ExtendedData>
            <Polygon><outerBoundaryIs><LinearRing><coordinates>8.6,47.3 8.62,47.3 8.62,47.32 8.6,47.3</coordinates></LinearRing></outerBoundaryIs></Polygon>
          </Placemark>
          <Placemark><name>Hut</name><Point><coordinates>8.61,47.31,700</coordinates></Point></Placemark>
          <Placemark><name>Nothing</name></Placemark>
        </Document></kml>
        """;

    [Fact]
    public void GeoJson_polygons_and_radius_points_become_zones_and_other_points_points_of_interest()
    {
        var result = ZoneFileParser.ReadGeoJson(GeoJson, "file");

        Assert.Equal(["ctr", "field", "expired"], result.Zones.Select(z => z.Id));
        var ctr = result.Zones[0];
        Assert.Equal(4, Assert.IsType<PolygonArea>(ctr.Area).Vertices.Length);
        Assert.Equal((ZoneKind.KeepOut, 900.0, true), (ctr.Kind, ctr.CeilingMeters!.Value, ctr.IncludeInGeofence));
        Assert.Equal(new CircleArea(new GeoPoint(47.39, 8.54), 1500), result.Zones[1].Area);
        Assert.Equal(ZoneKind.StayIn, result.Zones[1].Kind);
        Assert.False(result.Zones[2].IsValidAt(DateTime.UtcNow));
        var mast = Assert.Single(result.Points);
        Assert.Equal(("Mast", 612.0, "obstacle"), (mast.Name, mast.AltitudeMeters!.Value, mast.Category));
        Assert.Contains("LineString", Assert.Single(result.Skipped));
    }

    [Fact]
    public void Kml_placemarks_are_read_and_a_placemark_without_geometry_is_skipped()
    {
        var result = ZoneFileParser.ReadKml(Kml, "file");

        var lake = Assert.Single(result.Zones);
        Assert.Equal(("Lake", ZoneKind.Advisory), (lake.Name, lake.Kind));
        Assert.Equal(3, Assert.IsType<PolygonArea>(lake.Area).Vertices.Length);
        Assert.Equal(new GeoPoint(47.31, 8.61), Assert.Single(result.Points).Location);
        Assert.Single(result.Skipped);
        Assert.Throws<FormatException>(() => ZoneFileParser.ReadKml("<kml>", "file"));
        Assert.Throws<FormatException>(() => ZoneFileParser.ReadFile("zones.shp", "file"));
    }

    [Fact]
    public async Task Zone_files_in_the_settings_load_at_start_and_valid_marked_zones_join_the_geofence()
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("ketras-zones-").FullName;
        var geoJson = Path.Combine(directory, "airspace.geojson");
        var kml = Path.Combine(directory, "lakes.kml");
        File.WriteAllText(geoJson, GeoJson);
        File.WriteAllText(kml, Kml);
        await using var station = await StationFixture.StartAsync(options => options.Environment.ZoneFiles = [geoJson, kml, Path.Combine(directory, "missing.kml")]);
        var store = station.Get<EnvironmentStore>();

        Assert.Equal(4, store.Zones.Count);
        Assert.Equal(2, store.Points.Count);
        Assert.Equal(["airspace.geojson:ctr", "airspace.geojson:field"], store.GeofenceZones().Select(f => f.Id));
        Assert.True(store.GeofenceZones()[1].Inclusion);
        var history = station.Get<NotificationStore>().History;
        Assert.Contains(history, n => n.Title == "missing.kml could not be read");
        Assert.Contains(history, n => n.Title == "airspace.geojson: 1 feature(s) skipped");

        station.Get<ZoneFileEnvironmentProvider>().Unload("airspace.geojson");
        Assert.Equal(["Lake"], store.Zones.Select(z => z.Name));
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Adsb_traffic_from_the_autopilot_is_reported_and_times_out()
    {
        await using var station = await StationFixture.StartAsync(options => options.Environment.TrafficTimeout = TimeSpan.FromSeconds(1));
        await station.ConnectAsync();
        var store = station.Get<EnvironmentStore>();

        station.Vehicle.SendAdsb(0x4CA2B1, "EIN123", 47.41, 8.52, 1250, 270, 70);
        await StationFixture.WaitForAsync(() => store.Traffic.Count == 1);

        var target = store.Traffic[0];
        Assert.Equal(("adsb:4CA2B1", "EIN123", TrafficCategory.Light), (target.Id, target.Callsign, target.Category));
        Assert.Equal(new GeoPoint(47.41, 8.52), target.Position);
        Assert.Equal((1250.0, 270.0, 70.0), (target.AltitudeMeters!.Value, target.HeadingDegrees!.Value, target.GroundSpeedMps!.Value));
        await StationFixture.WaitForAsync(() => store.Traffic.Count == 0);
    }

    [Fact]
    public async Task The_vehicles_wind_and_air_become_weather_with_the_direction_the_wind_comes_from()
    {
        await using var station = await StationFixture.StartAsync();
        await station.ConnectAsync();
        var store = station.Get<EnvironmentStore>();

        station.Vehicle.SendWind(northMps: 0, eastMps: -4, downMps: 0.5);
        station.Vehicle.SendAir(21.5, 63);
        await StationFixture.WaitForAsync(() => store.Weather is [{ HumidityPercent: not null, WindSpeedMps: not null }]);

        var weather = store.Weather[0];
        Assert.Equal("Vehicle", weather.Source);
        Assert.Equal(90, weather.WindFromDegrees!.Value, 6); // air moving west comes from the east
        Assert.Equal(4, weather.WindSpeedMps!.Value, 6);
        Assert.Equal(-0.5, weather.VerticalWindMps!.Value, 6);
        Assert.Equal((21.5, 63.0), (weather.TemperatureCelsius!.Value, weather.HumidityPercent!.Value));
    }

    [Fact]
    public async Task Manual_zones_and_points_are_kept_and_providers_that_track_the_ownship_get_its_snapshots()
    {
        var ownship = new OwnshipRecorder();
        await using var station = await StationFixture.StartAsync(register: services => services.AddSingleton<IEnvironmentProvider>(ownship));
        var manual = station.Get<ManualEnvironmentProvider>();
        var store = station.Get<EnvironmentStore>();

        manual.UpsertZone(new Zone { Id = "a", Source = "x", Area = new CircleArea(new GeoPoint(47, 8), 100) });
        manual.UpsertZone(new Zone { Id = "a", Source = "x", Area = new CircleArea(new GeoPoint(47, 8), 200) });
        manual.UpsertPoint(new PointOfInterest { Id = "p", Source = "x", Location = new GeoPoint(47, 8) });
        manual.RemovePoint("p");

        Assert.Equal(200, Assert.IsType<CircleArea>(Assert.Single(store.Zones).Area).RadiusMeters);
        Assert.Equal("Manual", store.Zones[0].Source);
        Assert.Empty(store.Points);

        await station.ConnectAsync();
        station.Vehicle.SendPosition(47.39, 8.54, 520, 30);
        await StationFixture.WaitForAsync(() => ownship.Last?.Position is not null);
    }

    private sealed class OwnshipRecorder : IEnvironmentProvider, IOwnshipConsumer
    {
        public string Name => "Ownship recorder";

        public VehicleSnapshot? Last { get; private set; }

        public void OnOwnship(VehicleSnapshot ownship) => Last = ownship;

        public Task StartAsync(IEnvironmentSink sink, CancellationToken cancellation) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellation) => Task.CompletedTask;
    }
}
