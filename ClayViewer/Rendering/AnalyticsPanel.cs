using System.Globalization;
using System.Numerics;
using System.Reflection;
using ImGuiNET;

namespace ClayViewer.Rendering;

/// <summary>Live numbers the viewer hands to the panel each frame (render thread only).</summary>
public struct PanelFrameInfo
{
    public double Fps, PosesPerSec;
    public long StockTris;
    public bool HavePose;
    public Vector3 ToolPos;        // displayed tool origin, mm
    public float TiltDeg;          // tool axis (local Z) from world Z, 0..90
}

/// <summary>
/// Light "CamGh"-style UI (Dear ImGui): slim left sidebar (brand, sections, takes, viewer card), a 3D viewport framed as a
/// rounded light-grey card with a monospace readout and a hint line, sparkline cards under it with ~60 s rolling
/// histories, a status pill top right and a column of white cards on the right (Recording, Angularity, Layers, Job).
/// Pure UI on the render thread: reads engine stats and the latest PlaneAnalytics, never blocks the carver.
/// </summary>
public sealed class AnalyticsPanel
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---- palette (CamGh wwwroot :root variables)
    static Vector4 Rgb(int hex, float a = 1) => new(((hex >> 16) & 255) / 255f, ((hex >> 8) & 255) / 255f, (hex & 255) / 255f, a);
    public static readonly Vector4 PageBg = Rgb(0xF4F5F7), White = Rgb(0xFFFFFF), Text = Rgb(0x181A1F), Muted = Rgb(0x7D818A),
        Line = Rgb(0xEAEBEE), CardLine = Rgb(0xEFF0F2), ViewBg = Rgb(0xE9EBEF), ViewLine = Rgb(0xE5E7EB), Blue = Rgb(0x2672ED),
        Ok = Rgb(0x23A778), Orange = Rgb(0xEA9650), Bad = Rgb(0xD64545), Text2 = Rgb(0x5F636C), Text3 = Rgb(0x454952),
        Dark = Rgb(0x23262C), Field = Rgb(0xE3E5E9), Track = Rgb(0xF0F1F3), Band = Rgb(0x78A0EF);
    public static readonly Vector4 Accent = Blue;
    static readonly int[] TakeHex = { 0x2672ED, 0x23A778, 0x8B6FD6, 0xE0705A, 0x1EA5B5, 0xC9A227, 0xD0559B, 0x5F6B7A };
    public static Vector4 TakeColor(int take) => Rgb(TakeHex[((take - 1) % TakeHex.Length + TakeHex.Length) % TakeHex.Length]);

    // ---- layout (logical units before scaling)
    const float SideW = 216, ColW = 340, Pad = 16, TopH = 52, SparkH = 86, Gap = 12;

    readonly ClayEngine _engine;
    public float Scale = 1f;
    public static ImFontPtr FSemi, FLabel, FMono, FBrand, FSmall, FBig;
    public static bool HasFonts;

    // ---- state shared with the viewer
    public bool Open = true;
    public bool ShowStock = true, ShowPaths = true, ShowGlyphs = true, ShowTool = true, ShowTrail = true, Wireframe, XRay = true;
    public float StockOpacity = 1f;
    public readonly HashSet<int> HiddenTakes = new();
    public float RobotLimit = 30f;
    public int SelectedTake = -1;          // take number, -1 = none
    public bool OverlayDirty;              // limit / selection / visibility changed -> rebuild overlay
    public Action? FrameStock, Screenshot;
    /// <summary>3D viewport in window (logical) units, valid after Layout().</summary>
    public Vector2 ViewMin, ViewMax;

    public AnalyticsPanel(ClayEngine engine) { _engine = engine; }

    float S(float v) => v * Scale;
    static string F(string fmt, params object[] a) => string.Format(Inv, fmt, a);
    static uint U32(Vector4 c) => ImGui.GetColorU32(c);
    /// <summary>Automated screenshots: no hover tooltips from wherever the OS cursor happens to be.</summary>
    public static bool NoTooltips;
    static void Tip(string s) { if (NoTooltips) return; ImGui.BeginTooltip(); ImGui.TextUnformatted(s); ImGui.EndTooltip(); }

    public static void ApplyStyle(float scale)
    {
        var st = ImGui.GetStyle();
        st.WindowRounding = 0; st.ChildRounding = 8; st.FrameRounding = 7; st.GrabRounding = 7; st.PopupRounding = 8;
        st.ScrollbarRounding = 6; st.TabRounding = 6;
        st.WindowBorderSize = 0; st.FrameBorderSize = 0; st.ChildBorderSize = 0; st.PopupBorderSize = 1;
        st.WindowPadding = new Vector2(0, 0); st.FramePadding = new Vector2(12, 7); st.ItemSpacing = new Vector2(8, 6);
        st.ItemInnerSpacing = new Vector2(8, 4); st.CellPadding = new Vector2(4, 3); st.IndentSpacing = 18;
        st.ScrollbarSize = 6; st.GrabMinSize = 10;
        st.ScaleAllSizes(scale);
        var c = st.Colors;
        c[(int)ImGuiCol.Text] = Text;
        c[(int)ImGuiCol.TextDisabled] = Muted;
        c[(int)ImGuiCol.WindowBg] = PageBg;
        c[(int)ImGuiCol.ChildBg] = White;
        c[(int)ImGuiCol.PopupBg] = White;
        c[(int)ImGuiCol.Border] = Line;
        c[(int)ImGuiCol.FrameBg] = White;
        c[(int)ImGuiCol.FrameBgHovered] = Rgb(0xF6F7F9);
        c[(int)ImGuiCol.FrameBgActive] = Rgb(0xEEF0F3);
        c[(int)ImGuiCol.Header] = Rgb(0xEEF3FD);
        c[(int)ImGuiCol.HeaderHovered] = Rgb(0xF4F6F9);
        c[(int)ImGuiCol.HeaderActive] = Rgb(0xE4ECFB);
        c[(int)ImGuiCol.Button] = Dark;
        c[(int)ImGuiCol.ButtonHovered] = Rgb(0x353942);
        c[(int)ImGuiCol.ButtonActive] = Rgb(0x111317);
        c[(int)ImGuiCol.CheckMark] = Blue;
        c[(int)ImGuiCol.SliderGrab] = Blue;
        c[(int)ImGuiCol.SliderGrabActive] = Rgb(0x1B5FD0);
        c[(int)ImGuiCol.Separator] = Line;
        c[(int)ImGuiCol.TableHeaderBg] = Rgb(0xF6F7F9);
        c[(int)ImGuiCol.TableBorderStrong] = Line;
        c[(int)ImGuiCol.TableBorderLight] = Rgb(0xF1F2F4);
        c[(int)ImGuiCol.TableRowBg] = Rgb(0, 0);
        c[(int)ImGuiCol.TableRowBgAlt] = Rgb(0, 0.02f);
        c[(int)ImGuiCol.ScrollbarBg] = Rgb(0, 0);
        c[(int)ImGuiCol.ScrollbarGrab] = Rgb(0xD5D8DD);
        c[(int)ImGuiCol.ScrollbarGrabHovered] = Rgb(0xC4C7CD);
        c[(int)ImGuiCol.ScrollbarGrabActive] = Rgb(0xB0B4BB);
        c[(int)ImGuiCol.TitleBg] = PageBg;
        c[(int)ImGuiCol.TitleBgActive] = PageBg;
    }

    // ------------------------------------------------------------------ layout
    bool _sideFits = true;
    float SideWidth => Open && _sideFits ? S(SideW) : 0;
    float ColWidth => Open ? S(ColW) : 0;

    /// <summary>Computes the 3D viewport rectangle (call at the start of a frame with the window size in logical units).</summary>
    public void Layout(Vector2 display)
    {
        // the sidebar is optional: drop it when it would squeeze the 3D view (large UI scale on a small window)
        _sideFits = display.X - S(SideW) - S(ColW) - S(70) >= S(600);
        var left = SideWidth + S(Open ? 24 : Pad);
        var right = display.X - (Open ? ColWidth + S(Pad) + S(Gap + 3) : S(Pad));
        var top = S(TopH);
        var bottom = display.Y - S(Pad) - S(SparkH) - S(Gap);
        ViewMin = new Vector2(left, top);
        ViewMax = new Vector2(MathF.Max(left + 64, right), MathF.Max(top + 64, bottom));
    }

    public bool InView(Vector2 p) => p.X >= ViewMin.X && p.Y >= ViewMin.Y && p.X <= ViewMax.X && p.Y <= ViewMax.Y;

    // ------------------------------------------------------------------ rolling histories (render side, 4 Hz, ~64 s)
    sealed class Series
    {
        public const int N = 256;
        public readonly float[] V = new float[N];
        public readonly double[] T = new double[N];
        public int Head, Count;
        public void Add(double t, float v) { V[Head] = v; T[Head] = t; Head = (Head + 1) % N; if (Count < N) Count++; }
        public float Last => Count == 0 ? 0 : V[(Head - 1 + N) % N];
        public float At(int i) => V[(Head - Count + i + N) % N];
        public double TimeAt(int i) => T[(Head - Count + i + N) % N];
        public float Max() { var m = 0f; for (var i = 0; i < Count; i++) m = MathF.Max(m, At(i)); return m; }
    }

    const double SampleDt = 0.25, Window = 60;
    readonly Series _removed = new(), _rate = new(), _speed = new(), _tilt = new(), _px = new(), _py = new(), _pz = new();
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    readonly DateTime _started = DateTime.Now;
    double _lastSample = -1, _lastRem; Vector3 _lastPos; bool _lastHavePose;
    Vector2[] _pts = new Vector2[Series.N];

    void Sample(PanelFrameInfo info)
    {
        var now = _clock.Elapsed.TotalSeconds;
        if (_lastSample >= 0 && now - _lastSample < SampleDt) return;
        var rem = _engine.RemovedVolume;
        if (_lastSample >= 0)
        {
            var dt = now - _lastSample;
            _rate.Add(now, (float)Math.Max(0, (rem - _lastRem) / dt));
            _speed.Add(now, info.HavePose && _lastHavePose ? (float)(Vector3.Distance(info.ToolPos, _lastPos) / dt) : 0f);
        }
        _removed.Add(now, (float)rem);
        _tilt.Add(now, info.HavePose ? info.TiltDeg : 0f);
        if (info.HavePose) { _px.Add(now, info.ToolPos.X); _py.Add(now, info.ToolPos.Y); _pz.Add(now, info.ToolPos.Z); }
        _lastSample = now; _lastRem = rem; _lastPos = info.ToolPos; _lastHavePose = info.HavePose;
    }

    // ------------------------------------------------------------------ frame
    public void Draw(PanelFrameInfo info, PlaneAnalytics? pa, Vector2 display)
    {
        Layout(display);
        Sample(info);
        var bg = ImGui.GetBackgroundDrawList();
        DrawViewportChrome(bg, info, pa);
        DrawSparklines(bg);
        DrawTopBar(bg, pa, display);
        if (!Open) return;
        if (_sideFits) DrawSidebar(bg, info, pa, display);
        DrawColumn(info, pa, display);
    }

    // ------------------------------------------------------------------ text helpers
    void Font(ImFontPtr f) { if (HasFonts) ImGui.PushFont(f); }
    void PopF() { if (HasFonts) ImGui.PopFont(); }
    static void TextC(Vector4 c, string s) { ImGui.PushStyleColor(ImGuiCol.Text, c); ImGui.TextUnformatted(s); ImGui.PopStyleColor(); }

    /// <summary>Small uppercase letter-spaced section label (".label" in the web UI).</summary>
    float SpacedText(ImDrawListPtr dl, Vector2 p, string s, Vector4 col, float spacing = 0.9f)
    {
        Font(FLabel);
        var x = p.X; var c = U32(col);
        foreach (var ch in s)
        {
            var str = ch.ToString();
            dl.AddText(new Vector2(x, p.Y), c, str);
            x += ImGui.CalcTextSize(str).X + S(spacing);
        }
        PopF();
        return x - p.X;
    }

    void LabelItem(string s, Vector4? col = null)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        Font(FLabel); var h = ImGui.GetTextLineHeight(); PopF();
        var w = SpacedText(dl, p, s, col ?? Muted);
        ImGui.Dummy(new Vector2(w, h + S(4)));
    }

    Vector2 TextSize(ImFontPtr f, string s) { Font(f); var v = ImGui.CalcTextSize(s); PopF(); return v; }
    void DlText(ImDrawListPtr dl, ImFontPtr f, Vector2 p, Vector4 col, string s) { Font(f); dl.AddText(p, U32(col), s); PopF(); }

    // ------------------------------------------------------------------ top bar
    void DrawTopBar(ImDrawListPtr dl, PlaneAnalytics? pa, Vector2 display)
    {
        var y = S(TopH) * 0.5f - S(2);
        var h = TextSize(FSmall, "W").Y;
        var x = ViewMin.X + S(2);
        DlText(dl, FSmall, new Vector2(x, y - h * 0.5f), Muted, "Vive Clay");
        x += TextSize(FSmall, "Vive Clay").X + S(6);
        DlText(dl, FSmall, new Vector2(x, y - h * 0.5f), Rgb(0xC4C6CB), "/");
        x += TextSize(FSmall, "/").X + S(6);
        DlText(dl, FSemi, new Vector2(x, y - TextSize(FSemi, "W").Y * 0.5f), Text3, "Carving");

        // status pill
        var rec = pa?.Source.Recording == true;
        var conn = _engine.Connection;
        var online = conn is "connected" or "demo";
        var cutting = _rate.Count > 0 && _rate.Last > 0.5f;
        string text; Vector4 col; bool pulse = false;
        if (rec) { text = F("Recording take {0}", pa!.Source.CurrentTake); col = Bad; pulse = true; }
        else if (online) { text = cutting ? "Viewer online · carving" : "Viewer online · idle"; col = cutting ? Ok : Ok; }
        else { text = "Waiting for Grasshopper"; col = Orange; pulse = true; }
        var ts = TextSize(FSmall, text);
        var pw = ts.X + S(36); var ph = S(28);
        var right = Open ? display.X - S(Pad) : display.X - S(Pad);
        var p0 = new Vector2(right - pw, y - ph * 0.5f);
        dl.AddRectFilled(p0, p0 + new Vector2(pw, ph), U32(White), ph * 0.5f);
        dl.AddRect(p0, p0 + new Vector2(pw, ph), U32(Line), ph * 0.5f);
        var dc = p0 + new Vector2(S(15), ph * 0.5f);
        var a = pulse ? 0.55f + 0.45f * MathF.Abs(MathF.Sin((float)_clock.Elapsed.TotalSeconds * 3.1f)) : 1f;
        dl.AddCircleFilled(dc, S(6.5f), U32(col with { W = 0.16f }), 20);
        dl.AddCircleFilled(dc, S(3.5f), U32(col with { W = a }), 16);
        DlText(dl, FSmall, new Vector2(p0.X + S(26), y - ts.Y * 0.5f), Text2, text);

        // panels toggle (outlined, small) left of the pill
        ImGui.SetNextWindowPos(new Vector2(p0.X - S(118), y - S(14)));
        ImGui.SetNextWindowSize(new Vector2(S(110), S(28)));
        ImGui.Begin("##topbar", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoBringToFrontOnFocus);
        var lbl = Open ? "Hide panels" : "Show panels";
        var bw = TextSize(FSmall, lbl).X + S(20);
        ImGui.SetCursorPosX(S(110) - bw);
        if (SecondaryButton(lbl + "##tog", S(28), FSmall)) Open = !Open;
        if (ImGui.IsItemHovered()) Tip("Toggle sidebar and cards (Tab)");
        ImGui.End();
    }

    // ------------------------------------------------------------------ viewport chrome
    void DrawViewportChrome(ImDrawListPtr dl, PanelFrameInfo info, PlaneAnalytics? pa)
    {
        var r = S(13);
        var pg = U32(PageBg);
        // page-coloured corner masks turn the GL scissor rectangle into a rounded card
        void Corner(Vector2 corner, Vector2 centre, float a0)
        {
            dl.PathLineTo(corner);
            dl.PathArcTo(centre, r, a0, a0 + MathF.PI * 0.5f, 10);
            dl.PathFillConvex(pg);
        }
        Corner(ViewMin, ViewMin + new Vector2(r, r), MathF.PI);
        Corner(new Vector2(ViewMax.X, ViewMin.Y), new Vector2(ViewMax.X - r, ViewMin.Y + r), MathF.PI * 1.5f);
        Corner(ViewMax, ViewMax - new Vector2(r, r), 0);
        Corner(new Vector2(ViewMin.X, ViewMax.Y), new Vector2(ViewMin.X + r, ViewMax.Y - r), MathF.PI * 0.5f);
        dl.AddRect(ViewMin - new Vector2(0.5f), ViewMax + new Vector2(0.5f), U32(ViewLine), r, ImDrawFlags.None, MathF.Max(1, S(1)));

        // hint line
        var hint = "drag: orbit · right-drag / shift-drag: pan · wheel: zoom · double-click: fit";
        var hs = TextSize(FSmall, hint);
        DlText(dl, FSmall, new Vector2(ViewMin.X + S(16), ViewMax.Y - S(12) - hs.Y), Rgb(0x6D7179), hint);

        // monospace readout card
        var rec = pa?.Source.Recording == true;
        var st = _engine.Stats;
        var cut = TimeSpan.FromSeconds(st.CutSeconds);
        var rem = _engine.RemovedVolume; var v0 = Math.Max(1, _engine.InitialVolume);
        var lines = new List<(string k, string v, Vector4 c)>
        {
            ("removed", F("{0:N0} mm³ · {1:0.00} %", rem, 100 * rem / v0), Text),
            ("cut", F("{0}:{1:00}.{2}", (int)cut.TotalMinutes, cut.Seconds, cut.Milliseconds / 100), Text),
            ("tool", info.HavePose ? F("X {0:0.0} Y {1:0.0} Z {2:0.0}", info.ToolPos.X, info.ToolPos.Y, info.ToolPos.Z) : "no pose", info.HavePose ? Text : Muted),
            ("tilt", info.HavePose ? F("{0:0.0}°  (limit {1:0}°)", info.TiltDeg, RobotLimit) : "-", info.HavePose ? (info.TiltDeg > RobotLimit ? Bad : Text) : Muted),
            ("speed", F("{0:0} mm/s", _speed.Last), Text),
            ("rec", rec ? F("take {0} · {1} planes", pa!.Source.CurrentTake, pa.Count) : pa != null ? F("idle · {0} takes", pa.Takes.Count) : "no recorder", rec ? Bad : Muted),
        };
        Font(FMono);
        var lh = ImGui.GetTextLineHeight() * 1.32f;
        var kw = ImGui.CalcTextSize("removed ").X;
        var vw = 0f; foreach (var l in lines) vw = MathF.Max(vw, ImGui.CalcTextSize(l.v).X);
        PopF();
        var labelH = TextSize(FLabel, "C").Y;
        var pad = new Vector2(S(13), S(10));
        var size = new Vector2(MathF.Max(S(220), kw + vw) + 2 * pad.X, pad.Y * 2 + labelH + S(5) + lines.Count * lh);
        var p0 = ViewMin + new Vector2(S(14), S(14));
        dl.AddRectFilled(p0, p0 + size, U32(White with { W = 0.88f }), S(10));
        dl.AddRect(p0, p0 + size, U32(ViewLine), S(10));
        var tx = SpacedText(dl, p0 + pad, "CARVING", Muted);
        if (rec)
        {
            var blink = (Environment.TickCount64 / 500) % 2 == 0;
            var c = p0 + pad + new Vector2(tx + S(8), labelH * 0.5f);
            dl.AddCircleFilled(c, S(3.5f), U32(blink ? Bad : Bad with { W = 0.35f }), 12);
            SpacedText(dl, new Vector2(c.X + S(8), p0.Y + pad.Y), "REC", Bad);
        }
        var y = p0.Y + pad.Y + labelH + S(5);
        Font(FMono);
        foreach (var (k, v, c) in lines)
        {
            dl.AddText(new Vector2(p0.X + pad.X, y), U32(Muted), k);
            dl.AddText(new Vector2(p0.X + pad.X + kw, y), U32(c), v);
            y += lh;
        }
        PopF();
    }

    // ------------------------------------------------------------------ sparklines
    void DrawSparklines(ImDrawListPtr dl)
    {
        var y0 = ViewMax.Y + S(Gap);
        var h = S(SparkH);
        var gap = S(Gap);
        var w = (ViewMax.X - ViewMin.X - 3 * gap) / 4f;
        var x = ViewMin.X;
        Spark(dl, new Vector2(x, y0), new Vector2(w, h), "Removed", F("{0:N0} mm³", _removed.Last), _removed, -1); x += w + gap;
        SparkXyz(dl, new Vector2(x, y0), new Vector2(w, h)); x += w + gap;
        Spark(dl, new Vector2(x, y0), new Vector2(w, h), "Tool speed", F("{0:0} mm/s", _speed.Last), _speed, -1); x += w + gap;
        Spark(dl, new Vector2(x, y0), new Vector2(w, h), "Tool tilt", F("{0:0.0}°", _tilt.Last), _tilt, RobotLimit);
    }

    void Spark(ImDrawListPtr dl, Vector2 p, Vector2 size, string title, string value, Series s, float limit)
    {
        var r = S(13);
        dl.AddRectFilled(p, p + size, U32(White), r);
        dl.AddRect(p, p + size, U32(CardLine), r);
        var padX = S(12);
        var th = TextSize(FSmall, title);
        DlText(dl, FSmall, p + new Vector2(padX, S(9)), Muted, title);
        var vs = TextSize(FMono, value);
        var stacked = th.X + vs.X + S(12) > size.X - 2 * padX;      // narrow card: value under the title
        var vy = stacked ? p.Y + S(9) + th.Y + S(1) : p.Y + S(9) + (th.Y - vs.Y) * 0.5f;
        DlText(dl, FMono, new Vector2(stacked ? p.X + padX : p.X + size.X - padX - vs.X, vy), Text, value);

        var c0 = new Vector2(p.X + padX, (stacked ? vy + vs.Y : p.Y + S(9) + th.Y) + S(6));
        var c1 = new Vector2(p.X + size.X - padX, p.Y + size.Y - S(10));
        var cw = c1.X - c0.X; var ch = c1.Y - c0.Y;
        if (ch < 4) return;
        var max = MathF.Max(s.Max(), limit > 0 ? limit * 1.25f : 0);
        max = NiceCeil(MathF.Max(max, 1e-3f));
        var grid = U32(CardLine);
        dl.AddLine(new Vector2(c0.X, c0.Y), new Vector2(c1.X, c0.Y), grid);
        dl.AddLine(new Vector2(c0.X, c0.Y + ch * 0.5f), new Vector2(c1.X, c0.Y + ch * 0.5f), grid);
        dl.AddLine(new Vector2(c0.X, c1.Y), new Vector2(c1.X, c1.Y), U32(Field));
        var lbl = FormatAxis(max);
        DlText(dl, FLabel, new Vector2(c0.X + S(2), c0.Y + S(1)), Muted with { W = 0.8f }, lbl);

        var now = _clock.Elapsed.TotalSeconds;
        var span = Math.Clamp(now, 10, Window);           // fills the card from the start, then scrolls over the last 60 s
        var n = 0;
        for (var i = 0; i < s.Count; i++)
        {
            var t = s.TimeAt(i);
            if (now - t > span) continue;
            var u = (float)(1 - (now - t) / span);
            _pts[n++] = new Vector2(c0.X + cw * u, c1.Y - ch * Math.Clamp(s.At(i) / max, 0f, 1f));
        }
        if (n >= 2)
        {
            var band = U32(Band with { W = 0.22f });
            for (var i = 1; i < n; i++)
                dl.AddQuadFilled(_pts[i - 1], _pts[i], new Vector2(_pts[i].X, c1.Y), new Vector2(_pts[i - 1].X, c1.Y), band);
            dl.AddPolyline(ref _pts[0], n, U32(Blue), ImDrawFlags.None, MathF.Max(1, S(1.5f)));
            dl.AddCircleFilled(_pts[n - 1], S(2.6f), U32(Blue), 10);
        }
        if (limit > 0)
        {
            var ly = c1.Y - ch * Math.Clamp(limit / max, 0f, 1f);
            for (var x = c0.X; x < c1.X; x += S(9)) dl.AddLine(new Vector2(x, ly), new Vector2(MathF.Min(x + S(5), c1.X), ly), U32(Bad), MathF.Max(1, S(1.2f)));
            var ls = F("limit {0:0}°", limit);
            var lsz = TextSize(FLabel, ls);
            DlText(dl, FLabel, new Vector2(c1.X - lsz.X - S(2), ly - lsz.Y - S(1)), Bad, ls);
        }
    }

    /// <summary>Tool position card: X, Y and Z over the last minute on one auto-ranged chart.</summary>
    void SparkXyz(ImDrawListPtr dl, Vector2 p, Vector2 size)
    {
        var r = S(13);
        dl.AddRectFilled(p, p + size, U32(White), r);
        dl.AddRect(p, p + size, U32(CardLine), r);
        var padX = S(12);
        DlText(dl, FSmall, p + new Vector2(padX, S(9)), Muted, "Position");
        var th = TextSize(FSmall, "Position");
        (Series s, Vector4 c, string n)[] axes = { (_px, Bad, "X"), (_py, Ok, "Y"), (_pz, Blue, "Z") };

        // legend with current values: right of the title if it fits, otherwise on its own line under it
        var labels = axes.Select(a => a.s.Count > 0 ? F("{0} {1:0.0}", a.n, a.s.Last) : a.n + " -").ToArray();
        var sizes = labels.Select(l => TextSize(FMono, l)).ToArray();
        var legendW = sizes.Sum(z => z.X) + S(8) * (labels.Length - 1);
        var stacked = th.X + S(12) + legendW > size.X - 2 * padX;
        var lx = stacked ? p.X + padX : p.X + size.X - padX - legendW;
        var ly = stacked ? p.Y + S(9) + th.Y + S(1) : p.Y + S(9) + (th.Y - sizes[0].Y) * 0.5f;
        for (var a = 0; a < labels.Length; a++)
        {
            DlText(dl, FMono, new Vector2(lx, ly), axes[a].c, labels[a]);
            lx += sizes[a].X + S(8);
        }
        var top = stacked ? ly + sizes[0].Y : p.Y + S(9) + th.Y;

        var c0 = new Vector2(p.X + padX, top + S(6));
        var c1 = new Vector2(p.X + size.X - padX, p.Y + size.Y - S(10));
        var cw = c1.X - c0.X; var ch = c1.Y - c0.Y;
        if (ch < 4) return;
        var now = _clock.Elapsed.TotalSeconds;
        var span = Math.Clamp(now, 10, Window);

        // shared range over the visible window, so the three lines are comparable in mm
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var (s, _, _) in axes)
            for (var i = 0; i < s.Count; i++)
                if (now - s.TimeAt(i) <= span) { lo = MathF.Min(lo, s.At(i)); hi = MathF.Max(hi, s.At(i)); }
        var grid = U32(CardLine);
        dl.AddLine(new Vector2(c0.X, c0.Y), new Vector2(c1.X, c0.Y), grid);
        dl.AddLine(new Vector2(c0.X, c0.Y + ch * 0.5f), new Vector2(c1.X, c0.Y + ch * 0.5f), grid);
        dl.AddLine(new Vector2(c0.X, c1.Y), new Vector2(c1.X, c1.Y), U32(Field));
        if (lo > hi) return;
        if (hi - lo < 10) { var m = 0.5f * (hi + lo); lo = m - 5; hi = m + 5; }   // at least a 10 mm span so a still tool is a flat line
        DlText(dl, FLabel, new Vector2(c0.X + S(2), c0.Y + S(1)), Muted with { W = 0.8f }, F("{0:0}", hi));
        var lbl = F("{0:0}", lo);
        DlText(dl, FLabel, new Vector2(c0.X + S(2), c1.Y - TextSize(FLabel, lbl).Y - S(1)), Muted with { W = 0.8f }, lbl);

        foreach (var (s, c, _) in axes)
        {
            var n = 0;
            for (var i = 0; i < s.Count; i++)
            {
                var t = s.TimeAt(i);
                if (now - t > span) continue;
                _pts[n++] = new Vector2(c0.X + cw * (float)(1 - (now - t) / span), c1.Y - ch * (s.At(i) - lo) / (hi - lo));
            }
            if (n >= 2)
            {
                dl.AddPolyline(ref _pts[0], n, U32(c), ImDrawFlags.None, MathF.Max(1, S(1.5f)));
                dl.AddCircleFilled(_pts[n - 1], S(2.4f), U32(c), 10);
            }
        }
    }

    static float NiceCeil(float v)
    {
        var e = MathF.Pow(10, MathF.Floor(MathF.Log10(v)));
        foreach (var m in new[] { 1f, 2f, 2.5f, 5f, 10f }) if (v <= m * e * 1.0001f) return m * e;
        return 10 * e;
    }

    static string FormatAxis(float v) => v >= 10000 ? F("{0:0.#}k", v / 1000) : v >= 10 ? F("{0:0}", v) : F("{0:0.##}", v);

    // ------------------------------------------------------------------ sidebar
    int _scrollTo = -1;

    void DrawSidebar(ImDrawListPtr bg, PanelFrameInfo info, PlaneAnalytics? pa, Vector2 display)
    {
        var w = SideWidth;
        bg.AddRectFilled(Vector2.Zero, new Vector2(w, display.Y), U32(White with { W = 0.8f }));
        bg.AddLine(new Vector2(w, 0), new Vector2(w, display.Y), U32(Rgb(0xE9EAED)));

        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(new Vector2(w, display.Y));
        ImGui.Begin("##sidebar", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoBringToFrontOnFocus);
        var dl = ImGui.GetWindowDrawList();
        var x0 = S(15); var inner = w - 2 * x0;

        // brand
        var bp = new Vector2(S(26), S(25));
        var ic = S(34);
        dl.AddRectFilledMultiColor(bp, bp + new Vector2(ic, ic), U32(Rgb(0x30343C)), U32(Rgb(0x23262C)), U32(Rgb(0x15171C)), U32(Rgb(0x23262C)));
        // round the gradient square with page-white corner masks
        var rr = S(11); var wc = U32(White);
        void Mask(Vector2 corner, Vector2 centre, float a0) { dl.PathLineTo(corner); dl.PathArcTo(centre, rr, a0, a0 + MathF.PI * 0.5f, 8); dl.PathFillConvex(wc); }
        Mask(bp, bp + new Vector2(rr, rr), MathF.PI);
        Mask(bp + new Vector2(ic, 0), bp + new Vector2(ic - rr, rr), MathF.PI * 1.5f);
        Mask(bp + new Vector2(ic, ic), bp + new Vector2(ic - rr, ic - rr), 0);
        Mask(bp + new Vector2(0, ic), bp + new Vector2(rr, ic - rr), MathF.PI * 0.5f);
        CubeGlyph(dl, bp + new Vector2(ic, ic) * 0.5f, S(8.5f));
        DlText(dl, FBrand, bp + new Vector2(ic + S(11), S(0)), Text, "Vive Clay");
        DlText(dl, FLabel, bp + new Vector2(ic + S(11), S(20)), Muted, "Carving viewer");

        ImGui.SetCursorScreenPos(new Vector2(x0, S(25) + ic + S(30)));
        ImGui.BeginGroup();
        ImGui.SetCursorPosX(x0 + S(11)); LabelItem("WORKSPACE");
        ImGui.SetCursorPosX(x0);
        string[] nav = { "Recording", "Angularity", "Layers", "Job" };
        for (var i = 0; i < nav.Length; i++)
        {
            ImGui.SetCursorPosX(x0);
            if (NavItem(nav[i], _navActive == i || (_navActive < 0 && i == 0), inner, null)) { _scrollTo = i; _navActive = i; }
        }
        ImGui.Dummy(new Vector2(1, S(14)));
        ImGui.SetCursorPosX(x0 + S(11)); LabelItem("TAKES");
        if (pa == null || pa.Takes.Count == 0) { ImGui.SetCursorPosX(x0 + S(11)); Font(FSmall); TextC(Muted, "No recorded takes yet"); PopF(); }
        else
        {
            var listMaxY = display.Y - S(170);
            foreach (var t in pa.Takes)
            {
                if (ImGui.GetCursorScreenPos().Y > listMaxY) { ImGui.SetCursorPosX(x0 + S(11)); Font(FSmall); TextC(Muted, F("+ more takes")); PopF(); break; }
                ImGui.SetCursorPosX(x0);
                var live = pa.Source.Recording && pa.Source.CurrentTake == t.Take;
                if (NavItem(F("Take {0}{1}##side{0}", t.Take, live ? "  · rec" : ""), SelectedTake == t.Take, inner, TakeColor(t.Take)))
                { SelectedTake = SelectedTake == t.Take ? -1 : t.Take; OverlayDirty = true; }
            }
        }
        ImGui.EndGroup();

        // viewer card at the bottom (dark "engine" card)
        var ch = S(112);
        var cp = new Vector2(S(17), display.Y - ch - S(25));
        var cw = w - S(34);
        dl.AddRectFilled(cp, cp + new Vector2(cw, ch), U32(Rgb(0x2C3039)), S(12));
        SpacedText(dl, cp + new Vector2(S(15), S(15)), "VIEWER", Rgb(0xC0C3CB), 0.7f);
        var ver = Assembly.GetEntryAssembly()?.GetName().Version;
        DlText(dl, FMono, cp + new Vector2(S(15), S(36)), White, ver == null ? "dev" : F("{0}.{1}.{2}", ver.Major, ver.Minor, ver.Build));
        var up = DateTime.Now - _started;
        DlText(dl, FSmall, cp + new Vector2(S(15), S(60)), Rgb(0xC0C3CB), up.TotalHours >= 1 ? F("Up {0}h {1}m", (int)up.TotalHours, up.Minutes) : F("Up {0}m {1:00}s", (int)up.TotalMinutes, up.Seconds));
        var stt = _engine.Stats;
        var avg = stt.Sweeps > 0 ? stt.CarveMsTotal / stt.Sweeps : 0;
        DlText(dl, FSmall, cp + new Vector2(S(15), S(80)), Rgb(0xC0C3CB), F("{0:0} fps · carve {1:0.00} ms", info.Fps, avg));
        ImGui.End();
    }
    int _navActive = -1;

    void CubeGlyph(ImDrawListPtr dl, Vector2 c, float r)
    {
        var col = U32(White); var th = MathF.Max(1, S(1.4f));
        Vector2 P(float deg, float rad) => c + new Vector2(MathF.Cos(deg * MathF.PI / 180), MathF.Sin(deg * MathF.PI / 180)) * rad;
        var hex = new Vector2[6];
        for (var i = 0; i < 6; i++) hex[i] = P(-90 + 60 * i, r);
        for (var i = 0; i < 6; i++) dl.AddLine(hex[i], hex[(i + 1) % 6], col, th);
        dl.AddLine(c, hex[0], col, th); dl.AddLine(c, hex[2], col, th); dl.AddLine(c, hex[4], col, th);
    }

    bool NavItem(string label, bool active, float w, Vector4? dot)
    {
        var p = ImGui.GetCursorScreenPos();
        var h = S(dot == null ? 39 : 34);
        var clicked = ImGui.InvisibleButton("##nav" + label, new Vector2(w, h));
        var hov = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        if (active) dl.AddRectFilled(p, p + new Vector2(w, h), U32(Rgb(0xEDEFF2)), S(9));
        else if (hov) dl.AddRectFilled(p, p + new Vector2(w, h), U32(Rgb(0xF4F5F7)), S(9));
        var text = label.Split("##")[0];
        var f = active ? FSemi : (ImFontPtr?)null;
        var ts = f != null ? TextSize(FSemi, text) : ImGui.CalcTextSize(text);
        var x = p.X + S(11);
        if (dot is { } d) { dl.AddCircleFilled(new Vector2(x + S(4), p.Y + h * 0.5f), S(4), U32(d), 12); x += S(19); }
        if (f != null) DlText(dl, FSemi, new Vector2(x, p.Y + (h - ts.Y) * 0.5f), Text, text);
        else dl.AddText(new Vector2(x, p.Y + (h - ts.Y) * 0.5f), U32(active ? Text : Text2), text);
        return clicked;
    }

    // ------------------------------------------------------------------ right column of cards
    Vector2 _c0; float _cw;

    void DrawColumn(PanelFrameInfo info, PlaneAnalytics? pa, Vector2 display)
    {
        var x = display.X - ColWidth - S(Pad);
        ImGui.SetNextWindowPos(new Vector2(x, S(TopH)));
        ImGui.SetNextWindowSize(new Vector2(ColWidth + S(Pad) - S(4), display.Y - S(TopH)));
        ImGui.Begin("##column", ImGuiWindowFlags.NoDecoration & ~ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoCollapse);
        _cw = ColWidth;
        CardBegin(0, "RECORDING", RecBadge(pa)); RecordingCard(pa); CardEnd();
        CardBegin(1, "ANGULARITY", null); AngularityCard(pa); CardEnd();
        CardBegin(2, "LAYERS", null); LayersCard(pa); CardEnd();
        CardBegin(3, "JOB", null); JobCard(info); CardEnd();
        ImGui.Dummy(new Vector2(1, S(4)));
        _scrollTo = -1;
        ImGui.End();
    }

    (string text, Vector4 col)? RecBadge(PlaneAnalytics? pa)
    {
        if (pa == null) return null;
        if (pa.Source.Recording) return (F("REC {0}", pa.Source.CurrentTake), Bad);
        return (pa.Takes.Count.ToString(Inv), Ok);
    }

    float Inner => _cw - S(36);

    void CardBegin(int index, string label, (string text, Vector4 col)? badge)
    {
        var dl = ImGui.GetWindowDrawList();
        dl.ChannelsSplit(2); dl.ChannelsSetCurrent(1);
        _c0 = ImGui.GetCursorScreenPos();
        if (_scrollTo == index) ImGui.SetScrollHereY(0);
        ImGui.SetCursorScreenPos(_c0 + new Vector2(S(18), S(16)));
        ImGui.BeginGroup();
        var p = ImGui.GetCursorScreenPos();
        LabelItem(label);
        if (badge is { } b)
        {
            var lw = ImGui.GetItemRectSize().X;
            var ts = TextSize(FMono, b.text);
            var bp = p + new Vector2(lw + S(6), S(-1));
            var bs = new Vector2(ts.X + S(12), ts.Y + S(2));
            dl.AddRectFilled(bp, bp + bs, U32(b.col with { W = 0.12f }), bs.Y * 0.5f);
            DlText(dl, FMono, bp + new Vector2(S(6), S(1)), b.col, b.text);
        }
        ImGui.Dummy(new Vector2(Inner, S(2)));
    }

    void CardEnd()
    {
        ImGui.EndGroup();
        var dl = ImGui.GetWindowDrawList();
        var y1 = ImGui.GetItemRectMax().Y + S(14);
        dl.ChannelsSetCurrent(0);
        var p1 = new Vector2(_c0.X + _cw, y1);
        dl.AddRectFilled(_c0, p1, U32(White), S(13));
        dl.AddRect(_c0, p1, U32(CardLine), S(13));
        dl.ChannelsMerge();
        ImGui.SetCursorScreenPos(new Vector2(_c0.X, y1 + S(12)));
        ImGui.Dummy(new Vector2(_cw, 0));
    }

    // ---- widgets
    bool PrimaryButton(string label, float h)
    {
        Font(FSemi);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, S(8));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(S(14), (h - ImGui.GetTextLineHeight()) * 0.5f));
        ImGui.PushStyleColor(ImGuiCol.Text, White);
        var r = ImGui.Button(label);
        ImGui.PopStyleColor(); ImGui.PopStyleVar(2); PopF();
        return r;
    }

    bool SecondaryButton(string label, float h, ImFontPtr? font = null)
    {
        Font(font ?? FSemi);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, S(8));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, MathF.Max(1, S(1)));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(S(10), (h - ImGui.GetTextLineHeight()) * 0.5f));
        ImGui.PushStyleColor(ImGuiCol.Button, White);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Rgb(0xF6F7F9));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Rgb(0xEEF0F3));
        ImGui.PushStyleColor(ImGuiCol.Border, Field);
        ImGui.PushStyleColor(ImGuiCol.Text, Dark);
        var r = ImGui.Button(label);
        ImGui.PopStyleColor(5); ImGui.PopStyleVar(3); PopF();
        return r;
    }

    bool Check(string label, ref bool v, Vector4? dot = null, float indent = 0)
    {
        var p = ImGui.GetCursorScreenPos() + new Vector2(indent, 0);
        var h = S(25);
        if (indent > 0) ImGui.SetCursorScreenPos(p);
        var clicked = ImGui.InvisibleButton("##chk" + label, new Vector2(Inner - indent, h));
        if (clicked) v = !v;
        var dl = ImGui.GetWindowDrawList();
        var bs = S(15);
        var b0 = new Vector2(p.X + S(1), p.Y + (h - bs) * 0.5f);
        if (v)
        {
            dl.AddRectFilled(b0, b0 + new Vector2(bs, bs), U32(ImGui.IsItemHovered() ? Rgb(0x1B5FD0) : Blue), S(3.5f));
            var th = MathF.Max(1.5f, S(2f));
            dl.AddLine(b0 + new Vector2(bs * 0.24f, bs * 0.52f), b0 + new Vector2(bs * 0.43f, bs * 0.71f), U32(White), th);
            dl.AddLine(b0 + new Vector2(bs * 0.43f, bs * 0.71f), b0 + new Vector2(bs * 0.77f, bs * 0.30f), U32(White), th);
        }
        else
        {
            dl.AddRectFilled(b0, b0 + new Vector2(bs, bs), U32(White), S(3.5f));
            dl.AddRect(b0, b0 + new Vector2(bs, bs), U32(ImGui.IsItemHovered() ? Rgb(0x9DA2AB) : Rgb(0xB9BDC4)), S(3.5f), ImDrawFlags.None, MathF.Max(1, S(1.2f)));
        }
        var x = b0.X + bs + S(10);
        if (dot is { } d) { dl.AddCircleFilled(new Vector2(x + S(4), p.Y + h * 0.5f), S(4), U32(d), 12); x += S(14); }
        var text = label.Split("##")[0];
        var ts = ImGui.CalcTextSize(text);
        dl.AddText(new Vector2(x, p.Y + (h - ts.Y) * 0.5f), U32(Text), text);
        return clicked;
    }

    bool Slider(string id, ref float v, float min, float max, float w)
    {
        var p = ImGui.GetCursorScreenPos();
        var h = S(22);
        ImGui.InvisibleButton(id, new Vector2(w, h));
        var r = S(8);
        var changed = false;
        if (ImGui.IsItemActive())
        {
            var u = Math.Clamp((ImGui.GetIO().MousePos.X - p.X - r) / (w - 2 * r), 0f, 1f);
            var nv = min + u * (max - min);
            if (MathF.Abs(nv - v) > 1e-4f) { v = nv; changed = true; }
        }
        var dl = ImGui.GetWindowDrawList();
        var cy = p.Y + h * 0.5f;
        var uu = Math.Clamp((v - min) / (max - min), 0f, 1f);
        var gx = p.X + r + uu * (w - 2 * r);
        var th = S(5);
        dl.AddRectFilled(new Vector2(p.X + S(2), cy - th * 0.5f), new Vector2(p.X + w - S(2), cy + th * 0.5f), U32(Rgb(0xE3E6EB)), th);
        dl.AddRectFilled(new Vector2(p.X + S(2), cy - th * 0.5f), new Vector2(gx, cy + th * 0.5f), U32(Blue), th);
        if (ImGui.IsItemHovered() || ImGui.IsItemActive()) dl.AddCircleFilled(new Vector2(gx, cy), r + S(4), U32(Blue with { W = 0.15f }), 24);
        dl.AddCircleFilled(new Vector2(gx, cy), r, U32(Blue), 24);
        return changed;
    }

    void Kv(string k, string v, Vector4? col = null)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0); TextC(Muted, k);
        ImGui.TableSetColumnIndex(1);
        Font(FMono);
        var w = ImGui.CalcTextSize(v).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, ImGui.GetContentRegionAvail().X - w));
        TextC(col ?? Text, v);
        PopF();
    }

    // ---- cards
    void RecordingCard(PlaneAnalytics? pa)
    {
        if (PrimaryButton("Frame stock", S(34))) FrameStock?.Invoke();
        if (ImGui.IsItemHovered()) Tip("Frame the stock (F, or double-click the view)");
        ImGui.SameLine(0, S(8));
        if (SecondaryButton("Screenshot", S(34))) Screenshot?.Invoke();
        if (ImGui.IsItemHovered()) Tip("Save a PNG to Pictures (P)");
        ImGui.Dummy(new Vector2(1, S(2)));

        Font(FSmall);
        if (pa == null) { TextC(Muted, "Waiting for the Take Recorder (RecordedPlanes)."); PopF(); return; }
        TextC(Muted, pa.Source.Recording ? F("Recording take {0} · {1} planes so far", pa.Source.CurrentTake, pa.Count)
                                          : F("Idle · {0} planes in {1} takes · click a take to highlight", pa.Count, pa.Takes.Count));
        PopF();
        if (pa.Takes.Count == 0) return;
        ImGui.Dummy(new Vector2(1, S(2)));
        var dl = ImGui.GetWindowDrawList();
        foreach (var t in pa.Takes)
        {
            var p = ImGui.GetCursorScreenPos();
            var h = S(42);
            var sel = SelectedTake == t.Take;
            var live = pa.Source.Recording && pa.Source.CurrentTake == t.Take;
            if (ImGui.InvisibleButton(F("##take{0}", t.Take), new Vector2(Inner, h))) { SelectedTake = sel ? -1 : t.Take; OverlayDirty = true; }
            var hov = ImGui.IsItemHovered();
            if (hov)
                Tip(F("Take {0}: {1} planes over {2:0.0} s, path {3:0} mm\nspeed mean {4:0} / max {5:0} mm/s\ntilt min {6:0.0}° mean {7:0.0}° max {8:0.0}°\n{9} planes above the {10:0}° robot limit\nClick to highlight in 3D",
                    t.Take, t.Count, t.Duration, t.PathMm, t.MeanSpeed, t.MaxSpeed, t.MinTilt, t.MeanTilt, t.MaxTilt, pa.CountAbove(t, RobotLimit), RobotLimit));
            if (sel) dl.AddRectFilled(p, p + new Vector2(Inner, h), U32(Rgb(0xEEF3FD)), S(8));
            else if (hov) dl.AddRectFilled(p, p + new Vector2(Inner, h), U32(Rgb(0xF6F7F9)), S(8));
            var x = p.X + S(9);
            dl.AddCircleFilled(new Vector2(x + S(4), p.Y + S(13)), S(4), U32(TakeColor(t.Take)), 12);
            DlText(dl, FSemi, new Vector2(x + S(15), p.Y + S(4)), Text, F("Take {0}", t.Take));
            if (live)
            {
                var tw = TextSize(FSemi, F("Take {0}", t.Take)).X;
                var bp = new Vector2(x + S(15) + tw + S(8), p.Y + S(5));
                var ls = TextSize(FLabel, "REC");
                dl.AddRectFilled(bp, bp + new Vector2(ls.X + S(10), ls.Y + S(2)), U32(Bad), S(5));
                DlText(dl, FLabel, bp + new Vector2(S(5), S(1)), White, "REC");
            }
            var right = F("{0} planes · {1:0.0} s", t.Count, t.Duration);
            var rs = TextSize(FMono, right);
            DlText(dl, FMono, new Vector2(p.X + Inner - S(9) - rs.X, p.Y + S(6)), Text3, right);
            var sub = F("{0:0} mm · v {1:0}/{2:0} mm/s · tilt ", t.PathMm, t.MeanSpeed, t.MaxSpeed);
            var ss = TextSize(FSmall, sub);
            DlText(dl, FSmall, new Vector2(x + S(15), p.Y + S(22)), Muted, sub);
            DlText(dl, FSmall, new Vector2(x + S(15) + ss.X, p.Y + S(22)), Muted, F("{0:0}/{1:0}/", t.MinTilt, t.MeanTilt));
            var ms = TextSize(FSmall, F("{0:0}/{1:0}/", t.MinTilt, t.MeanTilt));
            DlText(dl, FSemi, new Vector2(x + S(15) + ss.X + ms.X, p.Y + S(22) - S(1)), t.MaxTilt > RobotLimit ? Bad : Text3, F("{0:0}°", t.MaxTilt));
        }
    }

    void AngularityCard(PlaneAnalytics? pa)
    {
        ImGui.AlignTextToFramePadding();
        var lw = S(84);
        var p = ImGui.GetCursorScreenPos();
        var lh = ImGui.GetTextLineHeight();
        ImGui.Dummy(new Vector2(1, 0));
        ImGui.SetCursorScreenPos(p + new Vector2(0, (S(22) - lh) * 0.5f));
        ImGui.TextUnformatted("Robot limit");
        ImGui.SetCursorScreenPos(p + new Vector2(lw, 0));
        var vw = TextSize(FMono, "90°").X + S(6);
        if (Slider("##limit", ref RobotLimit, 5f, 85f, Inner - lw - vw - S(6))) { RobotLimit = MathF.Round(RobotLimit); OverlayDirty = true; }
        if (ImGui.IsItemHovered()) Tip("Maximum tool tilt from vertical the robot cell can reproduce.\nPlanes above it are drawn red in 3D and in the plots.");
        ImGui.SameLine(0, S(6));
        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, p.Y + (S(22) - TextSize(FMono, "0").Y) * 0.5f));
        Font(FMono); ImGui.TextUnformatted(F("{0:0}°", RobotLimit)); PopF();
        ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + S(26)));

        if (pa == null || pa.Count == 0) { Font(FSmall); TextC(Muted, "No recorded planes yet."); PopF(); return; }
        var above = pa.CountAbove(RobotLimit);
        // big number like the web UI's .big-number
        var pct = F("{0:0.0}", 100.0 * above / pa.Count);
        var dl = ImGui.GetWindowDrawList();
        var bp = ImGui.GetCursorScreenPos();
        var bs = TextSize(FBig, pct);
        DlText(dl, FBig, bp, above > 0 ? Bad : Ok, pct);
        var ss = TextSize(FSmall, "%");
        DlText(dl, FSmall, bp + new Vector2(bs.X + S(5), bs.Y - ss.Y - S(4)), Muted, F("% above {0:0}° · {1} of {2} planes", RobotLimit, above, pa.Count));
        ImGui.Dummy(new Vector2(Inner, bs.Y + S(2)));
        Histogram(pa, Inner, S(112));
        Legend();
        // polar plot with the tilt statistics beside it
        var ps = S(138);
        var p0 = ImGui.GetCursorScreenPos();
        Polar(pa, ps);
        var x = p0.X + ps + S(14);
        var y = p0.Y + S(18);
        var rowH = TextSize(FMono, "0").Y + S(9);
        void Stat(string k, float v, Vector4 col)
        {
            SpacedText(dl, new Vector2(x, y), k, Muted);
            var vs = F("{0:0.0}°", v);
            DlText(dl, FMono, new Vector2(p0.X + Inner - TextSize(FMono, vs).X, y - S(1)), col, vs);
            y += rowH;
        }
        Stat("MEAN", pa.Mean, Text3);
        Stat("P95", pa.P95, pa.P95 > RobotLimit ? Bad : Text3);
        Stat("MAX", pa.Max, pa.Max > RobotLimit ? Bad : Text3);
        Stat("LIMIT", RobotLimit, Bad);
        dl.AddLine(new Vector2(x, y), new Vector2(p0.X + Inner, y), U32(CardLine));
        y += S(8);
        DlText(dl, FSmall, new Vector2(x, y), Muted, "tool axes, top view");
        DlText(dl, FSmall, new Vector2(x, y + TextSize(FSmall, "W").Y + S(1)), Muted, "centre = vertical");
    }

    void Histogram(PlaneAnalytics pa, float w, float h)
    {
        var dl = ImGui.GetWindowDrawList();
        var p0 = ImGui.GetCursorScreenPos();
        Font(FLabel);
        float padL = S(24), padB = S(18), padT = S(6), padR = S(6);
        var plotMin = p0 + new Vector2(padL, padT);
        var plotMax = p0 + new Vector2(w - padR, h - padB);
        var pw = plotMax.X - plotMin.X; var ph = plotMax.Y - plotMin.Y;
        dl.AddRectFilled(p0, p0 + new Vector2(w, h), U32(Rgb(0xFAFBFC)), S(8));

        var maxCount = Math.Max(1, pa.Histogram.Max());
        var grid = U32(CardLine);
        var dim = U32(Muted);
        for (var g = 0; g <= 2; g++)
        {
            var y = plotMax.Y - ph * g / 2f;
            dl.AddLine(new Vector2(plotMin.X, y), new Vector2(plotMax.X, y), grid);
            var label = (maxCount * g / 2).ToString(Inv);
            dl.AddText(new Vector2(plotMin.X - S(5) - ImGui.CalcTextSize(label).X, y - ImGui.GetTextLineHeight() * 0.5f), dim, label);
        }
        float X(float deg) => plotMin.X + pw * Math.Clamp(deg, 0, 90) / 90f;
        var bw = pw / PlaneAnalytics.Bins;
        var lx = X(RobotLimit);
        dl.AddRectFilled(new Vector2(lx, plotMin.Y), plotMax, U32(Bad with { W = 0.06f }));
        for (var b = 0; b < PlaneAnalytics.Bins; b++)
        {
            var c = pa.Histogram[b];
            if (c == 0) continue;
            var x0 = plotMin.X + b * bw + S(1); var x1 = x0 + bw - S(2);
            var y0 = plotMax.Y - MathF.Max(S(2), ph * c / maxCount);
            var col = PlaneAnalytics.TiltColor(b * 5 + 2.5f, RobotLimit);
            dl.AddRectFilled(new Vector2(x0, y0), new Vector2(x1, plotMax.Y), U32(new Vector4(col, 0.9f)), S(2), ImDrawFlags.RoundCornersTop);
        }
        dl.AddLine(new Vector2(plotMin.X, plotMax.Y), new Vector2(plotMax.X, plotMax.Y), U32(Field));
        for (var d = 0; d <= 90; d += 30)
        {
            var x = X(d);
            dl.AddLine(new Vector2(x, plotMax.Y), new Vector2(x, plotMax.Y + S(3)), dim);
            var s = d + "°";
            dl.AddText(new Vector2(Math.Clamp(x - ImGui.CalcTextSize(s).X * 0.5f, plotMin.X - S(4), plotMax.X - ImGui.CalcTextSize(s).X + S(4)), plotMax.Y + S(4)), dim, s);
        }
        Dashed(dl, lx, plotMin.Y, plotMax.Y, U32(Bad), MathF.Max(1, S(1.5f)), S(5), S(4));
        void Marker(float deg, Vector4 col, string tag, int row)
        {
            var x = X(deg);
            dl.AddLine(new Vector2(x, plotMin.Y), new Vector2(x, plotMax.Y), U32(col), MathF.Max(1, S(1.5f)));
            var ts = ImGui.CalcTextSize(tag);
            var tx = x + S(4) + ts.X > plotMax.X ? x - S(4) - ts.X : x + S(4);
            var ty = plotMin.Y + row * (ts.Y + S(1));
            dl.AddRectFilled(new Vector2(tx - S(3), ty), new Vector2(tx + ts.X + S(3), ty + ts.Y), U32(White with { W = 0.92f }), S(3));
            dl.AddText(new Vector2(tx, ty), U32(col), tag);
        }
        Marker(pa.Mean, Text3, "mean", 0);
        Marker(pa.P95, Blue, "p95", 1);
        Marker(pa.Max, Orange, "max", 2);
        PopF();
        ImGui.Dummy(new Vector2(w, h));
        if (ImGui.IsItemHovered())
        {
            var b = (int)((ImGui.GetMousePos().X - plotMin.X) / bw);
            if (b >= 0 && b < PlaneAnalytics.Bins) Tip(F("{0}-{1}° tilt: {2} planes", b * 5, b * 5 + 5, pa.Histogram[b]));
            else Tip("Tool tilt from world Z, 5° bins. Dashed red = robot limit.");
        }
    }

    static void Dashed(ImDrawListPtr dl, float x, float y0, float y1, uint col, float th, float dash, float gap)
    {
        for (var y = y0; y < y1; y += dash + gap) dl.AddLine(new Vector2(x, y), new Vector2(x, Math.Min(y + dash, y1)), col, th);
    }

    /// <summary>Stereographic projection of the (upward) tool axes: centre = vertical, rim = horizontal, X right, Y up.</summary>
    void Polar(PlaneAnalytics pa, float size)
    {
        var dl = ImGui.GetWindowDrawList();
        var p0 = ImGui.GetCursorScreenPos();
        Font(FLabel);
        var c = p0 + new Vector2(size * 0.5f, size * 0.5f);
        var r = size * 0.5f - S(14);
        float R(float deg) => r * MathF.Tan(deg * MathF.PI / 360f);       // stereographic, 90 deg -> r
        var ring = U32(Rgb(0x181A1F, 0.08f));
        var dim = U32(Muted);
        dl.AddCircleFilled(c, r, U32(Rgb(0xFAFBFC)), 64);
        dl.AddCircleFilled(c, R(RobotLimit), U32(Ok with { W = 0.07f }), 64);
        foreach (var d in new[] { 15, 30, 45, 60, 75, 90 })
            dl.AddCircle(c, R(d), d == 90 ? U32(Field) : ring, 64);
        dl.AddLine(c - new Vector2(r, 0), c + new Vector2(r, 0), ring);
        dl.AddLine(c - new Vector2(0, r), c + new Vector2(0, r), ring);
        dl.AddCircle(c, R(RobotLimit), U32(Bad with { W = 0.85f }), 64, MathF.Max(1, S(1.5f)));
        dl.AddText(c + new Vector2(r + S(3), -ImGui.GetTextLineHeight() * 0.5f), dim, "x");
        foreach (var d in new[] { 30, 60 })
            dl.AddText(c + new Vector2(R(d) * 0.7071f + S(2), R(d) * 0.7071f - S(2)), U32(Muted with { W = 0.8f }), d + "°");
        dl.AddText(c + new Vector2(S(4), -r - S(5) - ImGui.GetTextLineHeight() * 0.5f), dim, "y");
        var step = Math.Max(1, pa.Count / 4000);
        var dot = S(1.8f);
        for (var i = 0; i < pa.Count; i += step)
        {
            var a = pa.Axis[i];
            var k = r / (1 + a.Z);
            var col = PlaneAnalytics.TiltColor(pa.Tilt[i], RobotLimit);
            var alpha = SelectedTake < 0 || TakeOf(pa, i) == SelectedTake ? 0.9f : 0.15f;
            dl.AddCircleFilled(c + new Vector2(a.X * k, -a.Y * k), dot, U32(new Vector4(col, alpha)), 6);
        }
        PopF();
        ImGui.Dummy(new Vector2(size, size));
        if (ImGui.IsItemHovered()) Tip("Tool-axis directions (stereographic, top view)\ncentre = vertical, rings every 15°, red ring = robot limit");
    }

    void Legend()
    {
        var dl = ImGui.GetWindowDrawList();
        Font(FLabel);
        var p = ImGui.GetCursorScreenPos() + new Vector2(0, S(2));
        var h = ImGui.GetTextLineHeight();
        var dim = U32(Muted);
        var w = S(120);
        const int n = 24;
        for (var i = 0; i < n; i++)
        {
            var c0 = PlaneAnalytics.TiltColor(RobotLimit * i / n, RobotLimit);
            var c1 = PlaneAnalytics.TiltColor(RobotLimit * (i + 1) / n, RobotLimit);
            dl.AddRectFilledMultiColor(p + new Vector2(w * i / n, S(3)), p + new Vector2(w * (i + 1) / n, h - S(3)),
                U32(new Vector4(c0, 1)), U32(new Vector4(c1, 1)), U32(new Vector4(c1, 1)), U32(new Vector4(c0, 1)));
        }
        var x = p.X + w + S(6);
        var s0 = F("0-{0:0}°", RobotLimit);
        dl.AddText(new Vector2(x, p.Y), dim, s0);
        x += ImGui.CalcTextSize(s0).X + S(12);
        dl.AddRectFilled(new Vector2(x, p.Y + S(3)), new Vector2(x + S(16), p.Y + h - S(3)), U32(new Vector4(PlaneAnalytics.TiltColor(RobotLimit + 1, RobotLimit), 1)), S(2));
        dl.AddText(new Vector2(x + S(22), p.Y), dim, "above limit");
        PopF();
        ImGui.Dummy(new Vector2(Inner, h + S(6)));
    }

    static int TakeOf(PlaneAnalytics pa, int i)
    {
        foreach (var t in pa.Takes) if (i >= t.First && i < t.First + t.Count) return t.Take;
        return -1;
    }

    void LayersCard(PlaneAnalytics? pa)
    {
        Check("Stock", ref ShowStock);
        var p = ImGui.GetCursorScreenPos();
        var lh = TextSize(FLabel, "O").Y;
        var lw = SpacedText(ImGui.GetWindowDrawList(), p + new Vector2(0, (S(22) - lh) * 0.5f), "OPACITY", Muted);
        ImGui.SetCursorScreenPos(p + new Vector2(lw + S(14), 0));
        Slider("##opacity", ref StockOpacity, 0.15f, 1f, Inner - lw - S(14));
        if (ImGui.IsItemHovered()) Tip(F("Stock opacity {0:0} %", StockOpacity * 100));
        ImGui.Dummy(new Vector2(1, S(2)));
        if (Check("Recorded paths", ref ShowPaths)) OverlayDirty = true;
        if (pa != null)
            foreach (var t in pa.Takes)
            {
                var vis = !HiddenTakes.Contains(t.Take);
                if (Check(F("Take {0}##lt{0}", t.Take), ref vis, TakeColor(t.Take), S(24)))
                { if (vis) HiddenTakes.Remove(t.Take); else HiddenTakes.Add(t.Take); }
            }
        Check("Axis glyphs", ref ShowGlyphs);
        Check("X-ray paths", ref XRay);
        Check("Tool", ref ShowTool);
        Check("Tip trail", ref ShowTrail);
        Check("Wireframe", ref Wireframe);
    }

    void JobCard(PanelFrameInfo info)
    {
        var st = _engine.Stats;
        var conn = _engine.Connection;
        var avg = st.Sweeps > 0 ? st.CarveMsTotal / st.Sweeps : 0;
        var dropped = Interlocked.Read(ref st.PosesDropped);
        if (!ImGui.BeginTable("job", 2, ImGuiTableFlags.SizingStretchProp, new Vector2(Inner, 0))) return;
        ImGui.TableSetupColumn("k", ImGuiTableColumnFlags.WidthFixed, S(110));
        ImGui.TableSetupColumn("v", ImGuiTableColumnFlags.WidthStretch);
        Kv("Link", conn, conn is "connected" or "demo" ? Ok : Orange);
        if (_engine.HasStock)
        {
            var s = _engine.Box.Size;
            Kv("Stock", F("{0:0.#}×{1:0.#}×{2:0.#} mm", s.X, s.Y, s.Z));
            Kv("Resolution", F("{0:0.###} mm · {1:0.0} M cells", _engine.ResolutionMm, _engine.CellCount / 1e6));
            Kv("Removed", F("{0:N0} mm³", _engine.RemovedVolume));
        }
        else Kv("Stock", "waiting", Muted);
        Kv("Carve avg / max", F("{0:0.00} / {1:0.0} ms", avg, st.CarveMsMax), avg > 8 ? Orange : null);
        Kv("Poses", F("{0:0}/s · queue {1}", info.PosesPerSec, _engine.Backlog));
        Kv("Dropped", dropped.ToString(Inv), dropped > 0 ? Bad : Ok);
        Kv("Render", F("{0:0} fps · {1:N0}k tris", info.Fps, info.StockTris / 1000), info.Fps < 55 ? Orange : null);
        ImGui.EndTable();
        if (ImGui.IsItemHovered()) Tip("Carve = average / max ms per pose on the carving thread.\nDropped = poses discarded because the carver fell >4096 poses behind.");
    }
}
