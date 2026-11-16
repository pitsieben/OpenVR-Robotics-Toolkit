using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace ViveTcpRecorder;

/// <summary>Shared helpers: group recorded data into takes, and pure plane math (no Grasshopper state).</summary>
public static class PlaneTakeTools
{
    /// <summary>
    /// Groups a tree into takes of planes, in order.
    /// Take Recorder layout {dev;take;frame} (one branch per frame, several items) → take key {dev;take}.
    /// Any other layout (e.g. {take} with a list of planes) → each branch is one take.
    /// Only planes are used: GH_Plane, Plane, or Vive tracker data (its plane).
    /// </summary>
    public static List<(GH_Path Path, List<Plane> Planes)> GroupTakes(GH_Structure<IGH_Goo> tree)
    {
        var takes = new List<(GH_Path, List<Plane>)>();
        var index = new Dictionary<string, int>();
        var frameLayout = tree.Paths.Count > 0 && tree.Paths.All(p => p.Length >= 3);

        for (var b = 0; b < tree.PathCount; b++)
        {
            var path = tree.Paths[b];
            var key = frameLayout ? path.CullElement() : path;
            var planes = tree.Branches[b].Select(AsPlane).Where(p => p.HasValue).Select(p => p!.Value).ToList();
            if (planes.Count == 0) continue;
            if (frameLayout) planes = planes.Take(1).ToList(); // one plane per frame branch

            if (!index.TryGetValue(key.ToString(), out var i))
            {
                i = takes.Count;
                index[key.ToString()] = i;
                takes.Add((key, new List<Plane>()));
            }
            takes[i].Item2.AddRange(planes);
        }
        return takes;
    }

    private static Plane? AsPlane(IGH_Goo? goo)
    {
        if (goo is null) return null;
        if (goo is GH_Plane gp) return gp.Value;
        if (goo.ScriptVariable() is Plane p) return p;
        if (goo is GH_ViveTrackerData td) return td.Value.Plane;
        var plane = Plane.Unset;
        return goo.CastTo(out plane) && plane.IsValid ? plane : null;
    }

    // ---------- straighten ----------

    /// <summary>Angle in degrees between the plane's tool axis and the upright axis.</summary>
    public static double Tilt(Plane p, Vector3d up, bool flip)
    {
        var axis = flip ? -p.ZAxis : p.ZAxis;
        return Rhino.RhinoMath.ToDegrees(Vector3d.VectorAngle(axis, up));
    }

    /// <summary>
    /// Rotates the plane about its origin so its tool axis is at most maxTiltDeg from 'up' (minimal rotation).
    /// If fixRoll, the plane is then spun about its tool axis so X follows the projection of refX (no wrist twist).
    /// </summary>
    public static Plane Straighten(Plane p, Vector3d up, Vector3d refX, double maxTiltDeg, bool flip, bool fixRoll, out bool clamped)
    {
        up.Unitize();
        var result = p;
        clamped = false;
        var axis = flip ? -p.ZAxis : p.ZAxis;
        var tilt = Vector3d.VectorAngle(axis, up);
        var max = Rhino.RhinoMath.ToRadians(Math.Max(0, maxTiltDeg));
        if (tilt > max + 1e-9)
        {
            var rotAxis = Vector3d.CrossProduct(axis, up);
            if (rotAxis.IsTiny(1e-12)) // pointing exactly opposite to up: any perpendicular works
                rotAxis = p.XAxis;
            result = new Plane(p.Origin, Rotate(p.XAxis, tilt - max, rotAxis), Rotate(p.YAxis, tilt - max, rotAxis));
            clamped = true;
        }

        if (fixRoll)
        {
            var z = flip ? -result.ZAxis : result.ZAxis;
            var x = refX - (refX * z) * z;           // project reference X onto the plane
            if (x.IsTiny(1e-9)) x = Vector3d.CrossProduct(Vector3d.CrossProduct(z, result.XAxis), z);
            x.Unitize();
            var y = Vector3d.CrossProduct(result.ZAxis, x);
            result = new Plane(result.Origin, x, y);
        }
        return result;
    }

    // ---------- smoothing ----------

    private static double[] Kernel(double sigma)
    {
        var r = Math.Max(1, (int)Math.Ceiling(3 * sigma));
        var k = new double[2 * r + 1];
        for (var i = -r; i <= r; i++) k[i + r] = Math.Exp(-0.5 * i * i / (sigma * sigma));
        return k;
    }

    /// <summary>
    /// Zero-phase Gaussian smoothing per take. Position and orientation have separate sigmas (in frames).
    /// Orientation is averaged as sign-aligned, normalised quaternions (accurate for the small angles jitter has).
    /// Ends are handled by shrinking the window symmetrically, so the first/last frame stays put when keepEnds.
    /// </summary>
    public static List<Plane> Smooth(IReadOnlyList<Plane> planes, double posSigma, double rotSigma, bool keepEnds)
    {
        var n = planes.Count;
        if (n < 3 || (posSigma <= 0 && rotSigma <= 0)) return planes.ToList();

        var pts = planes.Select(p => p.Origin).ToArray();
        var quats = new Quaternion[n];
        for (var i = 0; i < n; i++)
        {
            quats[i] = Quaternion.Rotation(Plane.WorldXY, planes[i]);
            if (i > 0 && Dot(quats[i], quats[i - 1]) < 0) quats[i] = Neg(quats[i]); // keep hemisphere continuous
        }

        var kp = posSigma > 0 ? Kernel(posSigma) : null;
        var kr = rotSigma > 0 ? Kernel(rotSigma) : null;
        var outPlanes = new List<Plane>(n);
        for (var i = 0; i < n; i++)
        {
            var origin = pts[i];
            if (kp != null) origin = (Point3d)Average(i, n, kp, keepEnds, j => (Vector3d)pts[j]);

            var q = quats[i];
            if (kr != null)
            {
                var abc = Average(i, n, kr, keepEnds, j => new Vector3d(quats[j].B, quats[j].C, quats[j].D));
                var w = Average(i, n, kr, keepEnds, j => new Vector3d(quats[j].A, 0, 0)).X;
                q = new Quaternion(w, abc.X, abc.Y, abc.Z);
                q.Unitize();
            }

            q.GetRotation(out Plane rotated);
            outPlanes.Add(new Plane(origin, rotated.XAxis, rotated.YAxis));
        }
        return outPlanes;
    }

    private static Vector3d Average(int i, int n, double[] k, bool keepEnds, Func<int, Vector3d> get)
    {
        var radius = (k.Length - 1) / 2;
        var reach = keepEnds ? Math.Min(radius, Math.Min(i, n - 1 - i)) : radius;
        var sum = Vector3d.Zero;
        var wsum = 0.0;
        for (var d = -reach; d <= reach; d++)
        {
            var j = Math.Clamp(i + d, 0, n - 1); // clamp = hold the end value when not keeping ends
            sum += k[d + radius] * get(j);
            wsum += k[d + radius];
        }
        return sum / wsum;
    }

    /// <summary>Rodrigues rotation of a vector (managed, no native Rhino call).</summary>
    public static Vector3d Rotate(Vector3d v, double angle, Vector3d axis)
    {
        axis.Unitize();
        var c = Math.Cos(angle);
        var s = Math.Sin(angle);
        return v * c + Vector3d.CrossProduct(axis, v) * s + axis * (axis * v) * (1 - c);
    }

    private static Quaternion Neg(Quaternion q) => new(-q.A, -q.B, -q.C, -q.D);

    private static double Dot(Quaternion a, Quaternion b) => a.A * b.A + a.B * b.B + a.C * b.C + a.D * b.D;

    /// <summary>Resamples a take to equal spacing along its path (mm); orientation is slerped. spacing ≤ 0 = off.</summary>
    public static List<Plane> Resample(IReadOnlyList<Plane> planes, double spacing)
    {
        if (spacing <= 0 || planes.Count < 2) return planes.ToList();
        var cum = new double[planes.Count];
        for (var i = 1; i < planes.Count; i++) cum[i] = cum[i - 1] + planes[i].Origin.DistanceTo(planes[i - 1].Origin);
        var total = cum[^1];
        if (total < spacing) return new List<Plane> { planes[0], planes[^1] };

        var result = new List<Plane>();
        var seg = 0;
        var steps = (int)Math.Floor(total / spacing);
        for (var s = 0; s <= steps; s++)
        {
            var t = s * spacing;
            while (seg < planes.Count - 2 && cum[seg + 1] < t) seg++;
            var len = cum[seg + 1] - cum[seg];
            var f = len > 1e-12 ? (t - cum[seg]) / len : 0;
            result.Add(Lerp(planes[seg], planes[seg + 1], f));
        }
        if (total - steps * spacing > 1e-6) result.Add(planes[^1]);
        return result;
    }

    private static Plane Lerp(Plane a, Plane b, double t)
    {
        var qa = Quaternion.Rotation(Plane.WorldXY, a);
        var qb = Quaternion.Rotation(Plane.WorldXY, b);
        if (Dot(qa, qb) < 0) qb = Neg(qb);
        var q = Quaternion.Slerp(qa, qb, t);
        q.GetRotation(out Plane r);
        return new Plane(a.Origin + t * (b.Origin - a.Origin), r.XAxis, r.YAxis);
    }

    /// <summary>Largest position change (mm) and angle change (deg) between two equally long plane lists.</summary>
    public static (double Pos, double Ang) MaxDeviation(IReadOnlyList<Plane> a, IReadOnlyList<Plane> b)
    {
        double pos = 0, ang = 0;
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            pos = Math.Max(pos, a[i].Origin.DistanceTo(b[i].Origin));
            var qa = Quaternion.Rotation(Plane.WorldXY, a[i]);
            var qb = Quaternion.Rotation(Plane.WorldXY, b[i]);
            ang = Math.Max(ang, Rhino.RhinoMath.ToDegrees(2 * Math.Acos(Math.Min(1, Math.Abs(Dot(qa, qb))))));
        }
        return (pos, ang);
    }
}
