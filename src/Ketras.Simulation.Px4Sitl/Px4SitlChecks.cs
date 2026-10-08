using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Ketras.Station.Simulation;
using Ketras.Terrain;

namespace Ketras.Simulation.Px4Sitl;

/// <summary>The readiness checks of <see cref="Px4Sitl"/> and what it can do about them: build or remove its environment.</summary>
public sealed partial class Px4Sitl
{
    /// <summary>Action id: build the environment image of the chosen PX4 version.</summary>
    public const string BuildAction = "build";

    /// <summary>Action id: remove the environment image of the chosen PX4 version.</summary>
    public const string RemoveAction = "remove";

    private static readonly HttpClient Probe = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <inheritdoc />
    public async Task<IReadOnlyList<SimulatorCheck>> CheckAsync(SimulatorLaunch launch, CancellationToken cancellation = default)
    {
        var checks = new List<SimulatorCheck>();
        var image = ImageFor(launch);
        var version = image[(image.IndexOf(':', StringComparison.Ordinal) + 1)..];
        try
        {
            string client;
            try
            {
                client = (await _docker.RunAsync(["version", "--format", "{{.Client.Version}}"], cancellation)).Output.Trim();
            }
            catch (Win32Exception)
            {
                client = string.Empty;
            }

            if (client.Length == 0)
            {
                checks.Add(new("Docker", CheckState.Missing, "Docker is not installed.", OperatingSystem.IsWindows()
                    ? "Install Docker Desktop (https://docs.docker.com/desktop/setup/install/windows-install/), start it once, then check again."
                    : "Install Docker Engine (https://docs.docker.com/engine/install/), then check again."));
                checks.Add(new("Environment", CheckState.NotChecked, "Needs Docker."));
                return await FinishAsync(checks, cancellation);
            }

            checks.Add(new("Docker", CheckState.Ready, $"Docker {client}."));
            var engine = await _docker.RunAsync(["info", "--format", "{{.ServerVersion}}"], cancellation);
            if (!engine.Succeeded || engine.Output.Trim().Length == 0)
            {
                var denied = engine.Error.Contains("permission denied", StringComparison.OrdinalIgnoreCase);
                checks.Add(new("Docker engine", CheckState.Missing,
                    denied ? "This user may not use the Docker engine." : "The Docker engine is not running.",
                    denied ? "sudo usermod -aG docker $USER, then log out and in again."
                    : OperatingSystem.IsWindows() ? "Start Docker Desktop and wait until it shows the engine running."
                    : "sudo systemctl start docker"));
                checks.Add(new("Environment", CheckState.NotChecked, "Needs the Docker engine."));
                return await FinishAsync(checks, cancellation);
            }

            checks.Add(new("Docker engine", CheckState.Ready, "Running."));
            // "image ls" gives the size on disk ("8.01GB"); "image inspect" gives the compressed size under containerd.
            var listed = await _docker.RunAsync(["image", "ls", "--format", "{{.Size}}", image], cancellation);
            if (listed.Succeeded && listed.Output.Trim() is { Length: > 0 } size)
            {
                checks.Add(new("Environment", CheckState.Ready, $"PX4 {version}, {size.Split('\n')[0].Trim()} on disk.",
                    Action: new SimulatorAction(RemoveAction, "Remove", IsDestructive: true)));
            }
            else
            {
                checks.Add(new("Environment", CheckState.Missing, $"Not built for PX4 {version} yet.",
                    "Build it once here: it takes 30–60 minutes, about 8 GB of disk and an internet connection.",
                    Action: new SimulatorAction(BuildAction, "Build environment")));
            }

            checks.Add(OperatingSystem.IsWindows() || System.Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 }
                ? new("Gazebo window", CheckState.Ready, OperatingSystem.IsWindows() ? "Through Docker Desktop's WSLg display (Windows 11)." : "X11 display found.",
                    Required: false)
                : new("Gazebo window", CheckState.Warning, "No X11 display: the simulator runs, its window cannot open.", "Run the station in a desktop session.",
                    Required: false));
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            checks.Add(new("Docker", CheckState.Missing, $"Docker did not answer: {exception.Message}", "Check that Docker runs, then check again."));
        }

        return await FinishAsync(checks, cancellation);
    }

    /// <inheritdoc />
    public async Task RunActionAsync(string actionId, SimulatorLaunch launch, CancellationToken cancellation = default)
    {
        var image = ImageFor(launch);
        CancellationTokenSource preparing;
        lock (_lock)
        {
            if (_status.State != SimulatorRunState.Stopped)
            {
                throw new InvalidOperationException($"{Description.Name} is {_status.State.ToString().ToLowerInvariant()}.");
            }

            preparing = _preparing = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        }

        try
        {
            switch (actionId)
            {
                case BuildAction:
                    await BuildAsync(image, preparing.Token);
                    break;
                case RemoveAction:
                    SetStatus(new SimulatorStatus(SimulatorRunState.Preparing, $"removing {image}"), null);
                    var removed = await _docker.RunAsync(["image", "rm", image], preparing.Token, TimeSpan.FromMinutes(2));
                    Emit(removed.Succeeded ? $"Removed the environment {image}." : $"{image} could not be removed: {removed.Error.Trim()}",
                        removed.Succeeded ? SimulatorOutputLevel.Info : SimulatorOutputLevel.Error);
                    SetStatus(removed.Succeeded ? SimulatorStatus.Stopped : new SimulatorStatus(SimulatorRunState.Stopped, Error: removed.Error.Trim()), null);
                    break;
                default:
                    throw new ArgumentException($"Unknown action '{actionId}'.", nameof(actionId));
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            Emit(exception.Message, SimulatorOutputLevel.Error);
            SetStatus(new SimulatorStatus(SimulatorRunState.Stopped, Error: exception.Message), null);
        }
        finally
        {
            lock (_lock)
            {
                _preparing = null;
            }

            preparing.Dispose();
        }
    }

    /// <summary>Builds the image from the Dockerfile (sent on stdin, no build context); Stop cancels it.</summary>
    private async Task BuildAsync(string image, CancellationToken cancellation)
    {
        var version = image[(image.IndexOf(':', StringComparison.Ordinal) + 1)..];
        var dockerfile = await File.ReadAllTextAsync(Path.Combine(_scripts, "Dockerfile"), cancellation);
        SetStatus(new SimulatorStatus(SimulatorRunState.Preparing, $"building the environment for PX4 {version}"), null);
        Emit($"Building {image}: this takes 30–60 minutes the first time.", SimulatorOutputLevel.Info);
        var watch = Stopwatch.StartNew();
        string? lastError = null;
        var step = string.Empty;
        var lastProgress = DateTime.MinValue;
        // The build prints thousands of lines: the log gets each step and the errors, the status the progress.
        using var build = _docker.Launch(["build", "--progress=plain", "-t", image, "--build-arg", $"PX4_VERSION={version}", "-"], redirectInput: true, line =>
        {
            if (line is not { Length: > 0 } text)
            {
                return;
            }

            if (BuildStep().Match(text) is { Success: true } header)
            {
                step = $"step {header.Groups[1].Value} of {header.Groups[2].Value}";
                Emit($"build {step}: {header.Groups[3].Value.Trim()}", SimulatorOutputLevel.Info);
                SetStatus(new SimulatorStatus(SimulatorRunState.Preparing, $"building the environment for PX4 {version}: {step}"), null);
            }
            else if (text.Contains("ERROR", StringComparison.Ordinal) || text.Contains(" error:", StringComparison.OrdinalIgnoreCase))
            {
                lastError = text.Trim();
                Emit($"build: {lastError}", SimulatorOutputLevel.Warning);
            }
            else if (CompileProgress().Match(text) is { Success: true } progress && DateTime.UtcNow - lastProgress > TimeSpan.FromSeconds(1))
            {
                lastProgress = DateTime.UtcNow;
                SetStatus(new SimulatorStatus(SimulatorRunState.Preparing,
                    $"building the environment for PX4 {version}: {step}, compiling {progress.Groups[1].Value} of {progress.Groups[2].Value}"), null);
            }
        });
        await build.StandardInput.WriteAsync(dockerfile);
        build.StandardInput.Close();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(_options.BuildTimeout);
        try
        {
            await build.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            DockerCli.Kill(build);
            var reason = cancellation.IsCancellationRequested ? "The environment build was cancelled." : "The environment build took too long and was stopped.";
            Emit(reason, SimulatorOutputLevel.Warning);
            SetStatus(cancellation.IsCancellationRequested ? SimulatorStatus.Stopped : new SimulatorStatus(SimulatorRunState.Stopped, Error: reason), null);
            return;
        }

        if (build.ExitCode == 0)
        {
            Emit(string.Create(CultureInfo.CurrentCulture, $"The environment for PX4 {version} is ready ({watch.Elapsed.TotalMinutes:F0} min)."), SimulatorOutputLevel.Success);
            SetStatus(SimulatorStatus.Stopped, null);
        }
        else
        {
            var reason = $"The environment could not be built: {lastError ?? "see the simulator output"}";
            Emit(reason, SimulatorOutputLevel.Error);
            SetStatus(new SimulatorStatus(SimulatorRunState.Stopped, Error: reason), null);
        }
    }

    /// <summary>Adds the network check, which does not depend on Docker.</summary>
    private static async Task<IReadOnlyList<SimulatorCheck>> FinishAsync(List<SimulatorCheck> checks, CancellationToken cancellation)
    {
        var reachable = await DemReachableAsync(cancellation);
        checks.Add(new("Copernicus DEM", reachable ? CheckState.Ready : CheckState.Warning,
            reachable ? "Reachable for the home altitude and the terrain worlds."
                : "Not reachable: the home sits at 0 m unless cached, and a terrain world cannot be built the first time.",
            reachable ? null : "Check the internet connection.", Required: false));
        return checks;
    }

    // "#7 [4/6] RUN bash /opt/PX4-Autopilot/Tools/setup/ubuntu.sh --no-nuttx"
    [GeneratedRegex(@"^#\d+ \[(\d+)/(\d+)\] (.*)$")]
    private static partial Regex BuildStep();

    // Ninja's "[812/1532] Building CXX object …" inside a RUN step's output.
    [GeneratedRegex(@"\[(\d+)/(\d+)\] (?:Building|Linking|Generating)")]
    private static partial Regex CompileProgress();

    private static async Task<bool> DemReachableAsync(CancellationToken cancellation)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, CopernicusDem.BucketUrl);
            using var response = await Probe.SendAsync(request, cancellation);
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            return false;
        }
    }
}
