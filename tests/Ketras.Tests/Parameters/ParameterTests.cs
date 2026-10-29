using System.Net;
using System.Text;
using Ketras.Mavlink.Messages;
using Ketras.Mavlink.Protocols;
using Ketras.Station.Notifications;
using Ketras.Station.Parameters;
using Ketras.Station.Settings;
using Ketras.Station.Setup;
using Ketras.Station.Vehicles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ketras.Tests.Parameters;

/// <summary>Serves fixed bodies by URL and counts requests.</summary>
internal sealed class FakeHttp(IReadOnlyDictionary<string, byte[]> bodies) : HttpMessageHandler
{
    public int Requests { get; private set; }

    public bool Offline { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        if (Offline)
        {
            throw new HttpRequestException("offline");
        }

        return Task.FromResult(bodies.TryGetValue(request.RequestUri!.ToString(), out var body)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

public sealed class ParameterTests
{
    private const string Metadata = """
        {"version":1,"parameters":[
          {"name":"FW_AIRSPD_MIN","type":"Float","group":"FW TECS","shortDesc":"Minimum Airspeed","units":"m/s","min":0.5,"max":40,"decimalPlaces":1,"default":10},
          {"name":"FW_AIRSPD_MAX","type":"Float","group":"FW TECS","shortDesc":"Maximum Airspeed","units":"m/s","min":0.5,"max":40,"default":20,"rebootRequired":true},
          {"name":"SYS_AUTOSTART","type":"Int32","group":"System","shortDesc":"Auto-start script index","default":0,"min":0,"max":9999999},
          {"name":"GF_ACTION","type":"Int32","group":"Geofence","shortDesc":"Geofence violation action","default":2,
           "values":[{"value":0,"description":"None"},{"value":1,"description":"Warning"},{"value":2,"description":"Hold mode"}]},
          {"name":"SYS_HAS_MAG","type":"Int32","bitmask":[{"index":0,"description":"first"},{"index":70,"description":"bad"}]},
          {"name":"NO_TYPE","type":"Bool"},
          {"name":"THIS_NAME_IS_TOO_LONG","type":"Int32"}
        ]}
        """;

    private static string General(string parametersUri, string? fallback = null) =>
        $$"""{"version":1,"metadataTypes":[{"type":1,"uri":"{{parametersUri}}","fileCrc":0{{(fallback is null ? "" : $",\"uriFallback\":\"{fallback}\",\"fileCrcFallback\":1234")}}}]}""";

    [Fact]
    public void Metadata_parses_types_ranges_options_and_bits_and_skips_what_it_cannot_represent()
    {
        var parsed = ParameterMetadataParser.Parse(Metadata);

        Assert.Equal(["FW_AIRSPD_MAX", "FW_AIRSPD_MIN", "GF_ACTION", "SYS_AUTOSTART", "SYS_HAS_MAG"], parsed.Keys.Order());
        var min = parsed["FW_AIRSPD_MIN"];
        Assert.Equal((MavParamType.Real32, "m/s", 0.5, 40.0, 1, 10.0), (min.Type, min.Units, min.Min!.Value, min.Max!.Value, min.DecimalPlaces!.Value, min.Default!.Value));
        Assert.True(parsed["FW_AIRSPD_MAX"].RebootRequired);
        Assert.Equal(["None", "Warning", "Hold mode"], parsed["GF_ACTION"].Options.Select(o => o.Description));
        Assert.Equal([0], parsed["SYS_HAS_MAG"].Bits.Select(b => b.Index));
        Assert.Throws<FormatException>(() => ParameterMetadataParser.Parse("{}"));
        Assert.Throws<FormatException>(() => ParameterMetadataParser.Parse("not json"));
    }

    [Fact]
    public void Values_are_checked_against_their_type_and_range_and_compared_in_their_own_type()
    {
        var metadata = ParameterMetadataParser.Parse(Metadata);

        Assert.Equal(ValueProblem.None, ParameterRules.Check(12.5, MavParamType.Real32, metadata["FW_AIRSPD_MIN"]));
        Assert.Equal(ValueProblem.AboveMaximum, ParameterRules.Check(41, MavParamType.Real32, metadata["FW_AIRSPD_MIN"]));
        Assert.Equal(ValueProblem.BelowMinimum, ParameterRules.Check(0.1, MavParamType.Real32, metadata["FW_AIRSPD_MIN"]));
        Assert.Equal(ValueProblem.NotWhole, ParameterRules.Check(2.5, MavParamType.Int32, null));
        Assert.Equal(ValueProblem.None, ParameterRules.Check(-1e9, MavParamType.Real32, null));
        Assert.True(ParameterRules.Same(0.1, (float)0.1, MavParamType.Real32));
        Assert.False(ParameterRules.Same(0.1, (float)0.1, MavParamType.Real64));
        Assert.True(ParameterRules.IsModified(12, metadata["FW_AIRSPD_MIN"]));
        Assert.False(ParameterRules.IsModified(10, metadata["FW_AIRSPD_MIN"]));
        Assert.False(ParameterRules.IsModified(10, null));
    }

    [Fact]
    public void A_params_file_round_trips_and_compares_with_the_vehicle()
    {
        var vehicle = new Dictionary<string, ParameterEntry>
        {
            ["FW_AIRSPD_MIN"] = new("FW_AIRSPD_MIN", new(MavParamType.Real32, 12.3f), 0, 3),
            ["SYS_AUTOSTART"] = new("SYS_AUTOSTART", new(MavParamType.Int32, 4008), 1, 3),
            ["GF_ACTION"] = new("GF_ACTION", new(MavParamType.Int32, 2), 2, 3)
        };

        var text = ParamsFileFormat.Write(vehicle, 1, 1, "1.15.0");
        var entries = ParamsFileFormat.Read(text);
        Assert.Equal(3, entries.Count);
        Assert.Empty(ParamsFileFormat.Compare(entries, vehicle, _ => null, 1).Rows);

        var edited = ParamsFileFormat.Read(text.Replace("4008", "4009", StringComparison.Ordinal) + "1\t1\tNOT_HERE\t5\t6\n1\t1\tGF_ACTION\t1.5\t6\nFW_AIRSPD_MIN,abc\n");
        var diff = ParamsFileFormat.Compare(edited, vehicle, _ => null, 2);
        Assert.True(diff.OtherVehicle);
        Assert.Equal(
            [("SYS_AUTOSTART", ParamsDiffKind.Changed), ("NOT_HERE", ParamsDiffKind.NotOnVehicle), ("FW_AIRSPD_MIN", ParamsDiffKind.Invalid), ("GF_ACTION", ParamsDiffKind.Invalid)],
            diff.Rows.Select(r => (r.Name, r.Kind)));
        Assert.Equal(4009, diff.Rows[0].FileValue);
    }

    [Fact]
    public void Setup_rules_hold()
    {
        ParameterReader From(params (string Name, double Value)[] values) => name => values.Where(v => v.Name == name).Select(v => (double?)v.Value).FirstOrDefault();

        Assert.Equal(SetupState.None, SetupRules.Sensors(From()));
        Assert.Equal(SetupState.Required, SetupRules.Sensors(From(("CAL_GYRO0_ID", 1), ("CAL_ACC0_ID", 1), ("CAL_MAG0_ID", 0))));
        Assert.Equal(SetupState.Ready, SetupRules.Sensors(From(("CAL_GYRO0_ID", 1), ("CAL_ACC0_ID", 1), ("CAL_MAG0_ID", 0), ("SYS_HAS_MAG", 0))));
        Assert.Equal(SetupState.Ready, SetupRules.Radio(From(("RC_MAP_ROLL", 0), ("COM_RC_IN_MODE", 1))));
        Assert.Equal(SetupState.Required, SetupRules.Radio(From(("RC_MAP_ROLL", 0), ("COM_RC_IN_MODE", 0))));
        Assert.Equal(SetupState.Ready, SetupRules.Power(From(("BAT1_SOURCE", -1))));
        Assert.Equal(SetupState.Required, SetupRules.Power(From(("BAT1_SOURCE", 0), ("BAT1_N_CELLS", 0))));
        Assert.Equal(SetupState.Required, SetupRules.Airframe(From(("SYS_AUTOSTART", 0))));
        Assert.True(SetupRules.IsNotFixedWing(From(("CA_AIRFRAME", 0))));
    }

    [Fact]
    public void The_envelope_checks_catch_an_infeasible_bank_and_crossed_airspeeds()
    {
        ParameterReader From(Dictionary<string, double> values) => name => values.TryGetValue(name, out var v) ? v : null;
        bool Fails(string key, Dictionary<string, double> values) => Px4ParameterGroups.Envelope.Checks.Single(c => c.Key == key).Fails(From(values));

        var ok = new Dictionary<string, double> { ["FW_AIRSPD_STALL"] = 8, ["FW_AIRSPD_MIN"] = 12, ["FW_AIRSPD_TRIM"] = 16, ["FW_AIRSPD_MAX"] = 25, ["FW_R_LIM"] = 40 };
        Assert.DoesNotContain(Px4ParameterGroups.Envelope.Checks, c => c.Fails(From(ok)));
        Assert.True(Fails("bank-infeasible", new(ok) { ["FW_R_LIM"] = 80 }));
        Assert.True(Fails("stall-above-min", new(ok) { ["FW_AIRSPD_STALL"] = 13 }));
        Assert.True(Fails("trim-outside", new(ok) { ["FW_AIRSPD_TRIM"] = 26 }));
        Assert.Contains("GF_ACTION", Px4ParameterGroups.Failsafes.Parameters);
    }

    [Fact]
    public async Task Metadata_loads_from_the_vehicle_after_its_parameters()
    {
        await using var station = await StationFixture.StartAsync(vehicleSetup: v =>
        {
            v.ComponentMetadata = ("mftp://etc/general.json", 7);
            v.Files["/etc/general.json"] = Encoding.UTF8.GetBytes(General("mftp://etc/parameters.json"));
            v.Files["/etc/parameters.json"] = Encoding.UTF8.GetBytes(Metadata);
        });
        var metadata = station.Get<MetadataService>();
        var vehicle = await station.ConnectAsync();

        await StationFixture.WaitForAsync(() => metadata.For(vehicle.Id) is not null);

        var loaded = metadata.For(vehicle.Id)!;
        Assert.Equal(MetadataSource.Vehicle, loaded.Source);
        Assert.Equal("Minimum Airspeed", loaded["FW_AIRSPD_MIN"]!.ShortDescription);
    }

    [Fact]
    public async Task Metadata_not_on_board_is_downloaded_once_and_then_read_from_the_cache()
    {
        var http = new FakeHttp(new Dictionary<string, byte[]> { ["https://example.org/parameters.json"] = Encoding.UTF8.GetBytes(Metadata) });
        await using var station = await StationFixture.StartAsync(
            register: services => services.AddSingleton(provider => new MetadataService(provider.GetRequiredService<VehicleRegistry>(),
                provider.GetRequiredService<StationPaths>(), NullLogger<MetadataService>.Instance, http)),
            vehicleSetup: v =>
            {
                v.ComponentMetadata = ("mftp://etc/general.json", 7);
                v.Files["/etc/general.json"] = Encoding.UTF8.GetBytes(General("mftp://etc/missing.json", "https://example.org/parameters.json"));
            });
        var metadata = station.Get<MetadataService>();
        var vehicle = await station.ConnectAsync();
        var first = await metadata.LoadAsync(vehicle);
        Assert.Equal(MetadataSource.Download, first.Source);
        Assert.Equal(5, first.Parameters.Count);

        http.Offline = true;
        var again = new MetadataService(station.Get<VehicleRegistry>(), station.Get<StationPaths>(), NullLogger<MetadataService>.Instance, http);
        var second = await again.LoadAsync(vehicle);
        again.Dispose();

        Assert.Equal(MetadataSource.Download, second.Source);
        Assert.Equal(5, second.Parameters.Count);
        Assert.Equal(1, http.Requests);
    }

    [Fact]
    public async Task Pending_changes_are_written_on_apply_and_a_change_back_drops_out()
    {
        await using var station = await StationFixture.StartAsync();
        var pending = station.Get<PendingParameterStore>();
        Assert.False(pending.Set("FW_AIRSPD_MIN", 13));
        await station.ConnectAsync();

        Assert.True(pending.Set("FW_AIRSPD_MIN", 13));
        Assert.True(pending.Set("SYS_AUTOSTART", 4009));
        Assert.False(pending.Set("NOT_ON_VEHICLE", 1));
        Assert.Equal(2, pending.Count);
        pending.Set("SYS_AUTOSTART", 4008);
        Assert.Equal(["FW_AIRSPD_MIN"], pending.Pending.Keys);

        var results = await pending.ApplyAsync();

        Assert.True(Assert.Single(results).IsApplied);
        Assert.Equal(0, pending.Count);
        Assert.Equal(13, station.Vehicle.Parameter("FW_AIRSPD_MIN"));
        Assert.Contains(station.Get<NotificationStore>().History, n => n.Source == PendingParameterStore.Source && n.Severity == NotificationSeverity.Success);
    }

    [Fact]
    public async Task A_refused_write_stays_pending_and_nothing_is_written_while_armed()
    {
        await using var station = await StationFixture.StartAsync();
        var vehicle = await station.ConnectAsync();
        var pending = station.Get<PendingParameterStore>();
        station.Vehicle.ReadOnlyParameters.Add("FW_AIRSPD_MAX");

        pending.Set("FW_AIRSPD_MAX", 30);
        pending.Set("FW_R_LIM", 45);
        var results = await pending.ApplyAsync();

        Assert.Equal([("FW_AIRSPD_MAX", false), ("FW_R_LIM", true)], results.Select(r => (r.Name, r.IsApplied)));
        Assert.Equal(["FW_AIRSPD_MAX"], pending.Pending.Keys);

        station.Vehicle.ReadOnlyParameters.Clear();
        var commands = station.Get<Ketras.Station.Commands.CommandService>();
        await commands.ExecuteAsync(new Ketras.Station.Commands.CommandRequest(commands.Commands.Single(c => c.Id == "arm")));
        await StationFixture.WaitForAsync(() => vehicle.Snapshot.IsArmed);
        Assert.Equal(EditBlock.Armed, ParameterRules.WhyNotEditable(vehicle));
        Assert.Empty(await pending.ApplyAsync());
        Assert.Equal(25, station.Vehicle.Parameter("FW_AIRSPD_MAX"));
    }
}
