using Ketras.Model.Fences;
using Ketras.Model.Geo;
using Ketras.Model.Missions;
using Ketras.Model.Planning;
using Ketras.Station.Missions;
using Ketras.Station.Settings;
using Ketras.Terrain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TerrainGrid = Ketras.Model.Terrain.TerrainGrid;

namespace Ketras.Station.Terrain;

/// <summary>The terrain part of an upload: the aligned plan and a one-line summary of what was checked.</summary>
/// <param name="Plan">The plan with home and touchdown altitudes on the terrain.</param>
/// <param name="Summary">E.g. "terrain clear" or "terrain data missing (N47E008 not published)".</param>
public sealed record TerrainPreparation(Plan Plan, string Summary);

/// <summary>
/// Terrain for the plan, from Copernicus DEM cells: while the plan is edited it reads the cells already on disk and keeps
/// the home and touchdown altitudes on the terrain; before an upload it downloads the cells the plan needs. While the
/// station flies a simulator world without terrain (<see cref="UseFlatGround"/>), the ground is level with the planned
/// home everywhere (the DEM height there, where the simulator puts its ground), for the alignment, the grid and every
/// height read through <see cref="GroundAt"/> and <see cref="LoadGrid"/>. It never commands the vehicle. Thread-safe.
/// </summary>
public sealed class TerrainService : IDisposable
{
    /// <summary>Margin around the plan that covers orbit radii, return paths and fence circles.</summary>
    public const double PlanMarginMeters = 2000;

    private static readonly TimeSpan EditSettle = TimeSpan.FromMilliseconds(500);

    private readonly PlanStore _plans;
    private readonly TerrainOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<TerrainService> _logger;
    private readonly object _lock = new();
    private GeoBounds? _bounds;
    private ElevationGrid? _elevation;
    private TerrainGrid? _grid;
    private CancellationTokenSource? _settle;
    private bool _flat;

    /// <summary>Follows the plan in <paramref name="plans"/> when terrain is enabled.</summary>
    public TerrainService(PlanStore plans, IOptions<StationOptions> options, StationPaths paths, TimeProvider time, ILogger<TerrainService> logger)
        : this(plans, options, new TerrainTileStore(paths.DataPath(options.Value.Terrain.CacheDirectory, "terrain")), time, logger)
    {
    }

    internal TerrainService(PlanStore plans, IOptions<StationOptions> options, TerrainTileStore tiles, TimeProvider time, ILogger<TerrainService> logger)
    {
        _plans = plans;
        _options = options.Value.Terrain;
        Tiles = tiles;
        _time = time;
        _logger = logger;
        if (_options.Enabled)
        {
            plans.Changed += OnPlanChanged;
        }
    }

    /// <summary>True when terrain checks are on.</summary>
    public bool IsEnabled => _options.Enabled;

    /// <summary>The cell store; read-only use only (e.g. the 3D scene and the flight monitor read cells already on disk).</summary>
    public TerrainTileStore Tiles { get; }

    /// <summary>The grid around the plan; null without data.</summary>
    public TerrainGrid? Grid
    {
        get
        {
            lock (_lock)
            {
                return _grid;
            }
        }
    }

    /// <summary><see cref="Grid"/> changed.</summary>
    public event Action<TerrainGrid?>? GridChanged;

    /// <summary>True while the ground is taken as level with the planned home (a simulator world without terrain).</summary>
    public bool IsFlat
    {
        get
        {
            lock (_lock)
            {
                return _flat;
            }
        }
    }

    /// <summary>
    /// Takes the ground as level with the planned home (true) or as the DEM terrain (false); the grid and the plan's
    /// home and touchdown altitudes follow.
    /// </summary>
    public void UseFlatGround(bool flat)
    {
        lock (_lock)
        {
            if (_flat == flat)
            {
                return;
            }

            _flat = flat;
        }

        _logger.LogInformation("Terrain: {Ground}", flat ? "flat ground at the planned home (simulator world without terrain)" : "DEM terrain");
        ElevationGrid? elevation;
        GeoBounds? bounds;
        lock (_lock)
        {
            elevation = _elevation;
            bounds = _bounds;
        }

        if (elevation is not null && bounds is { } box)
        {
            SetGrid(box, elevation);
        }

        if (IsEnabled)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    Refresh(_plans.Current);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException)
                {
                    _logger.LogWarning(exception, "Terrain could not be read for the plan");
                }
            });
        }
    }

    /// <summary>
    /// The heights in <paramref name="bounds"/> from disk, or level with the planned home while <see cref="IsFlat"/>;
    /// null when no cell is on disk.
    /// </summary>
    public ElevationGrid? LoadGrid(GeoBounds bounds)
    {
        var load = Tiles.Load(bounds);
        var grid = load.MissingCells.Count == CopernicusDem.CellsFor(bounds).Count ? null : load.Grid;
        return grid is not null && FlatHeight() is { } level ? Level(grid, level) : grid;
    }

    /// <summary>Terrain height (m AMSL) at a point: the DEM, or the planned home's while <see cref="IsFlat"/>; null without data.</summary>
    public double? GroundAt(GeoPoint point) => FlatHeight() ?? DemAt(point);

    /// <summary>The level ground while flat: the DEM at the planned home; null when not flat or the home is not known.</summary>
    private double? FlatHeight()
    {
        if (!IsFlat || _plans.Current.Mission.Settings.PlannedHome is { IsUnset: true })
        {
            return null;
        }

        return DemAt(_plans.Current.Mission.Settings.PlannedHome);
    }

    private static ElevationGrid Level(ElevationGrid grid, double height)
    {
        var heights = new float[grid.Rows * grid.Columns];
        Array.Fill(heights, (float)height);
        return new ElevationGrid(grid.SouthLatitude, grid.WestLongitude, grid.LatitudeStep, grid.LongitudeStep, grid.Rows, grid.Columns, heights, grid.Attribution);
    }

    private double? DemAt(GeoPoint point)
    {
        ElevationGrid? grid;
        lock (_lock)
        {
            grid = _elevation;
        }

        if (grid?.HeightAt(point.Latitude, point.Longitude) is { } height)
        {
            return height;
        }

        try
        {
            return Tiles.Load(new GeoBounds(point.Latitude, point.Longitude, point.Latitude, point.Longitude).Expand(200)).Grid.HeightAt(point.Latitude, point.Longitude);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            _logger.LogWarning(exception, "Terrain could not be read");
            return null;
        }
    }

    /// <summary>
    /// Downloads what the plan needs, aligns it and returns it with a summary. Missing data or download failures only
    /// change the summary: they never stop the upload (owner decision).
    /// </summary>
    public async Task<TerrainPreparation> PrepareForUploadAsync(Plan plan, IEnumerable<Fence> extraFences, CancellationToken cancellation = default)
    {
        if (!IsEnabled)
        {
            return new TerrainPreparation(plan, "terrain checks off");
        }

        if (BoundsOf(plan.Mission, [.. plan.Fences, .. extraFences]) is not { } needed)
        {
            return new TerrainPreparation(plan, "terrain not checked: the plan has no positions");
        }

        var bounds = needed.Expand(PlanMarginMeters);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(_options.DownloadTimeout);
            var fetch = await Tiles.EnsureAsync(bounds, timeout.Token);
            foreach (var cell in fetch.Downloaded)
            {
                _logger.LogInformation("Terrain cell {Cell} downloaded", cell);
            }

            var load = await Task.Run(() => Tiles.Load(bounds), cancellation);
            SetGrid(bounds, load.Grid);
            var (mission, changes) = TerrainAlignment.Align(plan.Mission, GroundAt);
            if (changes.Count > 0)
            {
                _logger.LogInformation("Terrain alignment: {Changes}", string.Join("; ", changes));
            }

            var gaps = fetch.NotPublished.Select(cell => $"{cell} not published").Concat(fetch.Failed.Select(f => $"{f.Cell} {f.Reason}")).ToList();
            var summary = IsFlat ? "flat ground at the home (simulator world without terrain)"
                : gaps.Count == 0 ? "terrain data complete" : $"terrain data missing ({string.Join(", ", gaps)})";
            return new TerrainPreparation(plan with { Mission = mission }, summary);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException)
        {
            return new TerrainPreparation(plan, $"terrain not checked: {exception.Message}");
        }
    }

    private void OnPlanChanged(Plan plan, PlanChange change)
    {
        CancellationTokenSource settle;
        lock (_lock)
        {
            _settle?.Cancel();
            settle = _settle = new CancellationTokenSource();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(EditSettle, _time, settle.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                Refresh(_plans.Current);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                _logger.LogWarning(exception, "Terrain could not be read for the plan");
            }
        });
    }

    /// <summary>Reloads the grid from disk when the plan left it, then puts the plan on the terrain.</summary>
    private void Refresh(Plan plan)
    {
        if (BoundsOf(plan.Mission, plan.Fences) is not { } needed)
        {
            return;
        }

        bool covered;
        lock (_lock)
        {
            covered = _bounds is { } loaded && loaded.Contains(needed.South, needed.West) && loaded.Contains(needed.North, needed.East);
        }

        var bounds = needed.Expand(PlanMarginMeters);
        var cells = CopernicusDem.CellsFor(bounds).Count;
        if (!covered && cells <= TerrainTileStore.MaxCellsPerRequest)
        {
            var load = Tiles.Load(bounds);
            if (load.MissingCells.Count < cells)
            {
                SetGrid(bounds, load.Grid);
            }
        }

        _plans.Apply(current =>
        {
            var (mission, changes) = TerrainAlignment.Align(current.Mission, GroundAt);
            if (changes.Count == 0)
            {
                return current;
            }

            _logger.LogInformation("Terrain alignment: {Changes}", string.Join("; ", changes));
            return current with { Mission = mission };
        }, PlanChange.Mission);
    }

    private void SetGrid(GeoBounds bounds, ElevationGrid elevation)
    {
        lock (_lock)
        {
            _elevation = elevation;
        }

        var shown = FlatHeight() is { } level ? Level(elevation, level) : elevation;
        var grid = new TerrainGrid(shown.SouthLatitude, shown.WestLongitude, shown.LatitudeStep, shown.LongitudeStep,
            shown.Rows, shown.Columns, shown.Heights, shown.Attribution);
        lock (_lock)
        {
            _bounds = bounds;
            _elevation = elevation;
            _grid = grid;
        }

        GridChanged?.Invoke(grid);
    }

    /// <summary>The box around the home, every located item, landing orbits and fences; null when the plan has no position.</summary>
    public static GeoBounds? BoundsOf(Mission mission, IEnumerable<Fence> fences)
    {
        var points = new List<(double, double)>();
        if (!mission.Settings.PlannedHome.IsUnset)
        {
            points.Add((mission.Settings.PlannedHome.Latitude, mission.Settings.PlannedHome.Longitude));
        }

        foreach (var item in mission.Items)
        {
            if (item.Location is { } location)
            {
                points.Add((location.Latitude, location.Longitude));
            }

            if (MissionItemValues.LandingPatternOrbitCenter(item) is { } center)
            {
                points.Add((center.Latitude, center.Longitude));
            }
        }

        points.AddRange(fences.SelectMany(fence => fence.Area.Points).Select(p => (p.Latitude, p.Longitude)));
        return GeoBounds.Around(points);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _plans.Changed -= OnPlanChanged;
        lock (_lock)
        {
            _settle?.Cancel();
        }
    }
}
