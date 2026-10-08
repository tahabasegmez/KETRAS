using System.Numerics;
using Avalonia;
using Avalonia.Media;

namespace Ketras.Controls.Scene3D.Rendering;

/// <summary>Draw-order buckets; primitives depth-sort only within their own bucket.</summary>
internal enum RenderLayer : byte
{
    /// <summary>Terrain grid and runway markings - everything flat on y = 0.</summary>
    Ground = 0,

    /// <summary>Decals that must sit on the ground but under the scene (shadows).</summary>
    Decal = 1,

    /// <summary>Volumetric content: aircraft, zones, trails.</summary>
    World = 2,

    /// <summary>Content that must never be occluded (selection rings, leader lines).</summary>
    Overlay = 3
}

/// <summary>Software scene renderer: submit world-space primitives, then <see cref="Flush"/> depth-sorts and batches them.</summary>
internal sealed class SceneRenderer
{
    /// <summary>Distance in front of the eye where geometry starts to exist; nearer points are clipped, not projected.</summary>
    public const float NearPlaneMeters = 1.5f;

    private enum Shape : byte
    {
        Polygon,
        Polyline,
        Ellipse
    }

    private readonly struct Primitive
    {
        public readonly byte Layer;
        public readonly float Depth;
        public readonly int Order;
        public readonly Shape Kind;
        public readonly int Start;
        public readonly int Count;
        public readonly IBrush? Fill;
        public readonly IPen? Pen;
        public readonly double Radius;

        public Primitive(
            RenderLayer layer,
            float depth,
            int order,
            Shape kind,
            int start,
            int count,
            IBrush? fill,
            IPen? pen,
            double radius = 0)
        {
            Layer = (byte)layer;
            Depth = depth;
            Order = order;
            Kind = kind;
            Start = start;
            Count = count;
            Fill = fill;
            Pen = pen;
            Radius = radius;
        }
    }

    /// <summary>Sorts far-to-near inside each layer; insertion order is the tiebreak so co-planar primitives stay stable between frames.</summary>
    private sealed class PrimitiveComparer : IComparer<Primitive>
    {
        public static readonly PrimitiveComparer Instance = new();

        public int Compare(Primitive a, Primitive b)
        {
            var byLayer = a.Layer.CompareTo(b.Layer);
            if (byLayer != 0)
            {
                return byLayer;
            }

            var byDepth = b.Depth.CompareTo(a.Depth);
            return byDepth != 0 ? byDepth : a.Order.CompareTo(b.Order);
        }
    }

    private readonly List<Point> _screenPoints = new(8192);
    private readonly List<Primitive> _primitives = new(2048);
    private readonly List<Vector3> _clipSource = new(32);
    private readonly List<Vector3> _clipResult = new(32);

    private Matrix4x4 _view;
    private Vector3 _eye;
    private float _focalLength = 2f;
    private float _aspect = 1f;
    private double _width;
    private double _height;
    private Rect _cullRect;

    /// <summary>Camera position in world space for the current frame.</summary>
    public Vector3 Eye => _eye;

    public void BeginFrame(Size viewport, Vector3 eye, Vector3 target, double fieldOfViewDegrees)
    {
        _width = viewport.Width;
        _height = viewport.Height;
        _eye = eye;

        var direction = target - eye;
        if (direction.LengthSquared() < 1e-6f)
        {
            target = eye + Vector3.UnitZ;
        }

        _view = ViewMatrix(eye, target);
        _aspect = (float)Math.Max(0.25, _width / Math.Max(1d, _height));

        var fov = Math.Clamp(fieldOfViewDegrees, 20d, 110d) * Math.PI / 180d;
        _focalLength = 1f / MathF.Tan((float)fov * 0.5f);

        // Generous margin so geometry crossing the near plane isn't clipped too eagerly.
        _cullRect = new Rect(-_width, -_height, _width * 3d, _height * 3d);

        _screenPoints.Clear();
        _primitives.Clear();
    }

    /// <summary>
    /// World to view space (camera looks down -Z). The world axes (+X east, +Y up, +Z north) are left-handed, so the
    /// right-handed look-at is mirrored in X to keep east on the screen's right.
    /// </summary>
    internal static Matrix4x4 ViewMatrix(Vector3 eye, Vector3 target) =>
        Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY) * Matrix4x4.CreateScale(-1f, 1f, 1f);

    /// <summary>Transforms a world point into view space (camera looks down -Z).</summary>
    public Vector3 ToView(Vector3 world) => Vector3.Transform(world, _view);

    /// <summary>How many screen pixels one world meter spans at the given view depth.</summary>
    public double PixelsPerMeter(float depth) => _focalLength * 0.5d * _height / Math.Max(depth, NearPlaneMeters);

    public bool TryProjectWorld(Vector3 world, out Point screen, out float depth)
    {
        var view = Vector3.Transform(world, _view);
        depth = -view.Z;
        if (depth < NearPlaneMeters)
        {
            screen = default;
            return false;
        }

        screen = ProjectView(view);
        return true;
    }

    /// <summary>Screen Y of the ground plane's vanishing line; cameras never roll, so this is always horizontal.</summary>
    public bool TryGetHorizonY(out double screenY)
    {
        // Use the direction the camera actually faces, flattened to y = 0.
        var forward = new Vector3(-_view.M13, 0f, -_view.M33);
        if (forward.LengthSquared() < 1e-6f)
        {
            forward = new Vector3(-_view.M12, 0f, -_view.M32);
        }

        if (forward.LengthSquared() < 1e-6f)
        {
            screenY = _height * 0.5d;
            return false;
        }

        var view = Vector3.TransformNormal(Vector3.Normalize(forward), _view);
        if (view.Z >= -1e-4f)
        {
            screenY = 0;
            return false;
        }

        var ndcY = _focalLength * view.Y / -view.Z;
        screenY = (1d - ndcY) * 0.5d * _height;
        return true;
    }

    private Point ProjectView(Vector3 view)
    {
        var invDepth = -1f / view.Z;
        var ndcX = _focalLength / _aspect * view.X * invDepth;
        var ndcY = _focalLength * view.Y * invDepth;
        return new Point((ndcX + 1d) * 0.5d * _width, (1d - ndcY) * 0.5d * _height);
    }

    // ------------------------------------------------------------------
    // Primitive submission
    // ------------------------------------------------------------------

    public void AddWorldPolygon(
        ReadOnlySpan<Vector3> world,
        RenderLayer layer,
        IBrush? fill,
        IPen? pen = null,
        float depthBias = 0f)
    {
        if (world.Length < 3)
        {
            return;
        }

        _clipSource.Clear();
        for (var i = 0; i < world.Length; i++)
        {
            _clipSource.Add(Vector3.Transform(world[i], _view));
        }

        EmitPolygon(_clipSource, layer, fill, pen, depthBias);
    }

    /// <summary>Polygon submission for callers that already hold view-space vertices.</summary>
    public void AddViewPolygon(
        ReadOnlySpan<Vector3> view,
        RenderLayer layer,
        IBrush? fill,
        IPen? pen = null,
        float depthBias = 0f)
    {
        if (view.Length < 3)
        {
            return;
        }

        _clipSource.Clear();
        for (var i = 0; i < view.Length; i++)
        {
            _clipSource.Add(view[i]);
        }

        EmitPolygon(_clipSource, layer, fill, pen, depthBias);
    }

    public void AddWorldSegment(Vector3 fromWorld, Vector3 toWorld, RenderLayer layer, IPen pen, float depthBias = 0f)
    {
        var a = Vector3.Transform(fromWorld, _view);
        var b = Vector3.Transform(toWorld, _view);

        if (!ClipSegmentToNearPlane(ref a, ref b))
        {
            return;
        }

        var start = _screenPoints.Count;
        var pa = ProjectView(a);
        var pb = ProjectView(b);

        if (!SegmentIntersectsCullRect(pa, pb))
        {
            return;
        }

        _screenPoints.Add(pa);
        _screenPoints.Add(pb);
        var depth = (-a.Z + -b.Z) * 0.5f + depthBias;
        _primitives.Add(new Primitive(layer, depth, _primitives.Count, Shape.Polyline, start, 2, null, pen));
    }

    /// <summary>Submits a connected run of world points, splitting into runs where the near plane clips.</summary>
    public void AddWorldPolyline(ReadOnlySpan<Vector3> world, RenderLayer layer, IPen pen, float depthBias = 0f)
    {
        if (world.Length < 2)
        {
            return;
        }

        var runStart = -1;
        var runCount = 0;
        var depthSum = 0f;

        for (var i = 0; i < world.Length; i++)
        {
            var view = Vector3.Transform(world[i], _view);
            var depth = -view.Z;

            if (depth < NearPlaneMeters)
            {
                FinishRun(layer, pen, ref runStart, ref runCount, ref depthSum, depthBias);
                continue;
            }

            if (runStart < 0)
            {
                runStart = _screenPoints.Count;
                runCount = 0;
                depthSum = 0f;
            }

            _screenPoints.Add(ProjectView(view));
            runCount++;
            depthSum += depth;
        }

        FinishRun(layer, pen, ref runStart, ref runCount, ref depthSum, depthBias);
    }

    private void FinishRun(RenderLayer layer, IPen pen, ref int start, ref int count, ref float depthSum, float depthBias)
    {
        if (start < 0)
        {
            return;
        }

        if (count >= 2)
        {
            _primitives.Add(new Primitive(
                layer,
                depthSum / count + depthBias,
                _primitives.Count,
                Shape.Polyline,
                start,
                count,
                null,
                pen));
        }
        else
        {
            // A one-point run draws nothing; drop the stray vertex so the buffer stays tight.
            _screenPoints.RemoveRange(start, count);
        }

        start = -1;
        count = 0;
        depthSum = 0f;
    }

    public void AddWorldEllipse(
        Vector3 world,
        double radiusPixels,
        RenderLayer layer,
        IBrush? fill,
        IPen? pen = null,
        float depthBias = 0f)
    {
        if (!TryProjectWorld(world, out var screen, out var depth))
        {
            return;
        }

        var start = _screenPoints.Count;
        _screenPoints.Add(screen);
        _primitives.Add(new Primitive(layer, depth + depthBias, _primitives.Count, Shape.Ellipse, start, 1, fill, pen, radiusPixels));
    }

    private void EmitPolygon(List<Vector3> viewPolygon, RenderLayer layer, IBrush? fill, IPen? pen, float depthBias)
    {
        var clipped = ClipPolygonToNearPlane(viewPolygon);
        if (clipped.Count < 3)
        {
            return;
        }

        var start = _screenPoints.Count;
        var depthSum = 0f;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;

        for (var i = 0; i < clipped.Count; i++)
        {
            var view = clipped[i];
            var point = ProjectView(view);
            _screenPoints.Add(point);
            depthSum += -view.Z;

            if (point.X < minX)

            {

                minX = point.X;

            }

            if (point.X > maxX)
            {
                maxX = point.X;
            }

            if (point.Y < minY)
            {
                minY = point.Y;
            }

            if (point.Y > maxY)
            {
                maxY = point.Y;
            }
        }

        if (maxX < _cullRect.X || minX > _cullRect.Right || maxY < _cullRect.Y || minY > _cullRect.Bottom)
        {
            _screenPoints.RemoveRange(start, clipped.Count);
            return;
        }

        _primitives.Add(new Primitive(
            layer,
            depthSum / clipped.Count + depthBias,
            _primitives.Count,
            Shape.Polygon,
            start,
            clipped.Count,
            fill,
            pen));
    }

    /// <summary>Sutherland-Hodgman clip of a polygon against the near plane, in view space.</summary>
    private List<Vector3> ClipPolygonToNearPlane(List<Vector3> polygon)
    {
        var needsClipping = false;
        for (var i = 0; i < polygon.Count; i++)
        {
            if (-polygon[i].Z < NearPlaneMeters)
            {
                needsClipping = true;
                break;
            }
        }

        if (!needsClipping)
        {
            return polygon;
        }

        _clipResult.Clear();
        for (var i = 0; i < polygon.Count; i++)
        {
            var current = polygon[i];
            var next = polygon[(i + 1) % polygon.Count];
            var currentInside = -current.Z >= NearPlaneMeters;
            var nextInside = -next.Z >= NearPlaneMeters;

            if (currentInside)
            {
                _clipResult.Add(current);
            }

            if (currentInside != nextInside)
            {
                var denominator = next.Z - current.Z;
                if (MathF.Abs(denominator) > 1e-6f)
                {
                    var t = (-NearPlaneMeters - current.Z) / denominator;
                    _clipResult.Add(Vector3.Lerp(current, next, t));
                }
            }
        }

        return _clipResult;
    }

    private static bool ClipSegmentToNearPlane(ref Vector3 a, ref Vector3 b)
    {
        var aInside = -a.Z >= NearPlaneMeters;
        var bInside = -b.Z >= NearPlaneMeters;

        if (aInside && bInside)
        {
            return true;
        }

        if (!aInside && !bInside)
        {
            return false;
        }

        var denominator = b.Z - a.Z;
        if (MathF.Abs(denominator) < 1e-6f)
        {
            return false;
        }

        var t = (-NearPlaneMeters - a.Z) / denominator;
        var crossing = Vector3.Lerp(a, b, t);

        if (aInside)
        {
            b = crossing;
        }
        else
        {
            a = crossing;
        }

        return true;
    }

    private bool SegmentIntersectsCullRect(Point a, Point b)
    {
        var minX = Math.Min(a.X, b.X);
        var maxX = Math.Max(a.X, b.X);
        var minY = Math.Min(a.Y, b.Y);
        var maxY = Math.Max(a.Y, b.Y);
        return !(maxX < _cullRect.X || minX > _cullRect.Right || maxY < _cullRect.Y || minY > _cullRect.Bottom);
    }

    // ------------------------------------------------------------------
    // Emission
    // ------------------------------------------------------------------

    public void Flush(DrawingContext context)
    {
        if (_primitives.Count == 0)
        {
            return;
        }

        _primitives.Sort(PrimitiveComparer.Instance);

        var index = 0;
        while (index < _primitives.Count)
        {
            var head = _primitives[index];

            if (head.Kind == Shape.Ellipse)
            {
                var center = _screenPoints[head.Start];
                context.DrawEllipse(head.Fill, head.Pen, center, head.Radius, head.Radius);
                index++;
                continue;
            }

            // Merge the longest run of neighbours sharing this primitive's kind and brush/pen.
            var end = index + 1;
            while (end < _primitives.Count)
            {
                var candidate = _primitives[end];
                if (candidate.Kind != head.Kind ||
                    !ReferenceEquals(candidate.Fill, head.Fill) ||
                    !ReferenceEquals(candidate.Pen, head.Pen))
                {
                    break;
                }

                end++;
            }

            var closed = head.Kind == Shape.Polygon;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                for (var i = index; i < end; i++)
                {
                    var primitive = _primitives[i];
                    ctx.BeginFigure(_screenPoints[primitive.Start], closed);
                    for (var v = 1; v < primitive.Count; v++)
                    {
                        ctx.LineTo(_screenPoints[primitive.Start + v]);
                    }

                    ctx.EndFigure(closed);
                }
            }

            context.DrawGeometry(head.Fill, head.Pen, geometry);
            index = end;
        }
    }
}
