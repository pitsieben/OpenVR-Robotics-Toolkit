using System.Numerics;

namespace ClayViewer.Stock;

/// <summary>One ray/triangle crossing: coordinate along the ray axis and the triangle's unit normal (as wound).</summary>
public struct RayHit
{
    public float T;
    public Vector3 N;
}

/// <summary>
/// Casts a regular lattice of axis-parallel rays through a triangle mesh (used to turn a closed stock mesh into
/// dexels and to find the inside of a closed cutter). Sample points are treated as shifted by an infinitesimal
/// (eps, eps^2), so a ray through a shared edge or vertex hits exactly one of the triangles around it.
/// </summary>
public static class MeshRaster
{
    /// <summary>Rays run along axis <paramref name="f"/> through (ou + i*hu, ov + j*hv) on the axes u=(f+1)%3, v=(f+2)%3.
    /// Result[i*nv + j] is the hit list sorted by T, or null. <paramref name="triFilter"/> (optional) selects triangles.</summary>
    public static List<RayHit>?[] Cast(float[] verts, int[] idx, int f, double ou, double hu, int nu, double ov, double hv, int nv, bool[]? triFilter = null)
    {
        int u = (f + 1) % 3, v = (f + 2) % 3;
        var res = new List<RayHit>?[nu * nv];
        for (int t = 0, ti = 0; t + 2 < idx.Length; t += 3, ti++)
        {
            if (triFilter != null && !triFilter[ti]) continue;
            int a = idx[t] * 3, b = idx[t + 1] * 3, c = idx[t + 2] * 3;
            double ax = verts[a + u], ay = verts[a + v], az = verts[a + f];
            double bx = verts[b + u], by = verts[b + v], bz = verts[b + f];
            double cx = verts[c + u], cy = verts[c + v], cz = verts[c + f];
            var area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            if (area == 0) continue;
            var i0 = Math.Max(0, (int)Math.Ceiling((Math.Min(ax, Math.Min(bx, cx)) - ou) / hu));
            var i1 = Math.Min(nu - 1, (int)Math.Floor((Math.Max(ax, Math.Max(bx, cx)) - ou) / hu));
            var j0 = Math.Max(0, (int)Math.Ceiling((Math.Min(ay, Math.Min(by, cy)) - ov) / hv));
            var j1 = Math.Min(nv - 1, (int)Math.Floor((Math.Max(ay, Math.Max(by, cy)) - ov) / hv));
            if (i0 > i1 || j0 > j1) continue;

            var pa = new Vector3(verts[a], verts[a + 1], verts[a + 2]);
            var n = Vector3.Cross(new Vector3(verts[b], verts[b + 1], verts[b + 2]) - pa, new Vector3(verts[c], verts[c + 1], verts[c + 2]) - pa);
            var nl = n.Length();
            if (nl < 1e-20f) continue;
            n /= nl;
            var s = area > 0 ? 1 : -1;
            for (var i = i0; i <= i1; i++)
            {
                var px = ou + i * hu;
                for (var j = j0; j <= j1; j++)
                {
                    var py = ov + j * hv;
                    if (Side(ax, ay, bx, by, px, py) != s || Side(bx, by, cx, cy, px, py) != s || Side(cx, cy, ax, ay, px, py) != s) continue;
                    // barycentric depth
                    var wa = ((bx - px) * (cy - py) - (by - py) * (cx - px)) / area;
                    var wb = ((cx - px) * (ay - py) - (cy - py) * (ax - px)) / area;
                    var depth = wa * az + wb * bz + (1 - wa - wb) * cz;
                    (res[i * nv + j] ??= new List<RayHit>(4)).Add(new RayHit { T = (float)depth, N = n });
                }
            }
        }
        foreach (var l in res) l?.Sort(static (x, y) => x.T.CompareTo(y.T));
        return res;
    }

    /// <summary>Sign of the edge function of a->b at p, with the infinitesimal-shift tie break. Evaluated on the
    /// canonically ordered edge so both triangles sharing an edge compute exactly opposite values.</summary>
    static int Side(double ax, double ay, double bx, double by, double px, double py)
    {
        var flip = ax > bx || (ax == bx && ay > by);
        if (flip) { (ax, bx) = (bx, ax); (ay, by) = (by, ay); }
        double dx = bx - ax, dy = by - ay;
        var e = dx * (py - ay) - dy * (px - ax);
        int sgn = e > 0 ? 1 : e < 0 ? -1 : dy < 0 ? 1 : dy > 0 ? -1 : dx > 0 ? 1 : dx < 0 ? -1 : 0;
        return flip ? -sgn : sgn;
    }

    /// <summary>Material intervals from a sorted hit list: non-zero winding (so overlapping closed parts union), with an
    /// even-odd fallback when the windings do not balance. Calls emit(enterHit, exitHit) per interval.</summary>
    public static void ToIntervals(List<RayHit> hits, int f, Action<RayHit, RayHit> emit)
    {
        var w = 0;
        foreach (var h in hits) w += Axis(h.N, f) < 0 ? 1 : -1;
        if (w == 0)
        {
            RayHit start = default;
            foreach (var h in hits)
            {
                var before = w;
                w += Axis(h.N, f) < 0 ? 1 : -1;
                if (before == 0) start = h;
                else if (w == 0 && h.T > start.T) emit(start, h);
            }
            return;
        }
        for (var i = 0; i + 1 < hits.Count; i += 2)
            if (hits[i + 1].T > hits[i].T) emit(hits[i], hits[i + 1]);
    }

    public static float Axis(Vector3 v, int f) => f == 0 ? v.X : f == 1 ? v.Y : v.Z;
}
