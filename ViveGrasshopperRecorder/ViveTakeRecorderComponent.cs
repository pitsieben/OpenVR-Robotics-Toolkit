#pragma warning disable CA1416 // Validate platform compatibility for System.Drawing.Bitmap

using System.Diagnostics;
using System.Drawing;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using ViveClay.Protocol;

namespace ViveTcpRecorder;

/// <summary>
/// Records takes of the plane wired into <c>Data</c> (normally the tool TCP plane) while it is inside the working volume (or on trigger),
/// and carves the same volume in the external ClayViewer.exe with the live <c>Tool</c> mesh. The recorded planes are the input planes
/// (a tracker-data payload only supplies its plane when no plane is given), so the viewer, <c>Takes</c> and the analytics all see the TCP plane.
/// </summary>
public sealed class ViveTakeRecorderComponent : GH_Component, IClayButtons
{
    private static readonly Lazy<Bitmap> ComponentIcon = new(() => PlaneIcons.Load("TakeRecorder.png"));

    private readonly object _gate = new();
    private readonly List<List<DeviceFrame<Plane>[]>> _takes = new();        // completed takes: take -> frame -> devices
    private List<DeviceFrame<Plane>[]> _current = new();
    private bool _isRecording;
    private double _lastRecordTime;
    private long _version;
    private double _unitToMm = 1;
    private readonly ClayLink _clay;

    // cached outputs, rebuilt only when the recording or the unit scale changed
    private long _cacheVersion = -1;
    private double _cacheScale = double.NaN;
    private DataTree<Plane> _cacheTree = new();
    private List<TakeRecord> _cacheRecords = new();
    private int _cacheFrames;

    public bool RequireTriggerToRecord { get; set; }

    public ViveTakeRecorderComponent()
        : base(
            "Vive Take Recorder",
            "ViveTakes",
            "Records takes of the TCP plane while it is inside the working volume (or on trigger) and carves the same volume live in the external Clay Viewer with the Tool mesh. Buttons: Open Visualiser, Reset Stock, Get Mesh. Feed Results to Vive Take Analytics.",
            "Vive",
            "TCP")
    {
        _clay = new ClayLink(this) { PlanesProvider = BuildPlanesMessage };
    }

    public override Guid ComponentGuid => new("9F27C246-81B4-4B7E-874D-4EAE3890DF11");

    protected override Bitmap Icon => ComponentIcon.Value;

    public override void CreateAttributes() => m_attributes = new ClayViewerAttributes(this, this);

    protected override void RegisterInputParams(GH_InputParamManager parameters)
    {
        parameters.AddGenericParameter(
            "Data", "D",
            "The plane to record: normally the tool TCP plane. A tracker-data payload is accepted too (its plane is used only when no plane is given; its trigger is read either way). Tilt and all analytics refer to this plane.",
            GH_ParamAccess.tree);
        parameters[0].Optional = true;

        parameters.AddGenericParameter(
            "Brep", "B",
            "Working volume and carving stock: a Box, closed Brep or closed Mesh. Recording happens while the Data plane is inside; a box is carved as a box, anything else as a mesh.",
            GH_ParamAccess.item);
        parameters[1].Optional = true;

        parameters.AddBooleanParameter(
            "Record", "R",
            "Master recording switch. Set to True to allow take recording.",
            GH_ParamAccess.item,
            true);

        parameters.AddNumberParameter(
            "Delay", "Ms",
            "Minimum time in milliseconds between recorded frames (default 0).",
            GH_ParamAccess.item,
            0.0);

        parameters.AddNumberParameter(
            "Tolerance", "Tol",
            "Distance tolerance in mm. If > 0, skips recording frames if the plane hasn't moved more than this distance.",
            GH_ParamAccess.item,
            0.0);

        parameters.AddBooleanParameter(
            "Reset", "Rst",
            "Delete all recorded takes and planes when True.",
            GH_ParamAccess.item,
            false);

        parameters.AddMeshParameter(
            "Tool", "Tl",
            "Tool mesh already placed in world by the Vive Tracker component. The first mesh becomes the tool reference; later meshes must be rigid copies of it. Carving is always on.",
            GH_ParamAccess.item);
        parameters[6].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager parameters)
    {
        parameters.AddGenericParameter(
            "Takes", "T",
            "Recorded planes: one branch per take {take} (several devices: {device;take}), planes in recording order.",
            GH_ParamAccess.tree);

        parameters.AddIntegerParameter(
            "Take Count", "N",
            "Number of completed takes recorded.",
            GH_ParamAccess.item);

        parameters.AddIntegerParameter(
            "Frame Count", "F",
            "Total number of frames recorded across all takes.",
            GH_ParamAccess.item);

        parameters.AddBooleanParameter(
            "Is Inside", "In",
            "True if the Data plane origin is currently inside the working volume.",
            GH_ParamAccess.item);

        parameters.AddTextParameter(
            "Status", "S",
            "Recording and viewer status.",
            GH_ParamAccess.item);

        parameters.AddMeshParameter(
            "Carved Mesh", "M",
            "Carved stock in document units (after the Get Mesh button).",
            GH_ParamAccess.item);

        parameters.AddGenericParameter(
            "Results", "Res",
            "Takes (planes, times, triggers) plus the viewer's job numbers (removed volume, stock volume, resolution). Wire to Vive Take Analytics.",
            GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess dataAccess)
    {
        // Protection against Grasshopper multiple iterations on same frame
        if (dataAccess.Iteration > 0) return;

        GH_Structure<IGH_Goo>? rawTree = null;
        dataAccess.GetDataTree(0, out rawTree);

        IGH_Goo? volumeGoo = null;
        dataAccess.GetData(1, ref volumeGoo);

        var run = true;
        dataAccess.GetData(2, ref run);

        var delayMs = 0.0;
        dataAccess.GetData(3, ref delayMs);

        var tolerance = 0.0;
        dataAccess.GetData(4, ref tolerance);

        var resetAll = false;
        dataAccess.GetData(5, ref resetAll);

        Mesh? tool = null;
        dataAccess.GetData(6, ref tool);

        var volume = NormalizeVolume(volumeGoo?.ScriptVariable());
        if (volumeGoo != null && volume == null)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Brep must be a Box, closed Brep or closed Mesh.");

        _unitToMm = Rhino.RhinoMath.UnitScale(Rhino.RhinoDoc.ActiveDoc?.ModelUnitSystem ?? Rhino.UnitSystem.Millimeters, Rhino.UnitSystem.Millimeters);

        // carving side (never blocks: pipe client has its own thread)
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;   // seconds
        var tcpNow = TakeFrames.Extract(rawTree, now, out _);
        _clay.Sync(volume, tool, tcpNow.Length > 0 ? tcpNow[0].Item.ZAxis : (Vector3d?)null);
        var planesChanged = false;
        var isInside = true;
        var hasPlane = false;
        string recLine;
        string status;
        int completedTakes, totalFrames;
        List<TakeRecord> records;
        DataTree<Plane> tree;

        lock (_gate)
        {
            if (resetAll)
            {
                _takes.Clear();
                _current = new List<DeviceFrame<Plane>[]>();
                _isRecording = false;
                _version++;
                planesChanged = true;
            }
            else
            {
                // frame = the planes of all Data branches
                var frame = TakeFrames.Extract(rawTree, now, out var triggerActive);
                hasPlane = frame.Length > 0;
                var primary = hasPlane ? frame[0].Item : Plane.Unset;

                if (volume != null && hasPlane)
                    isInside = IsInside(volume, primary.Origin);
                else if (volume != null)
                    isInside = false;

                var toggle = hasPlane && run && (volume == null || isInside) && (!RequireTriggerToRecord || triggerActive);

                if (toggle)
                {
                    if (!_isRecording)
                    {
                        _isRecording = true;
                        _current = new List<DeviceFrame<Plane>[]>();
                        _lastRecordTime = now;
                        _version++;
                        planesChanged = true;
                    }

                    if (hasPlane && (_current.Count == 0 || (now - _lastRecordTime) * 1000.0 >= delayMs))
                    {
                        var shouldRecord = true;
                        if (_current.Count > 0 && tolerance > 0.0001)
                        {
                            var last = _current[^1][0].Item.Origin;
                            if (last.DistanceTo(primary.Origin) * _unitToMm < tolerance) shouldRecord = false;
                        }
                        if (shouldRecord)
                        {
                            _current.Add(frame);
                            _lastRecordTime = now;
                            _version++;
                            planesChanged = true;
                        }
                    }
                }
                else if (_isRecording)
                {
                    // plane left the volume or recording was switched off -> complete current take
                    if (_current.Count > 0) _takes.Add(_current);
                    _isRecording = false;
                    _current = new List<DeviceFrame<Plane>[]>();
                    _version++;
                    planesChanged = true;
                }
            }

            RebuildCache();
            completedTakes = _takes.Count;
            totalFrames = _cacheFrames;
            records = _cacheRecords;
            tree = _cacheTree;

            if (resetAll)
            {
                recLine = "Cleared";
                status = "Reset (0 takes)";
            }
            else if (_isRecording)
            {
                recLine = $"REC #{completedTakes + 1} ({_current.Count})";
                status = $"Recording Take #{completedTakes + 1} ({_current.Count} frames)";
            }
            else if (completedTakes > 0)
            {
                recLine = $"{completedTakes} takes";
                status = volume != null && !isInside
                    ? $"Outside volume ({completedTakes} takes saved, {totalFrames} frames)"
                    : $"Idle ({completedTakes} takes saved, {totalFrames} frames)";
            }
            else
            {
                recLine = "Idle";
                status = volume != null && !isInside ? "Outside volume" : "Idle (0 takes)";
            }
        }

        if (planesChanged) _clay.NotifyPlanes(immediate: resetAll);

        var viewer = _clay.StateText();
        Message = recLine + "\n" + viewer;

        dataAccess.SetDataTree(0, tree);
        dataAccess.SetData(1, completedTakes);
        dataAccess.SetData(2, totalFrames);
        dataAccess.SetData(3, isInside);
        dataAccess.SetData(4, status + " | " + viewer);
        dataAccess.SetData(5, _clay.Carved);
        dataAccess.SetData(6, new GH_ViveTakeResults(new ViveTakeResults
        {
            Takes = records,
            RemovedMm3 = _clay.RemovedMm3,
            StockMm3 = _clay.StockMm3,
            ResolutionMm = _clay.ResolutionMm,
        }));
    }

    // ------------------------------------------------------------------ outputs
    /// <summary>Rebuilds the takes tree and the analytics records when the recording changed (caller holds _gate).</summary>
    private void RebuildCache()
    {
        if (_cacheVersion == _version && _cacheScale == _unitToMm) return;
        var all = new List<IReadOnlyList<DeviceFrame<Plane>[]>>(_takes);
        if (_isRecording && _current.Count > 0) all.Add(_current);
        var groups = TakeLayout.Group(all);

        TakeFrames.Build(groups, _unitToMm, out var tree, out var records);
        _cacheTree = tree;
        _cacheRecords = records;
        _cacheFrames = _takes.Sum(t => t.Count) + (_isRecording ? _current.Count : 0);
        _cacheVersion = _version;
        _cacheScale = _unitToMm;
    }

    /// <summary>Snapshot of the primary device's recorded planes for the viewer's path/tilt display (any thread).</summary>
    private RecordedPlanesMsg? BuildPlanesMessage()
    {
        lock (_gate)
        {
            var k = _unitToMm;
            var all = new List<List<DeviceFrame<Plane>[]>>(_takes);
            if (_current.Count > 0 || _isRecording) all.Add(_current);
            var primary = int.MaxValue;
            foreach (var take in all)
                foreach (var frame in take)
                    foreach (var f in frame) primary = Math.Min(primary, f.Device);

            var n = 0;
            foreach (var take in all)
                foreach (var frame in take) if (frame.Any(f => f.Device == primary)) n++;

            var msg = new RecordedPlanesMsg
            {
                Recording = _isRecording,
                CurrentTake = _takes.Count,
                Take = new int[n], Origin = new float[n * 3], Axis = new float[n * 3], Time = new double[n],
            };
            var i = 0;
            for (var t = 0; t < all.Count; t++)
                foreach (var frame in all[t])
                {
                    var item = frame.FirstOrDefault(f => f.Device == primary);
                    if (item == null) continue;
                    var p = item.Item;
                    var z = p.ZAxis;
                    msg.Take[i] = t;
                    msg.Origin[i * 3] = (float)(p.OriginX * k); msg.Origin[i * 3 + 1] = (float)(p.OriginY * k); msg.Origin[i * 3 + 2] = (float)(p.OriginZ * k);
                    msg.Axis[i * 3] = (float)z.X; msg.Axis[i * 3 + 1] = (float)z.Y; msg.Axis[i * 3 + 2] = (float)z.Z;
                    msg.Time[i] = item.Time;
                    i++;
                }
            return msg;
        }
    }

    // ------------------------------------------------------------------ input handling
    /// <summary>Box, closed Brep or closed Mesh as a volume (Extrusions become Breps); anything else is null.</summary>
    private static object? NormalizeVolume(object? geo)
    {
        switch (geo)
        {
            case Box box when box.IsValid: return box;
            case Extrusion ex: return ex.ToBrep();
            case Brep brep when brep.IsValid: return brep;
            case Mesh mesh when mesh.IsValid: return mesh;
            default: return null;
        }
    }

    private static bool IsInside(object volume, Point3d p) => volume switch
    {
        Box box => box.Contains(p),
        Brep brep => brep.IsPointInside(p, 0.001, false),
        Mesh mesh => mesh.IsClosed && mesh.IsPointInside(p, 0.001, false),
        _ => true,
    };

    // ------------------------------------------------------------------ buttons
    void IClayButtons.OpenVisualiser() => _clay.OpenVisualiser();
    void IClayButtons.ResetStock() => _clay.ResetStock();
    void IClayButtons.GetMesh() => _clay.GetMesh();

    // ------------------------------------------------------------------ menu / persistence / lifetime
    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);
        int takeCount;
        lock (_gate) takeCount = _takes.Count + (_isRecording ? 1 : 0);

        Menu_AppendItem(menu, "Require Trigger Button to Record", (sender, e) =>
        {
            RecordUndoEvent("Require Trigger Button to Record");
            RequireTriggerToRecord = !RequireTriggerToRecord;
            ExpireSolution(true);
        }, true, RequireTriggerToRecord);

        Menu_AppendItem(menu, "Clear All Takes", (sender, e) =>
        {
            RecordUndoEvent("Clear All Takes");
            lock (_gate)
            {
                _takes.Clear();
                _current = new List<DeviceFrame<Plane>[]>();
                _isRecording = false;
                _version++;
            }
            _clay.NotifyPlanes(immediate: true);
            ExpireSolution(true);
        }, takeCount > 0);

        _clay.AppendResolutionMenu(menu);
    }

    public override void RemovedFromDocument(GH_Document document)
    {
        _clay.Detach();
        base.RemovedFromDocument(document);
    }

    public override bool Write(GH_IO.Serialization.GH_IWriter writer)
    {
        writer.SetBoolean("RequireTriggerToRecord", RequireTriggerToRecord);
        _clay.Write(writer);
        return base.Write(writer);
    }

    public override bool Read(GH_IO.Serialization.GH_IReader reader)
    {
        if (reader.ItemExists("RequireTriggerToRecord"))
            RequireTriggerToRecord = reader.GetBoolean("RequireTriggerToRecord");
        _clay.Read(reader);
        return base.Read(reader);
    }
}
