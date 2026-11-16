using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ViveClay.Protocol;

namespace ClayViewer.Stock;

/// <summary>
/// The cutter as a narrow-band signed distance field on a regular grid in the tool-local frame (negative inside).
/// Built once per cutter mesh; carving then only needs trilinear lookups along the dexel rays.
///
/// Thin and open cutters: every connected part of the mesh is classified as closed (a solid) or open (a sheet).
///  - open sheets (a single-surface knife blade, the filled face of a wire loop) become a slab of
///    <c>minThickness</c> centred on the surface;
///  - closed parts keep their true shape, except where they are locally thinner than <c>minThickness</c>
///    (measured by casting a ray through the part from each triangle): there the surface is pushed out just
///    enough to reach <c>minThickness</c>. Thick parts and the outline of a blade are not inflated.
/// So a blade of a few tenths of a millimetre always cuts a groove the stock grid can represent.
/// </summary>
public sealed class CutterSdf
{
    public readonly int Nx, Ny, Nz;
    public readonly float Cell;
    public readonly Vector3 Origin;          // position of grid point (0,0,0), tool-local mm
    public readonly float[] D;               // signed distance in mm, clamped to +-Band
    public readonly float Band;
    public readonly string Info;
    public Vector3 Min => Origin;
    public Vector3 Max => Origin + new Vector3(Nx - 1, Ny - 1, Nz - 1) * Cell;

    const int BandCells = 5;
    const long MaxCells = 6_000_000;

    CutterSdf(int nx, int ny, int nz, float cell, Vector3 origin, float[] d, float band, string info)
    { Nx = nx; Ny = ny; Nz = nz; Cell = cell; Origin = origin; D = d; Band = band; Info = info; }

    public static CutterSdf? Build(MeshData mesh, double resMm, double minThicknessMm)
    {
        var v = mesh.Vertices;
        // ---- triangles (degenerate ones dropped)
        var tris = new List<(Vector3 a, Vector3 b, Vector3 c, int ia, int ib, int ic)>();
        var weld = new Dictionary<(int, int, int), int>();
        int Weld(int i)
        {
            // weld within ~0.25 micron so unwelded creases and float noise (e.g. sphere poles) do not open the mesh
            var key = ((int)MathF.Round(v[i * 3] * 4096f), (int)MathF.Round(v[i * 3 + 1] * 4096f), (int)MathF.Round(v[i * 3 + 2] * 4096f));
            if (!weld.TryGetValue(key, out var w)) weld[key] = w = weld.Count;
            return w;
        }
        Vector3 P(int i) => new(v[i * 3], v[i * 3 + 1], v[i * 3 + 2]);
        var nv = v.Length / 3;
        for (var t = 0; t + 2 < mesh.Indices.Length; t += 3)
        {
            int i0 = mesh.Indices[t], i1 = mesh.Indices[t + 1], i2 = mesh.Indices[t + 2];
            if ((uint)i0 >= nv || (uint)i1 >= nv || (uint)i2 >= nv) continue;
            Vector3 a = P(i0), b = P(i1), c = P(i2);
            if (Vector3.Cross(b - a, c - a).LengthSquared() < 1e-20f) continue;
            int w0 = Weld(i0), w1 = Weld(i1), w2 = Weld(i2);
            if (w0 == w1 || w1 == w2 || w0 == w2) continue;
            tris.Add((a, b, c, w0, w1, w2));
        }
        var T = tris.Count;
        if (T == 0) return null;

        // ---- connected parts, closed or open
        var parent = new int[weld.Count];
        for (var i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var edges = new Dictionary<(int, int), int>();
        foreach (var t in tris)
        {
            parent[Find(t.ia)] = Find(t.ib); parent[Find(t.ia)] = Find(t.ic);
            foreach (var (p, q) in new[] { (t.ia, t.ib), (t.ib, t.ic), (t.ic, t.ia) })
            {
                var k = p < q ? (p, q) : (q, p);
                edges[k] = edges.GetValueOrDefault(k) + 1;
            }
        }
        var openPart = new HashSet<int>();
        foreach (var (k, n) in edges) if (n != 2) openPart.Add(Find(k.Item1));
        var closed = new bool[T];
        var partVol = new Dictionary<int, double>();
        var nClosed = 0;
        for (var t = 0; t < T; t++)
        {
            var root = Find(tris[t].ia);
            closed[t] = !openPart.Contains(root);
            if (!closed[t]) continue;
            nClosed++;
            partVol[root] = partVol.GetValueOrDefault(root) + Vector3.Dot(tris[t].a, Vector3.Cross(tris[t].b, tris[t].c));
        }

        // ---- local thickening of closed parts: delta per triangle corner
        var hmin = (float)(minThicknessMm * 0.5);
        var delta = new float[T * 3];
        var thinned = 0;
        Array.Fill(delta, hmin);                              // open sheets: half thickness everywhere
        if (nClosed > 0)
        {
            var measure = nClosed <= 30000;
            var corners = nClosed <= 6000;
            Parallel.For(0, T, t =>
            {
                if (!closed[t]) return;
                delta[t * 3] = delta[t * 3 + 1] = delta[t * 3 + 2] = 0;
                if (!measure) return;
                var (a, b, c, ia, _, _) = tris[t];
                var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
                if (partVol[Find2(parent, ia)] < 0) n = -n;   // outward
                var cen = (a + b + c) / 3f;
                for (var k = 0; k < 3; k++)
                {
                    var p = corners ? Vector3.Lerp(k == 0 ? a : k == 1 ? b : c, cen, 0.15f) : cen;
                    var th = Thickness(tris, closed, t, p, -n);
                    var dl = MathF.Max(0, hmin - th * 0.5f);
                    if (corners) delta[t * 3 + k] = dl; else { delta[t * 3] = delta[t * 3 + 1] = delta[t * 3 + 2] = dl; break; }
                }
                if (delta[t * 3] > 0 || delta[t * 3 + 1] > 0 || delta[t * 3 + 2] > 0) Interlocked.Increment(ref thinned);
            });
        }

        // ---- grid
        Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
        foreach (var t in tris) { mn = Vector3.Min(mn, Vector3.Min(t.a, Vector3.Min(t.b, t.c))); mx = Vector3.Max(mx, Vector3.Max(t.a, Vector3.Max(t.b, t.c))); }
        var cell = (float)(resMm / 3.0);
        int nx, ny, nz; float band, pad;
        while (true)
        {
            band = BandCells * cell; pad = band + hmin + cell;
            nx = (int)MathF.Ceiling((mx.X - mn.X + 2 * pad) / cell) + 1;
            ny = (int)MathF.Ceiling((mx.Y - mn.Y + 2 * pad) / cell) + 1;
            nz = (int)MathF.Ceiling((mx.Z - mn.Z + 2 * pad) / cell) + 1;
            if ((long)nx * ny * nz <= MaxCells) break;
            cell *= 1.1f;
        }
        var origin = mn - new Vector3(pad);
        var d = new float[nx * ny * nz];
        var reach = band + hmin + cell * 0.5f;
        var reach2 = reach * reach;

        // ---- inside test for closed parts (rays along X)
        byte[]? inside = null;
        if (nClosed > 0)
        {
            inside = new byte[d.Length];
            var flat = new float[T * 9]; var idx = new int[T * 3];
            for (var t = 0; t < T; t++)
            {
                var (a, b, c, _, _, _) = tris[t];
                a.CopyTo(flat, t * 9); b.CopyTo(flat, t * 9 + 3); c.CopyTo(flat, t * 9 + 6);
                idx[t * 3] = t * 3; idx[t * 3 + 1] = t * 3 + 1; idx[t * 3 + 2] = t * 3 + 2;
            }
            var hits = MeshRaster.Cast(flat, idx, 0, origin.Y, cell, ny, origin.Z, cell, nz, closed);
            for (var iy = 0; iy < ny; iy++)
                for (var iz = 0; iz < nz; iz++)
                {
                    var l = hits[iy * nz + iz];
                    if (l == null) continue;
                    var row = (iz * ny + iy) * nx;
                    MeshRaster.ToIntervals(l, 0, (lo, hi) =>
                    {
                        var i0 = Math.Max(0, (int)MathF.Ceiling((lo.T - origin.X) / cell));
                        var i1 = Math.Min(nx - 1, (int)MathF.Ceiling((hi.T - origin.X) / cell) - 1);
                        for (var i = i0; i <= i1; i++) inside[row + i] = 1;
                    });
                }
        }

        // ---- unsigned distance (+ thickening of the nearest triangle), slice by slice
        Parallel.For(0, nz, iz =>
        {
            var z = origin.Z + iz * cell;
            var best = new float[nx * ny];
            var bestDelta = new float[nx * ny];
            Array.Fill(best, float.MaxValue);
            for (var t = 0; t < T; t++)
            {
                var (a, b, c, _, _, _) = tris[t];
                if (MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) - reach > z || MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) + reach < z) continue;
                var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
                var i0 = Math.Max(0, (int)MathF.Ceiling((MathF.Min(a.X, MathF.Min(b.X, c.X)) - reach - origin.X) / cell));
                var i1 = Math.Min(nx - 1, (int)MathF.Floor((MathF.Max(a.X, MathF.Max(b.X, c.X)) + reach - origin.X) / cell));
                var j0 = Math.Max(0, (int)MathF.Ceiling((MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) - reach - origin.Y) / cell));
                var j1 = Math.Min(ny - 1, (int)MathF.Floor((MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) + reach - origin.Y) / cell));
                float d0 = delta[t * 3], d1 = delta[t * 3 + 1], d2 = delta[t * 3 + 2];
                for (var j = j0; j <= j1; j++)
                    for (var i = i0; i <= i1; i++)
                    {
                        var p = new Vector3(origin.X + i * cell, origin.Y + j * cell, z);
                        var pd = Vector3.Dot(n, p - a);
                        if (pd > reach || pd < -reach) continue;
                        var q = ClosestPoint(p, a, b, c, out var bv, out var bw);
                        var dist2 = Vector3.DistanceSquared(p, q);
                        var k = j * nx + i;
                        if (dist2 > reach2 || dist2 >= best[k]) continue;
                        best[k] = dist2;
                        bestDelta[k] = d0 * (1 - bv - bw) + d1 * bv + d2 * bw;
                    }
            }
            for (var k = 0; k < best.Length; k++)
            {
                var g = iz * nx * ny + k;
                var isIn = inside != null && inside[g] != 0;
                var u = best[k] == float.MaxValue ? reach : MathF.Sqrt(best[k]);
                d[g] = Math.Clamp((isIn ? -u : u) - bestDelta[k], -band, band);
            }
        });

        var kind = nClosed == T ? "closed" : nClosed == 0 ? "open sheet" : "mixed closed/open";
        var info = $"{kind}, {T} tris, sdf {nx}x{ny}x{nz} @{cell:0.###}mm, min thickness {minThicknessMm:0.###}mm" +
                   (nClosed > 0 ? $", {thinned} thin tris thickened" : "") + (nClosed > 30000 ? " (too many triangles: thin-part detection skipped)" : "");
        return new CutterSdf(nx, ny, nz, cell, origin, d, band, info);
    }

    static int Find2(int[] parent, int x) { while (parent[x] != x) x = parent[x]; return x; }

    /// <summary>Distance from p along dir to the nearest other closed triangle (float.MaxValue when none).</summary>
    static float Thickness(List<(Vector3 a, Vector3 b, Vector3 c, int ia, int ib, int ic)> tris, bool[] closed, int self, Vector3 p, Vector3 dir)
    {
        var best = float.MaxValue;
        for (var t = 0; t < tris.Count; t++)
        {
            if (t == self || !closed[t]) continue;
            var (a, b, c, _, _, _) = tris[t];
            Vector3 e1 = b - a, e2 = c - a;
            var h = Vector3.Cross(dir, e2);
            var det = Vector3.Dot(e1, h);
            if (det > -1e-12f && det < 1e-12f) continue;
            var inv = 1f / det;
            var s = p - a;
            var uu = Vector3.Dot(s, h) * inv;
            if (uu < -1e-5f || uu > 1.00001f) continue;
            var q = Vector3.Cross(s, e1);
            var vv = Vector3.Dot(dir, q) * inv;
            if (vv < -1e-5f || uu + vv > 1.00001f) continue;
            var tt = Vector3.Dot(e2, q) * inv;
            if (tt > 1e-5f && tt < best) best = tt;
        }
        return best;
    }

    /// <summary>Closest point on triangle abc to p (Ericson); result = a + v*(b-a) + w*(c-a).</summary>
    static Vector3 ClosestPoint(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out float v, out float w)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) { v = 0; w = 0; return a; }
        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) { v = 1; w = 0; return b; }
        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) { v = d1 / (d1 - d3); w = 0; return a + v * ab; }
        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) { v = 0; w = 1; return c; }
        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) { w = d2 / (d2 - d6); v = 0; return a + w * ac; }
        var va = d3 * d6 - d5 * d4;
        if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0) { w = (d4 - d3) / ((d4 - d3) + (d5 - d6)); v = 1 - w; return b + w * (c - b); }
        var denom = 1f / (va + vb + vc);
        v = vb * denom; w = vc * denom;
        return a + ab * v + ac * w;
    }

    /// <summary>Trilinear distance at grid coordinates (clamped to the grid).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Sample(Vector3 g)
    {
        int ix = (int)g.X, iy = (int)g.Y, iz = (int)g.Z;
        if ((uint)ix >= (uint)(Nx - 1)) ix = g.X < 0 ? 0 : Nx - 2;
        if ((uint)iy >= (uint)(Ny - 1)) iy = g.Y < 0 ? 0 : Ny - 2;
        if ((uint)iz >= (uint)(Nz - 1)) iz = g.Z < 0 ? 0 : Nz - 2;
        float fx = Math.Clamp(g.X - ix, 0f, 1f), fy = Math.Clamp(g.Y - iy, 0f, 1f), fz = Math.Clamp(g.Z - iz, 0f, 1f);
        ref var p = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(D), (iz * Ny + iy) * Nx + ix);
        ref var q = ref Unsafe.Add(ref p, Nx * Ny);
        var a = p + (Unsafe.Add(ref p, 1) - p) * fx;
        var b = Unsafe.Add(ref p, Nx) + (Unsafe.Add(ref p, Nx + 1) - Unsafe.Add(ref p, Nx)) * fx;
        var c = q + (Unsafe.Add(ref q, 1) - q) * fx;
        var e = Unsafe.Add(ref q, Nx) + (Unsafe.Add(ref q, Nx + 1) - Unsafe.Add(ref q, Nx)) * fx;
        var ab = a + (b - a) * fy;
        var ce = c + (e - c) * fy;
        return ab + (ce - ab) * fz;
    }

    /// <summary>Distance gradient (tool-local direction, not normalised) by central differences over +-0.75 cells.
    /// <paramref name="bend"/> is the summed second difference at a surface point (d = 0 there), relative to the
    /// step: 0 on a flat face, large next to an edge or on a tightly curved part.</summary>
    public Vector3 Gradient(Vector3 g, out float bend)
    {
        const float e = 0.75f;
        float xp = Sample(g + new Vector3(e, 0, 0)), xm = Sample(g - new Vector3(e, 0, 0));
        float yp = Sample(g + new Vector3(0, e, 0)), ym = Sample(g - new Vector3(0, e, 0));
        float zp = Sample(g + new Vector3(0, 0, e)), zm = Sample(g - new Vector3(0, 0, e));
        bend = (MathF.Abs(xp + xm) + MathF.Abs(yp + ym) + MathF.Abs(zp + zm)) / (e * Cell);
        return new Vector3(xp - xm, yp - ym, zp - zm);
    }
}
