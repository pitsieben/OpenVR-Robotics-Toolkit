using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace ViveTcpRecorder;

/// <summary>One take of one device: the recorded planes (document units), their times, trigger states and the mm samples used for analytics.</summary>
public sealed class TakeRecord
{
    public int Take, Device;
    /// <summary>Path of this take in the recorder's Takes tree: {take} or {device;take}.</summary>
    public int[] Path = Array.Empty<int>();
    public Plane[] Planes = Array.Empty<Plane>();
    public double[] Times = Array.Empty<double>();     // seconds
    public bool[] Triggers = Array.Empty<bool>();
    public PlaneSample[] Samples = Array.Empty<PlaneSample>();   // millimetres, built from Planes
}

/// <summary>Everything the Vive Take Analytics component needs: the takes and the job numbers reported by the viewer.</summary>
public sealed class ViveTakeResults
{
    public IReadOnlyList<TakeRecord> Takes = Array.Empty<TakeRecord>();
    public double RemovedMm3, StockMm3, ResolutionMm;

    public override string ToString() =>
        $"Take results: {Takes.Count} takes, {Takes.Sum(t => t.Planes.Length)} planes, removed {RemovedMm3:0} mm3";
}

/// <summary>Grasshopper wrapper so the recorder can hand a ViveTakeResults over a wire.</summary>
public sealed class GH_ViveTakeResults : GH_Goo<ViveTakeResults>
{
    public GH_ViveTakeResults() { }
    public GH_ViveTakeResults(ViveTakeResults value) : base(value) { }

    public override bool IsValid => Value != null;
    public override string TypeName => "ViveTakeResults";
    public override string TypeDescription => "Recorded takes (planes, times, triggers) and carving job statistics";
    public override IGH_Goo Duplicate() => new GH_ViveTakeResults(Value);
    public override string ToString() => Value?.ToString() ?? "Empty take results";

    public override bool CastFrom(object source)
    {
        switch (source)
        {
            case ViveTakeResults r: Value = r; return true;
            case GH_ViveTakeResults g: Value = g.Value; return true;
        }
        return false;
    }

    public override bool CastTo<Q>(ref Q target)
    {
        if (Value != null && typeof(Q).IsAssignableFrom(typeof(ViveTakeResults))) { target = (Q)(object)Value; return true; }
        return false;
    }
}
