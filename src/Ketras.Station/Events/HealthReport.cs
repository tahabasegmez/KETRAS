using Ketras.Station.Notifications;

namespace Ketras.Station.Events;

/// <summary>One failing health or arming check.</summary>
/// <param name="Message">What fails, e.g. "Navigation error: No valid global position estimate".</param>
/// <param name="Description">More detail; empty when none.</param>
/// <param name="Severity">Error blocks the modes it names; Warning does not.</param>
/// <param name="ModeGroups">The navigation mode groups it affects, one bit per group.</param>
/// <param name="Component">The subsystem it belongs to (e.g. "Global position estimate"); null when none.</param>
public sealed record HealthCheck(string Message, string Description, NotificationSeverity Severity, ulong ModeGroups, string? Component)
{
    /// <summary>True when it affects <paramref name="group"/>.</summary>
    public bool Affects(int group) => group is >= 0 and < 64 && (ModeGroups & (1UL << group)) != 0;
}

/// <summary>Whether the vehicle can arm in, or switch to, the modes of one navigation mode group.</summary>
/// <param name="Name">Its name, e.g. "mission".</param>
/// <param name="Description">Its label, e.g. "Mission".</param>
/// <param name="CanArm">Arming is possible in this group's modes.</param>
/// <param name="CanRun">Switching to this group's modes is possible.</param>
public sealed record ModeGroupState(string Name, string Description, bool CanArm, bool CanRun);

/// <summary>
/// The vehicle's latest health and arming check report (PX4 events protocol): which mode groups it can arm in or switch
/// to, and the checks that fail. Immutable.
/// </summary>
/// <param name="Checks">The failing checks, in the order the vehicle sent them.</param>
/// <param name="ModeGroups">Mode groups by bit index.</param>
public sealed record HealthReport(IReadOnlyList<HealthCheck> Checks, IReadOnlyDictionary<int, ModeGroupState> ModeGroups)
{
    /// <summary>True when arming is possible in the group's modes; true when the report does not know the group.</summary>
    public bool CanArm(int group) => !ModeGroups.TryGetValue(group, out var state) || state.CanArm;

    /// <summary>True when switching to the group's modes is possible; true when the report does not know the group.</summary>
    public bool CanRun(int group) => !ModeGroups.TryGetValue(group, out var state) || state.CanRun;

    /// <summary>The checks that affect the group, errors first.</summary>
    public IReadOnlyList<HealthCheck> ChecksFor(int group) =>
        [.. Checks.Where(check => check.Affects(group)).OrderByDescending(check => check.Severity)];
}

/// <summary>
/// Builds <see cref="HealthReport"/>s from the stream of interpreted events, as libevents' HealthAndArmingChecks
/// (libs/python/libevents_parse/health_and_arming_checks.py): an <c>arming_check</c> summary (chunk, error and warning
/// components, can-arm and can-run mode groups), then one event per failing check (its first argument the affected
/// mode groups, its second the health component index), then the <c>health</c> summary that closes the chunk. A
/// sequence out of this order is dropped. Not thread-safe; one per vehicle.
/// </summary>
public sealed class HealthReportAssembler
{
    private readonly Dictionary<int, Chunk> _chunks = [];
    private Expect _expected = Expect.ArmingSummary;
    private int _currentChunk;

    private enum Expect
    {
        ArmingSummary,
        CheckOrHealthSummary
    }

    /// <summary>Takes an event; returns the new report when it completes one, else null.</summary>
    public HealthReport? Add(ParsedEvent @event)
    {
        var isCheckGroup = @event.Group is "arming_check" or "health";
        if (!isCheckGroup)
        {
            return null;
        }

        if (@event.Type == "summary" && @event.Group == "arming_check")
        {
            StartChunk(@event);
            return null;
        }

        if (_expected != Expect.CheckOrHealthSummary)
        {
            return null;
        }

        if (@event.Type == "summary")
        {
            _expected = Expect.ArmingSummary;
            return @event.Values.Count >= 1 && (int)@event.Values[0].Raw == _currentChunk ? Combine() : null;
        }

        if (@event.Type.Length == 0 && @event.Values.Count >= 2 && _chunks.TryGetValue(_currentChunk, out var chunk))
        {
            var index = (int)@event.Values[1].Raw;
            var component = index > 0 ? chunk.Components.GetValueOrDefault(1UL << index) : null;
            chunk.Checks.Add(new HealthCheck(@event.Message, @event.Description,
                @event.Severity == NotificationSeverity.Error ? NotificationSeverity.Error : NotificationSeverity.Warning,
                @event.Values[0].Raw, component));
        }

        return null;
    }

    private void StartChunk(ParsedEvent summary)
    {
        _expected = Expect.ArmingSummary;
        if (summary.Values.Count < 5 || summary.Values[1].Enum is not { } healthEnum || summary.Values[3].Enum is not { } modeEnum)
        {
            return;
        }

        _currentChunk = (int)summary.Values[0].Raw;
        var chunk = new Chunk();
        var canArm = summary.Values[3].Raw;
        var canRun = summary.Values[4].Raw;
        foreach (var (bit, entry) in modeEnum.Entries)
        {
            chunk.ModeGroups[System.Numerics.BitOperations.Log2(bit)] = new ModeGroupState(entry.Name, entry.Description, (canArm & bit) != 0, (canRun & bit) != 0);
        }

        foreach (var (bit, entry) in healthEnum.Entries)
        {
            chunk.Components[bit] = entry.Description;
        }

        _chunks[_currentChunk] = chunk;
        _expected = Expect.CheckOrHealthSummary;
    }

    /// <summary>All chunks together: a mode group can arm or run only when every chunk says so.</summary>
    private HealthReport Combine()
    {
        var groups = new Dictionary<int, ModeGroupState>();
        var checks = new List<HealthCheck>();
        foreach (var chunk in _chunks.Values)
        {
            foreach (var (index, state) in chunk.ModeGroups)
            {
                groups[index] = groups.TryGetValue(index, out var before)
                    ? before with { CanArm = before.CanArm && state.CanArm, CanRun = before.CanRun && state.CanRun }
                    : state;
            }

            checks.AddRange(chunk.Checks);
        }

        return new HealthReport(checks, groups);
    }

    private sealed class Chunk
    {
        public Dictionary<int, ModeGroupState> ModeGroups { get; } = [];

        public Dictionary<ulong, string> Components { get; } = [];

        public List<HealthCheck> Checks { get; } = [];
    }
}
