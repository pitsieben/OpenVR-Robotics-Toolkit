#pragma warning disable CA1416

using System.Diagnostics;
using Grasshopper.Kernel;
using Rhino;
using Rhino.Geometry;
using ViveClay.Protocol;

namespace ViveTcpRecorder;

/// <summary>
/// Everything a component needs to talk to the external ClayViewer.exe (named pipe "ViveClayViewer"), which does the material
/// removal and rendering. One pipe client per Rhino session; one ClayLink per component.
/// The first Tool mesh is kept as the tool-local reference (SetTool); each following Tool mesh is turned into a rigid pose
/// (Horn fit reference -> current, <see cref="RigidFit"/>) and sent as Pose. Nothing here blocks the canvas: the pipe client has
/// its own thread, stock/tool are only re-sent when they change. Geometry goes out in millimetres and comes back in document units.
/// </summary>
internal sealed class ClayLink
{
    private const double MaxFitRmsMm = 0.5;
    private const int FitSamples = 200;
    private static ClayPipeClient? _sharedClient;
    private static readonly object ClientLock = new();

    private readonly GH_Component _owner;
    private bool _subscribed;
    private long _lastExpireTicks;
    private string _status = "";
    private DateTime _procCheck; private bool _procRunning;
    private bool _toolRigidWarn;
    private double _lastRms;

    // tool reference
    private double[]? _ref;
    private int _refV, _refF;
    private double _refScale;
    private int[] _spread = Array.Empty<int>();

    // stock change detection
    private object? _stockSrc;
    private int _stockHash;
    private double _lastRes = double.NaN;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // recorded planes -> viewer (<= 5 Hz, only on change)
    private readonly object _planesLock = new();
    private long _planesLastSent;
    private bool _planesDirty;
    private System.Threading.Timer? _planesTimer;

    public ClayLink(GH_Component owner) { _owner = owner; }

    /// <summary>Voxel size in mm: 0.25 / 0.5 / 1.0.</summary>
    public double ResolutionMm { get; set; } = 0.5;
    public Mesh? Carved { get; private set; }
    public double RemovedMm3 { get; private set; }
    public double StockMm3 { get; private set; }
    public double UnitScale { get; private set; } = 1;
    public bool HaveStock { get; private set; }
    public bool IsConnected => GetClient().IsConnected;

    /// <summary>Builds the full RecordedPlanes snapshot (called on whatever thread sends it; must be thread-safe). Null = nothing to send.</summary>
    public Func<RecordedPlanesMsg?>? PlanesProvider { get; set; }

    private static ClayPipeClient GetClient() { lock (ClientLock) return _sharedClient ??= new ClayPipeClient(); }

    // ------------------------------------------------------------------ lifetime
    public void Attach()
    {
        if (_subscribed) return;
        var client = GetClient();
        client.MessageReceived += OnMessage;
        client.ConnectionChanged += OnConnectionChanged;
        _subscribed = true;
        NotifyPlanes();
    }

    public void Detach()
    {
        if (_subscribed && _sharedClient != null)
        {
            _sharedClient.MessageReceived -= OnMessage;
            _sharedClient.ConnectionChanged -= OnConnectionChanged;
            _subscribed = false;
        }
        lock (_planesLock) { _planesTimer?.Dispose(); _planesTimer = null; }
    }

    // ------------------------------------------------------------------ per-solve sync
    /// <summary>Sends stock (only when changed) and tool pose to the viewer. <paramref name="stockObj"/> is a Box, Brep or Mesh (or null).</summary>
    public void Sync(object? stockObj, Mesh? tool, Vector3d? tcpAxis = null)
    {
        var client = GetClient();
        Attach();
        var docUnits = RhinoDoc.ActiveDoc?.ModelUnitSystem ?? UnitSystem.Millimeters;
        UnitScale = RhinoMath.UnitScale(docUnits, UnitSystem.Millimeters);
        var res = ResolutionMm;

        HaveStock = stockObj != null;
        if (stockObj != null && (!ReferenceEquals(stockObj, _stockSrc) || res != _lastRes))
        {
            var msg = BuildStock(stockObj, res, UnitScale, out var vol);
            if (msg == null) _owner.AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Brep (working volume / stock) must be a Box, closed Brep or closed Mesh to carve.");
            else
            {
                var h = HashStock(msg);
                if (h != _stockHash || res != _lastRes) { _stockHash = h; client.SetStock(msg); }
                StockMm3 = vol;
                _stockSrc = stockObj; _lastRes = res;
            }
        }

        _toolRigidWarn = false;
        if (tool != null && tool.IsValid && tool.Vertices.Count >= 3)
        {
            var cur = ToXyz(tool, UnitScale);
            if (_ref == null || tool.Vertices.Count != _refV || tool.Faces.Count != _refF || UnitScale != _refScale)
            {
                _ref = cur; _refV = tool.Vertices.Count; _refF = tool.Faces.Count; _refScale = UnitScale;
                _spread = RigidFit.SelectSpread(cur, FitSamples);
                client.SetTool(new SetToolMsg { Mesh = ToMeshData(tool, UnitScale) });
            }
            if (RigidFit.Fit(_ref, cur, _spread, out var m, out var rms) && rms <= MaxFitRmsMm)
            {
                _lastRms = rms;
                client.SendPose(new PoseMsg { Matrix = m, Timestamp = _clock.Elapsed.TotalSeconds, CuttingEnabled = true,
                    TcpAxis = tcpAxis is { } z && z.IsValid ? new[] { z.X, z.Y, z.Z } : null });
            }
            else
            {
                _lastRms = rms; _toolRigidWarn = true;
                _owner.AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"Tool mesh is not a rigid copy of the first mesh (fit residual {rms:0.00} mm > {MaxFitRmsMm} mm, or a different mesh): not carving. Feed the tracker-posed copy of one fixed mesh.");
            }
        }
    }

    /// <summary>One line describing the viewer state.</summary>
    public string StateText()
    {
        var client = GetClient();
        if (!client.IsConnected)
        {
            if ((DateTime.UtcNow - _procCheck).TotalSeconds > 1)
            {
                _procCheck = DateTime.UtcNow;
                try { _procRunning = Process.GetProcessesByName("ClayViewer").Length > 0; } catch { _procRunning = false; }
            }
            if (_procRunning) return "Viewer starting...";
            if (_status.StartsWith("ClayViewer.exe not found") || _status.StartsWith("Could not")) return "Viewer not found (see warning)";
            return "Viewer closed - click Open Visualiser";
        }
        if (_toolRigidWarn) return $"Viewer online - tool not rigid ({_lastRms:0.00} mm)";
        if (_ref == null) return "Viewer online - waiting for Tool";
        if (!HaveStock) return "Viewer online - waiting for Brep";
        return "Viewer online - carving";
    }

    public string StatusText => _status;

    // ------------------------------------------------------------------ buttons (UI thread)
    public void OpenVisualiser()
    {
        Attach();
        var procs = Process.GetProcessesByName("ClayViewer");
        if (procs.Length > 0) BringToFront(procs[0]);
        else
        {
            var err = Launch();
            _status = err ?? "";
            if (err != null) _owner.AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, err);
        }
        _procCheck = DateTime.MinValue;
        _owner.ExpireSolution(true);
    }

    public void ResetStock()
    {
        GetClient().Reset();
        RemovedMm3 = 0;
        _owner.ExpireSolution(true);
    }

    public void GetMesh()
    {
        GetClient().RequestMesh();
        _status = "Requested carved mesh...";
        _owner.ExpireSolution(true);
    }

    // ------------------------------------------------------------------ persistence
    public void Write(GH_IO.Serialization.GH_IWriter writer) => writer.SetDouble("ResolutionMm", ResolutionMm);

    public void Read(GH_IO.Serialization.GH_IReader reader)
    {
        if (!reader.ItemExists("ResolutionMm")) return;
        var r = reader.GetDouble("ResolutionMm");
        ResolutionMm = r < 0.375 ? 0.25 : r < 0.75 ? 0.5 : 1.0;
    }

    public void AppendResolutionMenu(ToolStripDropDown menu)
    {
        var sub = GH_DocumentObject.Menu_AppendItem(menu, "Carving Resolution");
        foreach (var r in new[] { 0.25, 0.5, 1.0 })
        {
            var value = r;
            GH_DocumentObject.Menu_AppendItem(sub.DropDown, $"{r.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} mm", (_, _) =>
            {
                _owner.RecordUndoEvent("Clay resolution");
                ResolutionMm = value;
                _owner.ExpireSolution(true);
            }, true, Math.Abs(ResolutionMm - value) < 1e-9);
        }
    }

    // ------------------------------------------------------------------ recorded planes -> viewer
    /// <summary>Tells the link that the recorded planes changed; the snapshot is sent at most 5 times per second. Call OUTSIDE the recorder's own lock.</summary>
    public void NotifyPlanes(bool immediate = false)
    {
        lock (_planesLock)
        {
            _planesDirty = true;
            var wait = 200 - (Environment.TickCount64 - _planesLastSent);
            if (wait <= 0 || immediate) SendPlanes();
            else _planesTimer ??= new System.Threading.Timer(_ => { lock (_planesLock) { _planesTimer?.Dispose(); _planesTimer = null; SendPlanes(); } }, null, wait, System.Threading.Timeout.Infinite);
        }
    }

    private void SendPlanes()   // caller holds _planesLock
    {
        if (!_subscribed || !_planesDirty) return;
        _planesDirty = false;
        _planesLastSent = Environment.TickCount64;
        var msg = PlanesProvider?.Invoke();
        if (msg != null) GetClient().SetRecordedPlanes(msg);
    }

    // ------------------------------------------------------------------ pipe events
    private void OnConnectionChanged(bool connected)
    {
        _status = connected ? "Connected." : "Viewer disconnected.";
        _procCheck = DateTime.MinValue;
        ExpireThrottled(force: true);
        if (connected) NotifyPlanes(immediate: true);
    }

    private void OnMessage(ClayMessage m)
    {
        switch (m)
        {
            case StatusMsg s:
                RemovedMm3 = s.RemovedVolumeMm3;
                _status = s.Text;
                ExpireThrottled(force: false);
                break;
            case CarvedMeshMsg cm:
                RemovedMm3 = cm.RemovedVolumeMm3;
                var full = ToRhinoMesh(cm.Mesh, 1.0 / UnitScale);
                Carved = Lighten(full, out var faces);
                _status = $"Carved mesh received: {cm.Mesh.TriangleCount} triangles reduced to {faces}, {cm.RemovedVolumeMm3:0} mm3 removed.";
                ExpireThrottled(force: true);
                break;
        }
    }

    /// <summary>Faces kept by Get Mesh (the viewer sends millions of voxel-sized triangles).</summary>
    public const int CarvedTargetFaces = 150_000;

    /// <summary>Quadric-error decimation down to <see cref="CarvedTargetFaces"/>, then light smoothing (fixed boundary)
    /// and smooth vertex normals, so the lower density reads as a soft clay surface instead of facets.</summary>
    private static Mesh Lighten(Mesh mesh, out int faces)
    {
        try
        {
            mesh.Weld(Math.PI);                                        // shared vertices: needed for decimation and smoothing
            if (mesh.Faces.Count > CarvedTargetFaces)
                mesh.Reduce(CarvedTargetFaces, allowDistortion: false, accuracy: 10, normalizeSize: false);
            for (var i = 0; i < 2; i++)                                 // gentle Laplacian: hides voxel steps, barely shrinks
                mesh.Smooth(0.35, true, true, true, true, SmoothingCoordinateSystem.World);
            mesh.Faces.CullDegenerateFaces();
            mesh.Normals.ComputeNormals();
            mesh.Compact();
        }
        catch { /* keep whatever we have */ }
        faces = mesh.Faces.Count;
        return mesh;
    }

    /// <summary>Re-solve on the UI thread, at most ~2x per second unless forced (status chatter must not retrigger the canvas).</summary>
    private void ExpireThrottled(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - _lastExpireTicks < 500) return;
        _lastExpireTicks = now;
        RhinoApp.InvokeOnUiThread(new Action(() => _owner.ExpireSolution(true)));
    }

    // ------------------------------------------------------------------ launching
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
        var dir = Path.GetDirectoryName(typeof(ClayLink).Assembly.Location);
        if (string.IsNullOrEmpty(dir)) return null;
        var direct = Path.Combine(dir, "ClayViewer", "ClayViewer.exe");
        if (File.Exists(direct)) return direct;
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

    private static SetStockMsg? BuildStock(object geo, double res, double scale, out double volumeMm3)
    {
        volumeMm3 = 0;
        Mesh? mesh = null;
        BoundingBox bb;
        bool isBox;
        switch (geo)
        {
            case Box box:
                bb = box.BoundingBox;
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
        var k3 = scale * scale * scale;
        if (isBox || mesh == null) volumeMm3 = (bb.Max.X - bb.Min.X) * (bb.Max.Y - bb.Min.Y) * (bb.Max.Z - bb.Min.Z) * k3;
        else volumeMm3 = Math.Abs(VolumeMassProperties.Compute(mesh)?.Volume ?? 0) * k3;
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
}
