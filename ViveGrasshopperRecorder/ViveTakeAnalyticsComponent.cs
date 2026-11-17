#pragma warning disable CA1416

using System.Drawing;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

namespace ViveTcpRecorder;

/// <summary>
/// Turns the Take Recorder's Results into numbers: per take (one item per take) frames, duration, path length, speeds and the tilt of the
/// recorded TCP plane against world Z; per plane (tree {take} like the recorder's Takes) tilt and speed; and the job numbers (removed volume,
/// share of the stock). Optionally flags planes steeper than a robot tilt limit. The math lives in <see cref="TakeAnalytics"/>.
/// </summary>
public sealed class ViveTakeAnalyticsComponent : GH_Component
{
    private static readonly Lazy<Bitmap> ComponentIcon = new(() => PlaneIcons.Load("TakeAnalytics.png"));

    public ViveTakeAnalyticsComponent()
        : base("Vive Take Analytics", "TakeStats",
            "Statistics of recorded takes: frames, duration, path length, speed and the tilt of the TCP plane against world Z, per take and per plane, plus removed volume and the share of planes above a robot tilt limit.",
            "Vive", "Utilities")
    {
    }

    public override Guid ComponentGuid => new("E434B40B-A3CB-4B95-8203-5AA9CDEFF36E");
    protected override Bitmap Icon => ComponentIcon.Value;

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddGenericParameter("Results", "Res", "Results output of the Vive Take Recorder.", GH_ParamAccess.item);
        p.AddNumberParameter("Robot Limit", "Lim", "Largest tilt (degrees from world Z) the robot can do. Planes above it are flagged. Default 30.", GH_ParamAccess.item, 30.0);
        p[1].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        // per take: one item per take
        p.AddIntegerParameter("Frames", "Fr", "Planes per take.", GH_ParamAccess.list);
        p.AddNumberParameter("Duration", "Dur", "Duration per take in seconds.", GH_ParamAccess.list);
        p.AddNumberParameter("Path Length", "Len", "Path length per take in mm.", GH_ParamAccess.list);
        p.AddNumberParameter("Mean Speed", "Vavg", "Path length / duration per take in mm/s.", GH_ParamAccess.list);
        p.AddNumberParameter("Max Speed", "Vmax", "Fastest segment per take in mm/s.", GH_ParamAccess.list);
        p.AddNumberParameter("Tilt Min", "TMin", "Smallest tilt per take: angle between the plane's Z axis and world Z, 0 (pointing up) to 180 degrees.", GH_ParamAccess.list);
        p.AddNumberParameter("Tilt Mean", "TAvg", "Mean tilt per take in degrees.", GH_ParamAccess.list);
        p.AddNumberParameter("Tilt Max", "TMax", "Largest tilt per take in degrees.", GH_ParamAccess.list);
        p.AddNumberParameter("% Above Limit", "%>", "Percentage of planes above Robot Limit, per take.", GH_ParamAccess.list);
        p.AddNumberParameter("% Above Limit All", "%>All", "Percentage of planes above Robot Limit over all takes.", GH_ParamAccess.item);
        // per plane: trees with the recorder's Takes paths
        p.AddNumberParameter("Plane Tilt", "Tilt", "Tilt of every plane in degrees, tree like the recorder's Takes.", GH_ParamAccess.tree);
        p.AddNumberParameter("Plane Speed", "Speed", "Speed at every plane in mm/s (first plane copies the second), tree like Takes.", GH_ParamAccess.tree);
        p.AddBooleanParameter("Above Limit", "Flag", "True for planes whose tilt is above Robot Limit, tree like Takes.", GH_ParamAccess.tree);
        // job
        p.AddNumberParameter("Removed Volume", "Vol", "Material removed by the carving, mm3.", GH_ParamAccess.item);
        p.AddNumberParameter("% of Stock", "%Stock", "Removed volume as a percentage of the stock volume.", GH_ParamAccess.item);
        p.AddTextParameter("Summary", "Sum", "Readable summary of the job and every take.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        IGH_Goo? goo = null;
        if (!da.GetData(0, ref goo) || goo == null) return;
        var limit = 30.0;
        da.GetData(1, ref limit);

        var results = goo is GH_ViveTakeResults g ? g.Value : goo.ScriptVariable() as ViveTakeResults;
        if (results == null)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Results must come from the Vive Take Recorder.");
            return;
        }

        var stats = new List<TakeStats>();
        var tilt = new DataTree<double>();
        var speed = new DataTree<double>();
        var flag = new DataTree<bool>();
        foreach (var t in results.Takes)
        {
            var st = TakeAnalytics.Compute(t.Take, t.Samples, limit);
            stats.Add(st);
            var path = new GH_Path(t.Path);
            tilt.AddRange(st.TiltsDeg, path);
            speed.AddRange(st.SpeedsMmS, path);
            flag.AddRange(st.TiltsDeg.Select(x => x > limit), path);
        }

        da.SetDataList(0, stats.Select(s => s.Frames));
        da.SetDataList(1, stats.Select(s => s.DurationSec));
        da.SetDataList(2, stats.Select(s => s.PathLengthMm));
        da.SetDataList(3, stats.Select(s => s.MeanSpeedMmS));
        da.SetDataList(4, stats.Select(s => s.MaxSpeedMmS));
        da.SetDataList(5, stats.Select(s => s.TiltMinDeg));
        da.SetDataList(6, stats.Select(s => s.TiltMeanDeg));
        da.SetDataList(7, stats.Select(s => s.TiltMaxDeg));
        da.SetDataList(8, stats.Select(s => s.PctAboveLimit));
        da.SetData(9, TakeAnalytics.OverallPctAbove(stats));
        da.SetDataTree(10, tilt);
        da.SetDataTree(11, speed);
        da.SetDataTree(12, flag);
        da.SetData(13, results.RemovedMm3);
        da.SetData(14, TakeAnalytics.PctOfStock(results.RemovedMm3, results.StockMm3));
        da.SetData(15, TakeAnalytics.Summary(stats, results.RemovedMm3, results.StockMm3, limit, results.ResolutionMm));
    }
}
