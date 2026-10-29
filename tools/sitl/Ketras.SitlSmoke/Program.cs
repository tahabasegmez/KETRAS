// Smoke run of the station against a real PX4 SITL: starts it through a simulator link, loads the parameters, uploads
// a plan and a geofence, reads the mission back, switches modes, flies into the mission, records a .tlog and stops it.
// Usage: dotnet run --project tools/sitl/Ketras.SitlSmoke [-- --no-fly] [--keep] [--verbose]
//   [--landing [--orbit cw|ccw] [--heading DEG]] [--world flat|terrain-low|…] [--speed N] [--wind S@D]
// --landing flies the whole mission through its landing pattern and measures it instead of returning to launch.
using Ketras;
using Ketras.Commands;
using Ketras.Comms;
using Ketras.Geo;
using Ketras.Mavlink.Messages;
using Ketras.Parameters;
using Ketras.Planning;
using Ketras.Planning.Fences;
using Ketras.Planning.Missions;
using Ketras.Settings;
using Ketras.Setup;
using Ketras.Simulation;
using Ketras.Simulation.Px4Sitl;
using Ketras.Terrain;
using Ketras.Vehicles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var fly = !args.Contains("--no-fly");
var keep = args.Contains("--keep");
var verbose = args.Contains("--verbose");
// --speed 2 runs simulated time faster; --wind 8@270 sets a steady wind (m/s, from degrees) and checks PX4 estimates it.
string? Argument(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
var speed = Argument("--speed");
var wind = Argument("--wind")?.Split('@');
var landing = args.Contains("--landing");
var orbit = Argument("--orbit") ?? "cw";
var landingHeading = double.Parse(Argument("--heading") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
var world = Argument("--world");
var directory = Directory.CreateTempSubdirectory("ketras-sitl-smoke-").FullName;

var services = new ServiceCollection();
services.AddLogging(logging => logging
    .AddSimpleConsole(console => console.SingleLine = true)
    .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Warning));
services.AddSingleton(new StationPaths(Path.Combine(directory, "config"), Path.Combine(directory, "data")));
services.AddKetrasStation(options => options.Terrain.CacheDirectory = TerrainTileStore.DefaultDirectory);
services.AddPx4Sitl();
await using var provider = services.BuildServiceProvider();
var hosted = provider.GetServices<IHostedService>().ToList();
foreach (var service in hosted)
{
    await service.StartAsync(CancellationToken.None);
}

var connection = provider.GetRequiredService<ConnectionService>();
var vehicles = provider.GetRequiredService<VehicleRegistry>();
var commands = provider.GetRequiredService<CommandService>();
var plans = provider.GetRequiredService<PlanStore>();
var sitl = provider.GetServices<ISimulator>().Single();
var failures = 0;
Console.WriteLine($"Ketras SITL smoke run: {sitl.Description.Summary}; files in {directory}");

try
{
    await Step("start the SITL and connect", TimeSpan.FromMinutes(4), async cancellation =>
    {
        connection.StatusChanged += status => Console.WriteLine($"       link: {status.State} {status.Detail ?? status.Error}");
        if (speed is not null || wind is not null || world is not null)
        {
            // Started here with the options; the simulator link below then uses the running simulator.
            var options = new Dictionary<string, string>();
            if (speed is not null)
            {
                options[Px4Sitl.SpeedOption] = speed;
            }

            if (wind is [var windSpeed, var windFrom])
            {
                options[Px4Sitl.WindSpeedOption] = windSpeed;
                options[Px4Sitl.WindDirectionOption] = windFrom;
            }

            await sitl.StartAsync(new SimulatorLaunch(world ?? sitl.Description.Worlds[0].Id, sitl.Description.DefaultHome) { Options = options }, cancellation);
        }

        var profile = new LinkProfile { Name = "SITL", Kind = LinkKind.Simulator, SimulatorId = Px4Sitl.SimulatorId };
        if (!await connection.ConnectAsync(profile, cancellation))
        {
            throw new InvalidOperationException(connection.Status.Error ?? "not connected");
        }

        return $"{sitl.Link?.Address}";
    });

    await Step("load every parameter", TimeSpan.FromMinutes(2), async cancellation =>
    {
        await Until(() => vehicles.Active is { Parameters.IsLoaded: true }, cancellation);
        var vehicle = vehicles.Active!;
        var count = vehicle.Parameters.Values.Count;
        if (count < 1000)
        {
            throw new InvalidOperationException($"only {count} parameters");
        }

        return $"{count} parameters; limits {vehicle.Limits}";
    });

    await Step("load the metadata and resolve the actuators", TimeSpan.FromMinutes(2), async cancellation =>
    {
        var vehicle = vehicles.Active!;
        var metadata = await provider.GetRequiredService<MetadataService>().LoadAsync(vehicle).WaitAsync(cancellation);
        if (metadata.Parameters.Count < 1000 || metadata.ActuatorsJson is null)
        {
            throw new InvalidOperationException($"{metadata.Parameters.Count} parameter descriptions, actuators {(metadata.ActuatorsJson is null ? "missing" : "present")}; {string.Join("; ", metadata.Notes)}");
        }

        var model = ActuatorModel.Build(ActuatorMetadataParser.Parse(metadata.ActuatorsJson), name => vehicle.Parameters[name],
            (name, value) => metadata[name]?.Options.FirstOrDefault(o => o.Value == value)?.Description);
        if (!model.HasMetadata || model.Geometry.Count == 0)
        {
            throw new InvalidOperationException("no actuator geometry");
        }

        var pending = provider.GetRequiredService<PendingParameterStore>();
        var original = vehicle.Parameters["NAV_LOITER_RAD"] ?? 80;
        pending.Set("NAV_LOITER_RAD", original + 1);
        var written = await pending.ApplyAsync(cancellation);
        pending.Set("NAV_LOITER_RAD", original);
        await pending.ApplyAsync(cancellation);
        if (written is not [{ IsApplied: true }])
        {
            throw new InvalidOperationException($"apply: {string.Join("; ", written.Select(w => $"{w.Name} {w.Reason}"))}");
        }

        return $"{metadata.Parameters.Count} descriptions from {metadata.Source}; {model.GeometryTitle}: {string.Join(", ", model.TestActuators.Select(t => t.Label))}; outputs {string.Join(", ", model.Outputs.Select(o => o.Label))}";
    });

    await Step("read the health report", TimeSpan.FromMinutes(1), async cancellation =>
    {
        var vehicle = vehicles.Active!;
        await Until(() => vehicle.Health is not null, cancellation);
        var report = vehicle.Health!;
        if (!report.Checks.Any(c => c.Message.Contains("mission", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"no mission check before the upload: {string.Join("; ", report.Checks.Select(c => c.Message))}");
        }

        return $"{vehicle.EventDefinitions?.Count} event definitions; failing: {string.Join("; ", report.Checks.Select(c => $"{c.Message} ({c.Severity})"))}";
    });

    await Step("take the planned home from the vehicle", TimeSpan.FromMinutes(1), async cancellation =>
    {
        await Until(() => !plans.Current.Mission.Settings.PlannedHome.IsUnset, cancellation);
        var settings = plans.Current.Mission.Settings;
        return $"{settings.PlannedHome.Latitude:F6}, {settings.PlannedHome.Longitude:F6} at {settings.HomeAltitudeMeters:F1} m";
    });

    var uploaded = 0;
    await Step("upload the mission", TimeSpan.FromMinutes(3), async cancellation =>
    {
        var home = plans.Current.Mission.Settings.PlannedHome;
        plans.AddItem(MissionItemType.Takeoff, GeoMath.Offset(home, 0, 400), new MissionItemOptions { AltitudeMeters = 50 });
        plans.AddItem(MissionItemType.Waypoint, GeoMath.Offset(home, 20, 900), new MissionItemOptions { AltitudeMeters = 80 });
        plans.AddItem(MissionItemType.Waypoint, GeoMath.Offset(home, 110, 900), new MissionItemOptions { AltitudeMeters = 80 });
        plans.AddItem(MissionItemType.LandingPattern, home, new MissionItemOptions { HeadingDegrees = landingHeading });
        if (orbit == "ccw")
        {
            plans.Apply(plan => plan with
            {
                Mission = plan.Mission with
                {
                    Items = plan.Mission.Items.SetItem(plan.Mission.Items.Count - 1,
                        MissionItemParameters.Write(plan.Mission.Items[^1], MissionItemField.OrbitDirection, -1))
                }
            }, PlanChange.Mission);
        }

        var result = await Run("upload-mission", cancellation);
        uploaded = provider.GetRequiredService<MissionProgressTracker>().Uploaded(vehicles.Active!.Id)?.Items.Count ?? 0;

        // PX4 reports the change: the missing-mission check goes.
        await Until(() => vehicles.Active!.Health?.Checks.Any(c => c.Message.Contains("mission", StringComparison.OrdinalIgnoreCase)) == false, cancellation);
        return result + "; the health report no longer misses a mission";
    });

    await Step("read the mission back", TimeSpan.FromMinutes(1), async cancellation =>
    {
        var download = await vehicles.Active!.Session.Missions.DownloadAsync(MavMissionType.Mission, cancellation);
        if (!download.IsSuccess || download.Items?.Count != uploaded)
        {
            throw new InvalidOperationException($"{download.Items?.Count} of {uploaded} items read back: {download.Reason}");
        }

        return $"{uploaded} items: {string.Join(", ", download.Items.Select(i => (MavCmd)i.Command))}";
    });

    await Step("upload the geofence", TimeSpan.FromMinutes(1), async cancellation =>
    {
        var home = plans.Current.Mission.Settings.PlannedHome;
        plans.UpsertFence(new Fence("area", new CircleArea(home, 3000), Inclusion: true));
        plans.UpsertFence(new Fence("keep-out", new PolygonArea([GeoMath.Offset(home, 220, 1500), GeoMath.Offset(home, 240, 1500), GeoMath.Offset(home, 230, 2200)])));
        return await Run("upload-fences", cancellation);
    });

    await Step("switch to hold", TimeSpan.FromSeconds(30), async cancellation =>
    {
        await Run("mode-hold", cancellation);
        await Until(() => vehicles.Active!.Snapshot.Mode == "AUTO.LOITER", cancellation);
        return vehicles.Active!.Snapshot.Mode!;
    });

    if (fly)
    {
        await Step("arm once PX4 is ready", TimeSpan.FromMinutes(2), async cancellation =>
        {
            while (true)
            {
                var result = await commands.ExecuteAsync(new CommandRequest(Command("arm")), cancellation);
                if (result.IsAccepted)
                {
                    return "armed";
                }

                Console.WriteLine($"       not yet: {result.Message}");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellation);
            }
        });

        await Step("fly into the mission", TimeSpan.FromMinutes(3), async cancellation =>
        {
            var tracker = provider.GetRequiredService<MissionProgressTracker>();
            await Run("mode-mission", cancellation);
            await Until(() => tracker.Progress is { State: MissionExecution.Executing, CurrentItemIndex: >= 1 }, cancellation);
            var snapshot = vehicles.Active!.Snapshot;
            return $"item {tracker.Progress.CurrentItemIndex + 1} of the plan, {snapshot.RelativeAltitudeMeters:F0} m above home, {snapshot.AirspeedMps:F1} m/s";
        });

        if (landing)
        {
            await Step("fly the landing pattern and land", TimeSpan.FromMinutes(15), async cancellation => await FlyLandingAsync(cancellation));
        }

        await Step("return to launch", TimeSpan.FromSeconds(30), async cancellation =>
        {
            if (landing)
            {
                return "not needed: landed";
            }

            await Run("mode-rtl", cancellation);
            await Until(() => vehicles.Active!.Snapshot.Mode == "AUTO.RTL", cancellation);
            return "AUTO.RTL";
        });

        if (wind is [var setSpeed, var setFrom])
        {
            // Circling home after RTL, the plane crabs into the wind: its ground velocity is its heading times its true
            // airspeed plus the wind. Solved by least squares over the circle, that gives the wind. (PX4's own estimate
            // and its airspeed are no check: Gazebo's airspeed sensor ignores the wind.)
            await Step("the plane drifts with the wind that was set", TimeSpan.FromMinutes(4), async cancellation =>
            {
                await Until(() => vehicles.Active!.Snapshot is { Position: { } at, Home: { } home } && GeoMath.DistanceMeters(at, home) < 300, cancellation);
                var samples = new List<(double GroundNorth, double GroundEast, double HeadingNorth, double HeadingEast)>();
                var until = DateTime.UtcNow + TimeSpan.FromSeconds(40);
                while (DateTime.UtcNow < until)
                {
                    if (vehicles.Active!.Snapshot is { GroundSpeedMps: { } ground, CourseDegrees: { } course, HeadingDegrees: { } heading })
                    {
                        var c = course * Math.PI / 180;
                        var h = heading * Math.PI / 180;
                        samples.Add((ground * Math.Cos(c), ground * Math.Sin(c), Math.Cos(h), Math.Sin(h)));
                    }

                    await Task.Delay(100, cancellation);
                }

                // Unknowns x = (airspeed, wind north, wind east); rows (hN, 1, 0) = gN and (hE, 0, 1) = gE.
                var a = new double[3, 3];
                var b = new double[3];
                foreach (var (gN, gE, hN, hE) in samples)
                {
                    foreach (var (row, value) in new[] { (new[] { hN, 1.0, 0.0 }, gN), (new[] { hE, 0.0, 1.0 }, gE) })
                    {
                        for (var i = 0; i < 3; i++)
                        {
                            b[i] += row[i] * value;
                            for (var j = 0; j < 3; j++)
                            {
                                a[i, j] += row[i] * row[j];
                            }
                        }
                    }
                }

                var x = Solve3(a, b);
                var (north, east) = (x[1], x[2]);
                var measured = Math.Sqrt((north * north) + (east * east));
                var measuredFrom = ((Math.Atan2(-east, -north) * 180 / Math.PI) + 360) % 360;
                var expected = double.Parse(setSpeed, System.Globalization.CultureInfo.InvariantCulture);
                var from = double.Parse(setFrom, System.Globalization.CultureInfo.InvariantCulture);
                var result = System.FormattableString.Invariant(
                    $"{measured:F1} m/s from {measuredFrom:F0}°, true airspeed {x[0]:F1} m/s, {samples.Count} samples (set {expected} m/s from {from}°)");
                if (Math.Abs(measured - expected) > Math.Max(2, expected * 0.35) || Math.Abs(((measuredFrom - from + 540) % 360) - 180) > 30)
                {
                    throw new InvalidOperationException(result);
                }

                return result;
            });
        }
    }

    await Step("disconnect and keep the .tlog", TimeSpan.FromSeconds(30), async _ =>
    {
        await connection.DisconnectAsync();
        var tlog = Directory.GetFiles(Path.Combine(directory, "data", "tlogs"), "*.tlog").Single();
        await using var file = File.OpenRead(tlog);
        var packets = TlogReader.Read(file).Count();
        return $"{Path.GetFileName(tlog)}: {packets} packets, {new FileInfo(tlog).Length / 1024} KiB";
    });
}
finally
{
    if (!keep)
    {
        await Step("stop the SITL", TimeSpan.FromMinutes(1), async _ =>
        {
            await sitl.StopAsync();
            return sitl.Status.State.ToString();
        });
    }

    foreach (var service in Enumerable.Reverse(hosted))
    {
        await service.StopAsync(CancellationToken.None);
    }

    await connection.DisposeAsync();
}

Console.WriteLine(failures == 0 ? "SMOKE RUN PASSED" : $"SMOKE RUN FAILED ({failures} step(s))");
return failures == 0 ? 0 : 1;

VehicleCommand Command(string id) => commands.Commands.Single(c => c.Id == id);

// Follows the vehicle through the loiter and the land item: which way it orbits, how far it strays from the drawn
// final, where it touches down, and that PX4 lands and disarms on its own. Positions come at 10 Hz from the snapshot.
async Task<string> FlyLandingAsync(CancellationToken cancellation)
{
    var vehicle = vehicles.Active!;
    var item = plans.Current.Mission.Items.Single(i => i.Type == MissionItemType.LandingPattern);
    var touchdown = item.Location!.Value;
    var start = MissionItemValues.LandingPatternApproachStart(item)!.Value;
    var uploaded = provider.GetRequiredService<MissionProgressTracker>().Uploaded(vehicle.Id)!.Items;
    var loiterSeq = uploaded.ToList().FindIndex(i => i.Command == MavCmd.NavLoiterToAlt);
    var landSeq = uploaded.ToList().FindIndex(i => i.Command == MavCmd.NavLand);
    var said = new System.Collections.Concurrent.ConcurrentQueue<string>();
    vehicle.Message += (_, text) => said.Enqueue(text);

    double turn = 0, maxCross = 0;
    double? lastCourse = null;
    GeoPoint? down = null;
    var lastSeq = -1;
    while (true)
    {
        var snapshot = vehicle.Snapshot;
        var seq = vehicle.Session.Status.MissionCurrent;
        if (seq != lastSeq)
        {
            Console.WriteLine($"       mission item {seq} ({(seq >= 0 && seq < uploaded.Count ? uploaded[seq].Command : default)}), {snapshot.RelativeAltitudeMeters:F0} m");
            lastSeq = seq;
        }

        if (seq == loiterSeq && snapshot.CourseDegrees is { } course && snapshot.GroundSpeedMps > 5)
        {
            if (lastCourse is { } previous)
            {
                turn += ((course - previous + 540) % 360) - 180;
            }

            lastCourse = course;
        }

        // The final: from the approach start to the touchdown, while still above flare height.
        if (seq == landSeq && snapshot.Position is { } at && snapshot.RelativeAltitudeMeters - item.Param1 > 5)
        {
            var along = GeoMath.DistanceMeters(start, at) * Math.Cos((GeoMath.BearingDegrees(start, at) - GeoMath.BearingDegrees(start, touchdown)) * Math.PI / 180);
            var cross = GeoMath.DistanceMeters(start, at) * Math.Sin((GeoMath.BearingDegrees(start, at) - GeoMath.BearingDegrees(start, touchdown)) * Math.PI / 180);
            if (along > 0 && along < GeoMath.DistanceMeters(start, touchdown))
            {
                maxCross = Math.Max(maxCross, Math.Abs(cross));
            }
        }

        if (down is null && seq == landSeq && snapshot.IsLanded == true && snapshot.Position is { } contact)
        {
            down = contact;
        }

        if (down is not null && !snapshot.IsArmed)
        {
            break;
        }

        await Task.Delay(100, cancellation);
    }

    var flown = turn > 0 ? "cw" : "ccw";
    var miss = GeoMath.DistanceMeters(down!.Value, touchdown);
    var result = System.FormattableString.Invariant(
        $"orbit {flown} ({turn:F0}° turned), final {GeoMath.DistanceMeters(start, touchdown):F0} m at {MissionItemValues.LandingGlideSlopeDegrees(item):F1}°, largest cross-track {maxCross:F1} m, touchdown {miss:F0} m from the plan, disarmed");
    Console.WriteLine($"       vehicle said: {string.Join(" | ", said.Distinct())}");
    if (flown != orbit || maxCross > 10 || miss > 60)
    {
        throw new InvalidOperationException(result);
    }

    return result;
}

async Task<string> Run(string id, CancellationToken cancellation)
{
    var result = await commands.ExecuteAsync(new CommandRequest(Command(id)), cancellation);
    return result.IsAccepted ? result.Message ?? "accepted" : throw new InvalidOperationException(result.Message);
}

async Task Step(string name, TimeSpan timeout, Func<CancellationToken, Task<string>> step)
{
    if (failures > 0 && name != "stop the SITL")
    {
        Console.WriteLine($"SKIP   {name}");
        return;
    }

    var started = DateTime.UtcNow;
    using var cancellation = new CancellationTokenSource(timeout);
    try
    {
        var detail = await step(cancellation.Token);
        Console.WriteLine($"PASS   {name} ({(DateTime.UtcNow - started).TotalSeconds:F1} s): {detail}");
    }
    catch (Exception exception)
    {
        failures++;
        var reason = exception is OperationCanceledException ? $"timed out after {timeout.TotalSeconds:F0} s" : exception.Message;
        Console.WriteLine($"FAIL   {name}: {reason}");
    }
}

static async Task Until(Func<bool> condition, CancellationToken cancellation)
{
    while (!condition())
    {
        await Task.Delay(100, cancellation);
    }
}

static double[] Solve3(double[,] a, double[] b)
{
    // Cramer's rule; the system is 3 × 3 and well conditioned while the plane circles.
    static double Det(double[,] m) =>
        (m[0, 0] * ((m[1, 1] * m[2, 2]) - (m[1, 2] * m[2, 1]))) - (m[0, 1] * ((m[1, 0] * m[2, 2]) - (m[1, 2] * m[2, 0])))
        + (m[0, 2] * ((m[1, 0] * m[2, 1]) - (m[1, 1] * m[2, 0])));
    var d = Det(a);
    var x = new double[3];
    for (var k = 0; k < 3; k++)
    {
        var m = (double[,])a.Clone();
        for (var i = 0; i < 3; i++)
        {
            m[i, k] = b[i];
        }

        x[k] = Det(m) / d;
    }

    return x;
}