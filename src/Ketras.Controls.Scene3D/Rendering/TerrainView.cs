using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.Media.Imaging;
using Avalonia.OpenGL.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Ketras.Model.Geo;
using TerrainGrid = Ketras.Model.Terrain.TerrainGrid;

namespace Ketras.Controls.Scene3D.Rendering;

/// <summary>
/// GPU layer under <see cref="AirspaceViewport"/>: draws the sky and the terrain mesh (grey relief, or draped with a map
/// imagery texture, <see cref="SetGround"/>) with a depth buffer, from the
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

    private const int GlTexture2D = 0x0DE1;
    private const int GlTexture0 = 0x84C0;
    private const int GlTextureMinFilter = 0x2801;
    private const int GlTextureMagFilter = 0x2800;
    private const int GlTextureWrapS = 0x2802;
    private const int GlTextureWrapT = 0x2803;
    private const int GlLinear = 0x2601;
    private const int GlLinearMipmapLinear = 0x2703;
    private const int GlClampToEdge = 0x812F;
    private const int GlRgba = 0x1908;
    private const int GlRgba8 = 0x8058;
    private const int GlUnsignedByte = 0x1401;
    private const int GlMaxTextureSize = 0x0D33;

    // Sky, haze and the ground beyond the data (ScenePalette, shared with AirspaceViewport).
    private ScenePalette _palette = ScenePalette.Night;
    private SceneGround _ground;

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

    private ImageryTexture? _pendingImagery;
    private bool _imageryChanged;
    private int _imageryGeneration;
    private ImageryTexture? _imagery;
    private int _imageryTexture;
    private int _maxTextureSize = 4096;
    private delegate* unmanaged[Stdcall]<int, void> _generateMipmap;

    private int _uViewProj, _uOffset, _uScale, _uEye, _uFog, _uHaze, _uImageryU, _uImageryV, _uAnchorLatitude;
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

    /// <summary>Paints the surface as grey relief or with the imagery; also picks the sky.</summary>
    internal void SetGround(SceneGround ground)
    {
        if (_ground == ground)
        {
            return;
        }

        _ground = ground;
        _palette = ScenePalette.For(ground);
        RequestNextFrameRendering();
    }

    /// <summary>Replaces the imagery draped in <see cref="SceneGround.Imagery"/>; decoded off the UI thread. Null removes it.</summary>
    internal void SetImagery(SceneImagery? imagery)
    {
        var generation = ++_imageryGeneration;
        if (imagery is null)
        {
            Accept(null);
            return;
        }

        var maxSize = _maxTextureSize;
        Task.Run(() => ImageryTexture.Build(imagery, maxSize)).ContinueWith(task => Dispatcher.UIThread.Post(() =>
        {
            if (generation == _imageryGeneration && task.IsCompletedSuccessfully)
            {
                Accept(task.Result);
            }
        }), TaskScheduler.Default);

        void Accept(ImageryTexture? texture)
        {
            _pendingImagery = texture;
            _imageryChanged = true;
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
        _uImageryU = gl.GetUniformLocationString(_terrainProgram, "uImageryU");
        _uImageryV = gl.GetUniformLocationString(_terrainProgram, "uImageryV");
        _uAnchorLatitude = gl.GetUniformLocationString(_terrainProgram, "uAnchorLatitude");
        _generateMipmap = (delegate* unmanaged[Stdcall]<int, void>)gl.GetProcAddress("glGenerateMipmap");
        gl.GetIntegerv(GlMaxTextureSize, out var maxTextureSize);
        _maxTextureSize = maxTextureSize > 0 ? Math.Min(maxTextureSize, 8192) : 4096;
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

        // Uploaded again if a new context comes.
        if (!_imageryChanged && _imagery is not null)
        {
            _pendingImagery = _imagery;
            _imageryChanged = true;
        }

        ReleaseImagery(gl);
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

        if (_imageryChanged)
        {
            _imageryChanged = false;
            UploadImagery(gl, _pendingImagery);
            _pendingImagery = null;
        }

        var scaling = VisualRoot?.RenderScaling ?? 1;
        var width = Math.Max(1, (int)(Bounds.Width * scaling));
        var height = Math.Max(1, (int)(Bounds.Height * scaling));

        gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, fb);
        gl.Viewport(0, 0, width, height);
        var haze = Rgb(_palette.Haze);
        gl.ClearColor(haze.X, haze.Y, haze.Z, 1);
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
        SetColor(_uSkyTop, Rgb(_palette.SkyTop));
        SetColor(_uSkyMid, Rgb(_palette.SkyMid));
        SetColor(_uSkyHaze, Rgb(_palette.Haze));
        SetColor(_uSkyGround, Rgb(_palette.Ground));
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
        SetColor(_uHaze, Rgb(_palette.Haze));
        SetImageryUniforms(gl, anchor);

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

    /// <summary>
    /// Imagery lookup, in the shader's terms: u = (Δlon + uImageryU.x) · uImageryU.y, and
    /// v = uImageryV.x + (uImageryV.z − ln tan(π/4 + φ/2)) · uImageryV.y with φ = uAnchorLatitude + Δlat (radians).
    /// The anchor terms are worked out here in double precision. uImageryU.z is 1 while imagery is drawn.
    /// </summary>
    private void SetImageryUniforms(GlInterface gl, (double Latitude, double Longitude) anchor)
    {
        if (_ground != SceneGround.Imagery || _imagery is not { } imagery || _imageryTexture == 0)
        {
            _uniform3f(_uImageryU, 0, 0, 0);
            return;
        }

        var spanX = imagery.MaxX - imagery.MinX;
        var spanY = imagery.MaxY - imagery.MinY;
        var anchorPhi = anchor.Latitude * Math.PI / 180;
        var anchorM = Math.Log(Math.Tan((Math.PI / 4) + (anchorPhi / 2)));
        _uniform3f(_uImageryU, (float)(anchor.Longitude - WebMercatorRules.Longitude(imagery.MinX)), (float)(1 / (360 * spanX)), 1);
        _uniform3f(_uImageryV, (float)((WebMercatorRules.Y(anchor.Latitude) - imagery.MinY) / spanY), (float)(1 / (2 * Math.PI * spanY)), (float)anchorM);
        gl.Uniform1f(_uAnchorLatitude, (float)anchorPhi);
        gl.ActiveTexture(GlTexture0);
        gl.BindTexture(GlTexture2D, _imageryTexture);
    }

    private void UploadImagery(GlInterface gl, ImageryTexture? imagery)
    {
        ReleaseImagery(gl);
        _imagery = imagery;
        if (imagery is null)
        {
            return;
        }

        _imageryTexture = gl.GenTexture();
        gl.ActiveTexture(GlTexture0);
        gl.BindTexture(GlTexture2D, _imageryTexture);
        fixed (byte* data = imagery.Pixels)
        {
            gl.TexImage2D(GlTexture2D, 0, GlRgba8, imagery.Width, imagery.Height, 0, GlRgba, GlUnsignedByte, (IntPtr)data);
        }

        var mipmapped = _generateMipmap != null;
        if (mipmapped)
        {
            _generateMipmap(GlTexture2D);
        }

        gl.TexParameteri(GlTexture2D, GlTextureMinFilter, mipmapped ? GlLinearMipmapLinear : GlLinear);
        gl.TexParameteri(GlTexture2D, GlTextureMagFilter, GlLinear);
        gl.TexParameteri(GlTexture2D, GlTextureWrapS, GlClampToEdge);
        gl.TexParameteri(GlTexture2D, GlTextureWrapT, GlClampToEdge);
        gl.BindTexture(GlTexture2D, 0);
    }

    private void ReleaseImagery(GlInterface gl)
    {
        if (_imageryTexture != 0)
        {
            gl.DeleteTexture(_imageryTexture);
            _imageryTexture = 0;
        }

        _imagery = null;
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

    private static Vector3 Rgb(Avalonia.Media.Color color) => new(color.R / 255f, color.G / 255f, color.B / 255f);

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
        out vec2 vDelta;
        void main()
        {
            vec3 world = vec3((aPosition.x + uOffset.x) * uScale.x, (aPosition.y - uOffset.y) * uScale.y, (aPosition.z + uOffset.z) * uScale.z);
            vWorld = world;
            vNormal = aNormal;
            vHeight = aPosition.y;
            vDelta = aPosition.xz;
            gl_Position = uViewProj * vec4(world, 1.0);
        }
        """;

    // Neutral greys lit from the north-west with faint 10 m / 50 m contour lines; or, where imagery is drawn, the
    // imagery under a mild hillshade with very thin dark contours. Then the scene's distance haze.
    private const string TerrainFragmentShader = """
        in vec3 vWorld;
        in vec3 vNormal;
        in float vHeight;
        in vec2 vDelta;
        uniform vec3 uEye;
        uniform vec3 uFog;
        uniform vec3 uHaze;
        uniform vec3 uImageryU;
        uniform vec3 uImageryV;
        uniform float uAnchorLatitude;
        uniform sampler2D uImagery;
        out vec4 fragColor;
        float contour(float height, float spacing, float thickness)
        {
            float x = height / spacing;
            float width = max(fwidth(x), 1e-4);
            float line = 1.0 - min(abs(fract(x - 0.5) - 0.5) / (width * thickness), 1.0);
            return line * clamp(1.5 - width * 4.0, 0.0, 1.0);
        }
        void main()
        {
            vec3 n = normalize(vNormal);
            vec3 sun = normalize(vec3(-0.55, 0.75, 0.40));
            float lambert = max(dot(n, sun), 0.0);
            vec4 texel = vec4(0.0);
            if (uImageryU.z > 0.5)
            {
                float phi = uAnchorLatitude + radians(vDelta.y);
                vec2 uv = vec2((vDelta.x + uImageryU.x) * uImageryU.y, uImageryV.x + (uImageryV.z - log(tan(0.78539816 + phi * 0.5))) * uImageryV.y);
                // Sampled outside any non-uniform branch, so the mipmap derivatives stay defined.
                vec4 sampled = texture(uImagery, clamp(uv, 0.0, 1.0));
                texel = uv == clamp(uv, 0.0, 1.0) ? sampled : vec4(0.0);
            }

            vec3 color;
            if (texel.a > 0.5)
            {
                color = texel.rgb * (0.78 + 0.32 * lambert);
                color = mix(color, vec3(0.08), 0.10 * contour(vHeight, 10.0, 0.6) + 0.18 * contour(vHeight, 50.0, 0.6));
            }
            else
            {
                float light = 0.55 + 0.85 * lambert;
                float t = clamp((vHeight - uFog.z) / 900.0 + 0.35, 0.0, 1.0);
                vec3 base = mix(vec3(0.145, 0.157, 0.169), vec3(0.235, 0.251, 0.267), t);
                color = base * light;
                color += vec3(0.030) * contour(vHeight, 10.0, 1.0) + vec3(0.050) * contour(vHeight, 50.0, 1.0);
            }
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
