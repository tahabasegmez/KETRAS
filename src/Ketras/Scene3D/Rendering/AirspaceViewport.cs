using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Ketras.Model.Geo;

namespace Ketras.Scene3D.Rendering;

/// <summary>The 3D stage, drawn in software over the GPU terrain. World frame: +X East, +Y Up, +Z North.</summary>
internal sealed class AirspaceViewport : Control
{
    private enum CameraMode
    {
        Auto,
        Free
    }

    // ------------------------------------------------------------------
    // Palette: the neutral dark scene of docs/UI_RULES.md ("3D scene palette"). Domain colours are the
    // KeepOut / StayIn / Takeoff / Cruise / Landing tokens; the ownship matches the planner's vehicle.
    // ------------------------------------------------------------------

    // Drawn at 27-65 % opacity over the ground, so the blend lands near MapGrid / MapGridMajor.
    private static readonly Color GridMinorColor = Color.FromRgb(0x3A, 0x3F, 0x44);
    private static readonly Color GridMajorColor = Color.FromRgb(0x4A, 0x50, 0x57);
    private static readonly Color RunwayColor = Color.FromRgb(0x2A, 0x2D, 0x2E);
    private static readonly Color RunwayMarkColor = Color.FromRgb(0x9D, 0x9D, 0x9D);
    private static readonly Color FenceExclusionColor = Color.FromRgb(0xF1, 0x4C, 0x4C);
    private static readonly Color FenceInclusionColor = Color.FromRgb(0x37, 0x94, 0xFF);
    private static readonly Color PathTakeoffColor = Color.FromRgb(0x89, 0xD1, 0x85);
    private static readonly Color PathCruiseColor = Color.FromRgb(0x37, 0x94, 0xFF);
    private static readonly Color PathLandingColor = Color.FromRgb(0xD1, 0x86, 0x16);
    private static readonly Color PointColor = Color.FromRgb(0xCC, 0xA7, 0x00);
    private static readonly Color PointEdgeColor = Color.FromRgb(0x1F, 0x1F, 0x1F);
    private static readonly Color ShadowColor = Color.FromRgb(0x0B, 0x0C, 0x0D);
    private static readonly Color OwnshipColor = Color.FromRgb(0x4D, 0xAA, 0xFC);
    private static readonly Color TrafficColor = Color.FromRgb(0xD1, 0x86, 0x16);
    private static readonly Color SelectionColor = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Color SymbolOwnshipFill = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Color SymbolEdge = Color.FromRgb(0x10, 0x10, 0x10);

    private static readonly IBrush HorizonGlowBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0),
            new GradientStop(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 0.5),
            new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1)
        }
    };

    // Label boxes as on the planner's map: one Mono 11 line, Overlay at 90 %, a Border frame, radius 2; the name in
    // Text, the readout after it in Muted.
    private static readonly IBrush LabelPlateBrush = new SolidColorBrush(Color.FromArgb(0xE6, 0x20, 0x20, 0x20));
    private static readonly IPen LabelPlatePen = new Pen(new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)), 1);
    private static readonly IBrush LabelTextBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
    private static readonly IBrush LabelMutedBrush = new SolidColorBrush(Color.FromRgb(0x9D, 0x9D, 0x9D));

    // Route points as on the planner's map: a disc of radius 9 in the phase colour, a #101010 outline of 1.5 and a
    // bold dark number.
    private const double NodeRadius = 9;
    private static readonly Color NodeInk = Color.FromRgb(0x10, 0x10, 0x10);
    private static readonly Typeface NodeTypeface = new(new FontFamily("Segoe UI, Inter, sans-serif"), FontStyle.Normal, FontWeight.Bold);
    private static readonly IPen CompassPen = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0x6E, 0x76, 0x81)), 1);
    private static readonly IPen CompassMajorPen = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0x9D, 0x9D, 0x9D)), 1.2);

    private static readonly Typeface LabelTypeface = new(new FontFamily("Segoe UI, Inter, sans-serif"), FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface DetailTypeface = new(new FontFamily("Cascadia Mono, Consolas, monospace"));

    // ------------------------------------------------------------------
    // Scene state
    // ------------------------------------------------------------------

    private const int MaxTrailPoints = 220;
    private static readonly TimeSpan TrailStaleAfter = TimeSpan.FromSeconds(6);
    private const double TrailMinDeltaMeters = 1.25;

    private sealed class TrailState
    {
        public readonly List<TrailPoint> Points = new(MaxTrailPoints);
        public DateTimeOffset LastSeen;
    }

    private readonly record struct TrailPoint(double Latitude, double Longitude, double AltitudeMeters, DateTimeOffset At);

    private readonly record struct LabelRequest(Point Anchor, string Title, string Detail, float Depth);

    private readonly record struct NodeRequest(Point Anchor, string Text, Color Fill, float Depth, float Fog);

    private readonly Dictionary<string, TrailState> _trails = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Color> _colorCache = new(StringComparer.OrdinalIgnoreCase);

    private TrackedAircraft? _ownship;
    private IReadOnlyList<TrackedAircraft> _traffic = [];
    private IReadOnlyList<Fence> _fences = [];
    private IReadOnlyList<MissionPathPointVO> _missionPath = [];
    private IReadOnlyList<PointVO> _points = [];
    private SceneOptions _options = new();
    private string? _trackingTargetVehicleId;

    // Horizontal anchor only; the vertical datum is never inferred from the scene - see SelectReferenceCenter.
    private (double Latitude, double Longitude)? _stableHomeCenter;

    // ------------------------------------------------------------------
    // Rendering scratch buffers (reused every frame)
    // ------------------------------------------------------------------

    private readonly SceneRenderer _renderer = new();
    private readonly ShadeCache _shades = new();
    private readonly List<LabelRequest> _labels = new(16);
    private readonly List<NodeRequest> _nodes = new(16);
    private readonly List<Rect> _occupiedLabelRects = new(16);
    private readonly Vector3[] _symbolWorld = new Vector3[AircraftSymbol.Outline.Length];
    private readonly Vector3[] _shadowWorld = new Vector3[AircraftSymbol.Outline.Length];
    private readonly Vector3[] _quad = new Vector3[4];
    private readonly List<Vector3> _polylineScratch = new(MaxTrailPoints);
    private readonly List<Vector3> _ringScratch = new(48);

    // ------------------------------------------------------------------
    // Camera state
    // ------------------------------------------------------------------

    private CameraMode _cameraMode = CameraMode.Auto;
    private const float MaxFreePitch = 1.48352986f; // ~85 degrees

    private Vector3 _freeEye;
    private float _freeYawRad;      // Rotation around +Y. 0 = forward along +Z (north).
    private float _freePitchRad;    // Positive = looking up, negative = looking down.
    private bool _rightButtonDown;
    private Point _lastPointerPosition;
    private readonly HashSet<Key> _heldKeys = new();
    private readonly DispatcherTimer _inputTimer;
    private DateTime _lastInputTick = DateTime.UtcNow;

    // Follow offset is stored in the tracked aircraft's local frame, so it damps heading changes smoothly.
    private Vector3 _followOffset;
    private bool _followOffsetValid;

    public AirspaceViewport()
    {
        Focusable = true;
        ClipToBounds = true;

        _shades.FogColor = _palette.Haze;

        _inputTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _inputTimer.Tick += OnInputTick;
        AttachedToVisualTree += (_, _) => _inputTimer.Start();
        DetachedFromVisualTree += (_, _) => _inputTimer.Stop();
    }

    /// <summary>GPU terrain layer drawn under this viewport; while it has a surface the flat backdrop, grid and runway are left out.</summary>
    public TerrainView? Terrain
    {
        get => _terrain;
        set
        {
            if (_terrain is not null)
            {
                _terrain.SurfaceChanged -= InvalidateVisual;
                _terrain.ImageChanged -= InvalidateVisual;
            }

            _terrain = value;
            if (_terrain is not null)
            {
                _terrain.SurfaceChanged += InvalidateVisual;
                _terrain.ImageChanged += InvalidateVisual;
            }

            InvalidateVisual();
        }
    }

    private TerrainView? _terrain;
    private ScenePalette _palette = ScenePalette.Night;

    // Imagery detail: a square block of tiles around where the camera looks, at the zoom its distance calls for.
    private const int DetailTiles = 12;
    private const int DetailMargin = 3;
    private const double MinimumDetailDistance = 30;
    private TileRange? _imageryBase;
    private int _imageryMaxZoom;
    private TileRange? _detailWanted;

    /// <summary>
    /// Raised on the UI thread when the camera wants a different block of finer imagery than the last one (null: the
    /// base imagery is fine enough, or the camera looks beyond it).
    /// </summary>
    public event Action<TileRange?>? DetailWanted;

    /// <summary>The block of the base imagery and the finest zoom its source has; null without imagery.</summary>
    public void SetImageryBase(TileRange? range, int maxZoom)
    {
        _imageryBase = range;
        _imageryMaxZoom = maxZoom;
        _detailWanted = null;
        InvalidateVisual();
    }

    /// <summary>
    /// Works out the detail block: where the line of sight meets the ground (at most four times the camera's height
    /// away, else straight below it), at the zoom whose pixels match the screen's from the camera's distance to that
    /// point. A block already asked for is kept while the point stays clear of its outer tiles.
    /// </summary>
    private void UpdateImageryDetail((double Latitude, double Longitude, double AltitudeMeters) center, CameraState camera, bool onTerrain)
    {
        if (!onTerrain || _palette != ScenePalette.Day || _imageryBase is not { } baseRange || Bounds.Height < 1)
        {
            WantDetail(null);
            return;
        }

        var scale = (float)Math.Clamp(_options.SceneScale, 0.2, 3.5);
        var eye = WorldToGeo(center, camera.Eye);
        var ground = _terrain?.Grid?.HeightAt(new GeoPoint(eye.Latitude, eye.Longitude)) ?? _options.HomeAltitudeMeters;
        var height = Math.Max(eye.AltitudeMeters - ground, 1);
        var sight = camera.Target - camera.Eye;
        var focus = camera.Eye with { Y = (float)((ground - center.AltitudeMeters) * scale) };
        if (sight.LengthSquared() > 1e-6f && Vector3.Normalize(sight) is var direction && direction.Y < -0.05f)
        {
            var reach = (float)Math.Min(height * scale / -direction.Y, 4 * height * scale);
            focus = camera.Eye + (direction * reach);
        }

        var point = WorldToGeo(center, focus);
        var distance = Math.Max(Vector3.Distance(camera.Eye, focus) / scale, MinimumDetailDistance);
        var zoom = WebMercatorRules.ZoomForHeight(distance, point.Latitude, _options.FieldOfViewDegrees, Bounds.Height, _imageryMaxZoom);
        var baseTiles = 1 << baseRange.Zoom;
        var (x, y) = (WebMercatorRules.X(point.Longitude), WebMercatorRules.Y(point.Latitude));
        var insideBase = x >= (double)baseRange.MinX / baseTiles && x < (double)(baseRange.MinX + baseRange.Columns) / baseTiles
                         && y >= (double)baseRange.MinY / baseTiles && y < (double)(baseRange.MinY + baseRange.Rows) / baseTiles;
        if (zoom <= baseRange.Zoom || !insideBase)
        {
            WantDetail(null);
            return;
        }

        if (_detailWanted is { } wanted && wanted.Zoom == zoom)
        {
            var n = 1 << zoom;
            var column = (int)Math.Floor(x * n);
            var row = (int)Math.Floor(y * n);
            if (column >= wanted.MinX + DetailMargin && column < wanted.MinX + wanted.Columns - DetailMargin
                && row >= wanted.MinY + DetailMargin && row < wanted.MinY + wanted.Rows - DetailMargin)
            {
                return;
            }
        }

        WantDetail(WebMercatorRules.Around(point.Latitude, point.Longitude, zoom, DetailTiles));
    }

    private void WantDetail(TileRange? range)
    {
        if (_detailWanted == range)
        {
            return;
        }

        _detailWanted = range;
        DetailWanted?.Invoke(range);
    }

    /// <summary>The sky, haze and flat ground to draw: <see cref="ScenePalette.Day"/> over imagery.</summary>
    public void SetGround(SceneGround ground)
    {
        var palette = ScenePalette.For(ground);
        if (palette == _palette)
        {
            return;
        }

        _palette = palette;
        _shades.FogColor = palette.Haze;
        InvalidateVisual();
    }

    public void SetScene(AirspaceScene scene, SceneOptions options, string? trackingTargetVehicleId)
    {
        ArgumentNullException.ThrowIfNull(scene);

        var incomingOptions = options ?? new SceneOptions();

        // If the consumer provides (or changes) an explicit home anchor, respect it.
        if (incomingOptions.HomeLatitude.HasValue && incomingOptions.HomeLongitude.HasValue)
        {
            _stableHomeCenter = (incomingOptions.HomeLatitude.Value, incomingOptions.HomeLongitude.Value);
        }
        else if (_options.HomeLatitude != incomingOptions.HomeLatitude ||
                 _options.HomeLongitude != incomingOptions.HomeLongitude)
        {
            // Explicit home was cleared; allow re-seed on next render.
            _stableHomeCenter = null;
        }

        _ownship = scene.Ownship;
        _traffic = scene.Traffic;
        _fences = scene.Fences;
        _missionPath = scene.MissionPath;
        _points = scene.Points;
        _options = incomingOptions;

        var normalizedTracking = string.IsNullOrWhiteSpace(trackingTargetVehicleId)
            ? null
            : trackingTargetVehicleId.Trim();

        // Switching the tracking target leaves free-fly mode so the camera UX is predictable.
        if (!string.Equals(_trackingTargetVehicleId, normalizedTracking, StringComparison.OrdinalIgnoreCase))
        {
            _cameraMode = CameraMode.Auto;
            _followOffsetValid = false;
        }

        _trackingTargetVehicleId = normalizedTracking;

        EnsureStableHomeSeeded();
        UpdateTrailHistory();

        InvalidateVisual();
    }

    /// <summary>Forces the camera back to overview/follow behavior, releasing any free-fly state.</summary>
    public void ResetCamera()
    {
        _cameraMode = CameraMode.Auto;
        _heldKeys.Clear();
        _rightButtonDown = false;
        _followOffsetValid = false;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------
    // Trail history
    // ------------------------------------------------------------------

    private void UpdateTrailHistory()
    {
        var now = DateTimeOffset.UtcNow;

        if (_ownship is not null)
        {
            AppendTrailPoint(_ownship, now);
        }

        for (var i = 0; i < _traffic.Count; i++)
        {
            AppendTrailPoint(_traffic[i], now);
        }

        // Drop trails for vehicles not seen recently so state doesn't accumulate forever.
        List<string>? expired = null;
        foreach (var pair in _trails)
        {
            if (now - pair.Value.LastSeen > TrailStaleAfter)
            {
                expired ??= [];
                expired.Add(pair.Key);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var id in expired)
        {
            _trails.Remove(id);
        }
    }

    private void AppendTrailPoint(TrackedAircraft telemetry, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(telemetry.VehicleId))
        {
            return;
        }

        if (!_trails.TryGetValue(telemetry.VehicleId, out var state))
        {
            state = new TrailState();
            _trails[telemetry.VehicleId] = state;
        }

        state.LastSeen = now;

        var incoming = new TrailPoint(telemetry.Latitude, telemetry.Longitude, telemetry.AltitudeMeters, now);

        if (state.Points.Count > 0 && GeoDistanceMeters(state.Points[^1], incoming) < TrailMinDeltaMeters)
        {
            // Skip tiny jitters so a stationary aircraft doesn't fill the history buffer.
            return;
        }

        state.Points.Add(incoming);

        // Trim by age first, then by capacity. Both are cheap because the list is ordered.
        var oldest = now - TimeSpan.FromSeconds(Math.Max(5d, _options.TrailSeconds));
        var drop = 0;
        while (drop < state.Points.Count && state.Points[drop].At < oldest)
        {
            drop++;
        }

        if (state.Points.Count - drop > MaxTrailPoints)
        {
            drop = state.Points.Count - MaxTrailPoints;
        }

        if (drop > 0)
        {
            state.Points.RemoveRange(0, drop);
        }
    }

    private static double GeoDistanceMeters(TrailPoint a, TrailPoint b)
    {
        const double earthRadius = 6378137;
        var latRad = DegreesToRadians(a.Latitude);
        var dNorth = (b.Latitude - a.Latitude) * Math.PI / 180d * earthRadius;
        var dEast = (b.Longitude - a.Longitude) * Math.PI / 180d * earthRadius * Math.Cos(latRad);
        var dUp = b.AltitudeMeters - a.AltitudeMeters;
        return Math.Sqrt((dNorth * dNorth) + (dEast * dEast) + (dUp * dUp));
    }

    private void EnsureStableHomeSeeded()
    {
        if (_stableHomeCenter.HasValue)
        {
            return;
        }

        if (_ownship is not null)
        {
            _stableHomeCenter = (_ownship.Latitude, _ownship.Longitude);
            return;
        }

        if (_missionPath.Count > 0)
        {
            _stableHomeCenter = (_missionPath[0].Position.Latitude, _missionPath[0].Position.Longitude);
            return;
        }

        if (_fences.Count > 0 && FenceAnchor(_fences[0]) is { } anchor)
        {
            _stableHomeCenter = (anchor.Latitude, anchor.Longitude);
            return;
        }

        if (_points.Count > 0)
        {
            _stableHomeCenter = (_points[0].Position.Latitude, _points[0].Position.Longitude);
            return;
        }

        if (_traffic.Count > 0)
        {
            var aircraft = _traffic[0];
            _stableHomeCenter = (aircraft.Latitude, aircraft.Longitude);
        }
    }

    // ------------------------------------------------------------------
    // Frame assembly
    // ------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 1 || bounds.Height <= 1)
        {
            return;
        }

        var center = SelectReferenceCenter();
        var camera = BuildCameraState(center);

        _renderer.BeginFrame(bounds.Size, camera.Eye, camera.Target, _options.FieldOfViewDegrees);
        _labels.Clear();
        _nodes.Clear();
        _occupiedLabelRects.Clear();

        _terrain?.SetFrame(new TerrainView.Frame(camera.Eye, camera.Target, _options.FieldOfViewDegrees,
            center.Latitude, center.Longitude, center.AltitudeMeters, _options.SceneScale, _options.FogStartMeters, _options.FogEndMeters));
        var onTerrain = _terrain is { HasSurface: true };
        UpdateImageryDetail(center, camera, onTerrain);

        // Scissor-clip everything to the control rect so nothing can spill outside the box.
        using (context.PushClip(bounds))
        {
            if (onTerrain && _terrain!.Image is { } terrain)
            {
                // The terrain layer's last frame (sky and ground), drawn here rather than relied on as its own layer.
                context.DrawImage(terrain, new Rect(terrain.Size), bounds);
            }
            else if (onTerrain)
            {
                // No frame read back yet; a transparent fill keeps the whole view hit-testable.
                context.FillRectangle(Brushes.Transparent, bounds);
            }
            else
            {
                DrawBackdrop(context, bounds, lookingUp: camera.Target.Y > camera.Eye.Y);
            }

            if (_options.ShowGrid && !onTerrain)
            {
                BuildGrid();
            }

            if (_options.ShowRunway && !onTerrain)
            {
                BuildRunway();
            }

            for (var i = 0; i < _fences.Count; i++)
            {
                BuildFence(center, _fences[i]);
            }

            if (_missionPath.Count > 0)
            {
                BuildMissionPath(center);
            }

            for (var i = 0; i < _points.Count; i++)
            {
                BuildPointMarker(center, _points[i]);
            }

            if (_ownship is not null)
            {
                BuildTrail(center, _ownship, isOwnship: true);
            }

            for (var i = 0; i < _traffic.Count; i++)
            {
                BuildTrail(center, _traffic[i], isOwnship: false);
            }

            if (_ownship is not null)
            {
                BuildAircraft(center, _ownship, isOwnship: true);
            }

            for (var i = 0; i < _traffic.Count; i++)
            {
                BuildAircraft(center, _traffic[i], isOwnship: false);
            }

            _renderer.Flush(context);

            if (_options.ShowCompassRibbon)
            {
                DrawCompassRibbon(context, bounds);
            }

            DrawNodes(context);
            DrawLabels(context, bounds);
        }
    }

    /// <summary>Sky and ground as two screen-space gradients split at the horizon line.</summary>
    private void DrawBackdrop(DrawingContext context, Rect bounds, bool lookingUp)
    {
        if (!_renderer.TryGetHorizonY(out var horizonY))
        {
            // Looking straight up/down: the horizon is off-screen, so one half-space fills the view.
            context.FillRectangle(lookingUp ? _palette.SkyBrush : _palette.GroundBrush, bounds);
            return;
        }

        var clampedHorizon = Math.Clamp(horizonY, bounds.Y, bounds.Bottom);

        if (clampedHorizon > bounds.Y)
        {
            context.FillRectangle(_palette.SkyBrush, new Rect(bounds.X, bounds.Y, bounds.Width, clampedHorizon - bounds.Y));
        }

        if (clampedHorizon < bounds.Bottom)
        {
            // Anchor to the true horizon even off-screen, so a steep pitch still ramps correctly.
            var groundTop = Math.Min(horizonY, clampedHorizon);
            context.FillRectangle(_palette.GroundBrush, new Rect(bounds.X, groundTop, bounds.Width, bounds.Bottom - groundTop));
        }

        if (horizonY > bounds.Y - 40 && horizonY < bounds.Bottom + 40)
        {
            context.FillRectangle(HorizonGlowBrush, new Rect(bounds.X, horizonY - 9, bounds.Width, 18));
        }
    }

    /// <summary>Camera-centred two-level grid so the ground never runs out at a fixed edge.</summary>
    private void BuildGrid()
    {
        var eye = _renderer.Eye;
        var fogEnd = (float)Math.Clamp(_options.FogEndMeters, 1500d, 40000d);

        // Spacing follows camera altitude so on-screen density stays roughly constant.
        var altitude = Math.Clamp(eye.Y, 25f, 8000f);
        var minor = NiceSpacing(altitude / 5f);

        BuildGridLevel(eye, minor, MathF.Min(minor * 30f, fogEnd), GridMinorColor, thickness: 1.0, alpha: 0x46, spans: 4);
        BuildGridLevel(eye, minor * 5f, MathF.Min(minor * 150f, fogEnd), GridMajorColor, thickness: 1.2, alpha: 0x7A, spans: 6);
        BuildGridLevel(eye, minor * 25f, fogEnd, GridMajorColor, thickness: 1.5, alpha: 0xA6, spans: 8);
    }

    /// <summary>Rounds to the nearest 1-2-5 decade step, the usual choice for map scales.</summary>
    private static float NiceSpacing(float value)
    {
        value = MathF.Max(value, 1f);
        var magnitude = MathF.Pow(10f, MathF.Floor(MathF.Log10(value)));
        var mantissa = value / magnitude;
        var snapped = mantissa <= 1.5f ? 1f : mantissa <= 3.5f ? 2f : mantissa <= 7.5f ? 5f : 10f;
        return snapped * magnitude;
    }

    private void BuildGridLevel(Vector3 eye, float spacing, float radius, Color color, double thickness, byte alpha, int spans)
    {
        const int maxSteps = 64;

        var centerX = MathF.Round(eye.X / spacing) * spacing;
        var centerZ = MathF.Round(eye.Z / spacing) * spacing;
        var steps = Math.Min((int)MathF.Ceiling(radius / spacing), maxSteps);
        radius = steps * spacing;

        for (var i = -steps; i <= steps; i++)
        {
            var offset = i * spacing;

            AddFoggedLine(
                new Vector3(centerX - radius, 0f, centerZ + offset),
                new Vector3(centerX + radius, 0f, centerZ + offset),
                color, thickness, alpha, spans, RenderLayer.Ground);

            AddFoggedLine(
                new Vector3(centerX + offset, 0f, centerZ - radius),
                new Vector3(centerX + offset, 0f, centerZ + radius),
                color, thickness, alpha, spans, RenderLayer.Ground);
        }
    }

    /// <summary>Splits a line into spans so it fades with distance instead of one flat colour.</summary>
    private void AddFoggedLine(
        Vector3 from,
        Vector3 to,
        Color color,
        double thickness,
        byte alpha,
        int spans,
        RenderLayer layer)
    {
        for (var i = 0; i < spans; i++)
        {
            var a = Vector3.Lerp(from, to, i / (float)spans);
            var b = Vector3.Lerp(from, to, (i + 1) / (float)spans);
            var fog = FogAt(Vector3.Distance((a + b) * 0.5f, _renderer.Eye));
            if (fog >= 0.985f)
            {
                continue;
            }

            _renderer.AddWorldSegment(a, b, layer, _shades.Pen(color, thickness, 1f, fog, alpha));
        }
    }

    private void BuildRunway()
    {
        var eye = _renderer.Eye;
        var distance = new Vector3(eye.X, 0f, eye.Z).Length();
        if (distance > _options.FogEndMeters)
        {
            return;
        }

        const float halfLength = 260f;
        const float halfWidth = 13f;

        var fog = FogAt(Vector3.Distance(Vector3.Zero, eye));

        _quad[0] = new Vector3(-halfLength, 0.05f, -halfWidth);
        _quad[1] = new Vector3(halfLength, 0.05f, -halfWidth);
        _quad[2] = new Vector3(halfLength, 0.05f, halfWidth);
        _quad[3] = new Vector3(-halfLength, 0.05f, halfWidth);
        _renderer.AddWorldPolygon(_quad, RenderLayer.Ground, _shades.Brush(RunwayColor, 1f, fog, 0xE0));

        // Centreline dashes show the runway's heading at a glance.
        for (var x = -halfLength + 20f; x < halfLength - 20f; x += 60f)
        {
            _quad[0] = new Vector3(x, 0.12f, -1.1f);
            _quad[1] = new Vector3(x + 30f, 0.12f, -1.1f);
            _quad[2] = new Vector3(x + 30f, 0.12f, 1.1f);
            _quad[3] = new Vector3(x, 0.12f, 1.1f);

            var dashFog = FogAt(Vector3.Distance(new Vector3(x + 15f, 0f, 0f), eye));
            if (dashFog >= 0.985f)
            {
                continue;
            }

            _renderer.AddWorldPolygon(_quad, RenderLayer.Ground, _shades.Brush(RunwayMarkColor, 1f, dashFog, 0x9A));
        }

        // Threshold bars at both ends.
        for (var end = 0; end < 2; end++)
        {
            var sign = end == 0 ? -1f : 1f;
            var baseX = sign * (halfLength - 6f);
            for (var lane = -3; lane <= 3; lane++)
            {
                var z = lane * 3.2f;
                _quad[0] = new Vector3(baseX - (sign * 14f), 0.12f, z - 1f);
                _quad[1] = new Vector3(baseX, 0.12f, z - 1f);
                _quad[2] = new Vector3(baseX, 0.12f, z + 1f);
                _quad[3] = new Vector3(baseX - (sign * 14f), 0.12f, z + 1f);

                var barFog = FogAt(Vector3.Distance(new Vector3(baseX, 0f, z), eye));
                if (barFog >= 0.985f)
                {
                    continue;
                }

                _renderer.AddWorldPolygon(_quad, RenderLayer.Ground, _shades.Brush(RunwayMarkColor, 1f, barFog, 0x88));
            }
        }
    }

    /// <summary>Where a fence sits, for anchoring the scene when nothing else is known.</summary>
    private static GeoPointVO? FenceAnchor(Fence fence) => fence switch
    {
        CircularFence circle => circle.Center,
        PolygonFence { Vertices.Count: > 0 } polygon => polygon.Vertices[0],
        _ => null
    };

    /// <summary>
    /// Fills <see cref="_ringScratch"/> with the fence's footprint in world space. A circle is
    /// just a sampled ring, which is what lets one extrusion routine draw both shapes.
    /// </summary>
    private void BuildFenceFootprint(
        (double Latitude, double Longitude, double AltitudeMeters) center,
        Fence fence,
        float scale)
    {
        _ringScratch.Clear();

        switch (fence)
        {
            case CircularFence circle:
            {
                const int segments = 36;

                var radius = (float)(Math.Clamp(circle.RadiusMeters, 8, 6000) * scale);
                var origin = GeoToWorld(center, circle.Center.Latitude, circle.Center.Longitude, fence.MinAltitudeMeters);

                for (var i = 0; i < segments; i++)
                {
                    var angle = Math.PI * 2d * i / segments;
                    _ringScratch.Add(new Vector3(
                        origin.X + (float)(Math.Cos(angle) * radius),
                        origin.Y,
                        origin.Z + (float)(Math.Sin(angle) * radius)));
                }

                break;
            }

            case PolygonFence polygon:
            {
                for (var i = 0; i < polygon.Vertices.Count; i++)
                {
                    var vertex = polygon.Vertices[i];
                    _ringScratch.Add(GeoToWorld(center, vertex.Latitude, vertex.Longitude, fence.MinAltitudeMeters));
                }

                break;
            }
        }
    }

    /// <summary>
    /// Fence as a translucent extrusion of its footprint: walls, capped top, outlines and ribs.
    /// Inclusion fences are cyan and exclusion fences red, so "stay inside" never reads as
    /// "keep out".
    /// </summary>
    private void BuildFence(
        (double Latitude, double Longitude, double AltitudeMeters) center,
        Fence fence)
    {
        var scale = (float)Math.Clamp(_options.SceneScale, 0.2, 3.5);
        BuildFenceFootprint(center, fence, scale);

        var count = _ringScratch.Count;
        if (count < 3)
        {
            return;
        }

        var color = fence.Inclusion ? FenceInclusionColor : FenceExclusionColor;

        var baseY = _ringScratch[0].Y;
        var height = (float)Math.Max(2d, fence.MaxAltitudeMeters - fence.MinAltitudeMeters) * scale;
        var topY = baseY + height;

        // Centroid and extent drive both the wall normals and the distance cull.
        var centroid = Vector3.Zero;
        for (var i = 0; i < count; i++)
        {
            centroid += _ringScratch[i];
        }

        centroid /= count;

        var extent = 0f;
        for (var i = 0; i < count; i++)
        {
            extent = MathF.Max(extent, Vector3.Distance(_ringScratch[i], centroid));
        }

        var eye = _renderer.Eye;
        if (Vector3.Distance(new Vector3(centroid.X, eye.Y, centroid.Z), eye) - extent > _options.FogEndMeters)
        {
            return;
        }

        // Side walls.
        for (var i = 0; i < count; i++)
        {
            var a = _ringScratch[i];
            var b = _ringScratch[(i + 1) % count];

            var outward = new Vector3(
                ((a.X + b.X) * 0.5f) - centroid.X,
                0f,
                ((a.Z + b.Z) * 0.5f) - centroid.Z);
            if (outward.LengthSquared() < 1e-6f)
            {
                continue;
            }

            outward = Vector3.Normalize(outward);

            var panelCenter = new Vector3((a.X + b.X) * 0.5f, baseY + (height * 0.5f), (a.Z + b.Z) * 0.5f);
            var fog = FogAt(Vector3.Distance(panelCenter, eye));
            if (fog >= 0.985f)
            {
                continue;
            }

            // Panels edge-on to the viewer get a brighter rim for a readable silhouette.
            var facing = MathF.Abs(Vector3.Dot(outward, Vector3.Normalize(eye - panelCenter)));
            var rim = 1f - facing;
            var alpha = (byte)Math.Clamp(34 + (rim * rim * 96f), 24f, 150f);

            _quad[0] = a;
            _quad[1] = b;
            _quad[2] = new Vector3(b.X, topY, b.Z);
            _quad[3] = new Vector3(a.X, topY, a.Z);
            _renderer.AddWorldPolygon(_quad, RenderLayer.World, _shades.Brush(color, _shades.LambertFor(outward), fog, alpha));
        }

        // Top cap, outlines and ribs.
        _polylineScratch.Clear();
        for (var i = 0; i < count; i++)
        {
            var p = _ringScratch[i];
            _polylineScratch.Add(new Vector3(p.X, topY, p.Z));
        }

        var capFog = FogAt(Vector3.Distance(new Vector3(centroid.X, topY, centroid.Z), eye));
        _renderer.AddWorldPolygon(
            CollectionsMarshal.AsSpan(_polylineScratch),
            RenderLayer.World,
            _shades.Brush(color, _shades.LambertFor(Vector3.UnitY), capFog, 0x30));

        _polylineScratch.Add(_polylineScratch[0]);
        _renderer.AddWorldPolyline(
            CollectionsMarshal.AsSpan(_polylineScratch),
            RenderLayer.World,
            _shades.Pen(color, 1.7, 1f, capFog, 0xE0));

        _polylineScratch.Clear();
        for (var i = 0; i < count; i++)
        {
            _polylineScratch.Add(_ringScratch[i]);
        }

        _polylineScratch.Add(_ringScratch[0]);
        _renderer.AddWorldPolyline(
            CollectionsMarshal.AsSpan(_polylineScratch),
            RenderLayer.World,
            _shades.Pen(color, 1.4, 1f, FogAt(Vector3.Distance(centroid, eye)), 0xC0));

        // Eight ribs around a circle; every corner of a polygon.
        var ribStep = Math.Max(1, count / 8);
        for (var i = 0; i < count; i += ribStep)
        {
            var p = _ringScratch[i];
            var ribFog = FogAt(Vector3.Distance(p, eye));
            if (ribFog >= 0.985f)
            {
                continue;
            }

            _renderer.AddWorldSegment(p, new Vector3(p.X, topY, p.Z), RenderLayer.World, _shades.Pen(color, 1.1, 1f, ribFog, 0x9A));
        }

        if (!string.IsNullOrWhiteSpace(fence.Label) &&
            _renderer.TryProjectWorld(new Vector3(centroid.X, topY, centroid.Z), out var labelAnchor, out var labelDepth))
        {
            _labels.Add(new LabelRequest(
                labelAnchor,
                fence.Label,
                string.Empty,
                labelDepth));
        }
    }

    private static Color PathColor(MissionPathPhase phase) => phase switch
    {
        MissionPathPhase.Takeoff => PathTakeoffColor,
        MissionPathPhase.Landing => PathLandingColor,
        _ => PathCruiseColor
    };

    /// <summary>
    /// The planned route in 3D: legs coloured by phase over a faint glow, a numbered disc at each point with its name,
    /// and a drop line down to the terrain under it (the datum plane without terrain there). The drop lines are what make altitude readable - without them a
    /// route floating in mid-air gives no sense of how high it is.
    /// </summary>
    private void BuildMissionPath((double Latitude, double Longitude, double AltitudeMeters) center)
    {
        var eye = _renderer.Eye;

        Vector3 WorldOf(MissionPathPointVO point) => GeoToWorld(
            center,
            point.Position.Latitude,
            point.Position.Longitude,
            point.AltitudeMeters);

        // Legs take the colour of the point they lead into, so phase changes read along the route.
        for (var i = 1; i < _missionPath.Count; i++)
        {
            var from = WorldOf(_missionPath[i - 1]);
            var to = WorldOf(_missionPath[i]);

            var fog = FogAt(Vector3.Distance((from + to) * 0.5f, eye));
            if (fog >= 0.985f)
            {
                continue;
            }

            var legColor = PathColor(_missionPath[i].Phase);
            _renderer.AddWorldSegment(from, to, RenderLayer.World, _shades.Pen(legColor, 7.0, 1f, fog, 0x40));
            _renderer.AddWorldSegment(from, to, RenderLayer.World, _shades.Pen(legColor, 2.5, 1f, fog, 0xE0));
        }

        for (var i = 0; i < _missionPath.Count; i++)
        {
            var point = _missionPath[i];
            var world = WorldOf(point);
            var fog = FogAt(Vector3.Distance(world, eye));
            if (fog >= 0.985f)
            {
                continue;
            }

            var color = PathColor(point.Phase);
            var ground = SurfaceBelow(center, point.Position.Latitude, point.Position.Longitude, world);

            _renderer.AddWorldSegment(ground, world, RenderLayer.World, _shades.Pen(color, 1.0, 1f, fog, 0x55));

            if (_renderer.TryProjectWorld(world, out var anchor, out var depth))
            {
                // The disc carries the sequence; a point without one (home) shows its label's first letter.
                var number = point.Sequence >= 0
                    ? point.Sequence.ToString(CultureInfo.InvariantCulture)
                    : string.IsNullOrWhiteSpace(point.Label) ? string.Empty : point.Label[..1].ToUpperInvariant();
                _nodes.Add(new NodeRequest(anchor, number, color, depth, fog));

                if (!string.IsNullOrWhiteSpace(point.Label))
                {
                    _labels.Add(new LabelRequest(anchor, point.Label, string.Empty, depth));
                }
            }
        }
    }

    /// <summary>A point of interest: a square ground plate with a short pin and its label.</summary>
    private void BuildPointMarker((double Latitude, double Longitude, double AltitudeMeters) center, PointVO point)
    {
        const float half = 5f;

        var eye = _renderer.Eye;
        var world = GeoToWorld(center, point.Position.Latitude, point.Position.Longitude, 0d);
        var distance = Vector3.Distance(world, eye);
        if (distance > _options.FogEndMeters)
        {
            return;
        }

        var fog = FogAt(distance);
        _quad[0] = new Vector3(world.X - half, 0.06f, world.Z - half);
        _quad[1] = new Vector3(world.X + half, 0.06f, world.Z - half);
        _quad[2] = new Vector3(world.X + half, 0.06f, world.Z + half);
        _quad[3] = new Vector3(world.X - half, 0.06f, world.Z + half);
        _renderer.AddWorldPolygon(_quad, RenderLayer.Decal, _shades.Brush(PointColor, 1f, fog, 0xC0));

        _polylineScratch.Clear();
        _polylineScratch.AddRange(_quad);
        _polylineScratch.Add(_quad[0]);
        _renderer.AddWorldPolyline(CollectionsMarshal.AsSpan(_polylineScratch), RenderLayer.Decal, _shades.Pen(PointEdgeColor, 1.2, 1f, fog, 0xE0));

        if (_renderer.TryProjectWorld(new Vector3(world.X, 0.6f, world.Z), out var anchor, out var depth))
        {
            _labels.Add(new LabelRequest(
                anchor,
                point.Label,
                string.Empty,
                depth));
        }
    }

    /// <summary>Flight history, faded by age and optionally extruded to the ground as a curtain.</summary>
    private void BuildTrail(
        (double Latitude, double Longitude, double AltitudeMeters) center,
        TrackedAircraft telemetry,
        bool isOwnship)
    {
        if (string.IsNullOrWhiteSpace(telemetry.VehicleId) ||
            !_trails.TryGetValue(telemetry.VehicleId, out var state) ||
            state.Points.Count < 2)
        {
            return;
        }

        const int ageBands = 5;

        var color = ResolveColor(telemetry, isOwnship);
        var now = DateTimeOffset.UtcNow;
        var span = Math.Max(5d, _options.TrailSeconds);
        var eye = _renderer.Eye;

        var currentBand = -1;
        _polylineScratch.Clear();

        for (var i = 0; i < state.Points.Count; i++)
        {
            var point = state.Points[i];
            var age = (now - point.At).TotalSeconds / span;
            var band = Math.Clamp((int)(age * ageBands), 0, ageBands - 1);

            if (band != currentBand && _polylineScratch.Count > 0)
            {
                FlushTrailRun(currentBand, ageBands, color, isOwnship, eye);
                // Keep the last point so consecutive bands stay visually connected.
                var carry = _polylineScratch[^1];
                _polylineScratch.Clear();
                _polylineScratch.Add(carry);
            }

            currentBand = band;
            _polylineScratch.Add(GeoToWorld(center, point.Latitude, point.Longitude, point.AltitudeMeters));
        }

        FlushTrailRun(currentBand, ageBands, color, isOwnship, eye);

        if (_options.ShowTrailCurtain && isOwnship)
        {
            BuildTrailCurtain(center, state, color, now, span);
        }
    }

    private void FlushTrailRun(int band, int bandCount, Color color, bool isOwnship, Vector3 eye)
    {
        if (band < 0 || _polylineScratch.Count < 2)
        {
            return;
        }

        // Newest band is fully opaque; the oldest fades to roughly a fifth.
        var freshness = 1f - (band / (float)bandCount);
        var alpha = (byte)Math.Clamp(40 + (freshness * 195f), 30f, 255f);
        var fog = FogAt(Vector3.Distance(_polylineScratch[_polylineScratch.Count / 2], eye));

        _renderer.AddWorldPolyline(
            CollectionsMarshal.AsSpan(_polylineScratch),
            RenderLayer.World,
            _shades.Pen(color, isOwnship ? 2.4 : 1.9, 1f, fog, alpha));
    }

    private void BuildTrailCurtain(
        (double Latitude, double Longitude, double AltitudeMeters) center,
        TrailState state,
        Color color,
        DateTimeOffset now,
        double span)
    {
        // Subsampled: this is a background cue, not worth a quad per telemetry sample.
        const int stride = 3;

        var eye = _renderer.Eye;

        for (var i = 0; i + stride < state.Points.Count; i += stride)
        {
            var a = state.Points[i];
            var b = state.Points[i + stride];

            var worldA = GeoToWorld(center, a.Latitude, a.Longitude, a.AltitudeMeters);
            var worldB = GeoToWorld(center, b.Latitude, b.Longitude, b.AltitudeMeters);

            var centroid = (worldA + worldB) * 0.5f;
            var fog = FogAt(Vector3.Distance(centroid, eye));
            if (fog >= 0.96f)
            {
                continue;
            }

            // Deliberately faint so it doesn't compete with the trail line itself.
            var freshness = 1d - Math.Clamp((now - b.At).TotalSeconds / span, 0d, 1d);
            var alpha = (byte)Math.Clamp(4 + (freshness * 15d), 3d, 20d);

            _quad[0] = worldA;
            _quad[1] = worldB;
            _quad[2] = new Vector3(worldB.X, 0f, worldB.Z);
            _quad[3] = new Vector3(worldA.X, 0f, worldA.Z);
            _renderer.AddWorldPolygon(_quad, RenderLayer.World, _shades.Brush(color, 0.85f, fog, alpha));
        }
    }

    /// <summary>
    /// One aircraft: the planner's arrow in its body plane, its shadow and drop line on the surface below (the terrain,
    /// or the datum plane without terrain there), the selection ring and its name.
    /// </summary>
    private void BuildAircraft(
        (double Latitude, double Longitude, double AltitudeMeters) center,
        TrackedAircraft telemetry,
        bool isOwnship)
    {
        var world = GeoToWorld(center, telemetry.Latitude, telemetry.Longitude, telemetry.AltitudeMeters);
        var eye = _renderer.Eye;
        var distance = Vector3.Distance(world, eye);

        if (distance > _options.FogEndMeters * 1.2)
        {
            return;
        }

        var color = ResolveColor(telemetry, isOwnship);
        var fog = FogAt(distance);
        var isTracked = !string.IsNullOrWhiteSpace(_trackingTargetVehicleId) &&
                        string.Equals(telemetry.VehicleId, _trackingTargetVehicleId, StringComparison.OrdinalIgnoreCase);

        // True scale up close, then magnified with distance so a contact stays readable.
        var wingspan = (float)Math.Clamp(_options.AircraftWingspanMeters, 1d, 200d);
        var scale = ReadableScale(wingspan, distance);
        var rotation = BuildAircraftRotation(telemetry);
        for (var i = 0; i < AircraftSymbol.Outline.Length; i++)
        {
            _symbolWorld[i] = world + Vector3.Transform(AircraftSymbol.Outline[i] * scale, rotation);
        }

        // As on the planner's map: the ownship white, others in their colour, both with a dark edge.
        var fill = isOwnship ? SymbolOwnshipFill : color;
        _renderer.AddWorldPolygon(_symbolWorld, RenderLayer.World, _shades.Brush(fill, 1f, fog), _shades.Pen(SymbolEdge, 1.5, 1f, fog));

        var surface = SurfaceBelow(center, telemetry.Latitude, telemetry.Longitude, world);
        if (_options.ShowAltitudeDropLines)
        {
            if (world.Y > surface.Y + 1f)
            {
                _renderer.AddWorldSegment(surface, world, RenderLayer.World, _shades.Pen(color, 1.1, 1f, fog, 0x62));
            }
        }

        if (_options.ShowGroundShadows)
        {
            // The shadow is farther from the eye, so it needs its own readability scale.
            BuildGroundShadow(surface, telemetry, ReadableScale(wingspan, Vector3.Distance(surface, eye)), fog);
        }

        if (isTracked)
        {
            BuildSelectionRing(surface, MathF.Max(scale * 0.9f, 18f), fog);
        }

        if (_renderer.TryProjectWorld(world, out var anchor, out var depth))
        {
            var name = string.IsNullOrWhiteSpace(telemetry.DisplayName) ? telemetry.VehicleId : telemetry.DisplayName;
            _labels.Add(new LabelRequest(
                anchor,
                name,
                string.Empty,
                depth));
        }
    }

    /// <summary>The point on the terrain under <paramref name="world"/>, or on the datum plane without terrain there.</summary>
    private Vector3 SurfaceBelow((double Latitude, double Longitude, double AltitudeMeters) center, double latitude, double longitude, Vector3 world)
    {
        var height = _terrain is { HasSurface: true, Grid: { } grid } ? grid.HeightAt(new Ketras.Model.Geo.GeoPoint(latitude, longitude)) : null;
        var y = height is { } h ? GeoToWorld(center, latitude, longitude, h).Y : 0f;
        return new Vector3(world.X, y, world.Z);
    }

    /// <summary>Symbol scale in meters: true size up close, magnified so it stays readable far out.</summary>
    private float ReadableScale(float trueSizeMeters, float distance)
    {
        var pixels = trueSizeMeters * _renderer.PixelsPerMeter(Math.Max(distance, SceneRenderer.NearPlaneMeters));
        var magnification = (float)Math.Max(1d, _options.MinimumAircraftPixels / Math.Max(pixels, 0.01d));
        return trueSizeMeters * magnification;
    }

    private void BuildGroundShadow(Vector3 ground, TrackedAircraft telemetry, float scale, float fog)
    {
        // Heading only - pitch/roll must not rotate a ground projection.
        var yaw = Matrix4x4.CreateRotationY((float)DegreesToRadians(telemetry.HeadingDegrees));

        for (var i = 0; i < AircraftSymbol.Outline.Length; i++)
        {
            var rotated = Vector3.Transform(AircraftSymbol.Outline[i] * scale, yaw);
            _shadowWorld[i] = new Vector3(ground.X + rotated.X, ground.Y + 0.2f, ground.Z + rotated.Z);
        }

        _renderer.AddWorldPolygon(_shadowWorld, RenderLayer.Decal, _shades.Brush(ShadowColor, 0.2f, fog, 0x7A));
    }

    private void BuildSelectionRing(Vector3 ground, float radius, float fog)
    {
        const int segments = 32;

        _polylineScratch.Clear();
        for (var i = 0; i <= segments; i++)
        {
            var angle = Math.PI * 2d * i / segments;
            _polylineScratch.Add(new Vector3(
                ground.X + (float)(Math.Cos(angle) * radius),
                0.3f,
                ground.Z + (float)(Math.Sin(angle) * radius)));
        }

        _renderer.AddWorldPolyline(
            CollectionsMarshal.AsSpan(_polylineScratch),
            RenderLayer.Decal,
            _shades.Pen(SelectionColor, 1.8, 1f, fog, 0xD0));
    }

    // ------------------------------------------------------------------
    // Screen-space overlays
    // ------------------------------------------------------------------

    /// <summary>Bearing ticks along the horizon so free-fly keeps a sense of direction.</summary>
    private void DrawCompassRibbon(DrawingContext context, Rect bounds)
    {
        if (!_renderer.TryGetHorizonY(out var horizonY) || horizonY < bounds.Y - 60 || horizonY > bounds.Bottom + 60)
        {
            return;
        }

        var eye = _renderer.Eye;
        var y = Math.Clamp(horizonY, bounds.Y + 12, bounds.Bottom - 12);

        for (var bearing = 0; bearing < 360; bearing += 15)
        {
            var radians = DegreesToRadians(bearing);
            var direction = new Vector3((float)Math.Sin(radians), 0f, (float)Math.Cos(radians));
            var far = eye + (direction * 20000f);
            far.Y = 0f;

            if (!_renderer.TryProjectWorld(far, out var point, out _))
            {
                continue;
            }

            if (point.X < bounds.X || point.X > bounds.Right)
            {
                continue;
            }

            var isMajor = bearing % 45 == 0;
            var length = isMajor ? 9d : 5d;
            context.DrawLine(
                isMajor ? CompassMajorPen : CompassPen,
                new Point(point.X, y - length),
                new Point(point.X, y + length));

            if (!isMajor)
            {
                continue;
            }

            var text = new FormattedText(
                CardinalName(bearing),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                11,
                LabelMutedBrush);

            context.DrawText(text, new Point(point.X - (text.Width * 0.5), y - length - text.Height - 2));
        }
    }

    private static string CardinalName(int bearing) => bearing switch
    {
        0 => "N",
        45 => "NE",
        90 => "E",
        135 => "SE",
        180 => "S",
        225 => "SW",
        270 => "W",
        _ => "NW"
    };

    /// <summary>Route discs, farthest first so nearer ones cover them; fog fades them like the scene.</summary>
    private void DrawNodes(DrawingContext context)
    {
        if (_nodes.Count == 0)
        {
            return;
        }

        _nodes.Sort(static (a, b) => b.Depth.CompareTo(a.Depth));
        foreach (var node in _nodes)
        {
            var opacity = 1d - node.Fog;
            var fill = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(node.Fill, opacity);
            var ink = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(NodeInk, opacity);
            context.DrawEllipse(fill, new Pen(ink, 1.5), node.Anchor, NodeRadius, NodeRadius);
            if (node.Text.Length == 0)
            {
                continue;
            }

            var number = new FormattedText(node.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, NodeTypeface, 11, ink);
            context.DrawText(number, new Point(node.Anchor.X - (number.Width / 2), node.Anchor.Y - (number.Height / 2)));
        }
    }

    /// <summary>Labels placed nearest-first, pushed up on overlap so a formation stays legible.</summary>
    private void DrawLabels(DrawingContext context, Rect bounds)
    {
        if (_labels.Count == 0)
        {
            return;
        }

        _labels.Sort(static (a, b) => a.Depth.CompareTo(b.Depth));

        for (var i = 0; i < _labels.Count; i++)
        {
            var request = _labels[i];

            var text = new FormattedText(
                string.IsNullOrEmpty(request.Detail) ? request.Title : $"{request.Title}  {request.Detail}",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                DetailTypeface,
                11,
                LabelTextBrush);
            if (!string.IsNullOrEmpty(request.Detail))
            {
                text.SetForegroundBrush(LabelMutedBrush, request.Title.Length, request.Detail.Length + 2);
            }

            const double padX = 5;
            const double padY = 2;
            var width = text.Width + (padX * 2);
            var height = text.Height + (padY * 2);

            // To the right of the symbol, clear of a route disc.
            var rect = new Rect(request.Anchor.X + NodeRadius + 5, request.Anchor.Y - (height / 2), width, height);
            rect = PlaceWithoutOverlap(rect, bounds);

            if (!bounds.Intersects(rect))
            {
                continue;
            }

            _occupiedLabelRects.Add(rect);

            // Leader line back to the symbol when the label had to move.
            if (Math.Abs(rect.Center.Y - request.Anchor.Y) > 1)
            {
                context.DrawLine(LabelPlatePen, request.Anchor, new Point(rect.X, Math.Clamp(request.Anchor.Y, rect.Y, rect.Bottom)));
            }

            context.DrawRectangle(LabelPlateBrush, LabelPlatePen, rect, 2, 2);
            context.DrawText(text, new Point(rect.X + padX, rect.Y + padY));
        }
    }

    private Rect PlaceWithoutOverlap(Rect rect, Rect bounds)
    {
        const int maxAttempts = 6;

        var candidate = rect;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var collides = false;
            for (var i = 0; i < _occupiedLabelRects.Count; i++)
            {
                if (_occupiedLabelRects[i].Intersects(candidate))
                {
                    collides = true;
                    break;
                }
            }

            if (!collides)
            {
                break;
            }

            candidate = candidate.WithY(candidate.Y - (candidate.Height + 3));
            if (candidate.Y < bounds.Y)
            {
                candidate = candidate.WithY(rect.Y + ((attempt + 1) * (rect.Height + 3)));
            }
        }

        return candidate;
    }

    // ------------------------------------------------------------------
    // Camera
    // ------------------------------------------------------------------

    /// <summary>World origin for this frame; the vertical datum is fixed, never derived from a vehicle's altitude.</summary>
    private (double Latitude, double Longitude, double AltitudeMeters) SelectReferenceCenter()
    {
        var groundAltitude = _options.HomeAltitudeMeters;

        if (_cameraMode == CameraMode.Auto && !string.IsNullOrWhiteSpace(_trackingTargetVehicleId))
        {
            var tracked = ResolveTrackedAircraft(_trackingTargetVehicleId!);
            if (tracked is not null)
            {
                return (tracked.Latitude, tracked.Longitude, groundAltitude);
            }
        }

        EnsureStableHomeSeeded();
        return _stableHomeCenter is { } home
            ? (home.Latitude, home.Longitude, groundAltitude)
            : (41.0, 29.0, groundAltitude);
    }

    private CameraState BuildCameraState((double Latitude, double Longitude, double AltitudeMeters) center)
    {
        if (_cameraMode == CameraMode.Free)
        {
            return new CameraState(_freeEye, _freeEye + GetFreeForward());
        }

        if (!string.IsNullOrWhiteSpace(_trackingTargetVehicleId))
        {
            var tracked = ResolveTrackedAircraft(_trackingTargetVehicleId!);
            if (tracked is not null)
            {
                var target = GeoToWorld(center, tracked.Latitude, tracked.Longitude, tracked.AltitudeMeters) + new Vector3(0, 6, 0);
                return new CameraState(target + GetFollowOffset(tracked), target);
            }
        }

        // Overview mode: pinned to the stable home anchor, configuration-driven framing.
        var overviewTarget = new Vector3(0f, 60f, 0f);
        var yaw = DegreesToRadians(_options.CameraYawDegrees);
        var pitch = DegreesToRadians(Math.Abs(_options.OverviewCameraPitchDegrees));
        var distance = (float)Math.Clamp(_options.OverviewRadiusMeters, 250d, 10000d);

        var overviewEye = overviewTarget + new Vector3(
            (float)(distance * Math.Cos(pitch) * Math.Cos(yaw)),
            (float)(distance * Math.Sin(pitch)),
            (float)(distance * Math.Cos(pitch) * Math.Sin(yaw)));

        return new CameraState(overviewEye, overviewTarget);
    }

    private Vector3 DesiredFollowOffset(TrackedAircraft tracked)
    {
        var headingRad = DegreesToRadians(tracked.HeadingDegrees);
        var forward = new Vector3((float)Math.Sin(headingRad), 0f, (float)Math.Cos(headingRad));
        if (forward.LengthSquared() < 0.0001f)
        {
            forward = Vector3.UnitZ;
        }

        var followDistance = (float)Math.Clamp(_options.FollowDistanceMeters, 80, 1200);
        var followHeight = (float)Math.Clamp(_options.FollowHeightMeters, 25, 500);
        return (-forward * followDistance) + new Vector3(0, followHeight, 0);
    }

    private Vector3 GetFollowOffset(TrackedAircraft tracked)
    {
        var desired = DesiredFollowOffset(tracked);
        if (!_followOffsetValid)
        {
            _followOffset = desired;
            _followOffsetValid = true;
        }

        return _followOffset;
    }

    private readonly record struct CameraState(Vector3 Eye, Vector3 Target);

    // ------------------------------------------------------------------
    // Geometry helpers
    // ------------------------------------------------------------------

    private static Matrix4x4 BuildAircraftRotation(TrackedAircraft telemetry)
    {
        // Heading 0 = North (+Z); pitch/roll inverted so +pitch = nose up, +roll = right wing down.
        var yaw = (float)DegreesToRadians(telemetry.HeadingDegrees);
        var pitch = (float)DegreesToRadians(-telemetry.PitchDegrees);
        var roll = (float)DegreesToRadians(-telemetry.RollDegrees);
        return Matrix4x4.CreateRotationZ(roll)
               * Matrix4x4.CreateRotationX(pitch)
               * Matrix4x4.CreateRotationY(yaw);
    }

    private Vector3 GeoToWorld(
        (double Latitude, double Longitude, double AltitudeMeters) center,
        double latitude,
        double longitude,
        double altitudeMeters)
    {
        const double earthRadius = 6378137;
        var latRad = DegreesToRadians(center.Latitude);
        var north = (latitude - center.Latitude) * (Math.PI / 180d) * earthRadius;
        var east = (longitude - center.Longitude) * (Math.PI / 180d) * earthRadius * Math.Cos(latRad);
        var up = altitudeMeters - center.AltitudeMeters;
        var scale = Math.Clamp(_options.SceneScale, 0.2, 3.5);
        // World axes: +X = East, +Y = Up, +Z = North.
        return new Vector3((float)(east * scale), (float)(up * scale), (float)(north * scale));
    }

    /// <summary>The inverse of <see cref="GeoToWorld"/>.</summary>
    private (double Latitude, double Longitude, double AltitudeMeters) WorldToGeo(
        (double Latitude, double Longitude, double AltitudeMeters) center,
        Vector3 world)
    {
        const double earthRadius = 6378137;
        var scale = Math.Clamp(_options.SceneScale, 0.2, 3.5);
        var north = world.Z / scale;
        var east = world.X / scale;
        return (
            center.Latitude + (north / earthRadius * (180d / Math.PI)),
            center.Longitude + (east / (earthRadius * Math.Cos(DegreesToRadians(center.Latitude))) * (180d / Math.PI)),
            center.AltitudeMeters + (world.Y / scale));
    }

    /// <summary>Smoothstep haze factor: 0 at FogStartMeters, 1 at FogEndMeters.</summary>
    private float FogAt(float distance)
    {
        var start = (float)Math.Max(1d, _options.FogStartMeters);
        var end = (float)Math.Max(start + 1f, _options.FogEndMeters);
        var t = Math.Clamp((distance - start) / (end - start), 0f, 1f);
        return t * t * (3f - (2f * t));
    }

    private Color ResolveColor(TrackedAircraft telemetry, bool isOwnship)
    {
        var hex = telemetry.ColorHex;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return isOwnship ? OwnshipColor : TrafficColor;
        }

        if (_colorCache.TryGetValue(hex, out var cached))
        {
            return cached;
        }

        var color = Color.TryParse(hex, out var parsed)
            ? parsed
            : isOwnship ? OwnshipColor : TrafficColor;

        _colorCache[hex] = color;
        return color;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;

    private TrackedAircraft? ResolveTrackedAircraft(string targetVehicleId)
    {
        if (_ownship is not null &&
            string.Equals(_ownship.VehicleId, targetVehicleId, StringComparison.OrdinalIgnoreCase))
        {
            return _ownship;
        }

        for (var i = 0; i < _traffic.Count; i++)
        {
            var aircraft = _traffic[i];
            if (string.Equals(aircraft.VehicleId, targetVehicleId, StringComparison.OrdinalIgnoreCase))
            {
                return aircraft;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Free-fly camera input handling
    // ------------------------------------------------------------------

    private Vector3 GetFreeForward()
    {
        var cosPitch = MathF.Cos(_freePitchRad);
        var sinPitch = MathF.Sin(_freePitchRad);
        var cosYaw = MathF.Cos(_freeYawRad);
        var sinYaw = MathF.Sin(_freeYawRad);
        return new Vector3(sinYaw * cosPitch, sinPitch, cosYaw * cosPitch);
    }

    /// <summary>Raised when the operator takes the camera off the followed aircraft (right button while following).</summary>
    public event Action? FollowReleased;

    /// <summary>The camera, as the next frame would place it; for tests.</summary>
    internal (Vector3 Eye, Vector3 Target, bool IsFree) CameraForTests()
    {
        var camera = BuildCameraState(SelectReferenceCenter());
        return (camera.Eye, camera.Target, _cameraMode == CameraMode.Free);
    }

    /// <summary>The detail block this frame's camera wants, as if terrain were drawn; for tests.</summary>
    internal TileRange? DetailForTests()
    {
        var center = SelectReferenceCenter();
        UpdateImageryDetail(center, BuildCameraState(center), onTerrain: true);
        return _detailWanted;
    }

    /// <summary>An aircraft's position in this frame's world; for tests.</summary>
    internal Vector3? AircraftForTests(string id) =>
        ResolveTrackedAircraft(id) is { } aircraft ? GeoToWorld(SelectReferenceCenter(), aircraft.Latitude, aircraft.Longitude, aircraft.AltitudeMeters) : null;

    /// <summary>
    /// What the right button does: free flight from where the camera is, and following ends (cleared here first, so the
    /// host's answer, following nothing, changes nothing).
    /// </summary>
    internal void TakeCamera()
    {
        if (_cameraMode != CameraMode.Free)
        {
            EnterFreeCameraFromAuto();
        }

        if (_trackingTargetVehicleId is not null)
        {
            _trackingTargetVehicleId = null;
            FollowReleased?.Invoke();
        }

        InvalidateVisual();
    }

    /// <summary>
    /// Frames the route, the aircraft and the volumes from above: free flight, looking down as steeply as the free
    /// camera allows, far enough out that the largest extent fits the vertical field of view with a margin. Follow ends.
    /// </summary>
    public void FitAll()
    {
        _trackingTargetVehicleId = null;
        _heldKeys.Clear();
        EnsureStableHomeSeeded();
        var home = HomeReferenceCenter();

        var minX = float.MaxValue;
        var minZ = float.MaxValue;
        var maxX = float.MinValue;
        var maxZ = float.MinValue;
        var top = 0f;
        void Add(double latitude, double longitude, double altitude)
        {
            var world = GeoToWorld(home, latitude, longitude, altitude);
            (minX, maxX, minZ, maxZ, top) = (MathF.Min(minX, world.X), MathF.Max(maxX, world.X), MathF.Min(minZ, world.Z), MathF.Max(maxZ, world.Z), MathF.Max(top, world.Y));
        }

        foreach (var point in _missionPath)
        {
            Add(point.Position.Latitude, point.Position.Longitude, point.AltitudeMeters);
        }

        foreach (var aircraft in _traffic.Prepend(_ownship).OfType<TrackedAircraft>())
        {
            Add(aircraft.Latitude, aircraft.Longitude, aircraft.AltitudeMeters);
        }

        foreach (var fence in _fences)
        {
            if (fence is CircularFence circle)
            {
                var c = GeoToWorld(home, circle.Center.Latitude, circle.Center.Longitude, home.AltitudeMeters);
                var radius = (float)(circle.RadiusMeters * Math.Clamp(_options.SceneScale, 0.2, 3.5));
                (minX, maxX, minZ, maxZ) = (MathF.Min(minX, c.X - radius), MathF.Max(maxX, c.X + radius), MathF.Min(minZ, c.Z - radius), MathF.Max(maxZ, c.Z + radius));
            }
            else if (fence is PolygonFence polygon)
            {
                foreach (var vertex in polygon.Vertices)
                {
                    Add(vertex.Latitude, vertex.Longitude, home.AltitudeMeters);
                }
            }
        }

        if (minX > maxX)
        {
            (minX, maxX, minZ, maxZ) = (-300f, 300f, -300f, 300f);
        }

        var center = new Vector3((minX + maxX) / 2, 0f, (minZ + maxZ) / 2);
        var half = (MathF.Max(maxX - minX, maxZ - minZ) / 2 * 1.15f) + 40f;
        var fov = (float)(Math.Clamp(_options.FieldOfViewDegrees, 20d, 110d) * Math.PI / 180d);
        var distance = (half / MathF.Tan(fov / 2)) + top;

        _freePitchRad = -MaxFreePitch;
        _freeYawRad = 0f;
        _freeEye = center - (GetFreeForward() * distance);
        ClampFreeEye();
        _cameraMode = CameraMode.Free;
        _followOffsetValid = false;
        InvalidateVisual();
    }

    private (double Latitude, double Longitude, double AltitudeMeters) HomeReferenceCenter() =>
        _stableHomeCenter is { } home
            ? (home.Latitude, home.Longitude, _options.HomeAltitudeMeters)
            : (41.0, 29.0, _options.HomeAltitudeMeters);

    private void EnterFreeCameraFromAuto()
    {
        var center = SelectReferenceCenter();
        var previousMode = _cameraMode;
        _cameraMode = CameraMode.Auto;
        var seed = BuildCameraState(center);
        _cameraMode = previousMode;

        // Following centres the world on the aircraft; free flight centres it on the home anchor. Moving the seed into
        // the home frame keeps the view where it was instead of jumping by the aircraft's distance from home.
        EnsureStableHomeSeeded();
        var shift = GeoToWorld(HomeReferenceCenter(), center.Latitude, center.Longitude, center.AltitudeMeters);
        seed = new CameraState(seed.Eye + shift, seed.Target + shift);
        _freeEye = seed.Eye;

        var direction = seed.Target - seed.Eye;
        if (direction.LengthSquared() < 0.0001f)
        {
            direction = new Vector3(0, -0.35f, 1f);
        }

        direction = Vector3.Normalize(direction);
        _freePitchRad = MathF.Asin(Math.Clamp(direction.Y, -0.9999f, 0.9999f));
        _freeYawRad = MathF.Atan2(direction.X, direction.Z);
        _cameraMode = CameraMode.Free;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        Focus();
        var properties = e.GetCurrentPoint(this).Properties;
        if (!properties.IsRightButtonPressed)
        {
            return;
        }

        _rightButtonDown = true;
        _lastPointerPosition = e.GetPosition(this);
        TakeCamera();

        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (e.InitialPressMouseButton != MouseButton.Right)
        {
            return;
        }

        _rightButtonDown = false;
        if (ReferenceEquals(e.Pointer.Captured, this))
        {
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!_rightButtonDown || _cameraMode != CameraMode.Free)
        {
            return;
        }

        var position = e.GetPosition(this);
        var dx = position.X - _lastPointerPosition.X;
        var dy = position.Y - _lastPointerPosition.Y;
        _lastPointerPosition = position;

        const float sensitivity = 0.0045f;
        _freeYawRad += (float)(dx * sensitivity);
        _freePitchRad -= (float)(dy * sensitivity);

        _freePitchRad = Math.Clamp(_freePitchRad, -MaxFreePitch, MaxFreePitch);

        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_cameraMode != CameraMode.Free)
        {
            return;
        }

        // Dolly step scales with altitude so the wheel is usable both low and high up.
        var step = (float)(e.Delta.Y * Math.Clamp(_freeEye.Y * 0.12f, 12f, 400f));
        _freeEye += GetFreeForward() * step;
        ClampFreeEye();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Escape)
        {
            if (_cameraMode == CameraMode.Free)
            {
                _cameraMode = CameraMode.Auto;
                _heldKeys.Clear();
                _rightButtonDown = false;
                _followOffsetValid = false;
                InvalidateVisual();
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Key.F)
        {
            if (_cameraMode == CameraMode.Free)
            {
                _cameraMode = CameraMode.Auto;
                _followOffsetValid = false;
            }
            else
            {
                EnterFreeCameraFromAuto();
            }

            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (!IsMovementKey(e.Key))
        {
            return;
        }

        if (_cameraMode != CameraMode.Free)
        {
            EnterFreeCameraFromAuto();
        }

        _heldKeys.Add(e.Key);
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        _heldKeys.Remove(e.Key);
    }

    private static bool IsMovementKey(Key key) =>
        key is Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E
        or Key.Space or Key.LeftShift or Key.RightShift
        or Key.LeftCtrl or Key.RightCtrl;

    private void OnInputTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = (float)(now - _lastInputTick).TotalSeconds;
        _lastInputTick = now;
        if (dt <= 0f || dt > 0.25f)
        {
            dt = 0.016f;
        }

        var dirty = AdvanceFollowSmoothing(dt);
        dirty |= AdvanceFreeCamera(dt);

        if (dirty)
        {
            InvalidateVisual();
        }
    }

    /// <summary>Eases the chase camera toward its target offset so heading changes don't jerk the view.</summary>
    private bool AdvanceFollowSmoothing(float dt)
    {
        if (_cameraMode != CameraMode.Auto || string.IsNullOrWhiteSpace(_trackingTargetVehicleId))
        {
            return false;
        }

        var tracked = ResolveTrackedAircraft(_trackingTargetVehicleId!);
        if (tracked is null)
        {
            return false;
        }

        var desired = DesiredFollowOffset(tracked);
        if (!_followOffsetValid)
        {
            _followOffset = desired;
            _followOffsetValid = true;
            return true;
        }

        var delta = desired - _followOffset;
        if (delta.LengthSquared() < 0.01f)
        {
            // Converged: stop invalidating so an idle follow view costs nothing.
            _followOffset = desired;
            return false;
        }

        // Frame-rate independent exponential ease.
        var alpha = 1f - MathF.Exp(-dt * 7f);
        _followOffset += delta * alpha;
        return true;
    }

    private bool AdvanceFreeCamera(float dt)
    {
        if (_cameraMode != CameraMode.Free || _heldKeys.Count == 0)
        {
            return false;
        }

        var cosPitch = MathF.Cos(_freePitchRad);
        var sinPitch = MathF.Sin(_freePitchRad);
        var cosYaw = MathF.Cos(_freeYawRad);
        var sinYaw = MathF.Sin(_freeYawRad);
        var forward = new Vector3(sinYaw * cosPitch, sinPitch, cosYaw * cosPitch);
        var right = new Vector3(cosYaw, 0f, -sinYaw);

        var move = Vector3.Zero;
        if (_heldKeys.Contains(Key.W))
        {
            move += forward;
        }

        if (_heldKeys.Contains(Key.S))
        {
            move -= forward;
        }

        if (_heldKeys.Contains(Key.D))
        {
            move += right;
        }

        if (_heldKeys.Contains(Key.A))
        {
            move -= right;
        }

        if (_heldKeys.Contains(Key.E) || _heldKeys.Contains(Key.Space))
        {
            move += Vector3.UnitY;
        }

        if (_heldKeys.Contains(Key.Q) || _heldKeys.Contains(Key.LeftCtrl) || _heldKeys.Contains(Key.RightCtrl))
        {
            move -= Vector3.UnitY;
        }

        if (move.LengthSquared() < 0.0001f)
        {
            return false;
        }

        move = Vector3.Normalize(move);

        // Baseline 140 m/s; Shift engages a 4x boost for quick repositioning.
        var speed = 140f;
        if (_heldKeys.Contains(Key.LeftShift) || _heldKeys.Contains(Key.RightShift))
        {
            speed *= 4f;
        }

        _freeEye += move * speed * dt;
        ClampFreeEye();
        return true;
    }

    private void ClampFreeEye()
    {
        // Wide enough for an aircraft followed far from home: the seed is moved into the home frame, not clamped back.
        const float horizontalLimit = 200000f;
        _freeEye.Y = Math.Clamp(_freeEye.Y, 3f, 20000f);
        _freeEye.X = Math.Clamp(_freeEye.X, -horizontalLimit, horizontalLimit);
        _freeEye.Z = Math.Clamp(_freeEye.Z, -horizontalLimit, horizontalLimit);
    }
}
