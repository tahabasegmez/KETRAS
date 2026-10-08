using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.Media.Imaging;
using Avalonia.OpenGL.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using TerrainGrid = Ketras.Model.Terrain.TerrainGrid;

namespace Ketras.Controls.Scene3D.Rendering;

/// <summary>
/// GPU layer under <see cref="AirspaceViewport"/>: draws the sky and the terrain mesh with a depth buffer, from the
/// camera the viewport publishes each frame. It renders only when the camera, the size or the mesh changes, and caps
/// the triangles per frame (<see cref="TriangleBudget"/>) by coarsening the farthest chunks first. Without an OpenGL
/// context it stays empty and the viewport keeps drawing its own flat backdrop.
/// Each frame is also read back into <see cref="Image"/>, which the viewport draws under its scene: with SkiaSharp 3
/// (which Mapsui 5 brings into a host) Avalonia 11.3 no longer puts an OpenGL control's picture on screen, so the
/// control's own output cannot be relied on.
/// </summary>
internal sealed unsafe class TerrainView : OpenGlControlBase
{
    /// <summary>Upper bound of terrain triangles drawn in one frame; integrated GPUs stay responsive well below it.</summary>
    public const int TriangleBudget = 450_000;

    // A full-resolution cell should cover at least this many pixels before a finer level is used.
    private const double TargetCellPixels = 2.5;
    private const double EarthRadius = 6378137;
    private const float NearPlane = 2f;

    private const int GlLess = 0x0201;
    private const int GlTrue = 1;
    private const int GlFalse = 0;

    // Scene palette (AirspaceViewport): sky top, mid, horizon haze and the ground beyond the data.
    private static readonly Vector3 SkyTop = Rgb(0x16, 0x17, 0x18);
    private static readonly Vector3 SkyMid = Rgb(0x1B, 0x1D, 0x1F);
    private static readonly Vector3 Haze = Rgb(0x23, 0x25, 0x27);
    private static readonly Vector3 Ground = Rgb(0x1B, 0x1D, 0x1F);

    private readonly List<GpuChunk> _chunks = [];
    private TerrainMesh? _pendingMesh;
    private bool _meshChanged;
    private int _buildGeneration;
    private Frame? _frame;

    private int _terrainProgram;
    private int _skyProgram;
    private int _vertexArray;
    private bool _glReady;
    private delegate* unmanaged[Stdcall]<int, float, float, float, void> _uniform3f;
    private delegate* unmanaged[Stdcall]<int, int, int, int, int, int, void*, void> _readPixels;
    private WriteableBitmap? _image;
    private byte[] _pixels = [];

    private int _uViewProj, _uOffset, _uScale, _uEye, _uFog, _uHaze;
    private int _uSkyHorizon, _uSkyTop, _uSkyMid, _uSkyHaze, _uSkyGround;

    /// <summary>What the viewport's camera sees this frame; world axes as in <see cref="AirspaceViewport"/>.</summary>
    public readonly record struct Frame(
        Vector3 Eye,
        Vector3 Target,
        double FieldOfViewDegrees,
        double CenterLatitude,
        double CenterLongitude,
        double DatumMeters,
        double SceneScale,
        double FogStartMeters,
        double FogEndMeters);

    private sealed record GpuChunk(TerrainMesh.Chunk Mesh, int VertexBuffer, int[] IndexBuffers);

    /// <summary>
    /// What the last frame cost: the GL renderer, chunks uploaded and drawn, triangles drawn, and milliseconds: the CPU
    /// time to submit, or the whole frame including the GPU when the KETRAS_TERRAIN_PROFILE environment variable is set.
    /// </summary>
    public readonly record struct FrameStats(string Renderer, int Chunks, int DrawnChunks, int Triangles, double Milliseconds);

    // Profiling waits for the GPU every frame, which costs throughput; it is off unless asked for.
    private static readonly bool Profile = Environment.GetEnvironmentVariable("KETRAS_TERRAIN_PROFILE") is { Length: > 0 };

    /// <summary>Statistics of the last rendered frame, for diagnostics; null before the first frame.</summary>
    public FrameStats? LastFrame { get; private set; }

    private string _renderer = string.Empty;

    /// <summary>True once terrain is on the GPU and being drawn; the viewport then leaves out its flat backdrop.</summary>
    public bool HasSurface { get; private set; }

    /// <summary>Attribution of the terrain being drawn, or null.</summary>
    public string? Attribution { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="HasSurface"/> or <see cref="Attribution"/> changes.</summary>
    public event Action? SurfaceChanged;

    /// <summary>The last frame read back, in device pixels; null before the first one or without terrain.</summary>
    public Bitmap? Image => HasSurface ? _image : null;

    /// <summary>Raised on the UI thread after <see cref="Image"/> got a new frame.</summary>
    public event Action? ImageChanged;

    /// <summary>The heights being drawn, for the scene to stand things on the surface; null without terrain.</summary>
    internal TerrainGrid? Grid { get; private set; }

    /// <summary>Replaces the terrain; the mesh is built off the UI thread. Null removes it.</summary>
    internal void SetGrid(TerrainGrid? grid)
    {
        Grid = grid;
        var generation = ++_buildGeneration;
        if (grid is null)
        {
            Accept(null);
            return;
        }

        Task.Run(() => TerrainMesh.Build(grid)).ContinueWith(task => Dispatcher.UIThread.Post(() =>
        {
            if (generation == _buildGeneration && task.IsCompletedSuccessfully)
            {
                Accept(task.Result);
            }
        }), TaskScheduler.Default);

        void Accept(TerrainMesh? mesh)
        {
            _pendingMesh = mesh;
            _meshChanged = true;
            RequestNextFrameRendering();
        }
    }

    internal void SetFrame(Frame frame)
    {
        if (_frame == frame)
        {
            return;
        }

        _frame = frame;
        RequestNextFrameRendering();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            RequestNextFrameRendering();
        }
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        var header = GlVersion.Type == GlProfileType.OpenGLES ? "#version 300 es\nprecision highp float;\n" : "#version 330 core\n";
        _terrainProgram = Link(gl, header + TerrainVertexShader, header + TerrainFragmentShader);
        _skyProgram = Link(gl, header + SkyVertexShader, header + SkyFragmentShader);
        var uniform3f = gl.GetProcAddress("glUniform3f");
        if (_terrainProgram == 0 || _skyProgram == 0 || uniform3f == IntPtr.Zero)
        {
            return;
        }

        _uniform3f = (delegate* unmanaged[Stdcall]<int, float, float, float, void>)uniform3f;
        _readPixels = (delegate* unmanaged[Stdcall]<int, int, int, int, int, int, void*, void>)gl.GetProcAddress("glReadPixels");
        _renderer = $"{gl.Renderer} ({gl.Version})";
        _vertexArray = gl.IsGenVertexArraysAvailable ? gl.GenVertexArray() : 0;

        _uViewProj = gl.GetUniformLocationString(_terrainProgram, "uViewProj");
        _uOffset = gl.GetUniformLocationString(_terrainProgram, "uOffset");
        _uScale = gl.GetUniformLocationString(_terrainProgram, "uScale");
        _uEye = gl.GetUniformLocationString(_terrainProgram, "uEye");
        _uFog = gl.GetUniformLocationString(_terrainProgram, "uFog");
        _uHaze = gl.GetUniformLocationString(_terrainProgram, "uHaze");
        _uSkyHorizon = gl.GetUniformLocationString(_skyProgram, "uHorizon");
        _uSkyTop = gl.GetUniformLocationString(_skyProgram, "uTop");
        _uSkyMid = gl.GetUniformLocationString(_skyProgram, "uMid");
        _uSkyHaze = gl.GetUniformLocationString(_skyProgram, "uHaze");
        _uSkyGround = gl.GetUniformLocationString(_skyProgram, "uGround");
        _glReady = true;
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        ReleaseChunks(gl);
        if (_vertexArray != 0)
        {
            gl.DeleteVertexArray(_vertexArray);
        }

        gl.DeleteProgram(_terrainProgram);
        gl.DeleteProgram(_skyProgram);
        _glReady = false;
        _image?.Dispose();
        _image = null;
        SetSurface(false, null);
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (!_glReady)
        {
            return;
        }

        if (_meshChanged)
        {
            _meshChanged = false;
            Upload(gl, _pendingMesh);
            SetSurface(_chunks.Count > 0, _pendingMesh?.Attribution);
            _pendingMesh = null;
        }

        var scaling = VisualRoot?.RenderScaling ?? 1;
        var width = Math.Max(1, (int)(Bounds.Width * scaling));
        var height = Math.Max(1, (int)(Bounds.Height * scaling));

        gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, fb);
        gl.Viewport(0, 0, width, height);
        gl.ClearColor(Haze.X, Haze.Y, Haze.Z, 1);
        gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT | GlConsts.GL_DEPTH_BUFFER_BIT);
        if (_frame is not { } frame)
        {
            return;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_vertexArray != 0)
        {
            gl.BindVertexArray(_vertexArray);
        }

        var focal = 1f / MathF.Tan((float)(Math.Clamp(frame.FieldOfViewDegrees, 20d, 110d) * Math.PI / 360d));
        var aspect = (float)Math.Max(0.25, Bounds.Width / Math.Max(1d, Bounds.Height));
        var view = SceneRenderer.ViewMatrix(frame.Eye, frame.Target);
        var far = (float)Math.Max(frame.FogEndMeters * 1.1, 1000);
        var viewProjection = view * Perspective(focal, aspect, NearPlane, far);

        DrawSky(gl, view, focal, frame.Target.Y > frame.Eye.Y);
        var (drawn, triangles) = DrawTerrain(gl, frame, viewProjection, focal, height, far);
        if (Profile)
        {
            gl.Finish();
        }

        ReadBack(width, height);
        LastFrame = new FrameStats(_renderer, _chunks.Count, drawn, triangles, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>Copies the frame into <see cref="Image"/>; GL rows run bottom-up, the bitmap's top-down.</summary>
    private void ReadBack(int width, int height)
    {
        const int GlRgba = 0x1908;
        const int GlUnsignedByte = 0x1401;
        if (_readPixels == null || !HasSurface)
        {
            return;
        }

        if (_image is null || _image.PixelSize.Width != width || _image.PixelSize.Height != height)
        {
            _image?.Dispose();
            _image = new WriteableBitmap(new PixelSize(width, height), new Avalonia.Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Opaque);
        }

        var rowBytes = width * 4;
        if (_pixels.Length != rowBytes * height)
        {
            _pixels = new byte[rowBytes * height];
        }

        fixed (byte* pixels = _pixels)
        {
            _readPixels(0, 0, width, height, GlRgba, GlUnsignedByte, pixels);
        }

        using (var target = _image.Lock())
        {
            for (var row = 0; row < height; row++)
            {
                Marshal.Copy(_pixels, (height - 1 - row) * rowBytes, target.Address + (row * target.RowBytes), rowBytes);
            }
        }

        Dispatcher.UIThread.Post(() => ImageChanged?.Invoke());
    }

    private void DrawSky(GlInterface gl, Matrix4x4 view, float focal, bool lookingUp)
    {
        // Horizon in NDC y, as SceneRenderer.TryGetHorizonY; the camera never rolls.
        var horizon = new Vector3(0, 0, lookingUp ? 1 : 0);
        var forward = new Vector3(-view.M13, 0f, -view.M33);
        if (forward.LengthSquared() > 1e-6f)
        {
            var direction = Vector3.TransformNormal(Vector3.Normalize(forward), view);
            if (direction.Z < -1e-4f)
            {
                horizon = new Vector3(focal * direction.Y / -direction.Z, 1, 0);
            }
        }

        gl.Disable(GlConsts.GL_DEPTH_TEST);
        gl.DepthMask(GlFalse);
        gl.UseProgram(_skyProgram);
        _uniform3f(_uSkyHorizon, horizon.X, horizon.Y, horizon.Z);
        SetColor(_uSkyTop, SkyTop);
        SetColor(_uSkyMid, SkyMid);
        SetColor(_uSkyHaze, Haze);
        SetColor(_uSkyGround, Ground);
        gl.DrawArrays(GlConsts.GL_TRIANGLES, 0, new IntPtr(3));
    }

    private (int Chunks, int Triangles) DrawTerrain(GlInterface gl, Frame frame, Matrix4x4 viewProjection, float focal, int heightPixels, float far)
    {
        if (_chunks.Count == 0)
        {
            return (0, 0);
        }

        var anchor = _anchor;
        var scale = Math.Clamp(frame.SceneScale, 0.2, 3.5);
        var kLat = Math.PI / 180 * EarthRadius * scale;
        var kLon = kLat * Math.Cos(frame.CenterLatitude * Math.PI / 180);
        var offLat = anchor.Latitude - frame.CenterLatitude;
        var offLon = anchor.Longitude - frame.CenterLongitude;
        var pixelsPerMeterAtUnit = focal * 0.5 * heightPixels;
        var cellWorld = _cellMeters * scale;

        var selection = new (GpuChunk Chunk, int Level, double Distance)[_chunks.Count];
        var count = 0;
        foreach (var chunk in _chunks)
        {
            var c = chunk.Mesh;
            var min = new Vector3((float)((c.MinDeltaLongitude + offLon) * kLon), (float)((c.MinHeight - frame.DatumMeters) * scale), (float)((c.MinDeltaLatitude + offLat) * kLat));
            var max = new Vector3((float)((c.MaxDeltaLongitude + offLon) * kLon), (float)((c.MaxHeight - frame.DatumMeters) * scale), (float)((c.MaxDeltaLatitude + offLat) * kLat));
            var distance = Vector3.Distance(frame.Eye, Vector3.Clamp(frame.Eye, min, max));
            if (distance > far || !InFrustum(viewProjection, min, max))
            {
                continue;
            }

            selection[count++] = (chunk, 0, distance);
        }

        // Coarsest first pass at the target size; loosen the target until the frame fits the budget.
        var target = TargetCellPixels;
        var triangles = 0;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            triangles = 0;
            for (var i = 0; i < count; i++)
            {
                var (chunk, _, distance) = selection[i];
                var pixels = cellWorld * pixelsPerMeterAtUnit / Math.Max(distance, NearPlane);
                var level = pixels >= target ? 0 : (int)Math.Ceiling(Math.Log2(target / pixels));
                level = Math.Min(level, chunk.Mesh.Levels - 1);
                selection[i].Level = level;
                triangles += chunk.Mesh.TriangleCount(level);
            }

            if (triangles <= TriangleBudget)
            {
                break;
            }

            target *= 1.6;
        }

        gl.Enable(GlConsts.GL_DEPTH_TEST);
        gl.DepthFunc(GlLess);
        gl.DepthMask(GlTrue);
        gl.UseProgram(_terrainProgram);
        gl.UniformMatrix4fv(_uViewProj, 1, false, &viewProjection);
        _uniform3f(_uOffset, (float)offLon, (float)frame.DatumMeters, (float)offLat);
        _uniform3f(_uScale, (float)kLon, (float)scale, (float)kLat);
        _uniform3f(_uEye, frame.Eye.X, frame.Eye.Y, frame.Eye.Z);
        _uniform3f(_uFog, (float)Math.Max(1, frame.FogStartMeters), (float)Math.Max(frame.FogStartMeters + 1, frame.FogEndMeters), (float)frame.DatumMeters);
        SetColor(_uHaze, Haze);

        const int stride = TerrainMesh.FloatsPerVertex * sizeof(float);
        gl.EnableVertexAttribArray(0);
        gl.EnableVertexAttribArray(1);
        for (var i = 0; i < count; i++)
        {
            var (chunk, level, _) = selection[i];
            var indexCount = chunk.Mesh.Indices[level].Length;
            if (indexCount == 0)
            {
                continue;
            }

            gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, chunk.VertexBuffer);
            gl.VertexAttribPointer(0, 3, GlConsts.GL_FLOAT, GlFalse, stride, IntPtr.Zero);
            gl.VertexAttribPointer(1, 3, GlConsts.GL_FLOAT, GlFalse, stride, new IntPtr(3 * sizeof(float)));
            gl.BindBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER, chunk.IndexBuffers[level]);
            gl.DrawElements(GlConsts.GL_TRIANGLES, indexCount, GlConsts.GL_UNSIGNED_SHORT, IntPtr.Zero);
        }

        return (count, triangles);
    }

    private (double Latitude, double Longitude) _anchor;
    private double _cellMeters;

    private void Upload(GlInterface gl, TerrainMesh? mesh)
    {
        ReleaseChunks(gl);
        if (mesh is null)
        {
            return;
        }

        _anchor = (mesh.AnchorLatitude, mesh.AnchorLongitude);
        _cellMeters = mesh.CellMeters;
        foreach (var chunk in mesh.Chunks)
        {
            var vertexBuffer = gl.GenBuffer();
            gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, vertexBuffer);
            fixed (float* data = chunk.Vertices)
            {
                gl.BufferData(GlConsts.GL_ARRAY_BUFFER, new IntPtr(chunk.Vertices.Length * sizeof(float)), (IntPtr)data, GlConsts.GL_STATIC_DRAW);
            }

            var indexBuffers = new int[chunk.Levels];
            for (var level = 0; level < chunk.Levels; level++)
            {
                indexBuffers[level] = gl.GenBuffer();
                gl.BindBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER, indexBuffers[level]);
                var indices = chunk.Indices[level];
                fixed (ushort* data = indices)
                {
                    gl.BufferData(GlConsts.GL_ELEMENT_ARRAY_BUFFER, new IntPtr(indices.Length * sizeof(ushort)), (IntPtr)data, GlConsts.GL_STATIC_DRAW);
                }
            }

            _chunks.Add(new GpuChunk(chunk, vertexBuffer, indexBuffers));
        }
    }

    private void ReleaseChunks(GlInterface gl)
    {
        foreach (var chunk in _chunks)
        {
            gl.DeleteBuffer(chunk.VertexBuffer);
            foreach (var buffer in chunk.IndexBuffers)
            {
                gl.DeleteBuffer(buffer);
            }
        }

        _chunks.Clear();
    }

    private void SetSurface(bool hasSurface, string? attribution)
    {
        if (HasSurface == hasSurface && Attribution == attribution)
        {
            return;
        }

        HasSurface = hasSurface;
        Attribution = hasSurface ? attribution : null;
        Dispatcher.UIThread.Post(() => SurfaceChanged?.Invoke());
    }

    private void SetColor(int location, Vector3 color) => _uniform3f(location, color.X, color.Y, color.Z);

    /// <summary>OpenGL perspective (depth −1…1) in System.Numerics' row-vector layout, same focal length as SceneRenderer.</summary>
    private static Matrix4x4 Perspective(float focal, float aspect, float near, float far) => new(
        focal / aspect, 0, 0, 0,
        0, focal, 0, 0,
        0, 0, (far + near) / (near - far), -1,
        0, 0, 2 * far * near / (near - far), 0);

    private static bool InFrustum(Matrix4x4 viewProjection, Vector3 min, Vector3 max)
    {
        Span<Vector4> corners = stackalloc Vector4[8];
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
            corners[i] = Vector4.Transform(new Vector4(corner, 1), viewProjection);
        }

        // Outside when every corner lies beyond the same clip plane (left, right, bottom, top, far, behind).
        for (var plane = 0; plane < 6; plane++)
        {
            var allOutside = true;
            foreach (var c in corners)
            {
                var outside = plane switch
                {
                    0 => c.X < -c.W,
                    1 => c.X > c.W,
                    2 => c.Y < -c.W,
                    3 => c.Y > c.W,
                    4 => c.Z > c.W,
                    _ => c.W <= 0
                };
                if (!outside)
                {
                    allOutside = false;
                    break;
                }
            }

            if (allOutside)
            {
                return false;
            }
        }

        return true;
    }

    private static int Link(GlInterface gl, string vertexSource, string fragmentSource)
    {
        var vertex = gl.CreateShader(GlConsts.GL_VERTEX_SHADER);
        var fragment = gl.CreateShader(GlConsts.GL_FRAGMENT_SHADER);
        if (gl.CompileShaderAndGetError(vertex, vertexSource) is { Length: > 0 } vertexError)
        {
            Trace("vertex shader", vertexError);
            return 0;
        }

        if (gl.CompileShaderAndGetError(fragment, fragmentSource) is { Length: > 0 } fragmentError)
        {
            Trace("fragment shader", fragmentError);
            return 0;
        }

        var program = gl.CreateProgram();
        gl.AttachShader(program, vertex);
        gl.AttachShader(program, fragment);
        if (gl.LinkProgramAndGetError(program) is { Length: > 0 } linkError)
        {
            Trace("link", linkError);
            return 0;
        }

        gl.DeleteShader(vertex);
        gl.DeleteShader(fragment);
        return program;
    }

    private static void Trace(string stage, string error) =>
        System.Diagnostics.Trace.WriteLine($"Ketras 3D terrain {stage}: {error}");

    private static Vector3 Rgb(byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f);

    // World = ((Δlon + offLon)·kLon, (h − datum)·scale, (Δlat + offLat)·kLat): AirspaceViewport.GeoToWorld on the GPU.
    private const string TerrainVertexShader = """
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aNormal;
        uniform mat4 uViewProj;
        uniform vec3 uOffset;
        uniform vec3 uScale;
        out vec3 vWorld;
        out vec3 vNormal;
        out float vHeight;
        void main()
        {
            vec3 world = vec3((aPosition.x + uOffset.x) * uScale.x, (aPosition.y - uOffset.y) * uScale.y, (aPosition.z + uOffset.z) * uScale.z);
            vWorld = world;
            vNormal = aNormal;
            vHeight = aPosition.y;
            gl_Position = uViewProj * vec4(world, 1.0);
        }
        """;

    // Neutral greys lit from the north-west, faint 10 m / 50 m contour lines, and the scene's distance haze.
    private const string TerrainFragmentShader = """
        in vec3 vWorld;
        in vec3 vNormal;
        in float vHeight;
        uniform vec3 uEye;
        uniform vec3 uFog;
        uniform vec3 uHaze;
        out vec4 fragColor;
        float contour(float height, float spacing)
        {
            float x = height / spacing;
            float width = max(fwidth(x), 1e-4);
            float line = 1.0 - min(abs(fract(x - 0.5) - 0.5) / width, 1.0);
            return line * clamp(1.5 - width * 4.0, 0.0, 1.0);
        }
        void main()
        {
            vec3 n = normalize(vNormal);
            vec3 sun = normalize(vec3(-0.55, 0.75, 0.40));
            float light = 0.55 + 0.85 * max(dot(n, sun), 0.0);
            float t = clamp((vHeight - uFog.z) / 900.0 + 0.35, 0.0, 1.0);
            vec3 base = mix(vec3(0.145, 0.157, 0.169), vec3(0.235, 0.251, 0.267), t);
            vec3 color = base * light;
            color += vec3(0.030) * contour(vHeight, 10.0) + vec3(0.050) * contour(vHeight, 50.0);
            float d = distance(vWorld, uEye);
            float f = smoothstep(uFog.x, uFog.y, d);
            fragColor = vec4(mix(color, uHaze, f), 1.0);
        }
        """;

    private const string SkyVertexShader = """
        out float vY;
        void main()
        {
            vec2 p = vec2(gl_VertexID == 1 ? 3.0 : -1.0, gl_VertexID == 2 ? 3.0 : -1.0);
            vY = p.y;
            gl_Position = vec4(p, 0.0, 1.0);
        }
        """;

    // uHorizon: (horizon NDC y, 1 when on a valid line, 1 when looking up without a horizon).
    private const string SkyFragmentShader = """
        in float vY;
        uniform vec3 uHorizon;
        uniform vec3 uTop;
        uniform vec3 uMid;
        uniform vec3 uHaze;
        uniform vec3 uGround;
        out vec4 fragColor;
        vec3 sky(float t)
        {
            return t < 0.45 ? mix(uHaze, uMid, t / 0.45) : mix(uMid, uTop, (t - 0.45) / 0.55);
        }
        void main()
        {
            vec3 color;
            if (uHorizon.y < 0.5)
            {
                color = uHorizon.z > 0.5 ? sky(0.7) : uGround;
            }
            else if (vY >= uHorizon.x)
            {
                color = sky(clamp((vY - uHorizon.x) / max(1.0 - uHorizon.x, 0.001), 0.0, 1.0));
            }
            else
            {
                color = mix(uHaze, uGround, smoothstep(0.0, 0.2, clamp((uHorizon.x - vY) / max(uHorizon.x + 1.0, 0.001), 0.0, 1.0)));
            }

            fragColor = vec4(color, 1.0);
        }
        """;
}
