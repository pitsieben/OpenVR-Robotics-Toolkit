#pragma warning disable CA1416 // Validate platform compatibility for System.Drawing.Bitmap

using System.Drawing;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace ViveTcpRecorder;

public sealed class ViveCalibrationRecorderComponent : GH_Component
{
    private static readonly Lazy<Bitmap> ComponentIcon = new(LoadIcon);

    private readonly object _gate = new();
    private readonly List<DataTree<object>> _allFrames = new();
    private readonly List<Plane> _allPlanes = new();
    private readonly List<Point3d> _allPoints = new();

    private bool _wasRecordDown;
    private bool _wasUndoDown;
    private bool _wasPhysicalTriggerDown;

    public ViveCalibrationRecorderComponent()
        : base(
            "Vive Calibration Recorder",
            "ViveCalib",
            "Records individual calibration planes on button click.",
            "Vive",
            "TCP")
    {
    }

    public override Guid ComponentGuid => new("2D8C3E5A-7A12-4C3D-B9E4-61B78923AF44");

    protected override Bitmap Icon => ComponentIcon.Value;

    protected override void RegisterInputParams(GH_InputParamManager parameters)
    {
        parameters.AddGenericParameter(
            "Data", "D",
            "Current tracker plane or data to record on button click.",
            GH_ParamAccess.tree);

        parameters.AddBooleanParameter(
            "Record", "R",
            "Connect to a Grasshopper Button. When clicked (rising edge), captures the current plane.",
            GH_ParamAccess.item,
            false);

        parameters.AddBooleanParameter(
            "Reset", "Rst",
            "Connect to a Grasshopper Button. Deletes all recorded calibration planes.",
            GH_ParamAccess.item,
            false);

        parameters.AddBooleanParameter(
            "Undo", "U",
            "Connect to a Grasshopper Button. Removes the last recorded calibration plane.",
            GH_ParamAccess.item,
            false);
        parameters[3].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager parameters)
    {
        parameters.AddPlaneParameter(
            "Planes", "P",
            "List of recorded calibration planes.",
            GH_ParamAccess.list);

        parameters.AddPointParameter(
            "Points", "Pt",
            "List of origin points of recorded calibration planes.",
            GH_ParamAccess.list);

        parameters.AddGenericParameter(
            "Tree", "T",
            "DataTree of all recorded calibration frames with path structure {Point_Index}.",
            GH_ParamAccess.tree);

        parameters.AddIntegerParameter(
            "Count", "N",
            "Number of calibration planes recorded.",
            GH_ParamAccess.item);

        parameters.AddTextParameter(
            "Status", "S",
            "Calibration recording status.",
            GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess dataAccess)
    {
        if (dataAccess.Iteration > 0)
            return;

        if (!dataAccess.GetDataTree(0, out GH_Structure<IGH_Goo> rawTree))
            return;

        var record = false;
        dataAccess.GetData(1, ref record);

        var reset = false;
        dataAccess.GetData(2, ref reset);

        var undo = false;
        dataAccess.GetData(3, ref undo);

        var dataTree = ConvertToDataTree(rawTree);

        lock (_gate)
        {
            // 1. Reset
            if (reset)
            {
                _allFrames.Clear();
                _allPlanes.Clear();
                _allPoints.Clear();
                _wasRecordDown = false;
                _wasUndoDown = false;

                dataAccess.SetDataList(0, new List<Plane>());
                dataAccess.SetDataList(1, new List<Point3d>());
                dataAccess.SetDataTree(2, new DataTree<object>());
                dataAccess.SetData(3, 0);
                dataAccess.SetData(4, "Reset (0 planes)");
                Message = "Cleared";
                return;
            }

            // 2. Undo (Remove Last) on rising edge
            if (undo && !_wasUndoDown && _allFrames.Count > 0)
            {
                int lastIdx = _allFrames.Count - 1;
                _allFrames.RemoveAt(lastIdx);
                if (_allPlanes.Count > lastIdx) _allPlanes.RemoveAt(lastIdx);
                if (_allPoints.Count > lastIdx) _allPoints.RemoveAt(lastIdx);
            }
            _wasUndoDown = undo;

            // Check physical trigger on tracker
            bool physicalTrigger = false;
            foreach (var path in dataTree.Paths)
            {
                foreach (var item in dataTree.Branch(path))
                {
                    if (item is ViveTrackerData td && td.Trigger)
                    {
                        physicalTrigger = true;
                        break;
                    }
                }
                if (physicalTrigger) break;
            }

            bool triggerRising = physicalTrigger && !_wasPhysicalTriggerDown;
            _wasPhysicalTriggerDown = physicalTrigger;

            // 3. Record on rising edge (Grasshopper button click OR physical pogo pin switch)
            if ((record && !_wasRecordDown) || triggerRising)
            {
                if (dataTree.Paths.Count > 0)
                {
                    _allFrames.Add(CloneTree(dataTree));

                    if (TryExtractPlane(dataTree, out Plane plane))
                    {
                        _allPlanes.Add(plane);
                        _allPoints.Add(plane.Origin);
                    }
                    else if (TryExtractPoint(dataTree, out Point3d point))
                    {
                        _allPlanes.Add(new Plane(point, Vector3d.ZAxis));
                        _allPoints.Add(point);
                    }
                    else
                    {
                        _allPlanes.Add(Plane.WorldXY);
                        _allPoints.Add(Point3d.Origin);
                    }
                }
            }
            _wasRecordDown = record;

            // 4. Output generation
            var outTree = new DataTree<object>();
            for (int i = 0; i < _allFrames.Count; i++)
            {
                var frame = _allFrames[i];
                var outPath = new GH_Path(i);
                foreach (var p in frame.Paths)
                {
                    foreach (var item in frame.Branch(p))
                    {
                        if (item is ViveTrackerData td)
                        {
                            outTree.Add(td.Plane, outPath);
                            outTree.Add(td.Origin, outPath);
                            outTree.Add(td.Trigger, outPath);
                            outTree.Add(new GH_ViveTrackerData(td), outPath);
                        }
                        else
                        {
                            outTree.Add(item, outPath);
                        }
                    }
                }
            }

            int count = _allPlanes.Count;
            string status = count > 0 ? $"{count} calibration planes recorded" : "Idle (0 planes)";

            dataAccess.SetDataList(0, _allPlanes);
            dataAccess.SetDataList(1, _allPoints);
            dataAccess.SetDataTree(2, outTree);
            dataAccess.SetData(3, count);
            dataAccess.SetData(4, status);

            Message = count > 0 ? $"{count} calib pts" : "Idle";
        }
    }

    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);
        int count;
        lock (_gate) count = _allPlanes.Count;

        Menu_AppendItem(menu, "Clear All Calibration Planes", (sender, e) =>
        {
            RecordUndoEvent("Clear All Calibration Planes");
            lock (_gate)
            {
                _allFrames.Clear();
                _allPlanes.Clear();
                _allPoints.Clear();
            }
            ExpireSolution(true);
        }, count > 0);

        Menu_AppendItem(menu, "Remove Last Plane", (sender, e) =>
        {
            RecordUndoEvent("Remove Last Plane");
            lock (_gate)
            {
                if (_allFrames.Count > 0)
                {
                    int last = _allFrames.Count - 1;
                    _allFrames.RemoveAt(last);
                    if (_allPlanes.Count > last) _allPlanes.RemoveAt(last);
                    if (_allPoints.Count > last) _allPoints.RemoveAt(last);
                }
            }
            ExpireSolution(true);
        }, count > 0);
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

    private static bool TryExtractPlane(DataTree<object> data, out Plane plane)
    {
        plane = Plane.Unset;
        if (data == null || data.Paths.Count == 0) return false;

        var firstPath = data.Paths[0];
        var branch = data.Branch(firstPath);
        if (branch == null || branch.Count == 0) return false;

        for (int i = 0; i < branch.Count; i++)
        {
            var item = branch[i];
            if (item is ViveTrackerData td && td.Plane.IsValid) { plane = td.Plane; return true; }
            if (item is GH_ViveTrackerData gtd && gtd.Value != null && gtd.Value.Plane.IsValid) { plane = gtd.Value.Plane; return true; }
            if (item is Plane pl && pl.IsValid) { plane = pl; return true; }
            if (item is GH_Plane gpl && gpl.Value.IsValid) { plane = gpl.Value; return true; }
            if (item is IGH_Goo goo && goo.CastTo(out Plane cpl) && cpl.IsValid) { plane = cpl; return true; }
        }
        return false;
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
            var item = branch[i];
            if (item is ViveTrackerData td && td.Plane.IsValid) { point = td.Origin; return true; }
            if (item is GH_ViveTrackerData gtd && gtd.Value != null && gtd.Value.Plane.IsValid) { point = gtd.Value.Origin; return true; }
            if (item is Point3d pt && pt.IsValid) { point = pt; return true; }
            if (item is GH_Point gpt && gpt.Value.IsValid) { point = gpt.Value; return true; }
            if (item is Plane pl && pl.IsValid) { point = pl.Origin; return true; }
            if (item is GH_Plane gpl && gpl.Value.IsValid) { point = gpl.Value.Origin; return true; }
            if (item is IGH_Goo goo)
            {
                if (goo.CastTo(out Point3d cpt) && cpt.IsValid) { point = cpt; return true; }
                if (goo.CastTo(out Plane cpl) && cpl.IsValid) { point = cpl.Origin; return true; }
            }
        }
        return false;
    }

    private static Bitmap LoadIcon()
    {
        using var stream = typeof(ViveCalibrationRecorderComponent).Assembly.GetManifestResourceStream("ViveTcpRecorder.Resources.CalibrationRecorder.png");
        if (stream is null)
            return new Bitmap(24, 24);

        using var source = new Bitmap(stream);
        return new Bitmap(source, new Size(24, 24));
    }
}
