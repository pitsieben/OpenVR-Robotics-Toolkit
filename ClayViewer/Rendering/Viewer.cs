using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using ImGuiNET;
using Silk.NET.Windowing;
using ClayViewer.Stock;
using ViveClay.Protocol;

namespace ClayViewer.Rendering;

/// <summary>
/// OpenGL 3.3 window: orbit camera (Z up like Rhino), the clay stock as per-chunk meshes (only re-meshed chunks are
/// re-uploaded), a procedural clay material (mottled terracotta, fine roughness, freshly cut faces darker with a wet
/// sheen that dries over ~10 s), a PCF shadow map from the key light (stock and tool cast onto the clay and the table),
/// the cutter drawn solid and the rest of the tool translucent at a lightly smoothed pose, a fading tip trail and a
/// HUD in the window title. The render thread only uploads finished buffers and never waits on the carver or the pipe.
/// </summary>
public sealed class Viewer : IDisposable
{
    readonly ClayEngine _engine;
    readonly IWindow _window;
    GL _gl = null!;
    IInputContext _input = null!;

    // GL objects
    uint _prog, _depthProg, _lineProg;
    int _lVP, _lAlpha, _lViewport, _lWidth;
    int _uModel, _uVP, _uColor, _uMode, _uEye, _uAlpha, _uTime, _uLightVP, _uLightDir, _uShadow, _uSpec, _uShadowOn;
    int _dModel, _dVP;
    uint _shadowFbo, _shadowTex;
    const int ShadowSize = 2048;
    readonly Mesh _tool = new(), _cutter = new(), _grid = new(), _axes = new(), _trail = new(), _ground = new();
    readonly Dictionary<int, Mesh> _chunks = new();
    int _epoch = -1;

    // UI + recorded-plane overlay
    ImGuiController? _imgui;
    AnalyticsPanel _panel = null!;
    PlaneAnalytics? _pa;
    RecordedPlanesMsg? _paSrc;
    readonly Mesh _paths = new(), _glyphs = new();
    readonly List<(int take, int pFirst, int pCount, int gFirst, int gCount)> _overlayRanges = new();
    const float GlyphStepMm = 8f, GlyphLenMm = 12f;
    long _stockTris;

    // scene state
    StockBox _box = new(Vector3.Zero, new Vector3(226.2f, 88, 100));
    bool _haveStock;
    Vector3 _toolTip;
    int _trailVerts;
    readonly List<(Vector3 pos, double t)> _trailPts = new();
    const double TrailSeconds = 2.0;
    static readonly Vector3 LightDir = Vector3.Normalize(new Vector3(0.45f, -0.6f, 0.72f));

    // displayed tool pose: lightly filtered (time constant ~8 ms, far below one frame of lag); carving uses raw poses
    bool _haveDisp; Vector3 _dispPos; Quaternion _dispRot = Quaternion.Identity; long _dispSeq;
    const double PoseFilterSeconds = 0.008;

    // camera
    Vector3 _target = new(113, 44, 50);
    float _yaw = -0.9f, _pitch = 0.5f, _dist = 450;
    Vector2 _lastMouse;
    bool _orbiting, _panning;
    Matrix4x4 _view, _proj;
    Vector2D<int> _size = new(1280, 800), _size0;

    // HUD / run control
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _hudT; int _hudFrames; double _fps;
    long _hudPoses; double _posesPerSec;
    public int FramesRendered { get; private set; }
    public double AvgFps => FramesRendered / Math.Max(0.001, _clock.Elapsed.TotalSeconds);
    public double WorstFrameMs { get; private set; }
    public int SlowFrames { get; private set; }
    public bool LogSlowFrames;
    /// <summary>Initial camera yaw / pitch in radians.</summary>
    public float Yaw { get => _yaw; set => _yaw = value; }
    public float Pitch { get => _pitch; set => _pitch = value; }
    /// <summary>Camera distance factor applied when the stock is framed (smaller = closer).</summary>
    public float Zoom = 1f;
    /// <summary>Close automatically after this many seconds (0 = never); a PNG is written first when ScreenshotPath is set.</summary>
    public double CloseAfterSeconds;
    public string? ScreenshotPath;
    double _lastFrameT;

    static readonly Vector4 PageBg = AnalyticsPanel.PageBg, ViewBg = AnalyticsPanel.ViewBg;

    sealed class Mesh
    {
        public uint Vao, Vbo, Ebo; public int IndexCount, VertexCount;
    }

    public Viewer(ClayEngine engine, Vector2D<int>? size = null)
    {
        _engine = engine;
        if (size is { } sz) _size = sz;
        _size0 = _size;
        var opts = WindowOptions.Default with
        {
            Size = _size,
            Title = "Vive Clay Viewer",
            VSync = true,
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
            Samples = 4,
        };
        _window = Window.Create(opts);
        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.Resize += s => { _size = s; };
        _window.Closing += () => { };
    }

    public void Run() => _window.Run();
    public void RequestClose() => _window.Close();
    public void Dispose() { _imgui?.Dispose(); _window.Dispose(); }

    // ------------------------------------------------------------------ load
    void OnLoad()
    {
        _gl = GL.GetApi(_window);
        _input = _window.CreateInput();
        foreach (var m in _input.Mice)
        {
            m.MouseDown += (_, b) =>
            {
                if (UiWantsMouse() || !_panel.InView(m.Position)) return;
                var shift = _input.Keyboards.Count > 0 && (_input.Keyboards[0].IsKeyPressed(Key.ShiftLeft) || _input.Keyboards[0].IsKeyPressed(Key.ShiftRight));
                if (b == MouseButton.Left) { if (shift) _panning = true; else _orbiting = true; }
                _panning |= b is MouseButton.Right or MouseButton.Middle; _lastMouse = m.Position;
            };
            m.MouseUp += (_, b) => { if (b == MouseButton.Left) { _orbiting = false; _panning = false; } if (b is MouseButton.Right or MouseButton.Middle) _panning = false; };
            m.MouseMove += (_, p) => OnMouseMove(p);
            m.Scroll += (_, w) => { if (!UiWantsMouse() && _panel.InView(m.Position)) _dist = Math.Clamp(_dist * MathF.Pow(0.9f, w.Y), 5f, 20000f); };
        }
        foreach (var k in _input.Keyboards)
            k.KeyDown += (_, key, _) =>
            {
                if (_imgui != null && ImGui.GetIO().WantTextInput) return;
                if (key == Key.F) Frame();
                if (key == Key.P) ScreenshotToPictures();
                if (key == Key.Tab) _panel.Open = !_panel.Open;
                if (key == Key.Escape) _window.Close();
            };

        _prog = BuildProgram(MainVs, MainFs);
        _uModel = U("uModel"); _uVP = U("uVP"); _uColor = U("uColor"); _uMode = U("uMode"); _uEye = U("uEye"); _uAlpha = U("uAlpha");
        _uTime = U("uTime"); _uLightVP = U("uLightVP"); _uLightDir = U("uLightDir"); _uShadow = U("uShadow"); _uSpec = U("uSpec"); _uShadowOn = U("uShadowOn");
        _depthProg = BuildProgram(DepthVs, DepthFs);
        _lineProg = BuildProgram(LineVs, LineFs, LineGs);
        _lVP = _gl.GetUniformLocation(_lineProg, "uVP"); _lAlpha = _gl.GetUniformLocation(_lineProg, "uAlpha");
        _lViewport = _gl.GetUniformLocation(_lineProg, "uViewport"); _lWidth = _gl.GetUniformLocation(_lineProg, "uWidth");
        _dModel = _gl.GetUniformLocation(_depthProg, "uModel"); _dVP = _gl.GetUniformLocation(_depthProg, "uVP");
        foreach (var m in new[] { _tool, _cutter, _grid, _axes, _trail, _ground, _paths, _glyphs }) CreateMesh(m, 6);
        CreateShadowMap();

        BuildAxes();
        BuildGrid();
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Multisample);
        _gl.VertexAttrib1(2, -1e6f);            // "cut time" for meshes without that attribute: never cut
        _gl.ClearColor(PageBg.X, PageBg.Y, PageBg.Z, 1f);
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        InitUi();
        Console.WriteLine($"[viewer] GL {_gl.GetStringS(StringName.Version)} on {_gl.GetStringS(StringName.Renderer)}");
    }

    // ------------------------------------------------------------------ UI
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);

    float DpiScale()
    {
        try
        {
            var h = _window.Native?.Win32?.Hwnd ?? IntPtr.Zero;
            if (h != IntPtr.Zero) { var dpi = GetDpiForWindow(h); if (dpi > 0) return dpi / 96f; }
        }
        catch { }
        return 1f;
    }

    /// <summary>UI scale override (--ui-scale); 0 = from the window's DPI.</summary>
    public float UiScale;
    /// <summary>Recorded take highlighted at start (-1 = none).</summary>
    public int SelectTake = -1;

    unsafe void InitUi()
    {
        _panel = new AnalyticsPanel(_engine) { FrameStock = Frame, Screenshot = ScreenshotToPictures, SelectedTake = SelectTake };
        try
        {
            var scale = Math.Clamp(UiScale > 0 ? UiScale : DpiScale(), 0.75f, 3f);
            var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            string Fnt(params string[] names) { foreach (var n in names) { var p = Path.Combine(fonts, n); if (File.Exists(p)) return p; } return ""; }
            var body = Fnt("segoeui.ttf"); var semi = Fnt("seguisb.ttf", "segoeuib.ttf"); var bold = Fnt("segoeuib.ttf", "seguisb.ttf");
            var mono = Fnt("CascadiaMono.ttf", "consola.ttf");
            ImGuiFontConfig? cfg = body != "" ? new ImGuiFontConfig(body, (int)MathF.Round(13.5f * scale)) : null;
            _imgui = new ImGuiController(_gl, _window, _input, cfg, () =>
            {
                var io = ImGui.GetIO();
                io.NativePtr->IniFilename = null;          // no imgui.ini next to the exe
                if (cfg != null && semi != "" && mono != "")
                {
                    AnalyticsPanel.FSemi = io.Fonts.AddFontFromFileTTF(semi, MathF.Round(13.5f * scale));
                    AnalyticsPanel.FLabel = io.Fonts.AddFontFromFileTTF(semi, MathF.Round(11f * scale));
                    AnalyticsPanel.FSmall = io.Fonts.AddFontFromFileTTF(body, MathF.Round(12.5f * scale));
                    AnalyticsPanel.FMono = io.Fonts.AddFontFromFileTTF(mono, MathF.Round(12f * scale));
                    AnalyticsPanel.FBrand = io.Fonts.AddFontFromFileTTF(bold, MathF.Round(16f * scale));
                    AnalyticsPanel.FBig = io.Fonts.AddFontFromFileTTF(semi, MathF.Round(26f * scale));
                    AnalyticsPanel.HasFonts = true;
                }
            });
            _panel.Scale = scale;
            AnalyticsPanel.ApplyStyle(scale);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[viewer] UI disabled: " + ex.Message);
            _imgui = null;
        }
    }

    bool UiWantsMouse() => _imgui != null && ImGui.GetIO().WantCaptureMouse;

    /// <summary>Framebuffer pixels per window (logical) unit.</summary>
    float FbRatio() => _window.Size.X > 0 ? FbSize().X / (float)_window.Size.X : 1f;

    void ScreenshotToPictures() =>
        ScreenshotNow(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), $"clay_{DateTime.Now:yyyyMMdd_HHmmss}.png"));

    /// <summary>Re-run the analytics when a new RecordedPlanes snapshot arrived; rebuild the overlay when it or the colouring changed.</summary>
    void UpdatePlanes()
    {
        var src = _engine.LatestPlanes;
        if (src != null && !ReferenceEquals(src, _paSrc))
        {
            _paSrc = src;
            _pa = PlaneAnalytics.Compute(src);
            if (_pa.Count == 0) _panel.SelectedTake = -1;     // recorder cleared
            _panel.OverlayDirty = true;
        }
        if (_panel.OverlayDirty && _pa != null) { _panel.OverlayDirty = false; BuildOverlay(_pa); }
    }

    /// <summary>Recorded paths as tilt-coloured line segments per take (breaks at tracking gaps) and short upward
    /// tool-axis glyphs every GlyphStepMm along the path. Vertex = pos + rgb (mode 3).</summary>
    void BuildOverlay(PlaneAnalytics pa)
    {
        var lines = new List<float>(pa.Count * 12); var glyphs = new List<float>(pa.Count * 4);
        _overlayRanges.Clear();
        var lim = _panel.RobotLimit;
        void V(List<float> d, Vector3 p, Vector3 c) { d.Add(p.X); d.Add(p.Y); d.Add(p.Z); d.Add(c.X); d.Add(c.Y); d.Add(c.Z); }
        foreach (var t in pa.Takes)
        {
            int pf = lines.Count / 6, gf = glyphs.Count / 6;
            var acc = GlyphStepMm;      // glyph at the start of each take
            for (var i = t.First; i < t.First + t.Count; i++)
            {
                var p = pa.Pos[i] + pa.Axis[i] * 0.3f; var c = PlaneAnalytics.TiltColor(pa.Tilt[i], lim);
                if (i > t.First)
                {
                    var q = pa.Pos[i - 1] + pa.Axis[i - 1] * 0.3f;
                    var d = Vector3.Distance(p, q);
                    if (d < 25f && pa.Time[i] - pa.Time[i - 1] < 0.5)
                    { V(lines, q, PlaneAnalytics.TiltColor(pa.Tilt[i - 1], lim)); V(lines, p, c); }
                    acc += d;
                }
                if (acc >= GlyphStepMm)
                {
                    acc = 0;
                    V(glyphs, p, c); V(glyphs, p + pa.Axis[i] * GlyphLenMm, c * 0.55f + new Vector3(0.45f));
                }
            }
            _overlayRanges.Add((t.Take, pf, lines.Count / 6 - pf, gf, glyphs.Count / 6 - gf));
        }
        SetLineBuffer(_paths, lines);
        SetLineBuffer(_glyphs, glyphs);
    }

    void DrawOverlay(Matrix4x4 vp, int viewW, int viewH)
    {
        if (!_panel.ShowPaths || _overlayRanges.Count == 0) return;
        _gl.UseProgram(_lineProg);
        SetMat(_lVP, vp);
        _gl.Uniform2(_lViewport, viewW * 0.5f, viewH * 0.5f);
        var px = Math.Max(1f, _panel.Scale);
        var sel = _panel.SelectedTake;
        void Pass(float alphaAll, float alphaSel)
        {
            foreach (var (take, pf, pc, gf, gc) in _overlayRanges)
            {
                if (_panel.HiddenTakes.Contains(take)) continue;
                var a = sel < 0 ? alphaAll : take == sel ? alphaSel : alphaAll * 0.35f;
                if (a <= 0) continue;
                _gl.Uniform1(_lAlpha, a);
                _gl.Uniform1(_lWidth, (take == sel ? 2.6f : 1.7f) * px);
                if (pc > 0) DrawRange(_paths, PrimitiveType.Lines, pf, pc);
                _gl.Uniform1(_lWidth, 0.9f * px);
                if (_panel.ShowGlyphs && gc > 0) DrawRange(_glyphs, PrimitiveType.Lines, gf, gc);
            }
        }
        if (_panel.XRay)
        {
            // hidden parts (inside cut grooves / behind the brick) stay readable as a faint ghost
            _gl.Disable(EnableCap.DepthTest);
            Pass(0.28f, 0.7f);
            _gl.Enable(EnableCap.DepthTest);
        }
        Pass(1f, 1f);
        _gl.UseProgram(_prog);
    }

    void DrawUi(double dt)
    {
        if (_imgui == null) return;
        _imgui.Update((float)dt);
        var io = ImGui.GetIO();
        var info = new PanelFrameInfo { Fps = _fps, PosesPerSec = _posesPerSec, StockTris = _stockTris, HavePose = _haveDisp && _engine.LatestPose != null };
        if (info.HavePose)
        {
            info.ToolPos = _dispPos;
            // tilt = angle between the TCP plane's Z (sent by the recorder) and world Z
            var ax = _engine.LatestPose?.TcpAxis ?? Vector3.Transform(Vector3.UnitZ, _dispRot);
            var len = ax.Length();
            info.TiltDeg = len < 1e-6f ? 0f : MathF.Acos(Math.Clamp(ax.Z / len, -1f, 1f)) * 180f / MathF.PI;
        }
        if (!io.WantCaptureMouse && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && _panel.InView(io.MousePos)) Frame();
        _panel.Draw(info, _pa, io.DisplaySize);
        _imgui.Render();
    }

    Vector2D<int> FbSize()
    {
        var fb = _window.FramebufferSize;
        return fb.X > 0 && fb.Y > 0 ? fb : _size;
    }

    int U(string name) => _gl.GetUniformLocation(_prog, name);

    unsafe void CreateShadowMap()
    {
        _shadowTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _shadowTex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24, ShadowSize, ShadowSize, 0, PixelFormat.DepthComponent, PixelType.UnsignedInt, (void*)0);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)GLEnum.CompareRefToTexture);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)GLEnum.Lequal);
        _shadowFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _shadowFbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, _shadowTex, 0);
        _gl.DrawBuffer(DrawBufferMode.None);
        _gl.ReadBuffer(ReadBufferMode.None);
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete) Console.Error.WriteLine("[viewer] shadow framebuffer incomplete: " + status);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    // ------------------------------------------------------------------ input / camera
    void OnMouseMove(Vector2 p)
    {
        var d = p - _lastMouse; _lastMouse = p;
        if (_orbiting)
        {
            _yaw -= d.X * 0.008f;
            _pitch = Math.Clamp(_pitch + d.Y * 0.008f, -1.5f, 1.5f);
        }
        else if (_panning)
        {
            // Pan in the camera plane, scaled so the scene follows the cursor.
            var scale = _dist * 0.0016f;
            Matrix4x4.Invert(_view, out var inv);
            var right = new Vector3(inv.M11, inv.M12, inv.M13);
            var up = new Vector3(inv.M21, inv.M22, inv.M23);
            _target += (-right * d.X + up * d.Y) * scale;
        }
    }

    void Frame()
    {
        _target = _box.Center;
        _dist = Math.Max(50f, _box.Size.Length() * 2.2f) * Zoom;
    }

    Vector3 EyePos() => _target + _dist * new Vector3(MathF.Cos(_pitch) * MathF.Cos(_yaw), MathF.Cos(_pitch) * MathF.Sin(_yaw), MathF.Sin(_pitch));

    // ------------------------------------------------------------------ frame
    void OnRender(double dt)
    {
        var nowT = _clock.Elapsed.TotalSeconds;
        if (FramesRendered > 30)
        {
            var fms = (nowT - _lastFrameT) * 1000;
            WorstFrameMs = Math.Max(WorstFrameMs, fms);
            if (fms > 34) SlowFrames++;
            if (LogSlowFrames && fms > 34) Console.WriteLine($"[viewer] slow frame {fms:0.0} ms at t={nowT:0.00}s");
        }
        _lastFrameT = nowT;
        FramesRendered++;
        if (CloseAfterSeconds > 0 && ScreenshotPath != null)
        {
            // automated screenshot run: keep the requested window size even if the OS resized the window
            AnalyticsPanel.NoTooltips = true;
            if (_window.Size != _size0 && FramesRendered % 30 == 0) _window.Size = _size0;
        }
        PumpUploads();
        UpdatePlanes();
        _gl.VertexAttrib1(2, -1e6f);            // generic attrib values are undefined after ImGui's draws: re-set every frame
        var havePose = UpdateDisplayPose(dt, out var toolMat);

        var eye = EyePos();
        _view = Matrix4x4.CreateLookAt(eye, _target, Vector3.UnitZ);
        // a pending screenshot renders this frame into an offscreen target (works even while the window is minimised)
        var shot = _shotPending; _shotPending = null;
        if (shot == null && CloseAfterSeconds > 0 && nowT >= CloseAfterSeconds) shot = ScreenshotPath;
        uint shotFbo = 0, shotColor = 0, shotDepth = 0;
        var fb = FbSize();
        if (shot != null)
        {
            shotFbo = _gl.GenFramebuffer(); shotColor = _gl.GenRenderbuffer(); shotDepth = _gl.GenRenderbuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, shotFbo);
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, shotColor);
            _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Rgba8, (uint)fb.X, (uint)fb.Y);
            _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, shotColor);
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, shotDepth);
            _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)fb.X, (uint)fb.Y);
            _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, shotDepth);
        }
        // the 3D view is the rounded card the panel lays out (logical units -> framebuffer pixels, GL origin bottom-left)
        _panel.Layout(new Vector2(_window.Size.X, _window.Size.Y));
        var fr = FbRatio();
        var viewX = (int)MathF.Round(_panel.ViewMin.X * fr);
        var viewW = Math.Max(1, (int)MathF.Round(_panel.ViewMax.X * fr) - viewX);
        var viewH = Math.Max(1, (int)MathF.Round((_panel.ViewMax.Y - _panel.ViewMin.Y) * fr));
        var viewY = Math.Max(0, fb.Y - (int)MathF.Round(_panel.ViewMax.Y * fr));
        _proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, viewW / (float)viewH, Math.Max(0.5f, _dist * 0.01f), _dist * 50f);
        var vp = _view * _proj;

        // ---- shadow pass (key light, orthographic around the stock)
        var radius = _box.Size.Length() * 0.5f + 140f;
        var lightView = Matrix4x4.CreateLookAt(_box.Center + LightDir * radius * 2f, _box.Center, Vector3.UnitZ);
        var lightVP = lightView * Matrix4x4.CreateOrthographic(radius * 2, radius * 2, 1f, radius * 4f);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _shadowFbo);
        _gl.Viewport(0, 0, ShadowSize, ShadowSize);
        _gl.Clear(ClearBufferMask.DepthBufferBit);
        _gl.UseProgram(_depthProg);
        SetMat(_dVP, lightVP);
        SetMat(_dModel, Matrix4x4.Identity);
        _gl.Disable(EnableCap.Blend);
        _gl.DepthMask(true);
        foreach (var c in _chunks.Values) Draw(c, PrimitiveType.Triangles);
        if (havePose)
        {
            SetMat(_dModel, toolMat);
            Draw(_cutter, PrimitiveType.Triangles);
            if (_tool.IndexCount > 0) Draw(_tool, PrimitiveType.Triangles);
        }
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, shotFbo);
        _gl.Viewport(0, 0, (uint)fb.X, (uint)fb.Y);

        // ---- main pass
        _gl.ClearColor(PageBg.X, PageBg.Y, PageBg.Z, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _gl.Enable(EnableCap.ScissorTest);
        _gl.Scissor(viewX, viewY, (uint)viewW, (uint)viewH);
        _gl.ClearColor(ViewBg.X, ViewBg.Y, ViewBg.Z, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        _gl.Viewport(viewX, viewY, (uint)viewW, (uint)viewH);
        _gl.UseProgram(_prog);
        SetMat(_uVP, vp);
        SetMat(_uLightVP, lightVP);
        _gl.Uniform3(_uEye, eye.X, eye.Y, eye.Z);
        _gl.Uniform3(_uLightDir, LightDir.X, LightDir.Y, LightDir.Z);
        _gl.Uniform1(_uTime, _engine.StockTime);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _shadowTex);
        _gl.Uniform1(_uShadow, 0);
        _gl.Uniform1(_uShadowOn, 1);

        // table under the stock (receives the shadows): light, close to the viewport grey so it melts into the background
        SetMat(_uModel, Matrix4x4.Identity);
        _gl.Uniform1(_uAlpha, 1f);
        _gl.Uniform1(_uMode, 2);
        _gl.Uniform1(_uSpec, 0.02f);
        _gl.Uniform3(_uColor, 0.86f, 0.855f, 0.84f);
        Draw(_ground, PrimitiveType.Triangles);

        // cutter: solid polished steel so it reads clearly against the clay
        if (havePose && _panel.ShowTool && _cutter.IndexCount > 0)
        {
            SetMat(_uModel, toolMat);
            _gl.Uniform1(_uSpec, 0.9f);
            _gl.Uniform3(_uColor, 0.80f, 0.84f, 0.90f);
            Draw(_cutter, PrimitiveType.Triangles);
            SetMat(_uModel, Matrix4x4.Identity);
        }

        // clay (after table and cutter so a reduced stock opacity shows them through it)
        if (_panel.ShowStock)
        {
            SetMat(_uModel, Matrix4x4.Identity);
            _gl.Uniform1(_uMode, 0);
            _gl.Uniform1(_uShadowOn, 1);
            var op = Math.Clamp(_panel.StockOpacity, 0.05f, 1f);
            _gl.Uniform1(_uAlpha, op);
            if (op < 0.999f) { _gl.Enable(EnableCap.Blend); _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); }
            _gl.Uniform3(_uColor, 0.70f, 0.42f, 0.30f);
            if (_panel.Wireframe)
            {
                _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
                _gl.Uniform3(_uColor, 0.62f, 0.36f, 0.25f);
            }
            foreach (var c in _chunks.Values) Draw(c, PrimitiveType.Triangles);
            _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
            _gl.Disable(EnableCap.Blend);
            _gl.Uniform1(_uAlpha, 1f);
        }

        // lines: grid, axes, trail
        _gl.Uniform1(_uMode, 1);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.Uniform3(_uColor, 0.42f, 0.45f, 0.50f);
        Draw(_grid, PrimitiveType.Lines);
        _gl.Uniform3(_uColor, 0.84f, 0.27f, 0.27f); DrawRange(_axes, PrimitiveType.Lines, 0, 2);
        _gl.Uniform3(_uColor, 0.14f, 0.65f, 0.47f); DrawRange(_axes, PrimitiveType.Lines, 2, 2);
        _gl.Uniform3(_uColor, 0.15f, 0.45f, 0.93f); DrawRange(_axes, PrimitiveType.Lines, 4, 2);
        UpdateTrail(havePose, toolMat);
        _gl.Uniform3(_uColor, 0.15f, 0.45f, 0.93f);
        if (_panel.ShowTrail && _trailVerts > 1) DrawRange(_trail, PrimitiveType.LineStrip, 0, _trailVerts);

        DrawOverlay(vp, viewW, viewH);
        _gl.Uniform1(_uAlpha, 1f);
        _gl.Uniform1(_uMode, 1);

        // rest of the tool (handle, tracker): translucent, no depth writes
        if (havePose && _panel.ShowTool && _tool.IndexCount > 0)
        {
            _gl.DepthMask(false);
            _gl.Uniform1(_uMode, 2);
            _gl.Uniform1(_uShadowOn, 0);
            _gl.Uniform1(_uSpec, 0.25f);
            _gl.Uniform1(_uAlpha, 0.38f);
            _gl.Uniform3(_uColor, 0.35f, 0.62f, 0.95f);
            SetMat(_uModel, toolMat);
            Draw(_tool, PrimitiveType.Triangles);
            _gl.DepthMask(true);
        }
        _gl.Disable(EnableCap.Blend);

        UpdateHud();
        _gl.Disable(EnableCap.ScissorTest);
        _gl.Viewport(0, 0, (uint)fb.X, (uint)fb.Y);
        DrawUi(dt);

        if (shot != null)
        {
            SaveTarget(shot, fb.X, fb.Y);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _gl.DeleteFramebuffer(shotFbo); _gl.DeleteRenderbuffer(shotColor); _gl.DeleteRenderbuffer(shotDepth);
        }
        if (CloseAfterSeconds > 0 && nowT >= CloseAfterSeconds)
        {
            CloseAfterSeconds = 0;
            _window.Close();
        }
    }

    /// <summary>Upload whatever finished buffers the carver / pipe handed over since the last frame.</summary>
    void PumpUploads()
    {
        var t = _engine.TakePendingTool();
        if (t != null) { Upload(_tool, t.Interleaved, t.Indices); _toolTip = t.Tip; _trailPts.Clear(); }
        var cu = _engine.TakePendingCutter();
        if (cu != null) { Upload(_cutter, cu.Interleaved, cu.Indices); _toolTip = cu.Tip; }

        while (_engine.TryTakeChunk(out var up) && up != null)
        {
            if (up.Epoch != _epoch)
            {
                foreach (var old in _chunks.Values) DeleteMesh(old);
                _chunks.Clear(); _stockTris = 0;
                _epoch = up.Epoch;
                if (!_haveStock || _box != up.Box) { _box = up.Box; Frame(); BuildGrid(); _haveStock = true; }
            }
            var cm = up.Mesh;
            _chunks.TryGetValue(cm.Id, out var mesh);
            if (cm.Indices.Length == 0)
            {
                if (mesh != null) { _stockTris -= mesh.IndexCount / 3; DeleteMesh(mesh); _chunks.Remove(cm.Id); }
                continue;
            }
            if (mesh == null) { mesh = new Mesh(); CreateMesh(mesh, 7); _chunks[cm.Id] = mesh; }
            _stockTris += (cm.Indices.Length - mesh.IndexCount) / 3;
            Upload(mesh, cm.Vertices, cm.Indices, 7);
        }
    }

    /// <summary>Displayed tool pose = exponentially smoothed raw pose (removes tracker jitter, adds well under a frame of lag).</summary>
    bool UpdateDisplayPose(double dt, out Matrix4x4 m)
    {
        m = Matrix4x4.Identity;
        var pose = _engine.LatestPose;
        if (pose == null) return false;
        var q = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(pose.Matrix));
        var p = pose.Matrix.Translation;
        if (!_haveDisp || Vector3.Distance(p, _dispPos) > 40f) { _dispPos = p; _dispRot = q; _haveDisp = true; }
        else if (pose.Seq != _dispSeq)
        {
            var a = (float)(1 - Math.Exp(-Math.Max(dt, 0.001) / PoseFilterSeconds));
            _dispPos = Vector3.Lerp(_dispPos, p, a);
            _dispRot = Quaternion.Normalize(Quaternion.Slerp(_dispRot, q, a));
        }
        _dispSeq = pose.Seq;
        m = Matrix4x4.CreateFromQuaternion(_dispRot);
        m.Translation = _dispPos;
        return true;
    }

    void UpdateTrail(bool havePose, Matrix4x4 toolMat)
    {
        var now = _clock.Elapsed.TotalSeconds;
        if (havePose)
        {
            var tip = Vector3.Transform(_toolTip, toolMat);
            if (_trailPts.Count == 0 || Vector3.DistanceSquared(_trailPts[^1].pos, tip) > 0.04f) _trailPts.Add((tip, now));
        }
        _trailPts.RemoveAll(p => now - p.t > TrailSeconds);
        if (_trailPts.Count == 0) { _trailVerts = 0; return; }
        // vertex = pos xyz + (alpha, 0, 0) in the normal slot (mode 1 reads alpha from there)
        var data = new float[_trailPts.Count * 6];
        for (var i = 0; i < _trailPts.Count; i++)
        {
            var (p, t) = _trailPts[i];
            data[i * 6] = p.X; data[i * 6 + 1] = p.Y; data[i * 6 + 2] = p.Z;
            data[i * 6 + 3] = (float)(1 - (now - t) / TrailSeconds);
        }
        _trailVerts = _trailPts.Count;
        _gl.BindVertexArray(_trail.Vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _trail.Vbo);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, data, BufferUsageARB.StreamDraw);
    }

    void UpdateHud()
    {
        _hudFrames++;
        var now = _clock.Elapsed.TotalSeconds;
        if (now - _hudT < 0.5) return;
        _fps = _hudFrames / (now - _hudT);
        var poses = _engine.PosesReceived;
        _posesPerSec = (poses - _hudPoses) / (now - _hudT);
        _hudPoses = poses; _hudFrames = 0; _hudT = now;
        var st = _engine.Stats;
        var carve = st.Sweeps > 0 ? st.CarveMsTotal / st.Sweeps : 0;
        _window.Title = $"Vive Clay Viewer | {_fps:0} fps | {_engine.Connection} | {_posesPerSec:0} poses/s (queue {_engine.Backlog}) | {_engine.StockInfo} | " +
                        $"{_stockTris / 1000}k tris | carve {carve:0.0} ms | removed {_engine.RemovedVolume:0} mm³";
    }

    // ------------------------------------------------------------------ screenshot
    string? _shotPending;
    void ScreenshotNow(string path) => _shotPending = path;

    /// <summary>Reads the currently bound (offscreen) colour target and writes it as a PNG.</summary>
    unsafe void SaveTarget(string path, int w, int h)
    {
        try
        {
            var px = new byte[w * h * 4];
            _gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
            _gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
            fixed (byte* p = px) _gl.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.UnsignedByte, p);
            WritePng(path, px, w, h);
            Console.WriteLine("[viewer] screenshot " + path);
        }
        catch (Exception ex) { Console.Error.WriteLine("[viewer] screenshot failed: " + ex.Message); }
    }

    static void WritePng(string path, byte[] rgba, int w, int h)
    {
        var raw = new byte[(w * 3 + 1) * h];
        for (var y = 0; y < h; y++)
        {
            var src = (h - 1 - y) * w * 4; var dst = y * (w * 3 + 1) + 1;
            for (var x = 0; x < w; x++) { raw[dst++] = rgba[src]; raw[dst++] = rgba[src + 1]; raw[dst++] = rgba[src + 2]; src += 4; }
        }
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Fastest, true)) zs.Write(raw);
        var crcTable = new uint[256];
        for (uint n = 0; n < 256; n++) { var c = n; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; crcTable[n] = c; }
        using var f = File.Create(path);
        f.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string type, byte[] data)
        {
            var len = BitConverter.GetBytes(data.Length); Array.Reverse(len); f.Write(len);
            var td = new byte[4 + data.Length];
            System.Text.Encoding.ASCII.GetBytes(type, 0, 4, td, 0); data.CopyTo(td, 4);
            f.Write(td);
            var crc = 0xFFFFFFFFu;
            foreach (var b in td) crc = crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            var cb = BitConverter.GetBytes(crc ^ 0xFFFFFFFFu); Array.Reverse(cb); f.Write(cb);
        }
        var ihdr = new byte[13];
        var wb = BitConverter.GetBytes(w); Array.Reverse(wb); wb.CopyTo(ihdr, 0);
        var hb = BitConverter.GetBytes(h); Array.Reverse(hb); hb.CopyTo(ihdr, 4);
        ihdr[8] = 8; ihdr[9] = 2;
        Chunk("IHDR", ihdr); Chunk("IDAT", z.ToArray()); Chunk("IEND", Array.Empty<byte>());
    }

    // ------------------------------------------------------------------ GL helpers
    /// <summary>floatsPerVertex 6 = pos + normal; 7 = pos + normal + cut time (stock chunks).</summary>
    void CreateMesh(Mesh m, int floatsPerVertex)
    {
        m.Vao = _gl.GenVertexArray();
        m.Vbo = _gl.GenBuffer();
        m.Ebo = _gl.GenBuffer();
        _gl.BindVertexArray(m.Vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, m.Vbo);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, m.Ebo);
        var stride = (uint)(floatsPerVertex * 4);
        unsafe
        {
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
            _gl.EnableVertexAttribArray(1);
            _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)12);
            if (floatsPerVertex == 7)
            {
                _gl.EnableVertexAttribArray(2);
                _gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)24);
            }
        }
    }

    void DeleteMesh(Mesh m)
    {
        _gl.DeleteVertexArray(m.Vao); _gl.DeleteBuffer(m.Vbo); _gl.DeleteBuffer(m.Ebo);
    }

    void Upload(Mesh m, float[] vertices, int[] indices, int floatsPerVertex = 6)
    {
        _gl.BindVertexArray(m.Vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, m.Vbo);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, vertices, BufferUsageARB.DynamicDraw);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, m.Ebo);
        _gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, MemoryMarshal.Cast<int, uint>(indices.AsSpan()), BufferUsageARB.DynamicDraw);
        m.IndexCount = indices.Length;
        m.VertexCount = vertices.Length / floatsPerVertex;
    }

    unsafe void Draw(Mesh m, PrimitiveType type)
    {
        if (m.IndexCount > 0)
        {
            _gl.BindVertexArray(m.Vao);
            _gl.DrawElements(type, (uint)m.IndexCount, DrawElementsType.UnsignedInt, (void*)0);
        }
        else if (m.VertexCount > 0) DrawRange(m, type, 0, m.VertexCount);
    }

    void DrawRange(Mesh m, PrimitiveType type, int first, int count)
    {
        _gl.BindVertexArray(m.Vao);
        _gl.DrawArrays(type, first, (uint)count);
    }

    unsafe void SetMat(int loc, Matrix4x4 m) => _gl.UniformMatrix4(loc, 1, false, &m.M11);

    void SetLineBuffer(Mesh m, List<float> data)
    {
        // vertex = pos xyz + alpha in the "normal.x" slot
        _gl.BindVertexArray(m.Vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, m.Vbo);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, CollectionsMarshal.AsSpan(data), BufferUsageARB.StaticDraw);
        m.VertexCount = data.Count / 6; m.IndexCount = 0;
    }

    void BuildAxes()
    {
        var d = new List<float>();
        void L(float x0, float y0, float z0, float x1, float y1, float z1)
        { d.AddRange(new[] { x0, y0, z0, 1, 0, 0, x1, y1, z1, 1, 0, 0 }); }
        L(0, 0, 0, 100, 0, 0); L(0, 0, 0, 0, 100, 0); L(0, 0, 0, 0, 0, 100);
        SetLineBuffer(_axes, d);
    }

    /// <summary>Table and grid on the stock's bottom plane, 50 mm cells, ~1 m square around the stock.</summary>
    void BuildGrid()
    {
        var d = new List<float>();
        const float step = 50, half = 500;
        var c = _box.Center; var z = _box.Min.Z - 0.15f;
        var cx = MathF.Round(c.X / step) * step; var cy = MathF.Round(c.Y / step) * step;
        for (var o = -half; o <= half; o += step)
        {
            var a = 0.30f * (1 - MathF.Abs(o) / half * 0.8f);
            d.AddRange(new[] { cx + o, cy - half, z, a, 0, 0, cx + o, cy + half, z, a, 0, 0 });
            d.AddRange(new[] { cx - half, cy + o, z, a, 0, 0, cx + half, cy + o, z, a, 0, 0 });
        }
        SetLineBuffer(_grid, d);
        var zg = _box.Min.Z - 0.3f; const float g = 1500;
        Upload(_ground, new[]
        {
            cx - g, cy - g, zg, 0, 0, 1,  cx + g, cy - g, zg, 0, 0, 1,  cx + g, cy + g, zg, 0, 0, 1,  cx - g, cy + g, zg, 0, 0, 1,
        }, new[] { 0, 1, 2, 0, 2, 3 });
    }

    const string MainVs = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNrm;
layout(location=2) in float aTime;
uniform mat4 uModel; uniform mat4 uVP;
out vec3 vWorld; out vec3 vNrm; out float vTime;
void main(){
  vec4 w = uModel * vec4(aPos,1.0);
  vWorld = w.xyz;
  vNrm = mat3(uModel) * aNrm;   // rigid transforms only
  vTime = aTime;
  gl_Position = uVP * w;
}";

    const string MainFs = @"#version 330 core
in vec3 vWorld; in vec3 vNrm; in float vTime;
uniform vec3 uColor; uniform int uMode; uniform vec3 uEye; uniform float uAlpha; uniform float uTime;
uniform mat4 uLightVP; uniform vec3 uLightDir; uniform sampler2DShadow uShadow; uniform float uSpec; uniform int uShadowOn;
out vec4 o;

float hash(vec3 p){ p = fract(p * vec3(0.1031, 0.1030, 0.0973)); p += dot(p, p.yxz + 33.33); return fract((p.x + p.y) * p.z); }
float vnoise(vec3 x){
  vec3 i = floor(x); vec3 f = fract(x); f = f*f*(3.0-2.0*f);
  return mix(mix(mix(hash(i), hash(i+vec3(1,0,0)), f.x), mix(hash(i+vec3(0,1,0)), hash(i+vec3(1,1,0)), f.x), f.y),
             mix(mix(hash(i+vec3(0,0,1)), hash(i+vec3(1,0,1)), f.x), mix(hash(i+vec3(0,1,1)), hash(i+vec3(1,1,1)), f.x), f.y), f.z);
}
float fbm(vec3 p){ return 0.55*vnoise(p) + 0.28*vnoise(p*2.03+7.1) + 0.17*vnoise(p*4.01+3.3); }

float shadow(vec3 N){
  if (uShadowOn == 0) return 1.0;
  vec4 lp = uLightVP * vec4(vWorld + N * 0.6, 1.0);        // normal offset instead of a large depth bias
  vec3 q = vec3(lp.xy * 0.5 + 0.5, lp.z * 0.5 + 0.5);
  if (q.x <= 0.0 || q.x >= 1.0 || q.y <= 0.0 || q.y >= 1.0 || lp.z >= 1.0) return 1.0;
  vec2 tx = 1.4 / vec2(textureSize(uShadow, 0));
  float s = 0.0;
  for (int i = -1; i <= 1; i++) for (int j = -1; j <= 1; j++)
    s += texture(uShadow, vec3(q.xy + vec2(i, j) * tx, q.z - 0.00012));
  return s / 9.0;
}

void main(){
  if (uMode == 1) { o = vec4(uColor, vNrm.x); return; }
  if (uMode == 3) { o = vec4(vNrm, uAlpha); return; }      // overlay lines: rgb rides in the normal slot   // line mode: alpha rides in the normal slot
  vec3 N = normalize(vNrm);
  vec3 V = normalize(uEye - vWorld);
  vec3 L = uLightDir;
  vec3 sky = vec3(0.66, 0.71, 0.80), gnd = vec3(0.34, 0.29, 0.26);
  vec3 fillDir = normalize(vec3(-0.7, 0.5, 0.3));

  if (uMode == 2) {                                        // tool / table: plain lit
    if (dot(N, V) < 0.0) N = -N;
    float sh = shadow(N);
    vec3 amb = mix(gnd, sky, N.z*0.5+0.5) * 0.55;
    float d = max(dot(N, L), 0.0) * sh * 0.85 + max(dot(N, fillDir), 0.0) * 0.2;
    float s = pow(max(dot(N, normalize(L + V)), 0.0), 48.0) * uSpec * sh;
    float rim = pow(1.0 - max(dot(N, V), 0.0), 3.0) * uSpec * 0.35;
    o = vec4(uColor * (amb + d) + vec3(s + rim), uAlpha);
    return;
  }

  // ---- clay
  bool cut = vTime > -1.0e5;
  float wet = cut ? exp(-max(uTime - vTime, 0.0) / 10.0) : 0.0;
  vec3 Ng = N;
  // fine roughness: perturb the normal with the gradient of a small-scale noise (weaker on cut, smeared faces)
  vec3 q = vWorld * 2.3;
  float n0 = fbm(q);
  vec3 g = vec3(fbm(q + vec3(0.4,0,0)) - n0, fbm(q + vec3(0,0.4,0)) - n0, fbm(q + vec3(0,0,0.4)) - n0);
  float bump = cut ? mix(0.35, 0.15, wet) : 0.8;
  N = normalize(N - bump * (g - Ng * dot(g, Ng)));

  // mottled terracotta, slightly greyer in patches; fresh cuts are damp and darker
  vec3 base = mix(uColor * vec3(0.86, 0.80, 0.76), uColor, fbm(vWorld * 0.11));
  base = mix(base, vec3(dot(base, vec3(0.33))) * vec3(1.05, 1.0, 0.95), 0.18 * vnoise(vWorld * 0.045 + 11.0));
  base *= 0.93 + 0.14 * vnoise(vWorld * 1.1);
  if (cut) base *= mix(0.95, 0.72, wet);

  float sh = shadow(Ng);
  float ndl = max(dot(N, L), 0.0);
  vec3 amb = mix(gnd, sky, N.z*0.5+0.5) * 0.62;
  vec3 light = amb + vec3(1.0, 0.96, 0.90) * ndl * sh * 0.92 + vec3(0.9, 0.82, 0.74) * max(dot(N, fillDir), 0.0) * 0.16;
  vec3 H = normalize(L + V);
  float gloss = mix(cut ? 26.0 : 9.0, 110.0, wet);
  float ks = mix(cut ? 0.07 : 0.03, 0.42, wet);
  float spec = pow(max(dot(N, H), 0.0), gloss) * ks * sh;
  float fres = pow(1.0 - max(dot(N, V), 0.0), 4.0) * (0.04 + 0.10 * wet);
  o = vec4(base * light + vec3(spec) + sky * fres, uAlpha);
}";

    const string DepthVs = @"#version 330 core
layout(location=0) in vec3 aPos;
uniform mat4 uModel; uniform mat4 uVP;
void main(){ gl_Position = uVP * (uModel * vec4(aPos,1.0)); }";

    const string DepthFs = @"#version 330 core
void main(){}";

    // Overlay lines: screen-space ribbons of uWidth pixels (core profile has no wide GL lines), rgb in the normal slot.
    const string LineVs = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aCol;
uniform mat4 uVP;
out vec3 gCol;
void main(){ gCol = aCol; gl_Position = uVP * vec4(aPos, 1.0); }";

    const string LineGs = @"#version 330 core
layout(lines) in;
layout(triangle_strip, max_vertices = 4) out;
in vec3 gCol[];
uniform vec2 uViewport; uniform float uWidth;
out vec3 fCol; out float fEdge;
void main(){
  vec4 a = gl_in[0].gl_Position, b = gl_in[1].gl_Position;
  if (a.w <= 0.0 || b.w <= 0.0) return;
  vec2 sa = a.xy / a.w * uViewport, sb = b.xy / b.w * uViewport;
  vec2 d = sb - sa; float l = length(d);
  vec2 n = l > 1e-4 ? vec2(-d.y, d.x) / l : vec2(0.0, 1.0);
  vec2 o = n * uWidth / uViewport;   // half width in NDC (uViewport is half the viewport size)
  fCol = gCol[0]; fEdge = -1.0; gl_Position = vec4(a.xy + o * a.w, a.zw); EmitVertex();
  fCol = gCol[0]; fEdge =  1.0; gl_Position = vec4(a.xy - o * a.w, a.zw); EmitVertex();
  fCol = gCol[1]; fEdge = -1.0; gl_Position = vec4(b.xy + o * b.w, b.zw); EmitVertex();
  fCol = gCol[1]; fEdge =  1.0; gl_Position = vec4(b.xy - o * b.w, b.zw); EmitVertex();
  EndPrimitive();
}";

    const string LineFs = @"#version 330 core
in vec3 fCol; in float fEdge;
uniform float uAlpha;
out vec4 o;
void main(){ float aa = 1.0 - smoothstep(0.55, 1.0, abs(fEdge)); o = vec4(fCol, uAlpha * aa); }";

    uint BuildProgram(string vs, string fs, string? gs = null)
    {
        var v = Compile(ShaderType.VertexShader, vs);
        var f = Compile(ShaderType.FragmentShader, fs);
        var p = _gl.CreateProgram();
        _gl.AttachShader(p, v); _gl.AttachShader(p, f);
        uint g = 0;
        if (gs != null) { g = Compile(ShaderType.GeometryShader, gs); _gl.AttachShader(p, g); }
        _gl.LinkProgram(p);
        _gl.GetProgram(p, ProgramPropertyARB.LinkStatus, out var ok);
        if (ok == 0) throw new Exception("Shader link: " + _gl.GetProgramInfoLog(p));
        _gl.DeleteShader(v); _gl.DeleteShader(f); if (g != 0) _gl.DeleteShader(g);
        return p;
    }

    uint Compile(ShaderType t, string src)
    {
        var s = _gl.CreateShader(t);
        _gl.ShaderSource(s, src);
        _gl.CompileShader(s);
        _gl.GetShader(s, ShaderParameterName.CompileStatus, out var ok);
        if (ok == 0) throw new Exception($"{t} compile: " + _gl.GetShaderInfoLog(s));
        return s;
    }
}
