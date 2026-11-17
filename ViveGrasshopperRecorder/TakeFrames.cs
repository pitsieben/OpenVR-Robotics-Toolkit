using Grasshopper;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace ViveTcpRecorder;

/// <summary>Reading a recorder frame from the Data input, and turning grouped takes into the Takes tree and the analytics records.</summary>
public static class TakeFrames
{
    /// <summary>
    /// One frame: for every Data branch that carries a plane, the plane to record (a plain plane wins; tracker data supplies its
    /// plane only when the branch has no plane) and the trigger state of its tracker data. Device = branch ordinal.
    /// </summary>
    public static DeviceFrame<Plane>[] Extract(GH_Structure<IGH_Goo>? tree, double timeSec, out bool anyTrigger)
    {
        anyTrigger = false;
        var frame = new List<DeviceFrame<Plane>>();
        if (tree == null) return Array.Empty<DeviceFrame<Plane>>();
        for (var b = 0; b < tree.PathCount; b++)
        {
            var branch = tree.Branches[b];
            Plane? plain = null, fromTracker = null;
            var trigger = false; var triggerValue = 0.0;
            foreach (var goo in branch)
            {
                if (goo == null) continue;
                if (goo is GH_ViveTrackerData gtd)
                {
                    var td = gtd.Value;
                    if (td == null) continue;
                    if (td.Plane.IsValid) fromTracker ??= td.Plane;
                    if (td.Trigger) trigger = true;
                    triggerValue = Math.Max(triggerValue, td.TriggerValue);
                    continue;
                }
                if (plain.HasValue) continue;
                if (goo is GH_Plane gp) { if (gp.Value.IsValid) plain = gp.Value; }
                else if (goo.ScriptVariable() is Plane sp) { if (sp.IsValid) plain = sp; }
                else if (goo.CastTo(out Plane cp) && cp.IsValid) plain = cp;
            }
            var plane = plain ?? fromTracker;
            if (plane == null) continue;
            anyTrigger |= trigger;
            frame.Add(new DeviceFrame<Plane> { Device = b, Item = plane.Value, Time = timeSec, Trigger = trigger, TriggerValue = triggerValue });
        }
        return frame.ToArray();
    }

    public static PlaneSample ToSample(Plane p, double timeSec, double toMm)
    {
        var z = p.ZAxis;
        return new PlaneSample(p.OriginX * toMm, p.OriginY * toMm, p.OriginZ * toMm, z.X, z.Y, z.Z, timeSec);
    }

    /// <summary>Takes tree (one branch per take, planes only) and one analytics record per branch.</summary>
    public static void Build(IReadOnlyList<TakeBranch<Plane>> groups, double toMm, out DataTree<Plane> tree, out List<TakeRecord> records)
    {
        tree = new DataTree<Plane>();
        records = new List<TakeRecord>(groups.Count);
        foreach (var g in groups)
        {
            var rec = new TakeRecord
            {
                Take = g.Take,
                Device = g.Device,
                Path = g.Path,
                Planes = g.Frames.Select(f => f.Item).ToArray(),
                Times = g.Frames.Select(f => f.Time).ToArray(),
                Triggers = g.Frames.Select(f => f.Trigger).ToArray(),
            };
            rec.Samples = rec.Planes.Select((p, i) => ToSample(p, rec.Times[i], toMm)).ToArray();
            tree.AddRange(rec.Planes, new GH_Path(g.Path));
            records.Add(rec);
        }
    }
}
