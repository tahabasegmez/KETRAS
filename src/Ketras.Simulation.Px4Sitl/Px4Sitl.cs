using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Ketras.Model.Geo;
using Ketras.Station.Links;
using Ketras.Station.Settings;
using Ketras.Station.Simulation;
using Ketras.Terrain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ketras.Simulation.Px4Sitl;

/// <summary>Where <see cref="Px4Sitl"/> finds Docker, its scripts and its data, and how it reaches PX4.</summary>
public sealed record Px4SitlOptions
{
    /// <summary>The folder with the Dockerfile, <c>px4.sh</c>, <c>gz_gui.sh</c> and <c>make_terrain_world.py</c>;
    /// null for the <c>sitl</c> folder this package copies next to the application.</summary>
    public string? ScriptsDirectory { get; init; }

    /// <summary>The docker executable.</summary>
    public string DockerCommand { get; init; } = "docker";

    /// <summary>Host folder for the generated worlds, the logs and PX4's working directory; null for <c>sitl</c> in the station's data folder.</summary>
    public string? DataDirectory { get; init; }

    /// <summary>Copernicus DEM cache, for the home altitude and the terrain worlds; null for Ketras.Terrain's default.</summary>
    public string? TerrainCacheDirectory { get; init; }

    /// <summary>The PX4 releases offered, newest first; each is its own image.</summary>
    public IReadOnlyList<string> Px4Versions { get; init; } = ["v1.17.0", "v1.16.2"];

    /// <summary>The images' repository; the tag is the PX4 version.</summary>
    public string ImageRepository { get; init; } = "ketras/px4-sitl";

    /// <summary>Name of the simulator's container.</summary>
    public string ContainerName { get; init; } = "ketras-px4-sitl";

    /// <summary>PX4's GCS MAVLink port, published on the host's loopback.</summary>
    public int GcsPort { get; init; } = 18570;

    /// <summary>Name of the saved link to the running SITL.</summary>
    public string ProfileName { get; init; } = "PX4 SITL";

    /// <summary>PX4's default world origin (its gz default.sdf): the default home of every world.</summary>
    public GeoPoint WorldOrigin { get; init; } = new(47.397971057728974, 8.546163739800146);

    /// <summary>How long a start may take before it is given up (building a terrain world counts).</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long building an environment may take.</summary>
    public TimeSpan BuildTimeout { get; init; } = TimeSpan.FromHours(2);
}

/// <summary>
/// Strategy (<see cref="ISimulator"/>): PX4 SITL (fixed-wing gz_advanced_plane, Gazebo) in a Docker container, the same
/// on Windows and Linux. The environment is an image per PX4 version, built here from the Dockerfile next to the
/// application; the scripts and the data folder are mounted into the container. A start builds a missing terrain world,
/// runs px4.sh (Gazebo server with the wind, then PX4 at the home on the DEM) and publishes PX4's GCS port on the
/// loopback; PX4 is ready once its startup script returns. The Gazebo window opens on request once the simulator runs
/// and a display reaches the container. A stop asks PX4 to shut down and removes the container.
/// </summary>
public sealed partial class Px4Sitl : ISimulator
{
    /// <summary><see cref="SimulatorDescription.Id"/> of this simulator, as saved in SITL link profiles.</summary>
    public const string SimulatorId = "px4-sitl";

    /// <summary>Option key: the PX4 version (image).</summary>
    public const string VersionOption = "px4.version";

    /// <summary>Option key: the simulation speed factor.</summary>
    public const string SpeedOption = "speed";

    /// <summary>Option key: the wind speed, m/s.</summary>
    public const string WindSpeedOption = "wind.speed";

    /// <summary>Option key: the direction the wind blows from, degrees.</summary>
    public const string WindDirectionOption = "wind.direction";

    private const string FlatWorld = "flat";
    private const string WorldLabel = "ketras.world";
    private const string ScriptsMount = "/opt/ketras";
    private const string DataMount = "/data";
    private const string DemMount = "/dem";
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(8);

    private readonly Px4SitlOptions _options;
    private readonly ILogger _logger;
    private readonly DockerCli _docker;
    private readonly string _scripts;
    private readonly string _data;
    private readonly object _lock = new();
    private Process? _px4;
    private Process? _gui;
    private CancellationTokenSource? _preparing;
    private SimulatorStatus _status;
    private LinkProfile? _link;
    private string _worldName = string.Empty;
    private string? _worldId;
    private string? _lastError;
    private Timer? _startTimer;
    private string? _user;

    /// <summary>A SITL runner in Docker; <paramref name="logger"/> also gets every output line.</summary>
    public Px4Sitl(Px4SitlOptions? options = null, ILogger<Px4Sitl>? logger = null)
    {
        _options = options ?? new Px4SitlOptions();
        _logger = logger ?? NullLogger<Px4Sitl>.Instance;
        _docker = new DockerCli(_options.DockerCommand);
        _scripts = Path.GetFullPath(_options.ScriptsDirectory ?? Path.Combine(AppContext.BaseDirectory, "sitl"));
        _data = Path.GetFullPath(_options.DataDirectory ?? Path.Combine(StationPaths.Default.DataDirectory, "sitl"));
        _status = new SimulatorStatus(SimulatorRunState.Stopped, Window: WindowWhileNotRunning);
        Description = new SimulatorDescription
        {
            Id = SimulatorId,
            Name = "PX4 SITL",
            Summary = "Fixed-wing gz_advanced_plane in Gazebo, in Docker",
            WindowName = "Gazebo",
            Worlds =
            [
                new(FlatWorld, "Flat", "PX4's default flat world; the home can be anywhere and sits at the DEM height.", false) { HasTerrain = false },
                new("terrain-low", "Terrain, low detail", "Copernicus DEM around the home, 10 km square, 78 m height spacing. Built once per home.", false),
                new("terrain-medium", "Terrain, medium detail", "Copernicus DEM around the home, 10 km square, 39 m height spacing. Built once per home.", false),
                new("terrain-high", "Terrain, high detail", "Copernicus DEM around the home, 10 km square, 20 m height spacing. Built once per home; slower in Gazebo.", false)
            ],
            Options =
            [
                new(VersionOption, "PX4 version",
                    "The PX4 release the simulator runs. Each version is its own environment, built once from the Environment section.",
                    _options.Px4Versions[0])
                {
                    Choices = [.. _options.Px4Versions.Select(v => new SimulatorChoice(v, v))]
                },
                new(SpeedOption, "Simulation speed",
                    "How fast simulated time runs against the clock. 2× and 4× fly a mission sooner; PX4 stretches its link-loss "
                    + "timeouts to match. A slow computer lags behind at higher speeds.", "1")
                {
                    Choices = [new("1", "1× (real time)"), new("2", "2×"), new("4", "4×")]
                },
                new(WindSpeedOption, "Wind speed", "A steady wind over the whole world. 0 is calm air.", "0") { Minimum = 0, Maximum = 20, Unit = "m/s" },
                new(WindDirectionOption, "Wind from", "Where the wind blows from: 0 north, 90 east, 180 south, 270 west.", "270") { Minimum = 0, Maximum = 359, Unit = "°" }
            ],
            DefaultHome = _options.WorldOrigin
        };
    }

    /// <inheritdoc />
    public SimulatorDescription Description { get; }

    /// <inheritdoc />
    public SimulatorStatus Status
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    /// <inheritdoc />
    public LinkProfile? Link
    {
        get
        {
            lock (_lock)
            {
                return _link;
            }
        }
    }

    /// <inheritdoc />
    public event Action<SimulatorStatus>? StatusChanged;

    /// <inheritdoc />
    public event Action<string, SimulatorOutputLevel>? Output;

    private static SimulatorWindow WindowWhileNotRunning => new(false, false, "The Gazebo window opens once the simulator runs.");

    /// <inheritdoc />
    public async Task RefreshAsync(CancellationToken cancellation = default)
    {
        lock (_lock)
        {
            if (_px4 is not null || _status.State != SimulatorRunState.Stopped)
            {
                return;
            }
        }

        try
        {
            if (await IsContainerRunningAsync(cancellation))
            {
                _worldId = await ContainerWorldAsync(cancellation);
                SetStatus(new SimulatorStatus(SimulatorRunState.Running, "started outside this application"), NewLink());
                await UpdateWindowAsync(cancellation);
            }
        }
        catch (Exception exception) when (exception is Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            // No docker, or it did not answer: nothing is running that the station could use.
        }
    }

    /// <inheritdoc />
    public async Task StartAsync(SimulatorLaunch launch, CancellationToken cancellation = default)
    {
        lock (_lock)
        {
            if (_status.State != SimulatorRunState.Stopped)
            {
                throw new InvalidOperationException($"{Description.Name} is already {_status.State.ToString().ToLowerInvariant()}.");
            }

            _status = new SimulatorStatus(SimulatorRunState.Starting, "checking the environment", Window: WindowWhileNotRunning);
        }

        StatusChanged?.Invoke(Status);
        try
        {
            if ((await CheckAsync(launch, cancellation)).FirstOrDefault(c => c.Blocks) is { } blocker)
            {
                throw new InvalidOperationException(blocker.Fix is { } fix ? $"{blocker.Detail} {fix}" : blocker.Detail);
            }

            if (await IsContainerRunningAsync(cancellation))
            {
                Emit("PX4 SITL is already running in Docker; using it.", SimulatorOutputLevel.Warning);
                _worldId = await ContainerWorldAsync(cancellation);
                SetStatus(new SimulatorStatus(SimulatorRunState.Running, "started outside this application"), NewLink());
                await UpdateWindowAsync(cancellation);
                return;
            }

            lock (_lock)
            {
                _lastError = null;
                _startTimer?.Dispose();
                _startTimer = new Timer(_ => _ = GiveUpStartAsync(), null, _options.StartTimeout, Timeout.InfiniteTimeSpan);
            }

            var world = Description.Worlds.FirstOrDefault(w => w.Id == launch.WorldId) ?? Description.Worlds[0];
            _worldName = world.Name.ToLowerInvariant();
            _worldId = world.Id;
            var image = ImageFor(launch);
            var environment = new List<string>();
            var arguments = new List<string> { "bash", $"{ScriptsMount}/px4.sh" };
            if (world.Id.StartsWith("terrain-", StringComparison.Ordinal))
            {
                var quality = world.Id["terrain-".Length..];
                var name = TerrainWorldName(launch.Home);
                await EnsureTerrainWorldAsync(image, quality, name, launch.Home, cancellation);
                arguments.AddRange(["terrain", quality, name]);
            }
            else
            {
                SetStatus(new SimulatorStatus(SimulatorRunState.Starting, "reading the home altitude"), null);
                var altitude = await GroundHeightAsync(launch.Home.Latitude, launch.Home.Longitude, cancellation);
                environment.AddRange([
                    Invariant($"KETRAS_HOME_LAT={launch.Home.Latitude:R}"),
                    Invariant($"KETRAS_HOME_LON={launch.Home.Longitude:R}"),
                    Invariant($"KETRAS_HOME_ALT={altitude:F1}")
                ]);
            }

            environment.Add($"KETRAS_WIND_SPEED={Number(launch, WindSpeedOption)}");
            environment.Add($"KETRAS_WIND_DIRECTION={Number(launch, WindDirectionOption)}");
            if (Number(launch, SpeedOption) is var speed && speed != "1")
            {
                environment.Add($"PX4_SIM_SPEED_FACTOR={speed}");
            }

            SetStatus(new SimulatorStatus(SimulatorRunState.Starting, $"starting PX4 ({_worldName} world)"), null);
            var run = new List<string>
            {
                "run", "--rm", "-i", "--name", _options.ContainerName, "-p", $"127.0.0.1:{_options.GcsPort}:{_options.GcsPort}/udp",
                "--label", $"{WorldLabel}={world.Id}"
            };
            run.AddRange(await CommonRunArgumentsAsync(cancellation));
            run.AddRange(DisplayArguments());
            foreach (var variable in environment)
            {
                run.AddRange(["-e", variable]);
            }

            run.Add(image);
            run.AddRange(arguments);
            // stdin stays open and silent: PX4's pxh> console waits on it instead of spinning on an end of file.
            var px4 = _docker.Launch(run, redirectInput: true, line => OnLine("", line));
            px4.Exited += (_, _) => OnPx4Exited(px4);
            lock (_lock)
            {
                _px4 = px4;
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException
                                              or OperationCanceledException or HttpRequestException)
        {
            // A start that fails leaves nothing behind and says why in the status and the output.
            Emit(exception.Message, SimulatorOutputLevel.Error);
            await CleanUpAsync();
            SetStatus(new SimulatorStatus(SimulatorRunState.Stopped, Error: exception.Message), null);
        }
    }

    /// <inheritdoc />
    public async Task OpenWindowAsync()
    {
        lock (_lock)
        {
            if (_status.Window is not { CanOpen: true })
            {
                return;
            }
        }

        var gui = _docker.Launch(["exec", "-i", _options.ContainerName, "bash", $"{ScriptsMount}/gz_gui.sh"], redirectInput: true, line => OnLine("Gazebo: ", line));
        gui.Exited += (_, _) => OnGuiExited(gui);
        lock (_lock)
        {
            _gui = gui;
        }

        SetWindow(new SimulatorWindow(false, true, "The Gazebo window is open."));
        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        Process? px4;
        lock (_lock)
        {
            if (_status.State == SimulatorRunState.Preparing)
            {
                _preparing?.Cancel();
                return;
            }

            if (_status.State == SimulatorRunState.Stopped)
            {
                return;
            }

            px4 = _px4;
        }

        SetStatus(new SimulatorStatus(SimulatorRunState.Stopping, "stopping PX4"), null);
        if (px4 is { HasExited: false })
        {
            try
            {
                await px4.StandardInput.WriteLineAsync("shutdown");
                await px4.StandardInput.FlushAsync();
                using var wait = new CancellationTokenSource(ShutdownWait);
                await px4.WaitForExitAsync(wait.Token);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or InvalidOperationException)
            {
                // It did not stop on request; removing the container ends it.
            }
        }

        await CleanUpAsync();
        SetStatus(SimulatorStatus.Stopped, null);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Process? px4;
        lock (_lock)
        {
            px4 = _px4;
            _preparing?.Cancel();
        }

        // The station closing stops a SITL it started; one started outside it is left running.
        if (px4 is not null)
        {
            await StopAsync();
        }
    }

    /// <summary>Removes the container (which ends the Gazebo window with it) and drops the processes.</summary>
    private async Task CleanUpAsync()
    {
        try
        {
            await _docker.RunAsync(["rm", "-f", _options.ContainerName], CancellationToken.None);
        }
        catch (Exception exception) when (exception is Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            Emit($"The container could not be removed: {exception.Message}", SimulatorOutputLevel.Warning);
        }

        Process? px4, gui;
        lock (_lock)
        {
            px4 = _px4;
            gui = _gui;
            _px4 = null;
            _gui = null;
        }

        foreach (var process in new[] { px4, gui })
        {
            if (process is not null)
            {
                DockerCli.Kill(process);
                process.Dispose();
            }
        }
    }

    private void OnPx4Exited(Process px4)
    {
        bool expected;
        lock (_lock)
        {
            if (!ReferenceEquals(_px4, px4))
            {
                return;
            }

            expected = _status.State == SimulatorRunState.Stopping;
        }

        if (expected)
        {
            return;
        }

        // PX4 ended on its own (a crash, or it could not start, e.g. Gazebo did not come up): clear the rest and say why.
        var code = px4.ExitCode;
        var reason = LastError;
        _ = Task.Run(async () =>
        {
            await CleanUpAsync();
            SetStatus(new SimulatorStatus(SimulatorRunState.Stopped,
                Error: reason is null ? $"PX4 exited (code {code}); see the simulator output." : $"PX4 exited (code {code}): {reason}"), null);
        });
    }

    private void OnGuiExited(Process gui)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_gui, gui))
            {
                return;
            }

            _gui = null;
        }

        gui.Dispose();
        _ = UpdateWindowAsync(CancellationToken.None);
    }

    private string? LastError
    {
        get
        {
            lock (_lock)
            {
                return _lastError;
            }
        }
    }

    /// <summary>A start that never reached Running (PX4 waiting on a Gazebo that does not come up) is stopped with the last error.</summary>
    private async Task GiveUpStartAsync()
    {
        lock (_lock)
        {
            if (_status.State != SimulatorRunState.Starting)
            {
                return;
            }

            _status = new SimulatorStatus(SimulatorRunState.Stopping, "giving up the start", Window: WindowWhileNotRunning);
        }

        var timeout = _options.StartTimeout;
        var limit = timeout.TotalMinutes >= 1
            ? timeout.TotalMinutes.ToString("0.#", CultureInfo.CurrentCulture) + " min"
            : timeout.TotalSeconds.ToString("0", CultureInfo.CurrentCulture) + " s";
        var reason = LastError is { } error ? $"PX4 did not get ready within {limit}: {error}" : $"PX4 did not get ready within {limit}; see the simulator output.";
        Emit(reason, SimulatorOutputLevel.Error);
        await CleanUpAsync();
        SetStatus(new SimulatorStatus(SimulatorRunState.Stopped, Error: reason), null);
    }

    private void OnLine(string prefix, string? raw)
    {
        if (raw is null)
        {
            return;
        }

        var line = AnsiEscape().Replace(raw, string.Empty).Replace("pxh> ", string.Empty).Trim();
        if (line.Length == 0)
        {
            return;
        }

        var level = line.StartsWith("ERROR", StringComparison.Ordinal) ? SimulatorOutputLevel.Error
            : line.StartsWith("WARN", StringComparison.Ordinal) || line.StartsWith("Warning", StringComparison.Ordinal) ? SimulatorOutputLevel.Warning
            : line.Contains("Ready for takeoff", StringComparison.Ordinal) ? SimulatorOutputLevel.Success
            : SimulatorOutputLevel.Info;
        Emit(prefix + line, level);
        if (level == SimulatorOutputLevel.Error || line.Contains("not found", StringComparison.OrdinalIgnoreCase) || line.Contains("Timed out", StringComparison.Ordinal))
        {
            lock (_lock)
            {
                _lastError = prefix + line;
            }
        }

        // MAVLink is up once the startup script has run through; Gazebo was up before PX4 started.
        if (prefix.Length == 0 && line.Contains("Startup script returned successfully", StringComparison.Ordinal))
        {
            SetStatus(new SimulatorStatus(SimulatorRunState.Running, $"{_worldName} world"), NewLink(), onlyFrom: SimulatorRunState.Starting);
            _ = UpdateWindowAsync(CancellationToken.None);
        }
    }

    private void Emit(string line, SimulatorOutputLevel level)
    {
        _logger.Log(level switch
        {
            SimulatorOutputLevel.Error => LogLevel.Error,
            SimulatorOutputLevel.Warning => LogLevel.Warning,
            SimulatorOutputLevel.Success => LogLevel.Information,
            _ => LogLevel.Debug
        }, "SITL: {Line}", line);
        Output?.Invoke(line, level);
    }

    private void SetStatus(SimulatorStatus status, LinkProfile? link, SimulatorRunState? onlyFrom = null)
    {
        lock (_lock)
        {
            if (onlyFrom is { } from && _status.State != from)
            {
                return;
            }

            // The window stays as it was while running; otherwise it waits for the simulator.
            var window = status.Window ?? (status.State == SimulatorRunState.Running && _status.State == SimulatorRunState.Running
                ? _status.Window
                : status.State == SimulatorRunState.Running ? new SimulatorWindow(false, false, "Checking whether Gazebo can open.") : WindowWhileNotRunning);
            status = status with
            {
                Window = window,
                WorldId = status.State is SimulatorRunState.Starting or SimulatorRunState.Running ? status.WorldId ?? _worldId : null
            };
            _status = status;
            _link = status.State == SimulatorRunState.Running ? link ?? _link : null;
            if (status.State != SimulatorRunState.Starting)
            {
                _startTimer?.Dispose();
                _startTimer = null;
            }
        }

        StatusChanged?.Invoke(status);
    }

    /// <summary>The world a running container was started with (its label); null when it has none.</summary>
    private async Task<string?> ContainerWorldAsync(CancellationToken cancellation)
    {
        var result = await _docker.RunAsync(["inspect", "--format", $"{{{{index .Config.Labels \"{WorldLabel}\"}}}}", _options.ContainerName], cancellation);
        var id = result.Output.Trim();
        return Description.Worlds.Any(w => w.Id == id) ? id : null;
    }

    private void SetWindow(SimulatorWindow window)
    {
        SimulatorStatus status;
        lock (_lock)
        {
            if (_status.State != SimulatorRunState.Running)
            {
                return;
            }

            _status = status = _status with { Window = window };
        }

        StatusChanged?.Invoke(status);
    }

    /// <summary>Whether the Gazebo window can open now: the server answers in the container and a display reaches it.</summary>
    private async Task UpdateWindowAsync(CancellationToken cancellation)
    {
        lock (_lock)
        {
            if (_gui is not null)
            {
                return;
            }
        }

        SimulatorWindow window;
        try
        {
            const string probe = "export GZ_IP=127.0.0.1; "
                + "gz topic -l 2>/dev/null | grep -qE '^/world/[^/]+/clock' && echo gazebo; "
                + "d=${DISPLAY#*:}; d=${d%%.*}; [ -n \"$DISPLAY\" ] && [ -S /tmp/.X11-unix/X$d ] && echo display; true";
            var result = await _docker.RunAsync(["exec", _options.ContainerName, "bash", "-c", probe], cancellation);
            window = !result.Output.Contains("gazebo", StringComparison.Ordinal) ? new SimulatorWindow(false, false, "Gazebo is not answering in the container.")
                : !result.Output.Contains("display", StringComparison.Ordinal) ? new SimulatorWindow(false, false, NoDisplayReason)
                : new SimulatorWindow(true, false);
        }
        catch (Exception exception) when (exception is Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            window = new SimulatorWindow(false, false, $"The container did not answer: {exception.Message}");
        }

        SetWindow(window);
    }

    private static string NoDisplayReason => OperatingSystem.IsWindows()
        ? "No display reaches the container: Docker Desktop shares WSLg's display on Windows 11 only (wsl --update helps on Windows 10)."
        : "No display reaches the container: run the station in a desktop session (X11).";

    /// <summary>Builds a terrain world that is not there yet, from DEM cells fetched into the cache first.</summary>
    /// <summary>
    /// The terrain world's name for a home: one world per home (to about a metre), so a world is built once and found
    /// again. make_terrain_world.py writes it to worlds/&lt;name&gt;_&lt;quality&gt;/&lt;name&gt;.sdf.
    /// </summary>
    internal static string TerrainWorldName(GeoPoint home) =>
        Invariant($"ketras_terrain_{home.Latitude:F5}_{home.Longitude:F5}").Replace('.', 'p').Replace('-', 'm');

    private string TerrainWorldFile(string quality, string name) => Path.Combine(_data, "worlds", $"{name}_{quality}", $"{name}.sdf");

    private async Task EnsureTerrainWorldAsync(string image, string quality, string name, GeoPoint home, CancellationToken cancellation)
    {
        if (File.Exists(TerrainWorldFile(quality, name)))
        {
            return;
        }

        SetStatus(new SimulatorStatus(SimulatorRunState.Starting, $"building the {quality} terrain world"), null);
        var store = TerrainStore();
        var bounds = new GeoBounds(home.Latitude, home.Longitude, home.Latitude, home.Longitude).Expand(7_500);
        await store.EnsureAsync(bounds, cancellation);
        var run = new List<string> { "run", "--rm", "--name", $"{_options.ContainerName}-terrain" };
        run.AddRange(await CommonRunArgumentsAsync(cancellation));
        run.AddRange(Mount(Path.GetFullPath(store.CacheDirectory), DemMount, readOnly: true));
        run.AddRange([image, "python3", $"{ScriptsMount}/make_terrain_world.py", "--quality", quality, "--name", name,
            "--lat", Invariant($"{home.Latitude:R}"), "--lon", Invariant($"{home.Longitude:R}"), "--dem-dir", DemMount, "--out", $"{DataMount}/worlds"]);
        using var builder = _docker.Launch(run, redirectInput: false, line => OnLine("terrain: ", line));
        await builder.WaitForExitAsync(cancellation);
        if (!File.Exists(TerrainWorldFile(quality, name)))
        {
            throw new InvalidOperationException($"The {quality} terrain world could not be built; see the simulator output.");
        }
    }

    /// <summary>The DEM height at the home, fetched into the cache when missing; 0 with a warning when there is none.</summary>
    private async Task<double> GroundHeightAsync(double latitude, double longitude, CancellationToken cancellation)
    {
        var store = TerrainStore();
        var bounds = new GeoBounds(latitude, longitude, latitude, longitude).Expand(100);
        try
        {
            await store.EnsureAsync(bounds, cancellation);
            if (store.Load(bounds).Grid.HeightAt(latitude, longitude) is { } height)
            {
                Emit(Invariant($"Home {latitude:F6}, {longitude:F6} at {height:F1} m (Copernicus DEM)."), SimulatorOutputLevel.Info);
                return height;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            Emit($"The home altitude could not be read from the DEM: {exception.Message}", SimulatorOutputLevel.Warning);
        }

        Emit("No terrain data at the home: it sits at 0 m, so the terrain checks will not match it.", SimulatorOutputLevel.Warning);
        return 0;
    }

    private TerrainTileStore TerrainStore() => new(_options.TerrainCacheDirectory ?? TerrainTileStore.DefaultDirectory);

    private LinkProfile NewLink() => new()
    {
        Name = _options.ProfileName,
        Kind = LinkKind.Udp,
        Host = "127.0.0.1",
        Port = _options.GcsPort,
        Autopilot = "px4"
    };

    private async Task<bool> IsContainerRunningAsync(CancellationToken cancellation)
    {
        var result = await _docker.RunAsync(["ps", "--quiet", "--filter", $"name=^{_options.ContainerName}$"], cancellation);
        return result.Succeeded && result.Output.Trim().Length > 0;
    }

    /// <summary>The image of the chosen PX4 version.</summary>
    private string ImageFor(SimulatorLaunch launch)
    {
        var version = launch.ValueOf(Option(VersionOption));
        return $"{_options.ImageRepository}:{(_options.Px4Versions.Contains(version) ? version : _options.Px4Versions[0])}";
    }

    private SimulatorOption Option(string key) => Description.Options.Single(o => o.Key == key);

    /// <summary>A number option, clamped to its range and written in the invariant culture.</summary>
    private string Number(SimulatorLaunch launch, string key)
    {
        var option = Option(key);
        var value = double.TryParse(launch.ValueOf(option), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed
            : double.Parse(option.Default, CultureInfo.InvariantCulture);
        return Math.Clamp(value, option.Minimum, option.Maximum).ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>The scripts and the data folder, and on Linux the host user, so files in the data folder stay the operator's.</summary>
    private async Task<IReadOnlyList<string>> CommonRunArgumentsAsync(CancellationToken cancellation)
    {
        Directory.CreateDirectory(_data);
        var arguments = new List<string>();
        arguments.AddRange(Mount(_scripts, ScriptsMount, readOnly: true));
        arguments.AddRange(Mount(_data, DataMount, readOnly: false));
        if (!OperatingSystem.IsWindows() && await HostUserAsync(cancellation) is { } user)
        {
            arguments.AddRange(["--user", user]);
        }

        return arguments;
    }

    /// <summary>
    /// The host's display for the Gazebo window: on Windows Docker Desktop's WSLg mount, on Linux the X11 socket and
    /// its cookie. Nothing when the host has no display; the simulator runs either way.
    /// </summary>
    private static IEnumerable<string> DisplayArguments()
    {
        if (OperatingSystem.IsWindows())
        {
            return ["-v", "/run/desktop/mnt/host/wslg/.X11-unix:/tmp/.X11-unix", "-v", "/run/desktop/mnt/host/wslg:/mnt/wslg",
                "-e", "DISPLAY=:0", "-e", "WAYLAND_DISPLAY=wayland-0", "-e", "XDG_RUNTIME_DIR=/mnt/wslg/runtime-dir"];
        }

        if (System.Environment.GetEnvironmentVariable("DISPLAY") is not { Length: > 0 } display || !Directory.Exists("/tmp/.X11-unix"))
        {
            return [];
        }

        var arguments = new List<string> { "-v", "/tmp/.X11-unix:/tmp/.X11-unix", "-e", $"DISPLAY={display}" };
        if (System.Environment.GetEnvironmentVariable("XAUTHORITY") is { Length: > 0 } cookie && File.Exists(cookie))
        {
            arguments.AddRange(["-v", $"{cookie}:/tmp/.Xauthority:ro", "-e", "XAUTHORITY=/tmp/.Xauthority"]);
        }

        return arguments;
    }

    private static IEnumerable<string> Mount(string source, string target, bool readOnly) =>
        ["--mount", $"type=bind,source={source},target={target}{(readOnly ? ",readonly" : string.Empty)}"];

    private async Task<string?> HostUserAsync(CancellationToken cancellation)
    {
        if (_user is not null)
        {
            return _user;
        }

        try
        {
            var id = new DockerCli("id");
            var user = (await id.RunAsync(["-u"], cancellation)).Output.Trim();
            var group = (await id.RunAsync(["-g"], cancellation)).Output.Trim();
            return _user = user.Length > 0 && group.Length > 0 ? $"{user}:{group}" : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]|\r")]
    private static partial Regex AnsiEscape();
}
