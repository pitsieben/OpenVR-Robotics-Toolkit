#pragma warning disable CA1416

using System.Drawing;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace ViveTcpRecorder;

/// <summary>
/// Removes tracking jitter per take: zero-phase Gaussian smoothing of position and orientation
/// (separate strengths), optional resampling to equal spacing for robot targets.
/// </summary>
public sealed class ViveTakeSmoothComponent : GH_Component
{
    private static readonly Lazy<Bitmap> ComponentIcon = new(() => PlaneIcons.Load("TakeSmooth.png"));

    public ViveTakeSmoothComponent()
        : base("Vive Take Smooth", "Smooth",
            "Smooths recorded planes per take to remove jitter. Position and angle are smoothed separately (Gaussian, no lag). Optionally resamples the take to equal spacing.",
            "Vive", "Utilities")
    {
    }

    public override Guid ComponentGuid => new("E8B2D4F1-6A3C-4B7E-9D05-71C4A8E2F3B9");
    protected override Bitmap Icon => ComponentIcon.Value;

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddGenericParameter("Planes", "P", "Recorded planes: Take Recorder 'Takes' output, or a tree with one list of planes per take.", GH_ParamAccess.tree);
        p.AddNumberParameter("Position", "Pos", "Position smoothing strength in frames (Gaussian sigma). 0 = off. At 50 fps, 3 ≈ 60 ms window.", GH_ParamAccess.item, 3.0);
        p.AddNumberParameter("Angle", "Ang", "Orientation smoothing strength in frames (Gaussian sigma). 0 = off. Angle jitter is usually larger, so this is often higher than Position.", GH_ParamAccess.item, 5.0);
        p.AddBooleanParameter("Keep Ends", "E", "Keep the first and last plane of each take exactly where they were recorded.", GH_ParamAccess.item, true);
        p.AddNumberParameter("Spacing", "Sp", "Resample each take to equal spacing along the path (model units, e.g. mm). 0 = keep the recorded frames.", GH_ParamAccess.item, 0.0);
        p.AddIntegerParameter("Degree", "D", "Degree of the output curve per take (1 = polyline, 3 = smooth curve).", GH_ParamAccess.item, 3);
        for (var i = 1; i < p.ParamCount; i++) p[i].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddPlaneParameter("Planes", "P", "Smoothed planes, one branch per take.", GH_ParamAccess.tree);
        p.AddCurveParameter("Curve", "C", "Path curve through the smoothed plane origins, one per take.", GH_ParamAccess.tree);
        p.AddNumberParameter("Position Change", "dP", "Largest distance a plane was moved by smoothing, per take.", GH_ParamAccess.tree);
        p.AddNumberParameter("Angle Change", "dA", "Largest rotation (degrees) applied by smoothing, per take.", GH_ParamAccess.tree);
        p.AddTextParameter("Status", "S", "Summary.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        if (!da.GetDataTree(0, out GH_Structure<IGH_Goo> tree)) return;
        double posSigma = 3, rotSigma = 5, spacing = 0;
        var keepEnds = true;
        var degree = 3;
        da.GetData(1, ref posSigma);
        da.GetData(2, ref rotSigma);
        da.GetData(3, ref keepEnds);
        da.GetData(4, ref spacing);
        da.GetData(5, ref degree);

        var outPlanes = new DataTree<Plane>();
        var outCurves = new DataTree<Curve>();
        var outDp = new DataTree<double>();
        var outDa = new DataTree<double>();
        int takes = 0, frames = 0;
        double maxDp = 0, maxDa = 0;

        foreach (var (path, planes) in PlaneTakeTools.GroupTakes(tree))
        {
            var smooth = PlaneTakeTools.Smooth(planes, Math.Max(0, posSigma), Math.Max(0, rotSigma), keepEnds);
            var (dp, dAng) = PlaneTakeTools.MaxDeviation(planes, smooth);
            var result = PlaneTakeTools.Resample(smooth, spacing);

            outPlanes.AddRange(result, path);
            outDp.Add(dp, path);
            outDa.Add(dAng, path);
            var pts = result.Select(pl => pl.Origin).ToList();
            if (pts.Count >= 2)
                outCurves.Add(degree <= 1 || pts.Count < 4
                    ? new PolylineCurve(pts)
                    : Curve.CreateInterpolatedCurve(pts, Math.Clamp(degree, 1, 5)), path);

            takes++;
            frames += result.Count;
            maxDp = Math.Max(maxDp, dp);
            maxDa = Math.Max(maxDa, dAng);
        }

        if (takes == 0)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No planes found in the input.");

        da.SetDataTree(0, outPlanes);
        da.SetDataTree(1, outCurves);
        da.SetDataTree(2, outDp);
        da.SetDataTree(3, outDa);
        da.SetData(4, $"{takes} takes, {frames} planes out; max change {maxDp:0.##} units / {maxDa:0.##}°.");
    }
}
