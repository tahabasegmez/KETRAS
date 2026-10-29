using System.Collections.Concurrent;
using Ketras.Geo;
using Ketras.Simulation;
using Ketras.Simulation.Px4Sitl;
using Ketras.Terrain;

namespace Ketras.Tests.Simulation;

/// <summary>Runs only on Linux, where the fake docker is a bash script.</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "needs bash for the fake docker (Linux)";
        }
    }
}

/// <summary>
/// The SITL runner against a fake docker: a bash script that records its arguments (calls.log) and plays the parts of
/// the docker commands Px4Sitl uses. Files in its folder set the scene: engine-down, image, running, display,
/// px4-failure, px4-not-ready, px4-exit, build-fails. Its "run" prints PX4's startup lines and waits for "shutdown" on
/// stdin, the way PX4's pxh> does; "rm -f" ends it.
/// </summary>
public sealed class Px4SitlTests : IDisposable
{
    private const string FakeDocker = """
        #!/bin/bash
        dir="$(cd "$(dirname "$0")" && pwd)"
        echo "$*" >> "$dir/calls.log"
        case "$1" in
          version) echo "28.5.2" ;;
          info)
            if [ -f "$dir/engine-down" ]; then echo "Cannot connect to the Docker daemon" >&2; exit 1; fi
            echo "28.5.2" ;;
          image)
            if [ "$2" = "ls" ]; then [ -f "$dir/image" ] && echo "8.01GB"; exit 0; fi
            if [ "$2" = "rm" ]; then rm -f "$dir/image"; echo "Untagged"; exit 0; fi ;;
          ps) [ -f "$dir/running" ] && echo "abc123"; exit 0 ;;
          inspect) [ -f "$dir/world" ] && cat "$dir/world"; exit 0 ;;
          rm)
            [ -f "$dir/run.pid" ] && kill "$(cat "$dir/run.pid")" 2>/dev/null
            rm -f "$dir/run.pid" "$dir/running"; exit 0 ;;
          exec)
            if [[ "$*" == *gz_gui.sh* ]]; then echo "INFO  [ketras] Opening the Gazebo window"; read -r _; exit 0; fi
            echo gazebo; [ -f "$dir/display" ] && echo display; exit 0 ;;
          build)
            echo "#1 [1/2] FROM docker.io/library/ubuntu:22.04"
            echo "#2 [2/2] RUN make px4_sitl"
            echo "#2 12.3 [812/1532] Building CXX object src/foo.cpp.o"
            if [ -f "$dir/build-fails" ]; then echo "#2 ERROR: process did not complete successfully"; exit 1; fi
            cat > /dev/null; touch "$dir/image"; exit 0 ;;
          run)
            echo $$ > "$dir/run.pid"; touch "$dir/running"
            echo "INFO  [ketras] Starting Gazebo"
            echo "INFO  [init] PX4 starting"
            echo "WARN  [health] no RC"
            [ -f "$dir/px4-failure" ] && cat "$dir/px4-failure"
            [ -f "$dir/px4-not-ready" ] || echo "pxh> INFO  [init] Startup script returned successfully"
            if [ -f "$dir/px4-exit" ]; then sleep 0.5; rm -f "$dir/running"; exit "$(cat "$dir/px4-exit")"; fi
            while read -r line; do
              if [ "$line" = "shutdown" ]; then echo "INFO  [shutdown] bye"; rm -f "$dir/running"; exit 0; fi
            done ;;
        esac
        """;

    private readonly string _directory = Directory.CreateTempSubdirectory("ketras-sitl-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static readonly SimulatorLaunch Flat = new("flat", new GeoPoint(47.5, 8.5));

    [Fact]
    public void The_description_offers_the_worlds_and_the_version_speed_and_wind_options()
    {
        var origin = new GeoPoint(47, 8);
        var sitl = new Px4Sitl(new Px4SitlOptions { WorldOrigin = origin, DataDirectory = _directory });

        Assert.Equal(Px4Sitl.SimulatorId, sitl.Description.Id);
        Assert.Equal(origin, sitl.Description.DefaultHome);
        Assert.Equal("Gazebo", sitl.Description.WindowName);
        Assert.Equal(["flat", "terrain-low", "terrain-medium", "terrain-high"], sitl.Description.Worlds.Select(w => w.Id));
        Assert.Equal([false, true, true, true], sitl.Description.Worlds.Select(w => w.HasTerrain));
        Assert.All(sitl.Description.Worlds, world => Assert.False(world.FixedHome));
        Assert.Equal("ketras_terrain_47p39797_8p54616", Px4Sitl.TerrainWorldName(new GeoPoint(47.397971, 8.546164)));
        Assert.Equal("ketras_terrain_m33p86000_151p21000", Px4Sitl.TerrainWorldName(new GeoPoint(-33.86, 151.21)));
        Assert.Equal([Px4Sitl.VersionOption, Px4Sitl.SpeedOption, Px4Sitl.WindSpeedOption, Px4Sitl.WindDirectionOption], sitl.Description.Options.Select(o => o.Key));
        Assert.All(sitl.Description.Options, option => Assert.NotEmpty(option.Help));
        Assert.Equal("v1.17.0", sitl.Description.Options[0].Default);
        Assert.Equal(SimulatorRunState.Stopped, sitl.Status.State);
        Assert.Equal(new SimulatorWindow(false, false, "The Gazebo window opens once the simulator runs."), sitl.Status.Window);
        Assert.Null(sitl.Link);
    }

    [Fact]
    public async Task Without_docker_the_checklist_says_how_to_install_it_and_a_start_fails_with_that()
    {
        var sitl = new Px4Sitl(new Px4SitlOptions { DockerCommand = "ketras-no-such-docker", DataDirectory = _directory });

        var checks = await sitl.CheckAsync(Flat);
        var docker = checks.Single(c => c.Title == "Docker");
        Assert.True(docker.Blocks);
        Assert.Contains("Install Docker", docker.Fix, StringComparison.Ordinal);
        Assert.Equal(CheckState.NotChecked, checks.Single(c => c.Title == "Environment").State);

        await sitl.StartAsync(Flat);
        Assert.Equal(SimulatorRunState.Stopped, sitl.Status.State);
        Assert.StartsWith("Docker is not installed. Install Docker", sitl.Status.Error);
    }

    [LinuxFact]
    public async Task The_checklist_follows_the_engine_and_offers_to_build_or_remove_the_chosen_version()
    {
        var (sitl, _) = Create();
        Touch("engine-down");
        var checks = await sitl.CheckAsync(Flat);
        Assert.Equal(("The Docker engine is not running.", "sudo systemctl start docker"),
            (checks.Single(c => c.Title == "Docker engine").Detail, checks.Single(c => c.Title == "Docker engine").Fix));

        File.Delete(Path.Combine(_directory, "engine-down"));
        var old = Flat with { Options = new Dictionary<string, string> { [Px4Sitl.VersionOption] = "v1.16.2" } };
        var environment = (await sitl.CheckAsync(old)).Single(c => c.Title == "Environment");
        Assert.Equal(("Not built for PX4 v1.16.2 yet.", true), (environment.Detail, environment.Blocks));
        Assert.Equal(new SimulatorAction(Px4Sitl.BuildAction, "Build environment"), environment.Action);
        Assert.Contains("image ls --format {{.Size}} ketras/px4-sitl:v1.16.2", Calls());

        Touch("image");
        environment = (await sitl.CheckAsync(Flat)).Single(c => c.Title == "Environment");
        Assert.Equal((CheckState.Ready, "PX4 v1.17.0, 8.01GB on disk."), (environment.State, environment.Detail));
        Assert.Equal(new SimulatorAction(Px4Sitl.RemoveAction, "Remove", IsDestructive: true), environment.Action);
        Assert.DoesNotContain(await sitl.CheckAsync(Flat), c => c.Blocks);
    }

    [LinuxFact]
    public async Task Building_the_environment_prepares_reports_progress_and_ends_stopped()
    {
        var (sitl, output) = Create();
        var states = new ConcurrentQueue<SimulatorStatus>();
        sitl.StatusChanged += states.Enqueue;

        await sitl.RunActionAsync(Px4Sitl.BuildAction, Flat);

        Assert.True(File.Exists(Path.Combine(_directory, "image")));
        Assert.Contains("build --progress=plain -t ketras/px4-sitl:v1.17.0 --build-arg PX4_VERSION=v1.17.0 -", Calls());
        Assert.Contains(states, s => s is { State: SimulatorRunState.Preparing, Detail: "building the environment for PX4 v1.17.0: step 2 of 2, compiling 812 of 1532" });
        Assert.Contains(output, o => o.Item1 == "build step 2 of 2: RUN make px4_sitl");
        Assert.DoesNotContain(output, o => o.Item1.Contains("Building CXX", StringComparison.Ordinal));
        Assert.Equal(SimulatorStatus.Stopped with { Window = sitl.Status.Window }, sitl.Status);

        Touch("build-fails");
        await sitl.RunActionAsync(Px4Sitl.BuildAction, Flat);
        Assert.Equal("The environment could not be built: #2 ERROR: process did not complete successfully", sitl.Status.Error);

        await sitl.RunActionAsync(Px4Sitl.RemoveAction, Flat);
        Assert.False(File.Exists(Path.Combine(_directory, "image")));
    }

    [LinuxFact]
    public async Task A_start_runs_the_container_with_home_wind_and_speed_and_a_stop_shuts_px4_down()
    {
        var (sitl, output) = Create();
        Touch("image");
        var launch = Flat with
        {
            Options = new Dictionary<string, string> { [Px4Sitl.SpeedOption] = "2", [Px4Sitl.WindSpeedOption] = "35", [Px4Sitl.WindDirectionOption] = "90" }
        };

        await sitl.StartAsync(launch);
        await WaitForAsync(() => sitl.Status.State == SimulatorRunState.Running);

        var run = Calls().Single(c => c.StartsWith("run --rm -i", StringComparison.Ordinal));
        Assert.Contains("--name ketras-px4-sitl -p 127.0.0.1:18570:18570/udp --label ketras.world=flat", run, StringComparison.Ordinal);
        Assert.Equal("flat", sitl.Status.WorldId);
        Assert.Contains("-e KETRAS_HOME_LAT=47.5 -e KETRAS_HOME_LON=8.5 -e KETRAS_HOME_ALT=450.0", run, StringComparison.Ordinal);
        Assert.Contains("-e KETRAS_WIND_SPEED=20 -e KETRAS_WIND_DIRECTION=90 -e PX4_SIM_SPEED_FACTOR=2", run, StringComparison.Ordinal);
        Assert.EndsWith("ketras/px4-sitl:v1.17.0 bash /opt/ketras/px4.sh", run, StringComparison.Ordinal);
        Assert.Equal(("udp://127.0.0.1:18570", "px4"), (sitl.Link!.Address, sitl.Link.Autopilot));
        Assert.Contains(("WARN  [health] no RC", SimulatorOutputLevel.Warning), output);

        await sitl.StopAsync();

        Assert.Equal(SimulatorRunState.Stopped, sitl.Status.State);
        Assert.Null(sitl.Link);
        Assert.Contains(("INFO  [shutdown] bye", SimulatorOutputLevel.Info), output);
        Assert.Contains("rm -f ketras-px4-sitl", Calls());
    }

    [LinuxFact]
    public async Task The_gazebo_window_opens_only_once_running_with_a_display()
    {
        var (sitl, _) = Create();
        Touch("image");
        await sitl.StartAsync(Flat);
        await WaitForAsync(() => sitl.Status.Window?.Reason?.StartsWith("No display", StringComparison.Ordinal) == true);
        Assert.False(sitl.Status.Window!.CanOpen);

        await sitl.StopAsync();
        Touch("display");
        await sitl.StartAsync(Flat);
        await WaitForAsync(() => sitl.Status.Window is { CanOpen: true });

        await sitl.OpenWindowAsync();
        Assert.Equal(new SimulatorWindow(false, true, "The Gazebo window is open."), sitl.Status.Window);
        await WaitForAsync(() => Calls().Any(c => c.StartsWith("exec -i ketras-px4-sitl bash /opt/ketras/gz_gui.sh", StringComparison.Ordinal)));
        await sitl.StopAsync();
    }

    [LinuxFact]
    public async Task A_container_already_running_is_picked_up_on_refresh()
    {
        var (sitl, _) = Create();
        await sitl.RefreshAsync();
        Assert.Equal(SimulatorRunState.Stopped, sitl.Status.State);

        Touch("running");
        Touch("display");
        File.WriteAllText(Path.Combine(_directory, "world"), "terrain-low\n");
        await sitl.RefreshAsync();

        Assert.Equal(new SimulatorStatus(SimulatorRunState.Running, "started outside this application", Window: new SimulatorWindow(true, false)) { WorldId = "terrain-low" },
            sitl.Status);
        Assert.Equal("udp://127.0.0.1:18570", sitl.Link!.Address);
    }

    [LinuxFact]
    public async Task Px4_ending_on_its_own_stops_the_simulator_with_its_last_error()
    {
        var (sitl, _) = Create();
        Touch("image");
        File.WriteAllText(Path.Combine(_directory, "px4-failure"), "ERROR [gz_bridge] world not found\n");
        File.WriteAllText(Path.Combine(_directory, "px4-exit"), "3");

        await sitl.StartAsync(Flat);
        await WaitForAsync(() => sitl.Status is { State: SimulatorRunState.Stopped, Error: not null });

        Assert.Equal("PX4 exited (code 3): ERROR [gz_bridge] world not found", sitl.Status.Error);
        Assert.Null(sitl.Link);
    }

    [LinuxFact]
    public async Task A_start_that_never_gets_ready_gives_up_with_the_last_error()
    {
        var (sitl, _) = Create(startTimeout: TimeSpan.FromSeconds(1));
        Touch("image");
        Touch("px4-not-ready");
        File.WriteAllText(Path.Combine(_directory, "px4-failure"), "ERROR [ketras] Gazebo did not start\n");

        await sitl.StartAsync(Flat);
        await WaitForAsync(() => sitl.Status is { State: SimulatorRunState.Stopped, Error: not null });

        Assert.Equal("PX4 did not get ready within 1 s: ERROR [ketras] Gazebo did not start", sitl.Status.Error);
    }

    private (Px4Sitl Sitl, ConcurrentQueue<(string, SimulatorOutputLevel)> Output) Create(TimeSpan? startTimeout = null)
    {
        var docker = Path.Combine(_directory, "docker");
        File.WriteAllText(docker, FakeDocker.ReplaceLineEndings("\n") + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(docker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var scripts = Directory.CreateDirectory(Path.Combine(_directory, "scripts")).FullName;
        File.WriteAllText(Path.Combine(scripts, "Dockerfile"), "FROM ubuntu:22.04\n");
        var cache = Path.Combine(_directory, "dem");
        // A synthetic N47E008 cell: 400 m at 48° N, 1 m higher per 0.01° southwards, so 450 m at 47.5° N.
        SyntheticGeoTiff.Write(new TerrainTileStore(cache).TilePath(47, 8), 101, 101, 32, 48, 8, 0.01, (row, _) => 400 + row);

        var sitl = new Px4Sitl(new Px4SitlOptions
        {
            DockerCommand = docker, ScriptsDirectory = scripts, DataDirectory = Path.Combine(_directory, "data"), TerrainCacheDirectory = cache,
            StartTimeout = startTimeout ?? TimeSpan.FromMinutes(5)
        });
        var output = new ConcurrentQueue<(string, SimulatorOutputLevel)>();
        sitl.Output += (line, level) => output.Enqueue((line, level));
        return (sitl, output);
    }

    private void Touch(string name) => File.WriteAllText(Path.Combine(_directory, name), string.Empty);

    private List<string> Calls() => [.. File.ReadAllLines(Path.Combine(_directory, "calls.log"))];

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition not reached");
            }

            await Task.Delay(20);
        }
    }
}
