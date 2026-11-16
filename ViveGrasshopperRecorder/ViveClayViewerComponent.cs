#pragma warning disable CA1416

using System.Diagnostics;
using System.Drawing;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Geometry;
using ViveClay.Protocol;

namespace ViveTcpRecorder;

/// <summary>
/// Streams the stock and the live tool to the external ClayViewer.exe (named pipe "ViveClayViewer"), which does the material
/// removal and rendering. Inputs: Tool (mesh already placed in world by the Vive Tracker component) and Stock.
/// The first Tool mesh is kept as the tool-local reference (sent as SetTool); each following Tool mesh is turned into a rigid
/// pose (Horn fit reference -> current) and sent as Pose. Nothing here blocks the canvas: the pipe client has its own thread,
/// stock/tool are only re-sent when they change. All geometry goes out in millimetres and comes back in document units.
/// Take Recorder link: follows the MOST RECENTLY UPDATED Take Recorder in this Rhino session through TakeHub (no wires) and
/// sends RecordedPlanes (at most 5 Hz, only on change).
/// </summary>
public sealed class ViveClayViewerComponent : GH_Component
{
    private const double MaxFitRmsMm = 0.5;
    private const int FitSamples = 200;
    private static readonly Lazy<Bitmap> ComponentIcon = new(() => PlaneIcons.Load("ClayViewer.png"));
    private static ClayPipeClient? _sharedClient;          // one pipe client per Rhino session
    private static readonly object ClientLock = new();

    private string _status = "";
    private Mesh? _carved;
    private double _removedMm3;
    private bool _subscribed;
    private long _lastExpireTicks;
    private bool _haveStock, _toolRigidWarn;
    private double _lastRms;
    private DateTime _procCheck; private bool _procRunning;

    // tool reference / pose
    private double[]? _ref;                // reference vertices (mm), tool-local frame = world at the first mesh
    private int _refV, _refF;
    private double _refScale;
    private int[] _spread = Array.Empty<int>();

    // change detection
    private object? _stockSrc;
    private int _stockHash;
    private double _lastRes = double.NaN;
    private double _unitScale = 1;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // take hub link
    private readonly object _hubLock = new();
    private long _hubLastSent, _hubSentVersion = -1;
    private System.Threading.Timer? _hubTimer;
    private TakeHubSnapshot? _snap;
    private bool _hubEverSent;
    private string _analytics = "";
    private long _analyticsKey = long.MinValue;

    /// <summary>Voxel size in mm: 0.25 / 0.5 / 1.0 (right-click menu).</summary>
    public double ResolutionMm { get; private set; } = 0.5;

    public ViveClayViewerComponent()
        : base("Vive Clay Viewer", "Clay",
            "Live clay-carving preview in an external window (ClayViewer.exe). Wire the tracker-posed tool mesh and the stock; the viewer removes what the tool sweeps. Buttons: Open Visualiser, Reset Stock, Get Mesh. Follows the latest Vive Take Recorder automatically.",
            "Vive", "Utilities")
    {
    }

    public override Guid ComponentGuid => new("B2D6E3A4-5C17-4F89-A0E1-7D3C94B8F052");
    protected override Bitmap Icon => ComponentIcon.Value;

    public override void CreateAttributes() => m_attributes = new ClayViewerAttributes(this);

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddMeshParameter("Tool", "T", "Tool mesh already placed in world by the Vive Tracker component (the tracker/tool mesh transformed to the live pose). The first mesh becomes the tool reference; later meshes must be rigid copies of it.", GH_ParamAccess.item);
        p.AddGenericParameter("Stock", "S", "Clay block: Box, Brep or Mesh. An axis-aligned box is sent as min/max; anything else is also sent as a mesh.", GH_ParamAccess.item);
        for (var i = 0; i < p.ParamCount; i++) p[i].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddMeshParameter("Carved Mesh", "M", "Carved stock (after the Get Mesh button), in document units.", GH_ParamAccess.item);
        p.AddTextParameter("Analytics", "A", "Removed volume and, per recorded take: frames, path length, mean/max speed, tool-axis tilt from world Z (min/mean/max).", GH_ParamAccess.item);
        p.AddTextParameter("Status", "St", "Connection and viewer state.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        if (da.Iteration > 0) return;

        Mesh? tool = null;
        IGH_Goo? stockGoo = null;
        da.GetData(0, ref tool);
        da.GetData(1, ref stockGoo);

        var client = GetClient();
        Subscribe(client);

        var docUnits = RhinoDoc.ActiveDoc?.ModelUnitSystem ?? UnitSystem.Millimeters;
        _unitScale = RhinoMath.UnitScale(docUnits, UnitSystem.Millimeters);
        var res = ResolutionMm;

        // ---- stock: only when changed (reference check first, then a content hash of the converted data)
        var stockObj = stockGoo?.ScriptVariable();
        _haveStock = stockObj != null;
        if (stockObj != null && (!ReferenceEquals(stockObj, _stockSrc) || res != _lastRes))
        {
            var msg = BuildStock(stockObj, res, _unitScale);
            if (msg == null) AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Stock must be a Box, Brep or Mesh.");
            else
            {
                var h = HashStock(msg);
                if (h != _stockHash || res != _lastRes) { _stockHash = h; client.SetStock(msg); }
                _stockSrc = stockObj; _lastRes = res;
            }
        }

        // ---- tool: reference on the first mesh (or when its size changes), then a rigid pose per mesh
        _toolRigidWarn = false;
        if (tool != null && tool.IsValid && tool.Vertices.Count >= 3)
        {
            var cur = ToXyz(tool, _unitScale);
            if (_ref == null || tool.Vertices.Count != _refV || tool.Faces.Count != _refF || _unitScale != _refScale)
            {
                _ref = cur; _refV = tool.Vertices.Count; _refF = tool.Faces.Count; _refScale = _unitScale;
                _spread = RigidFit.SelectSpread(cur, FitSamples);
                client.SetTool(new SetToolMsg { Mesh = ToMeshData(tool, _unitScale) });
            }
            if (RigidFit.Fit(_ref, cur, _spread, out var m, out var rms) && rms <= MaxFitRmsMm)
            {
                _lastRms = rms;
                client.SendPose(new PoseMsg { Matrix = m, Timestamp = _clock.Elapsed.TotalSeconds, CuttingEnabled = true });
            }
            else
            {
                _lastRms = rms; _toolRigidWarn = true;
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"Tool mesh is not a rigid copy of the first mesh (fit residual {rms:0.00} mm > {MaxFitRmsMm} mm, or a different mesh): not carving. Feed the tracker-posed copy of one fixed mesh.");
            }
        }

        UpdateMessage();
        da.SetData(0, _carved);
        da.SetData(1, BuildAnalytics());
        da.SetData(2, Message.Replace("\n", " "));
    }

    private void Subscribe(ClayPipeClient client)
    {
        if (_subscribed) return;
        client.MessageReceived += OnMessage;
        client.ConnectionChanged += OnConnectionChanged;
        TakeHub.Changed += OnHubChanged;
        _subscribed = true;
        OnHubChanged();
    }

    // ------------------------------------------------------------------ buttons (called by the attributes on the UI thread)
    internal void OpenVisualiser()
    {
        var client = GetClient();
        Subscribe(client);
        var procs = Process.GetProcessesByName("ClayViewer");
        if (procs.Length > 0) BringToFront(procs[0]);
        else
        {
            var err = Launch();
            _status = err ?? "";
            if (err != null) AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, err);
        }
        _procCheck = DateTime.MinValue;
        ExpireSolution(true);
    }

    internal void ResetStock()
    {
        GetClient().Reset();
        _removedMm3 = 0;
        ExpireSolution(true);
    }

    internal void GetMesh()
    {
        GetClient().RequestMesh();
        _status = "Requested carved mesh...";
        ExpireSolution(true);
    }

    private void UpdateMessage()
    {
        var client = GetClient();
        string msg;
        if (!client.IsConnected)
        {
            if ((DateTime.UtcNow - _procCheck).TotalSeconds > 1)
            {
                _procCheck = DateTime.UtcNow;
                try { _procRunning = Process.GetProcessesByName("ClayViewer").Length > 0; } catch { _procRunning = false; }
            }
            if (_procRunning) msg = "Viewer starting...\nConnecting.";
            else if (_status.StartsWith("ClayViewer.exe not found") || _status.StartsWith("Could not")) msg = "Viewer not found.\nSee warning.";
            else msg = "Viewer closed.\nClick Open Visualiser.";
        }
        else
        {
            var s = _snap;
            if (s != null && s.Recording) msg = $"Viewer online · recording take {s.CurrentTake + 1}";
            else if (_toolRigidWarn) msg = $"Viewer online\nTool not rigid ({_lastRms:0.00} mm)";
            else if (_ref == null) msg = "Viewer online\nWaiting for Tool mesh.";
            else if (!_haveStock) msg = "Viewer online\nWaiting for Stock.";
            else msg = "Viewer online · carving";
        }
        Message = msg;
    }

    private string BuildAnalytics()
    {
        var snap = _snap ?? TakeHub.Snapshot();
        var key = (snap?.Version ?? 0) * 31 + (long)Math.Round(_removedMm3);
        if (key == _analyticsKey) return _analytics;
        var stats = new List<TakeStats>();
        if (snap != null)
            for (var i = 0; i < snap.Takes.Length; i++)
                if (snap.Takes[i].Length > 0) stats.Add(TakeAnalytics.Compute(i, snap.Takes[i]));
        _analytics = TakeAnalytics.Summary(stats, snap?.Recording ?? false, snap?.CurrentTake ?? 0, _removedMm3);
        _analyticsKey = key;
        return _analytics;
    }

    // ------------------------------------------------------------------ resolution menu + persistence
    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);
        var sub = Menu_AppendItem(menu, "Resolution");
        foreach (var r in new[] { 0.25, 0.5, 1.0 })
        {
            var value = r;
            Menu_AppendItem(sub.DropDown, $"{r.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} mm", (_, _) =>
            {
                RecordUndoEvent("Clay resolution");
                ResolutionMm = value;
                ExpireSolution(true);
            }, true, Math.Abs(ResolutionMm - value) < 1e-9);
        }
    }

    public override bool Write(GH_IO.Serialization.GH_IWriter writer)
    {
        writer.SetDouble("ResolutionMm", ResolutionMm);
        return base.Write(writer);
    }

    public override bool Read(GH_IO.Serialization.GH_IReader reader)
    {
        if (reader.ItemExists("ResolutionMm"))
        {
            var r = reader.GetDouble("ResolutionMm");
            ResolutionMm = r < 0.375 ? 0.25 : r < 0.75 ? 0.5 : 1.0;
        }
        return base.Read(reader);
    }

    // ------------------------------------------------------------------ take hub -> viewer (<= 5 Hz, only on change)
    private void OnHubChanged()
    {
        lock (_hubLock)
        {
            var wait = 200 - (Environment.TickCount64 - _hubLastSent);
            if (wait <= 0) SendHub();
            else _hubTimer ??= new System.Threading.Timer(_ => { lock (_hubLock) { _hubTimer?.Dispose(); _hubTimer = null; SendHub(); } }, null, wait, System.Threading.Timeout.Infinite);
        }
    }

    private void SendHub()   // caller holds _hubLock
    {
        if (!_subscribed) return;
        var version = TakeHub.CurrentVersion;
        if (version == _hubSentVersion) return;
        var snap = TakeHub.Snapshot();
        var msg = new RecordedPlanesMsg();
        if (snap == null)
        {
            if (!_hubEverSent) { _hubSentVersion = version; return; }   // never had a recorder: nothing to clear
        }
        else
        {
            var n = snap.Takes.Sum(t => t.Length);
            msg.Recording = snap.Recording; msg.CurrentTake = snap.CurrentTake;
            msg.Take = new int[n]; msg.Origin = new float[n * 3]; msg.Axis = new float[n * 3]; msg.Time = new double[n];
            var k = 0;
            for (var t = 0; t < snap.Takes.Length; t++)
                foreach (var s in snap.Takes[t])
                {
                    msg.Take[k] = t;
                    msg.Origin[k * 3] = (float)s.X; msg.Origin[k * 3 + 1] = (float)s.Y; msg.Origin[k * 3 + 2] = (float)s.Z;
                    msg.Axis[k * 3] = (float)s.Ax; msg.Axis[k * 3 + 1] = (float)s.Ay; msg.Axis[k * 3 + 2] = (float)s.Az;
                    msg.Time[k] = s.T;
                    k++;
                }
        }
        var recChanged = (_snap?.Recording ?? false) != (snap?.Recording ?? false) || (_snap?.Takes.Length ?? 0) != (snap?.Takes.Length ?? 0);
        _snap = snap;
        _hubEverSent = true;
        _hubSentVersion = version;
        _hubLastSent = Environment.TickCount64;
        GetClient().SetRecordedPlanes(msg);
        ExpireThrottled(force: recChanged);
    }

    // ------------------------------------------------------------------ client plumbing
    private static ClayPipeClient GetClient()
    {
        lock (ClientLock) return _sharedClient ??= new ClayPipeClient();
    }

    private void OnConnectionChanged(bool connected)
    {
        _status = connected ? "Connected." : "Viewer disconnected.";
        _procCheck = DateTime.MinValue;
        if (connected) lock (_hubLock) { _hubSentVersion = -1; }
        ExpireThrottled(force: true);
        if (connected) OnHubChanged();
    }

    private void OnMessage(ClayMessage m)
    {
        switch (m)
        {
            case StatusMsg s:
                _removedMm3 = s.RemovedVolumeMm3;
                _status = s.Text;
                ExpireThrottled(force: false);
                break;
            case CarvedMeshMsg cm:
                _removedMm3 = cm.RemovedVolumeMm3;
                _carved = ToRhinoMesh(cm.Mesh, 1.0 / _unitScale);
                _status = $"Carved mesh received: {cm.Mesh.TriangleCount} triangles, {cm.RemovedVolumeMm3:0} mm3 removed.";
                ExpireThrottled(force: true);
                break;
        }
    }

    /// <summary>Re-solve on the UI thread, at most ~2x per second unless forced (status chatter must not retrigger the canvas).</summary>
    private void ExpireThrottled(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - _lastExpireTicks < 500) return;
        _lastExpireTicks = now;
        RhinoApp.InvokeOnUiThread(new Action(() => ExpireSolution(true)));
    }

    // ------------------------------------------------------------------ launching
    /// <summary>Starts ClayViewer.exe; returns an error text, or null on success.</summary>
    private static string? Launch()
    {
        var exe = FindViewerExe();
        if (exe is null) return "ClayViewer.exe not found (expected in a ClayViewer subfolder next to the .gha, or ClayViewer\\publish-win-x64 in the repo).";
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
            return null;
        }
        catch (Exception ex) { return "Could not start viewer: " + ex.Message; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

    private static void BringToFront(Process p)
    {
        try
        {
            p.Refresh();
            var h = p.MainWindowHandle;
            if (h == IntPtr.Zero) return;
            if (IsIconic(h)) ShowWindow(h, 9);   // SW_RESTORE
            SetForegroundWindow(h);
        }
        catch { /* best effort */ }
    }

    private static string? FindViewerExe()
    {
        var dir = Path.GetDirectoryName(typeof(ViveClayViewerComponent).Assembly.Location);
        if (string.IsNullOrEmpty(dir)) return null;
        var direct = Path.Combine(dir, "ClayViewer", "ClayViewer.exe");
        if (File.Exists(direct)) return direct;
        // Fallback: walk up (bin\Release\net7.0-windows -> repo root) looking for the repo publish folder.
        for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "ClayViewer", "publish-win-x64", "ClayViewer.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // ------------------------------------------------------------------ geometry conversion
    private static double[] ToXyz(Mesh m, double scale)
    {
        var v = new double[m.Vertices.Count * 3];
        for (var i = 0; i < m.Vertices.Count; i++)
        {
            var p = m.Vertices[i];
            v[i * 3] = p.X * scale; v[i * 3 + 1] = p.Y * scale; v[i * 3 + 2] = p.Z * scale;
        }
        return v;
    }

    private static SetStockMsg? BuildStock(object geo, double res, double scale)
    {
        Mesh? mesh = null;
        BoundingBox bb;
        bool isBox;
        switch (geo)
        {
            case Box box:
                bb = box.BoundingBox;
                // A rotated Box is not axis-aligned: send it as a mesh as well.
                isBox = Math.Abs(box.Plane.ZAxis.Z) > 1 - 1e-9 && Math.Abs(box.Plane.XAxis.X) > 1 - 1e-9;
                if (!isBox) mesh = Mesh.CreateFromBox(box, 1, 1, 1);
                break;
            case Brep brep:
                bb = brep.GetBoundingBox(true);
                mesh = MeshBrep(brep);
                isBox = mesh != null && IsBoxLike(mesh, bb);
                break;
            case Mesh m:
                bb = m.GetBoundingBox(true);
                mesh = m;
                isBox = IsBoxLike(m, bb);
                break;
            default: return null;
        }
        if (!bb.IsValid) return null;
        var msg = new SetStockMsg
        {
            Min = new[] { bb.Min.X * scale, bb.Min.Y * scale, bb.Min.Z * scale },
            Max = new[] { bb.Max.X * scale, bb.Max.Y * scale, bb.Max.Z * scale },
            ResolutionMm = res,
        };
        if (!isBox && mesh != null) msg.Mesh = ToMeshData(mesh, scale);
        return msg;
    }

    private static Mesh? MeshBrep(Brep brep)
    {
        var parts = Mesh.CreateFromBrep(brep, MeshingParameters.FastRenderMesh);
        if (parts == null || parts.Length == 0) return null;
        var joined = new Mesh();
        foreach (var p in parts) joined.Append(p);
        return joined;
    }

    /// <summary>True when the closed mesh fills (nearly) all of its bounding box, i.e. it is an axis-aligned box.</summary>
    private static bool IsBoxLike(Mesh mesh, BoundingBox bb)
    {
        if (!mesh.IsClosed) return false;
        var vol = VolumeMassProperties.Compute(mesh)?.Volume ?? 0;
        var boxVol = (bb.Max.X - bb.Min.X) * (bb.Max.Y - bb.Min.Y) * (bb.Max.Z - bb.Min.Z);
        return boxVol > 0 && Math.Abs(Math.Abs(vol) - boxVol) / boxVol < 1e-3;
    }

    private static MeshData ToMeshData(Mesh source, double scale)
    {
        var m = source.DuplicateMesh();
        m.Faces.ConvertQuadsToTriangles();
        var v = new float[m.Vertices.Count * 3];
        for (var i = 0; i < m.Vertices.Count; i++)
        {
            var p = m.Vertices[i];
            v[i * 3] = (float)(p.X * scale); v[i * 3 + 1] = (float)(p.Y * scale); v[i * 3 + 2] = (float)(p.Z * scale);
        }
        var idx = new int[m.Faces.Count * 3];
        for (var f = 0; f < m.Faces.Count; f++)
        {
            var face = m.Faces[f];
            idx[f * 3] = face.A; idx[f * 3 + 1] = face.B; idx[f * 3 + 2] = face.C;
        }
        return new MeshData(v, idx);
    }

    private static Mesh ToRhinoMesh(MeshData d, double scale)
    {
        var m = new Mesh();
        for (var i = 0; i < d.Vertices.Length; i += 3)
            m.Vertices.Add(d.Vertices[i] * scale, d.Vertices[i + 1] * scale, d.Vertices[i + 2] * scale);
        for (var i = 0; i + 2 < d.Indices.Length; i += 3)
            m.Faces.AddFace(d.Indices[i], d.Indices[i + 1], d.Indices[i + 2]);
        m.Normals.ComputeNormals();
        m.Compact();
        return m;
    }

    private static int HashMesh(MeshData d)
    {
        var h = new HashCode();
        h.Add(d.Vertices.Length); h.Add(d.Indices.Length);
        foreach (var f in d.Vertices) h.Add(f);
        foreach (var i in d.Indices) h.Add(i);
        return h.ToHashCode();
    }

    private static int HashStock(SetStockMsg m)
    {
        var h = new HashCode();
        foreach (var d in m.Min) h.Add(d);
        foreach (var d in m.Max) h.Add(d);
        h.Add(m.ResolutionMm);
        if (m.Mesh != null) h.Add(HashMesh(m.Mesh));
        return h.ToHashCode();
    }

    public override void RemovedFromDocument(GH_Document document)
    {
        if (_subscribed && _sharedClient != null)
        {
            _sharedClient.MessageReceived -= OnMessage;
            _sharedClient.ConnectionChanged -= OnConnectionChanged;
            TakeHub.Changed -= OnHubChanged;
            _subscribed = false;
            lock (_hubLock) { _hubTimer?.Dispose(); _hubTimer = null; }
        }
        base.RemovedFromDocument(document);
    }
}
