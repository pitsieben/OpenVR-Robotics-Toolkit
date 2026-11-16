#pragma warning disable CA1416

using System.Drawing;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace ViveTcpRecorder;

/// <summary>
/// Straightens recorded planes for robot reproduction: clamps how steep the tool axis may lean
/// away from an upright direction, and optionally removes wrist roll.
/// </summary>
public sealed class VivePlaneStraightenComponent : GH_Component
{
    private static readonly Lazy<Bitmap> ComponentIcon = new(() => PlaneIcons.Load("PlaneStraighten.png"));

    public VivePlaneStraightenComponent()
        : base("Vive Plane Straighten", "Straighten",
            "Straightens recorded planes so a robot can reproduce them: tool axes steeper than Max Tilt are rotated back to Max Tilt (minimal rotation about the plane origin, positions unchanged). Optional Fix Roll removes the twist about the tool axis.",
            "Vive", "Utilities")
    {
    }

    public override Guid ComponentGuid => new("C41E8A57-2B6D-4F93-8E0A-5D17B3C92A68");
    protected override Bitmap Icon => ComponentIcon.Value;

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddGenericParameter("Planes", "P", "Recorded planes: Take Recorder 'Takes' output, or a tree with one list of planes per take.", GH_ParamAccess.tree);
        p.AddNumberParameter("Max Tilt", "T", "Maximum angle (degrees) between the tool axis and Up. 0 = all planes perfectly upright.", GH_ParamAccess.item, 30.0);
        p.AddPlaneParameter("Reference", "R", "Its Z axis is 'Up' (e.g. the work surface plane); its X axis is the roll reference for Fix Roll.", GH_ParamAccess.item, Plane.WorldXY);
        p.AddBooleanParameter("Flip Axis", "F", "Use the plane's -Z as the tool axis (when the tool points down along the tracker plane's Z).", GH_ParamAccess.item, false);
        p.AddBooleanParameter("Fix Roll", "Roll", "Spin each plane about its tool axis so X follows the Reference X: removes wrist twist.", GH_ParamAccess.item, false);
        for (var i = 1; i < p.ParamCount; i++) p[i].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddPlaneParameter("Planes", "P", "Straightened planes, one branch per take.", GH_ParamAccess.tree);
        p.AddNumberParameter("Tilt", "T", "Original tilt of each plane in degrees.", GH_ParamAccess.tree);
        p.AddBooleanParameter("Clamped", "C", "True where the plane was steeper than Max Tilt and has been straightened.", GH_ParamAccess.tree);
        p.AddTextParameter("Status", "S", "Summary.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        if (!da.GetDataTree(0, out GH_Structure<IGH_Goo> tree)) return;
        double maxTilt = 30;
        var reference = Plane.WorldXY;
        bool flip = false, fixRoll = false;
        da.GetData(1, ref maxTilt);
        da.GetData(2, ref reference);
        da.GetData(3, ref flip);
        da.GetData(4, ref fixRoll);

        var outPlanes = new DataTree<Plane>();
        var outTilt = new DataTree<double>();
        var outClamped = new DataTree<bool>();
        int total = 0, clampedCount = 0;
        double worst = 0;

        foreach (var (path, planes) in PlaneTakeTools.GroupTakes(tree))
        {
            foreach (var plane in planes)
            {
                var tilt = PlaneTakeTools.Tilt(plane, reference.ZAxis, flip);
                var straight = PlaneTakeTools.Straighten(plane, reference.ZAxis, reference.XAxis, maxTilt, flip, fixRoll, out var clamped);
                outPlanes.Add(straight, path);
                outTilt.Add(tilt, path);
                outClamped.Add(clamped, path);
                total++;
                if (clamped) clampedCount++;
                worst = Math.Max(worst, tilt);
            }
        }

        if (total == 0)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No planes found in the input.");

        da.SetDataTree(0, outPlanes);
        da.SetDataTree(1, outTilt);
        da.SetDataTree(2, outClamped);
        da.SetData(3, $"{outPlanes.BranchCount} takes, {total} planes; {clampedCount} straightened to {maxTilt:0.#}° (steepest was {worst:0.#}°){(fixRoll ? "; roll fixed" : "")}.");
    }
}

internal static class PlaneIcons
{
    public static Bitmap Load(string name)
    {
        using var stream = typeof(PlaneIcons).Assembly.GetManifestResourceStream("ViveTcpRecorder.Resources." + name);
        if (stream is null) return new Bitmap(24, 24);
        using var source = new Bitmap(stream);
        return new Bitmap(source, new Size(24, 24));
    }
}
