#pragma warning disable CA1416 // Validate platform compatibility for System.Drawing.Bitmap

using System.Diagnostics;
using System.Drawing;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace ViveTcpRecorder;

public sealed class ViveTakeRecorderComponent : GH_Component
{
    private static readonly Lazy<Bitmap> ComponentIcon = new(LoadIcon);

    private readonly object _gate = new();
    private readonly List<List<DataTree<object>>> _allRecordedTakes = new();
    private List<DataTree<object>> _currentRecordingTake = new();
    private bool _isRecording;
    private double _lastRecordTime;

    public bool RequireTriggerToRecord { get; set; }

    public ViveTakeRecorderComponent()
        : base(
            "Vive Take Recorder",
            "ViveTakes",
            "Records trajectory takes of tracker planes while inside a working area Brep (or on trigger).",
            "Vive",
            "TCP")
    {
    }

    public override Guid ComponentGuid => new("9F27C246-81B4-4B7E-874D-4EAE3890DF11");

    protected override Bitmap Icon => ComponentIcon.Value;

    protected override void RegisterInputParams(GH_InputParamManager parameters)
    {
        parameters.AddGenericParameter(
            "Data", "D",
            "Tracker data (Plane, or DataTree containing Plane/metadata to record).",
            GH_ParamAccess.tree);

        parameters.AddBrepParameter(
            "Brep", "B",
            "Optional working area Brep. Recording automatically occurs while tracker plane is inside this volume.",
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
            "Distance tolerance in mm. If > 0, skips recording frames if the tracker hasn't moved more than this distance.",
            GH_ParamAccess.item,
            0.0);

        parameters.AddBooleanParameter(
            "Reset", "Rst",
            "Delete all recorded takes and planes when True.",
            GH_ParamAccess.item,
            false);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager parameters)
    {
        parameters.AddGenericParameter(
            "Takes", "T",
            "Recorded data tree with path structure {ID_Device; ID_Take; Frame_Index}.",
            GH_ParamAccess.tree);

        parameters.AddIntegerParameter(
            "TakeCount", "N",
            "Number of completed takes recorded.",
            GH_ParamAccess.item);

        parameters.AddIntegerParameter(
            "FrameCount", "F",
            "Total number of frames recorded across all takes.",
            GH_ParamAccess.item);

        parameters.AddBooleanParameter(
            "IsInside", "In",
            "True if the tracker plane origin is currently inside the working volume Brep.",
            GH_ParamAccess.item);

        parameters.AddTextParameter(
            "Status", "S",
            "Recording status description.",
            GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess dataAccess)
    {
        // Protection against Grasshopper multiple iterations on same frame
        if (dataAccess.Iteration > 0)
        {
            dataAccess.SetDataTree(0, new DataTree<object>());
            return;
        }

        if (!dataAccess.GetDataTree(0, out GH_Structure<IGH_Goo> rawTree))
            return;

        Brep? brep = null;
        dataAccess.GetData(1, ref brep);

        var run = true;
        dataAccess.GetData(2, ref run);

        var delayMs = 0.0;
        dataAccess.GetData(3, ref delayMs);

        var tolerance = 0.0;
        dataAccess.GetData(4, ref tolerance);

        var resetAll = false;
        dataAccess.GetData(5, ref resetAll);

        // Convert incoming GH_Structure to DataTree<object>
        var dataTree = ConvertToDataTree(rawTree);

        double currentTime = Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency);

        lock (_gate)
        {
            // 1. Reset
            if (resetAll)
            {
                _allRecordedTakes.Clear();
                _currentRecordingTake.Clear();
                _isRecording = false;

                dataAccess.SetDataTree(0, new DataTree<object>());
                dataAccess.SetData(1, 0);
                dataAccess.SetData(2, 0);
                dataAccess.SetData(3, false);
                dataAccess.SetData(4, "Reset (0 takes)");
                Message = "Cleared";
                return;
            }

            // 2. Brep inside/outside check
            var hasPoint = TryExtractPoint(dataTree, out Point3d trackerPoint);
            var isInside = true;

            if (brep != null && brep.IsValid)
            {
                if (hasPoint)
                {
                    isInside = brep.IsPointInside(trackerPoint, 0.001, false);
                }
                else
                {
                    isInside = false;
                }
            }

            // Check if any tracker in dataTree has physical trigger active
            bool triggerActive = false;
            foreach (var path in dataTree.Paths)
            {
                foreach (var item in dataTree.Branch(path))
                {
                    if (item is ViveTrackerData td && td.Trigger)
                    {
                        triggerActive = true;
                        break;
                    }
                }
                if (triggerActive) break;
            }

            // 3. Recording logic
            var toggle = run && (brep == null || isInside) && (!RequireTriggerToRecord || triggerActive);

            if (toggle)
            {
                if (!_isRecording)
                {
                    _isRecording = true;
                    _currentRecordingTake = new List<DataTree<object>>();
                    _lastRecordTime = currentTime;
                }

                if (_currentRecordingTake.Count == 0 || (currentTime - _lastRecordTime) >= delayMs)
                {
                    var shouldRecord = true;

                    if (dataTree.Paths.Count > 0)
                    {
                        if (_currentRecordingTake.Count > 0)
                        {
                            var lastFrame = _currentRecordingTake.Last();
                            if (lastFrame.Paths.Count > 0 && tolerance > 0.0001)
                            {
                                var p0 = dataTree.Paths[0];
                                var pLast = lastFrame.Paths.Count > 0 ? lastFrame.Paths[0] : null;

                                if (pLast != null)
                                {
                                    var bCurr = dataTree.Branch(p0);
                                    var bLast = lastFrame.Branch(pLast);

                                    // If branch has >1 items [time, plane], compare index 1; otherwise index 0
                                    object? itemCurr = bCurr.Count > 1 ? bCurr[1] : (bCurr.Count > 0 ? bCurr[0] : null);
                                    object? itemLast = bLast.Count > 1 ? bLast[1] : (bLast.Count > 0 ? bLast[0] : null);

                                    if (itemCurr != null && itemLast != null && IsSimilar(itemCurr, itemLast, tolerance))
                                    {
                                        shouldRecord = false;
                                    }
                                }
                            }
                        }

                        if (shouldRecord)
                        {
                            _currentRecordingTake.Add(CloneTree(dataTree));
                            _lastRecordTime = currentTime;
                        }
                    }
                }
            }
            else if (_isRecording)
            {
                // Tracker exited Brep or run was toggled off -> complete current take
                if (_currentRecordingTake.Count > 0)
                {
                    _allRecordedTakes.Add(new List<DataTree<object>>(_currentRecordingTake));
                }
                _isRecording = false;
                _currentRecordingTake = new List<DataTree<object>>();
            }

            // 4. Output generation: structure { ID_Device ; ID_Take ; Frame_Index }
            var outTree = new DataTree<object>();

            void ProcesarToma(List<DataTree<object>> toma, int takeIndex)
            {
                for (int f = 0; f < toma.Count; f++)
                {
                    var frameData = toma[f];
                    foreach (var p in frameData.Paths)
                    {
                        int devId = p.Indices.Length > 0 ? p.Indices[0] : 0;
                        var outPath = new GH_Path(devId, takeIndex, f);
                        foreach (var item in frameData.Branch(p))
                        {
                            if (item is ViveTrackerData td)
                            {
                                outTree.Add(td.Plane, outPath);
                                outTree.Add(td.Trigger, outPath);
                                outTree.Add(td.TriggerValue, outPath);
                                outTree.Add(new GH_ViveTrackerData(td), outPath);
                            }
                            else
                            {
                                outTree.Add(item, outPath);
                            }
                        }
                    }
                }
            }

            for (int t = 0; t < _allRecordedTakes.Count; t++)
            {
                ProcesarToma(_allRecordedTakes[t], t);
            }

            if (_isRecording && _currentRecordingTake.Count > 0)
            {
                ProcesarToma(_currentRecordingTake, _allRecordedTakes.Count);
            }

            int completedTakes = _allRecordedTakes.Count;
            int totalFrames = _allRecordedTakes.Sum(t => t.Count) + _currentRecordingTake.Count;

            string status;
            if (_isRecording)
            {
                status = $"Recording Take #{completedTakes + 1} ({_currentRecordingTake.Count} frames)";
                Message = $"REC #{completedTakes + 1} ({_currentRecordingTake.Count})";
            }
            else if (completedTakes > 0)
            {
                status = brep != null && !isInside
                    ? $"Outside Brep ({completedTakes} takes saved, {totalFrames} frames)"
                    : $"Idle ({completedTakes} takes saved, {totalFrames} frames)";
                Message = $"{completedTakes} takes";
            }
            else
            {
                status = brep != null && !isInside ? "Outside Brep" : "Idle (0 takes)";
                Message = "Idle";
            }

            dataAccess.SetDataTree(0, outTree);
            dataAccess.SetData(1, completedTakes);
            dataAccess.SetData(2, totalFrames);
            dataAccess.SetData(3, isInside);
            dataAccess.SetData(4, status);
        }
    }

    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);
        int takeCount;
        lock (_gate) takeCount = _allRecordedTakes.Count + (_isRecording ? 1 : 0);

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
                _allRecordedTakes.Clear();
                _currentRecordingTake.Clear();
                _isRecording = false;
            }
            ExpireSolution(true);
        }, takeCount > 0);
    }

    public override bool Write(GH_IO.Serialization.GH_IWriter writer)
    {
        writer.SetBoolean("RequireTriggerToRecord", RequireTriggerToRecord);
        return base.Write(writer);
    }

    public override bool Read(GH_IO.Serialization.GH_IReader reader)
    {
        if (reader.ItemExists("RequireTriggerToRecord"))
            RequireTriggerToRecord = reader.GetBoolean("RequireTriggerToRecord");
        return base.Read(reader);
    }

    private static bool IsSimilar(object? dataA, object? dataB, double tol)
    {
        if (dataA == null || dataB == null) return false;
        if (tol <= 0.0001) return false;

        if (dataA is ViveTrackerData tdA && dataB is ViveTrackerData tdB)
            return tdA.Origin.DistanceTo(tdB.Origin) < tol;
        if (dataA is GH_ViveTrackerData gtdA && dataB is GH_ViveTrackerData gtdB)
            return gtdA.Value != null && gtdB.Value != null && gtdA.Value.Origin.DistanceTo(gtdB.Value.Origin) < tol;
        if (dataA is Plane pA && dataB is Plane pB)
            return pA.Origin.DistanceTo(pB.Origin) < tol;
        if (dataA is Point3d ptA && dataB is Point3d ptB)
            return ptA.DistanceTo(ptB) < tol;
        if (dataA is GH_Plane gpA && dataB is GH_Plane gpB)
            return gpA.Value.Origin.DistanceTo(gpB.Value.Origin) < tol;
        if (dataA is GH_Point gptA && dataB is GH_Point gptB)
            return gptA.Value.DistanceTo(gptB.Value) < tol;
        if (dataA is double dA && dataB is double dB)
            return Math.Abs(dA - dB) < tol;

        return dataA.Equals(dataB);
    }

    private static DataTree<object> CloneTree(DataTree<object> tree)
    {
        var clone = new DataTree<object>();
        if (tree == null) return clone;
        foreach (var p in tree.Paths)
        {
            clone.AddRange(tree.Branch(p).Select(x => x), p);
        }
        return clone;
    }

    private static DataTree<object> ConvertToDataTree(GH_Structure<IGH_Goo> rawTree)
    {
        var tree = new DataTree<object>();
        if (rawTree == null) return tree;

        foreach (var path in rawTree.Paths)
        {
            var branch = rawTree.get_Branch(path);
            foreach (var goo in branch)
            {
                if (goo == null) continue;
                object raw = UnwrapGoo(goo);
                tree.Add(raw, path);
            }
        }
        return tree;
    }

    private static object UnwrapGoo(object? item)
    {
        if (item == null) return null!;
        if (item is GH_ViveTrackerData gtd) return gtd.Value;
        if (item is ViveTrackerData td) return td;
        if (item is GH_Plane gp) return gp.Value;
        if (item is GH_Point gpt) return gpt.Value;
        if (item is GH_Number gn) return gn.Value;
        if (item is GH_Integer gi) return gi.Value;
        if (item is GH_String gs) return gs.Value;
        if (item is GH_Boolean gb) return gb.Value;
        if (item is GH_Time gt) return gt.Value;
        if (item is GH_Vector gv) return gv.Value;
        if (item is IGH_Goo goo)
        {
            if (goo.CastTo(out Plane pl)) return pl;
            if (goo.CastTo(out Point3d pt)) return pt;
            if (goo.CastTo(out double d)) return d;
        }
        return item;
    }

    private static bool TryExtractPoint(DataTree<object> data, out Point3d point)
    {
        point = Point3d.Unset;
        if (data == null || data.Paths.Count == 0) return false;

        var firstPath = data.Paths[0];
        var branch = data.Branch(firstPath);
        if (branch == null || branch.Count == 0) return false;

        for (int i = 0; i < branch.Count; i++)
        {
            if (TryGetPoint(branch[i], out point))
                return true;
        }

        return false;
    }

    private static bool TryGetPoint(object? obj, out Point3d point)
    {
        point = Point3d.Unset;
        if (obj == null) return false;

        if (obj is ViveTrackerData td && td.Plane.IsValid) { point = td.Origin; return true; }
        if (obj is GH_ViveTrackerData gtd && gtd.Value != null && gtd.Value.Plane.IsValid) { point = gtd.Value.Origin; return true; }
        if (obj is Plane pl) { point = pl.Origin; return true; }
        if (obj is Point3d pt) { point = pt; return true; }
        if (obj is GH_Plane gpl) { point = gpl.Value.Origin; return true; }
        if (obj is GH_Point gpt) { point = gpt.Value; return true; }
        if (obj is IGH_Goo goo)
        {
            if (goo.CastTo(out Plane cpl)) { point = cpl.Origin; return true; }
            if (goo.CastTo(out Point3d cpt)) { point = cpt; return true; }
        }
        return false;
    }

    private static Bitmap LoadIcon()
    {
        using var stream = typeof(ViveTakeRecorderComponent).Assembly.GetManifestResourceStream("ViveTcpRecorder.Resources.TakeRecorder.png");
        if (stream is null)
            return new Bitmap(24, 24);

        using var source = new Bitmap(stream);
        return new Bitmap(source, new Size(24, 24));
    }
}
