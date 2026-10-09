using Avalonia.Controls;
using Avalonia.Threading;
using Ketras.Controls.Scene3D.Rendering;
using Ketras.Model.Geo;
using Ketras.Model.Missions;
using Ketras.Model.Terrain;

namespace Ketras.Controls.Scene3D;

/// <summary>
/// Facade: the 3D airspace view. The GPU terrain is drawn under a software scene of volumes (fences and zones), the
/// planned route, points of interest and aircraft with trails, drop lines and shadows. Cameras: overview, follow an
/// aircraft (<see cref="Follow"/>), everything from above (<see cref="FitAll"/>) and free flight (right mouse button,
/// then W A S D / Q E, Shift, F, Esc); taking the camera with the right button ends following
/// (<see cref="FollowReleased"/>). The terrain is grey relief or draped with map imagery (<see cref="SetGround"/>). The host
/// shows the terrain data's and the imagery's credits.
/// Every Set and Follow method is safe from any thread; frequent updates are merged into one redraw. No panels: a
/// host puts its own over it.
/// </summary>
public sealed class AirspaceView : Panel
{
    private readonly object _lock = new();
    private readonly AirspaceViewport _viewport = new();
    private readonly TerrainView _terrain = new();

    private SceneOptions _options = new();
    private TrackedAircraft? _ownship;
    private IReadOnlyList<TrackedAircraft> _traffic = [];
    private IReadOnlyList<Fence> _volumes = [];
    private IReadOnlyList<MissionPathPointVO> _path = [];
    private IReadOnlyList<PointVO> _points = [];
    private string? _followed;
    private bool _queued;

    /// <summary>An empty scene.</summary>
    public AirspaceView()
    {
        Children.Add(_terrain);
        Children.Add(_viewport);
        _viewport.Terrain = _terrain;
        _viewport.FollowReleased += () =>
        {
            lock (_lock)
            {
                _followed = null;
            }

            FollowReleased?.Invoke();
        };
        Refresh();
    }

    /// <summary>Raised on the UI thread when the operator moves the camera off the followed aircraft (right button).</summary>
    public event Action? FollowReleased;

    /// <summary>The followed aircraft's id; null in overview or free flight.</summary>
    public string? Followed
    {
        get
        {
            lock (_lock)
            {
                return _followed;
            }
        }
    }

    /// <summary>Sets how the scene is framed and drawn, with its origin and vertical datum.</summary>
    public void SetOptions(SceneOptions options) => Update(() => _options = options);

    /// <summary>Sets the ownship; null removes it.</summary>
    public void SetOwnship(SceneAircraft? ownship) => Update(() => _ownship = ownship is null ? null : Track(ownship));

    /// <summary>Replaces the traffic.</summary>
    public void SetTraffic(IEnumerable<SceneAircraft> traffic)
    {
        var tracked = traffic.OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase).Select(Track).ToList();
        Update(() => _traffic = tracked);
    }

    /// <summary>Replaces the volumes (fences and zones).</summary>
    public void SetVolumes(IEnumerable<SceneVolume> volumes)
    {
        var fences = volumes.Select(ToFence).ToList();
        Update(() => _volumes = fences);
    }

    /// <summary>Replaces the planned route.</summary>
    public void SetPath(IEnumerable<ScenePathPoint> path)
    {
        var points = path.OrderBy(p => p.Sequence).Select(p => new MissionPathPointVO(
            p.Sequence, new GeoPointVO(p.Position.Latitude, p.Position.Longitude), p.AltitudeMeters, p.Label, ToPhase(p.Phase))).ToList();
        Update(() => _path = points);
    }

    /// <summary>Replaces the points of interest.</summary>
    public void SetPoints(IEnumerable<ScenePoint> points)
    {
        var mapped = points.Select(p => new PointVO(new GeoPointVO(p.Position.Latitude, p.Position.Longitude), p.Label)).ToList();
        Update(() => _points = mapped);
    }

    /// <summary>
    /// Sets the terrain under the scene (heights AMSL, the same datum as <see cref="SceneOptions.HomeAltitudeMeters"/>);
    /// null removes it. Drawn when OpenGL is available, otherwise the flat ground stays.
    /// </summary>
    public void SetTerrain(TerrainGrid? terrain) => OnUiThread(() => _terrain.SetGrid(terrain));

    /// <summary>
    /// Paints the terrain as grey relief under a dark sky, or with the imagery (<see cref="SetImagery"/>) under a blue sky
    /// fading to the horizon. Where no imagery is available the relief shows.
    /// </summary>
    public void SetGround(SceneGround ground) => OnUiThread(() =>
    {
        Ground = ground;
        _terrain.SetGround(ground);
        _viewport.SetGround(ground);
    });

    /// <summary>Sets the map tiles draped over the terrain in <see cref="SceneGround.Imagery"/>; null removes them.</summary>
    public void SetImagery(SceneImagery? imagery) => OnUiThread(() =>
    {
        Imagery = imagery;
        _terrain.SetImagery(imagery);
    });

    /// <summary>The ground style in use; for tests.</summary>
    internal SceneGround Ground { get; private set; }

    /// <summary>The imagery last set; for tests.</summary>
    internal SceneImagery? Imagery { get; private set; }

    /// <summary>Follows an aircraft by id; null returns to the overview.</summary>
    public void Follow(string? id) => Update(() => _followed = string.IsNullOrWhiteSpace(id) ? null : id.Trim());

    /// <summary>Frames the route, the aircraft and the volumes from above, ending following.</summary>
    public void FitAll() => OnUiThread(() =>
    {
        lock (_lock)
        {
            _followed = null;
        }

        Refresh();
        _viewport.FitAll();
    });

    /// <summary>The viewport; for tests.</summary>
    internal AirspaceViewport Viewport => _viewport;

    /// <summary>The scene as the renderer receives it; for tests.</summary>
    internal (AirspaceScene Scene, SceneOptions Options, string? Followed) Snapshot()
    {
        lock (_lock)
        {
            return (new AirspaceScene { Ownship = _ownship, Traffic = _traffic, Fences = _volumes, MissionPath = _path, Points = _points }, _options, _followed);
        }
    }

    private static TrackedAircraft Track(SceneAircraft aircraft) => new()
    {
        VehicleId = aircraft.Id,
        DisplayName = aircraft.Name,
        Latitude = aircraft.Position.Latitude,
        Longitude = aircraft.Position.Longitude,
        AltitudeMeters = aircraft.AltitudeMeters,
        HeadingDegrees = aircraft.HeadingDegrees,
        PitchDegrees = aircraft.PitchDegrees,
        RollDegrees = aircraft.RollDegrees,
        GroundSpeedMetersPerSecond = aircraft.GroundSpeedMps,
        VerticalSpeedMetersPerSecond = aircraft.VerticalSpeedMps,
        Mode = aircraft.Mode
    };

    private static Fence ToFence(SceneVolume volume) => volume.Area switch
    {
        CircleArea circle => new CircularFence
        {
            FenceId = volume.Id, Label = volume.Label, Inclusion = volume.StayIn, MinAltitudeMeters = volume.FloorMeters,
            MaxAltitudeMeters = volume.CeilingMeters, Center = new GeoPointVO(circle.Center.Latitude, circle.Center.Longitude), RadiusMeters = circle.RadiusMeters
        },
        PolygonArea polygon => new PolygonFence
        {
            FenceId = volume.Id, Label = volume.Label, Inclusion = volume.StayIn, MinAltitudeMeters = volume.FloorMeters,
            MaxAltitudeMeters = volume.CeilingMeters, Vertices = [.. polygon.Vertices.Select(v => new GeoPointVO(v.Latitude, v.Longitude))]
        },
        _ => throw new ArgumentException($"Unsupported area {volume.Area.GetType().Name}", nameof(volume))
    };

    private static MissionPathPhase ToPhase(MissionPhase phase) => phase switch
    {
        MissionPhase.Takeoff => MissionPathPhase.Takeoff,
        MissionPhase.Landing => MissionPathPhase.Landing,
        _ => MissionPathPhase.Cruise
    };

    private void Update(Action change)
    {
        lock (_lock)
        {
            change();
            if (_queued)
            {
                return;
            }

            _queued = true;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Refresh();
        }
        else
        {
            Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Render);
        }
    }

    private void Refresh()
    {
        AirspaceScene scene;
        SceneOptions options;
        string? followed;
        lock (_lock)
        {
            _queued = false;
            (scene, options, followed) = Snapshot();
        }

        _viewport.SetScene(scene, options, followed);
    }

    private static void OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
