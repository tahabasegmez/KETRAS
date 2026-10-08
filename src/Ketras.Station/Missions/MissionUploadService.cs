using System.Globalization;
using Ketras.Mavlink.Messages;
using Ketras.Mavlink.Autopilots;
using Ketras.Model.Missions;
using MissionTransferResult = Ketras.Mavlink.Protocols.MissionTransferResult;
using Ketras.Model.Planning;
using Ketras.Station.Commands;
using Ketras.Station.Environment;
using Ketras.Station.Terrain;
using Ketras.Station.Vehicles;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ketras.Station.Missions;

/// <summary>
/// Uploads the plan to the active vehicle, registered as the station actions <see cref="StationActions.UploadMission"/>
/// and <see cref="StationActions.UploadFences"/>. A mission upload first puts the plan on the terrain (downloading what it
/// needs), runs every check (findings warn, they never stop the upload; owner decision), then refuses only what PX4
/// would reject outright. A mission that lands also writes the plan's landing settings to a PX4 vehicle
/// (FW_LND_USETER, FW_LND_ABORT) where they differ; a refused write is reported, never a reason to fail the upload. The
/// geofence upload adds the environment zones marked for it.
/// </summary>
public sealed class MissionUploadService(
    VehicleRegistry vehicles,
    PlanStore plans,
    TerrainService terrain,
    EnvironmentStore environment,
    MissionProgressTracker progress,
    CommandService commands,
    ILogger<MissionUploadService> logger) : IHostedService
{
    private readonly List<IDisposable> _actions = [];

    /// <summary>Uploads the mission; with no items, clears the vehicle's mission. The result text says what was uploaded and how the terrain check went.</summary>
    public async Task<CommandResult> UploadMissionAsync(CancellationToken cancellation = default)
    {
        if (vehicles.Active is not { IsConnected: true } vehicle)
        {
            return CommandResult.Refused("no vehicle");
        }

        if (plans.Current.Mission.Items.IsEmpty)
        {
            var cleared = await vehicle.Session.Missions.ClearAsync(MavMissionType.Mission, cancellation);
            if (!cleared.IsSuccess)
            {
                return CommandResult.Refused($"mission clear failed: {Describe(cleared)}");
            }

            progress.SetUploaded(vehicle.Id, MissionEncoder.Encode(plans.Current.Mission, vehicle.Session.Profile.MissionHomeAtSequenceZero, terrain.GroundAt));
            return CommandResult.Accepted("vehicle mission cleared");
        }

        var zones = environment.GeofenceZones();
        var preparation = await terrain.PrepareForUploadAsync(plans.Current, zones, cancellation);
        var plan = preparation.Plan;
        if (!ReferenceEquals(plan, plans.Current))
        {
            plans.Apply(_ => plan, PlanChange.Mission);
        }

        var findings = PlanRules.Validate(plan, vehicle.Limits, terrain.Grid, zones);
        foreach (var finding in findings)
        {
            logger.LogWarning("Plan finding before upload: {Message}", finding.Message);
        }

        var encoded = MissionEncoder.Encode(plan.Mission, vehicle.Session.Profile.MissionHomeAtSequenceZero, terrain.GroundAt);
        if (encoded.Blockers.Count > 0)
        {
            return CommandResult.Refused($"not uploaded: {string.Join("; ", encoded.Blockers)}");
        }

        var result = await vehicle.Session.Missions.UploadAsync(MavMissionType.Mission, encoded.Items, cancellation);
        if (!result.IsSuccess)
        {
            return CommandResult.Refused($"mission upload failed: {Describe(result)}");
        }

        progress.SetUploaded(vehicle.Id, encoded);
        var terrainFindings = findings.Count(f => f.Source == FindingSource.Terrain);
        var terrainText = terrain.IsEnabled ? $"; {(terrainFindings == 0 ? "terrain clear" : $"terrain: {terrainFindings} warning(s)")}; {preparation.Summary}" : string.Empty;
        var landingText = await WriteLandingSettingsAsync(vehicle, plan.Mission, cancellation);
        return CommandResult.Accepted($"mission uploaded: {encoded.Items.Count} items{terrainText}{landingText}");
    }

    /// <summary>The PX4 parameters a plan's landing settings stand for, with the values the plan asks for.</summary>
    public static IReadOnlyList<(string Name, double Value)> LandingParameters(LandingSettings landing) =>
    [
        ("FW_LND_USETER", (int)landing.TerrainUse),
        ("FW_LND_ABORT", (landing.AbortIfTerrainNotFound ? 1 : 0) | (landing.AbortIfTerrainLost ? 2 : 0))
    ];

    /// <summary>Writes the landing settings that differ on a PX4 vehicle when the mission lands; the summary text, empty when nothing changed.</summary>
    private async Task<string> WriteLandingSettingsAsync(Vehicles.Vehicle vehicle, Mission mission, CancellationToken cancellation)
    {
        if (vehicle.Session.Profile.Name != "px4" || !mission.Items.Any(item => item.Type is MissionItemType.LandingPattern or MissionItemType.Land))
        {
            return string.Empty;
        }

        var said = new List<string>();
        foreach (var (name, value) in LandingParameters(mission.Settings.Landing))
        {
            if (!vehicle.Parameters.Values.TryGetValue(name, out var entry) || entry.Value.Value == value)
            {
                continue;
            }

            var written = await vehicle.Parameters.WriteAsync(name, new ParameterValue(entry.Value.Type, value), cancellation);
            said.Add(written.IsApplied
                ? string.Create(CultureInfo.InvariantCulture, $"{name} {entry.Value.Value:G} → {value:G}")
                : $"{name} not written ({(written.Reason.Length > 0 ? written.Reason : "no answer")})");
            logger.LogInformation("Landing setting {Name}: {Outcome}", name, said[^1]);
        }

        return said.Count == 0 ? string.Empty : $"; landing: {string.Join(", ", said)}";
    }

    /// <summary>Uploads the operator's fences with the zones marked for the geofence; with none, clears the vehicle's geofence.</summary>
    public async Task<CommandResult> UploadFencesAsync(CancellationToken cancellation = default)
    {
        if (vehicles.Active is not { IsConnected: true } vehicle)
        {
            return CommandResult.Refused("no vehicle");
        }

        var fences = plans.Current.Fences.Concat(environment.GeofenceZones()).ToList();
        var items = MissionEncoder.EncodeFences(fences);
        var result = items.Count == 0
            ? await vehicle.Session.Missions.ClearAsync(MavMissionType.Fence, cancellation)
            : await vehicle.Session.Missions.UploadAsync(MavMissionType.Fence, items, cancellation);
        return result.IsSuccess
            ? CommandResult.Accepted(items.Count == 0 ? "geofence cleared" : $"geofence uploaded: {fences.Count} zones")
            : CommandResult.Refused($"geofence upload failed: {Describe(result)}");
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _actions.Add(commands.RegisterAction(StationActions.UploadMission, (_, cancellation) => UploadMissionAsync(cancellation)));
        _actions.Add(commands.RegisterAction(StationActions.UploadFences, (_, cancellation) => UploadFencesAsync(cancellation)));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _actions.ForEach(action => action.Dispose());
        _actions.Clear();
        return Task.CompletedTask;
    }

    private static string Describe(MissionTransferResult result) => result.Reason.Length > 0 ? result.Reason : result.Result?.ToString() ?? "failed";
}
