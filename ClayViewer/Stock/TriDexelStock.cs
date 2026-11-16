using System.Diagnostics;
using System.Numerics;
using ViveClay.Protocol;

namespace ClayViewer.Stock;

/// <summary>One end of a material interval on a dexel ray: coordinate along the ray axis, the outward surface
/// normal there (packed 10:10:10, plus a 2-bit confidence in the top bits: 3 = taken on a smooth part of the cutter,
/// lower = taken next to a cutter edge where the normal is blurred) and the stock-clock time it was cut
/// (very negative = original skin).</summary>
public struct Hit
{
    public float T;
    public float Time;
    public uint N;
}

/// <summary>
/// Tri-dexel clay stock with Hermite data.
///
/// Representation: three families of axis-parallel rays (X, Y, Z) through the cell centres of a regular grid
/// (cell = requested resolution, adjusted so the cells tile the box exactly). Each ray stores its sorted material
/// intervals; every interval end keeps the exact crossing position, the surface normal and the cut time. Undercuts,
/// through-cuts and thin walls are all representable; the only discretisation is the ray pitch.
///
/// Removal: the cutter is a signed distance field in the tool frame (see <see cref="CutterSdf"/>). A sweep between two
/// poses is split into sub-steps no longer than StepFraction * cell at the fastest-moving point of the cutter
/// (rotation slerped, translation lerped); at each sub-step every ray that can touch the cutter is sphere-traced
/// through the field, crossings are root-refined and the covered spans subtracted. The removed volume is the removed
/// ray length times the ray's cross-section, averaged over the three families.
///
/// Surface: manifold dual contouring per 16^3 chunk. Grid nodes are classified by majority vote of the three
/// families (so the mesh is closed whatever the rays say), each sign-changing grid edge takes its exact crossing and
/// normal from the ray lying on it, and every cell gets one vertex per surface sheet, placed by a regularised QEF so
/// flat faces stay flat and cut edges stay crisp. Only chunks touched by a cut are re-meshed.
/// </summary>
public sealed class TriDexelStock : IClayStock
{
    public const int ChunkShift = 4, ChunkSize = 1 << ChunkShift;
    const int W = ChunkSize + 2, W3 = W * W * W;
    const float OldTime = -1e6f;

    /// <summary>Cutter parts thinner than this many cells are thickened to it (see CutterSdf).</summary>
    public static double MinThicknessFactor = 1.25;

    StockBox _box;
    double _res = 0.5;
    MeshData? _stockMesh, _cutterMesh;
    CutterSdf? _sdf;
    readonly int[] _n = new int[3];
    readonly float[] _h = new float[3], _min = new float[3];
    readonly Hit[]?[][] _rays = new Hit[]?[3][];
    readonly double[] _removed = new double[3];           // mm^3 per family
    readonly int[] _nc = new int[3];
    byte[] _dirty = Array.Empty<byte>();
    volatile bool _anyDirty;
    double[] _chunkVol = Array.Empty<double>();           // signed volume enclosed by the triangles of each chunk
    double _meshVol, _meshVol0;
    readonly List<ChunkMesh> _pending = new();
    int _pendingChunks;
    bool[] _hasMesh = Array.Empty<bool>();
    readonly Stopwatch _clock = new();
    readonly object _lock = new();
    readonly ThreadLocal<Scratch> _scratch = new(() => new Scratch());

    public double StepFraction { get; set; } = 0.3;
    public float Now => (float)_clock.Elapsed.TotalSeconds;
    public long CellCount => (long)_n[0] * _n[1] * _n[2];
    /// <summary>Volume enclosed by the original surface mesh minus the volume enclosed by the current one, i.e. exactly
    /// what the displayed / exported mesh has lost. Kept up to date chunk by chunk.</summary>
    public double RemovedVolume { get { EnsureMeshed(); return _meshVol0 - _meshVol; } }
    /// <summary>Independent estimate straight from the rays: removed ray length x ray cross-section, averaged over the
    /// three families. Unbiased, but noisy for cuts about one cell wide that run parallel to a ray family.</summary>
    public double RemovedVolumeDexel => (_removed[0] + _removed[1] + _removed[2]) / 3.0;
    public string CutterInfo => _sdf?.Info ?? "no cutter";

    public string Describe() => $"tri-dexel {_n[0]}x{_n[1]}x{_n[2]} @{_h[0]:0.###}mm";

    // ====================================================================================== init
    public void Init(StockBox box, double resolutionMm, MeshData? mesh)
    {
        var resChanged = Math.Abs(resolutionMm - _res) > 1e-9;
        _box = box; _res = Math.Max(0.02, resolutionMm); _stockMesh = mesh;
        for (var a = 0; a < 3; a++)
        {
            var size = Comp(box.Size, a);
            _n[a] = Math.Max(1, (int)Math.Round(size / _res));
            _h[a] = size / _n[a];
            _min[a] = Comp(box.Min, a);
            _nc[a] = (_n[a] >> ChunkShift) + 1;
        }
        Fill();
        if (_cutterMesh != null && (resChanged || _sdf == null)) SetCutter(_cutterMesh);
    }

    public void Reset() => Fill();

    void Fill()
    {
        for (var f = 0; f < 3; f++)
        {
            int u = (f + 1) % 3, v = (f + 2) % 3;
            var rays = new Hit[]?[_n[u] * _n[v]];
            float lo = _min[f], hi = _min[f] + _n[f] * _h[f];
            if (_stockMesh == null)
            {
                Vector3 ax = Axis(f);
                uint nLo = Pack(-ax) | 3u << 30, nHi = Pack(ax) | 3u << 30;
                for (var i = 0; i < rays.Length; i++)
                    rays[i] = new[] { new Hit { T = lo, Time = OldTime, N = nLo }, new Hit { T = hi, Time = OldTime, N = nHi } };
            }
            else
            {
                var hits = MeshRaster.Cast(_stockMesh.Vertices, _stockMesh.Indices, f,
                    _min[u] + 0.5 * _h[u], _h[u], _n[u], _min[v] + 0.5 * _h[v], _h[v], _n[v]);
                var tmp = new List<Hit>();
                for (var i = 0; i < rays.Length; i++)
                {
                    if (hits[i] == null) continue;
                    tmp.Clear();
                    MeshRaster.ToIntervals(hits[i]!, f, (a, b) =>
                    {
                        float ta = Math.Clamp(a.T, lo, hi), tb = Math.Clamp(b.T, lo, hi);
                        if (tb - ta < 1e-4f) return;
                        tmp.Add(new Hit { T = ta, Time = OldTime, N = Pack(MeshRaster.Axis(a.N, f) < 0 ? a.N : -a.N) | 3u << 30 });
                        tmp.Add(new Hit { T = tb, Time = OldTime, N = Pack(MeshRaster.Axis(b.N, f) > 0 ? b.N : -b.N) | 3u << 30 });
                    });
                    if (tmp.Count > 0) rays[i] = tmp.ToArray();
                }
            }
            _rays[f] = rays;
            _removed[f] = 0;
        }
        _dirty = new byte[_nc[0] * _nc[1] * _nc[2]];
        Array.Fill(_dirty, (byte)1);
        _anyDirty = true;
        _chunkVol = new double[_dirty.Length];
        _meshVol = 0;
        var had = _hasMesh;
        _hasMesh = new bool[_dirty.Length];
        if (had.Length == _hasMesh.Length) Array.Copy(had, _hasMesh, had.Length);   // so emptied chunks get cleared in the viewer
        _clock.Restart();
        _pending.Clear(); _pendingChunks = 0;
        EnsureMeshed();
        _meshVol0 = _meshVol;
    }

    /// <summary>Remaining material in mm^3, integrated on each family and averaged (diagnostics / tests).</summary>
    public double RemainingVolume()
    {
        double sum = 0;
        for (var f = 0; f < 3; f++)
        {
            double len = 0;
            foreach (var r in _rays[f])
                if (r != null) for (var k = 0; k < r.Length; k += 2) len += r[k + 1].T - r[k].T;
            sum += len * _h[(f + 1) % 3] * _h[(f + 2) % 3];
        }
        return sum / 3;
    }

    // ====================================================================================== cutter
    public void SetCutter(MeshData cutter)
    {
        _cutterMesh = cutter;
        var sw = Stopwatch.StartNew();
        _sdf = CutterSdf.Build(cutter, _res, MinThicknessFactor * _res);
        Console.WriteLine($"[stock] cutter: {CutterInfo} ({sw.ElapsedMilliseconds} ms)");
    }

    // ====================================================================================== carving
    public int CarveSweep(Matrix4x4 from, Matrix4x4 to)
    {
        var sdf = _sdf;
        if (sdf == null || _rays[0] == null) return 0;
        var q0 = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(from));
        var q1 = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(to));
        Vector3 p0 = from.Translation, p1 = to.Translation;
        Matrix4x4 m0 = Rigid(q0, p0), m1 = Rigid(q1, p1);

        // largest travel of any point of the cutter's bounding box
        float travel = 0;
        Vector3 bmin = sdf.Min, bmax = sdf.Max;
        for (var c = 0; c < 8; c++)
        {
            var p = new Vector3((c & 1) == 0 ? bmin.X : bmax.X, (c & 2) == 0 ? bmin.Y : bmax.Y, (c & 4) == 0 ? bmin.Z : bmax.Z);
            travel = MathF.Max(travel, Vector3.Distance(Vector3.Transform(p, m0), Vector3.Transform(p, m1)));
        }
        var hmin = MathF.Min(_h[0], MathF.Min(_h[1], _h[2]));
        var steps = Math.Clamp((int)Math.Ceiling(travel / (StepFraction * hmin)), 1, 20000);
        var now = Now;
        for (var s = 1; s <= steps; s++)
        {
            var t = s / (float)steps;
            CarvePose(sdf, Rigid(Quaternion.Slerp(q0, q1, t), Vector3.Lerp(p0, p1, t)), now);
        }
        return steps;
    }

    static Matrix4x4 Rigid(Quaternion q, Vector3 p)
    {
        var m = Matrix4x4.CreateFromQuaternion(q);
        m.Translation = p;
        return m;
    }

    void CarvePose(CutterSdf sdf, Matrix4x4 m, float now)
    {
        // world bounds of the cutter grid -> node index ranges
        Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
        Vector3 bmin = sdf.Min, bmax = sdf.Max;
        for (var c = 0; c < 8; c++)
        {
            var p = Vector3.Transform(new Vector3((c & 1) == 0 ? bmin.X : bmax.X, (c & 2) == 0 ? bmin.Y : bmax.Y, (c & 4) == 0 ? bmin.Z : bmax.Z), m);
            mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p);
        }
        Span<int> lo = stackalloc int[3], hi = stackalloc int[3];
        for (var a = 0; a < 3; a++)
        {
            lo[a] = Math.Max(1, (int)MathF.Floor((Comp(mn, a) - _min[a]) / _h[a] + 0.5f));
            hi[a] = Math.Min(_n[a], (int)MathF.Ceiling((Comp(mx, a) - _min[a]) / _h[a] + 0.5f));
            if (lo[a] > hi[a]) return;
        }
        Matrix4x4.Invert(m, out var inv);
        var g = inv * Matrix4x4.CreateTranslation(-sdf.Origin) * Matrix4x4.CreateScale(1f / sdf.Cell);   // world mm -> sdf grid coords

        int c0 = hi[1] - lo[1] + 1, c1 = hi[2] - lo[2] + 1, c2 = hi[0] - lo[0] + 1;    // rows (index u) of families 0, 1, 2
        int lo0 = lo[0], lo1 = lo[1], lo2 = lo[2], hi0 = hi[0], hi1 = hi[1], hi2 = hi[2];
        var total = c0 + c1 + c2;

        void Row(int w, double[] acc)
        {
            if (w < c0) acc[0] += CarveRow(sdf, 0, lo1 + w, lo2, hi2, g, m, now);
            else if (w < c0 + c1) acc[1] += CarveRow(sdf, 1, lo2 + (w - c0), lo0, hi0, g, m, now);
            else acc[2] += CarveRow(sdf, 2, lo0 + (w - c0 - c1), lo1, hi1, g, m, now);
        }
        if (total < 12)
        {
            var acc = new double[3];
            for (var w = 0; w < total; w++) Row(w, acc);
            for (var f = 0; f < 3; f++) _removed[f] += acc[f];
        }
        else
            Parallel.For(0, total, () => new double[3], (w, _, acc) => { Row(w, acc); return acc; },
                acc => { lock (_lock) for (var f = 0; f < 3; f++) _removed[f] += acc[f]; });
    }

    [ThreadStatic] static Hit[]? _buf;

    /// <summary>Carves all rays (iu, vLo..vHi) of family f against the cutter at one pose. Returns removed volume (mm^3).</summary>
    double CarveRow(CutterSdf sdf, int f, int iu, int vLo, int vHi, in Matrix4x4 g, in Matrix4x4 m, float now)
    {
        int u = (f + 1) % 3, v = (f + 2) % 3;
        Vector3 rowF = MRow(g, f), rowU = MRow(g, u), rowV = MRow(g, v);
        var gu = (_min[u] + (iu - 0.5f) * _h[u]) * rowU + g.Translation;
        var gd = rowF;                                    // grid coords per mm along the ray
        var rays = _rays[f];
        var nv = _n[v];
        var minStep = 0.5f * sdf.Cell;
        var touchTol = 0.02f * _h[f];
        Vector3 gridMax = new(sdf.Nx - 1.02f, sdf.Ny - 1.02f, sdf.Nz - 1.02f);
        const float gridMin = 0.02f;
        var buf = _buf ??= new Hit[256];
        double removed = 0;
        var axisN = Axis(f);

        for (var iv = vLo; iv <= vHi; iv++)
        {
            var ri = (iu - 1) * nv + (iv - 1);
            var r = rays[ri];
            if (r == null) continue;
            var g0 = gu + (_min[v] + (iv - 0.5f) * _h[v]) * rowV;

            // clip the ray to the cutter grid and to the material on the ray
            float t0 = r[0].T, t1 = r[^1].T;
            if (!Slab(g0.X, gd.X, gridMin, gridMax.X, ref t0, ref t1) || !Slab(g0.Y, gd.Y, gridMin, gridMax.Y, ref t0, ref t1) ||
                !Slab(g0.Z, gd.Z, gridMin, gridMax.Z, ref t0, ref t1) || t0 >= t1) continue;

            if (buf.Length < r.Length * 2 + 64) _buf = buf = new Hit[r.Length * 4 + 64];
            var n = 0; var changed = false; double gone = 0;
            float touchLo = float.MaxValue, touchHi = float.MinValue;

            for (var k = 0; k < r.Length; k += 2)
            {
                Hit lo = r[k], hi = r[k + 1];
                float s = MathF.Max(lo.T, t0), e = MathF.Min(hi.T, t1);
                if (s >= e) { buf[n++] = lo; buf[n++] = hi; continue; }
                if (n + 8 > buf.Length) { Array.Resize(ref buf, buf.Length * 2); _buf = buf; }

                var t = s;
                var d = sdf.Sample(g0 + t * gd);
                var curLo = lo; var open = true; float remStart = 0;
                if (d < 0)
                {
                    if (s - lo.T > 1e-4f) { buf[n++] = lo; buf[n++] = Cut(sdf, g0, gd, s, m, now, axisN); }
                    remStart = s - lo.T > 1e-4f ? s : lo.T; open = false; changed = true;
                }
                else if (d < touchTol && s == lo.T && lo.Time > -1e5f && Refresh(sdf, g0, gd, m, ref curLo)) changed = true;
                while (t < e)
                {
                    var tn = MathF.Min(t + MathF.Max(MathF.Abs(d) * 0.9f, minStep), e);
                    var dn = sdf.Sample(g0 + tn * gd);
                    if ((d < 0) != (dn < 0))
                    {
                        if (n + 8 > buf.Length) { Array.Resize(ref buf, buf.Length * 2); _buf = buf; }
                        // root between t and tn: secant, one refinement
                        var tr = t + (tn - t) * d / (d - dn);
                        var dr = sdf.Sample(g0 + tr * gd);
                        if (dr != 0)
                        {
                            if ((dr < 0) == (d < 0)) { if (dr != dn) tr += (tn - tr) * dr / (dr - dn); }
                            else if (dr != d) tr = t + (tr - t) * d / (d - dr);
                        }
                        tr = Math.Clamp(tr, t, tn);
                        if (d >= 0)
                        {   // entering the cutter: the open material piece ends here
                            if (tr - curLo.T > 1e-4f) { buf[n++] = curLo; buf[n++] = Cut(sdf, g0, gd, tr, m, now, axisN); remStart = tr; }
                            else remStart = curLo.T;
                            open = false;
                        }
                        else
                        {   // leaving the cutter: a new material piece starts
                            gone += tr - remStart;
                            curLo = Cut(sdf, g0, gd, tr, m, now, -axisN);
                            open = true;
                        }
                        changed = true;
                    }
                    t = tn; d = dn;
                }
                if (open)
                {
                    // the cutter rests on this end without cutting deeper: take its (possibly better) normal
                    if (d < touchTol && e == hi.T && hi.Time > -1e5f && Refresh(sdf, g0, gd, m, ref hi)) changed = true;
                    if (hi.T - curLo.T > 1e-4f) { buf[n++] = curLo; buf[n++] = hi; }
                    else { gone += hi.T - curLo.T; changed = true; }
                }
                else if (hi.T - e > 1e-4f)
                {   // still inside the cutter at the edge of its grid (cannot normally happen: the grid is padded)
                    gone += e - remStart;
                    buf[n++] = Cut(sdf, g0, gd, e, m, now, -axisN); buf[n++] = hi;
                }
                else gone += hi.T - remStart;
                if (changed) { touchLo = MathF.Min(touchLo, s); touchHi = MathF.Max(touchHi, e); }
            }
            if (!changed) continue;

            if (n == r.Length) Array.Copy(buf, r, n);
            else rays[ri] = n == 0 ? null : buf.AsSpan(0, n).ToArray();
            removed += gone;
            MarkDirty(f, iu, iv, touchLo, touchHi);
        }
        return removed * _h[u] * _h[v];
    }

    static bool Slab(float o, float d, float lo, float hi, ref float t0, ref float t1)
    {
        if (MathF.Abs(d) < 1e-9f) return o >= lo && o <= hi;
        float a = (lo - o) / d, b = (hi - o) / d;
        if (a > b) (a, b) = (b, a);
        if (a > t0) t0 = a;
        if (b < t1) t1 = b;
        return t0 < t1;
    }

    /// <summary>New interval end at t: the clay's outward normal is the cutter's inward normal.</summary>
    static Hit Cut(CutterSdf sdf, Vector3 g0, Vector3 gd, float t, in Matrix4x4 m, float now, Vector3 fallback)
    {
        var grad = sdf.Gradient(g0 + t * gd, out var bend);
        var n = -Vector3.TransformNormal(grad, m);
        var l = n.Length();
        n = l > 1e-6f ? n / l : fallback;
        return new Hit { T = t, Time = now, N = Pack(n) | Quality(l, bend, sdf) << 30 };
    }

    /// <summary>Confidence of a cutter normal: high where the distance field is locally a plane (full gradient length,
    /// no second difference), low where the stencil straddles a cutter edge or the medial surface of a thin blade.</summary>
    static uint Quality(float gradLen, float bend, CutterSdf sdf)
    {
        var r = gradLen / (1.5f * sdf.Cell);
        if (r > 0.97f && bend < 0.04f) return 3;
        if (r > 0.9f && bend < 0.12f) return 2;
        return r > 0.75f && bend < 0.3f ? 1u : 0u;
    }

    /// <summary>The cutter surface passes through an existing cut end without moving it. Sweeping a flat face leaves
    /// the position right early (while the leading edge passes) but the normal only once the face itself is over the
    /// point, so the better-conditioned normal replaces the stored one. Returns true when the hit changed.</summary>
    static bool Refresh(CutterSdf sdf, Vector3 g0, Vector3 gd, in Matrix4x4 m, ref Hit h)
    {
        var grad = sdf.Gradient(g0 + h.T * gd, out var bend);
        var n = -Vector3.TransformNormal(grad, m);
        var l = n.Length();
        if (l < 1e-6f) return false;
        n /= l;
        uint q = Quality(l, bend, sdf), old = h.N >> 30;
        if (q < old) return false;
        var packed = Pack(n) | q << 30;
        if (q == old && (packed == h.N || Vector3.Dot(n, Unpack(h.N)) > 0.9995f)) return false;
        h.N = packed;
        return true;
    }

    void MarkDirty(int f, int iu, int iv, float a, float b)
    {
        int u = (f + 1) % 3, v = (f + 2) % 3;
        Span<int> c0 = stackalloc int[3], c1 = stackalloc int[3];
        c0[u] = (iu - 1) >> ChunkShift; c1[u] = (iu + 1) >> ChunkShift;
        c0[v] = (iv - 1) >> ChunkShift; c1[v] = (iv + 1) >> ChunkShift;
        c0[f] = ((int)MathF.Floor((a - _min[f]) / _h[f] + 0.5f) - 2) >> ChunkShift;
        c1[f] = ((int)MathF.Floor((b - _min[f]) / _h[f] + 0.5f) + 2) >> ChunkShift;
        for (var k = 0; k < 3; k++) { c0[k] = Math.Max(0, c0[k]); c1[k] = Math.Min(_nc[k] - 1, c1[k]); }
        for (var x = c0[0]; x <= c1[0]; x++)
            for (var y = c0[1]; y <= c1[1]; y++)
                for (var z = c0[2]; z <= c1[2]; z++)
                    _dirty[(x * _nc[1] + y) * _nc[2] + z] = 1;
        _anyDirty = true;
    }

    // ====================================================================================== meshing
    public int RemeshDirty(List<ChunkMesh> output)
    {
        EnsureMeshed();
        output.AddRange(_pending);
        _pending.Clear();
        var n = _pendingChunks; _pendingChunks = 0;
        return n;
    }

    /// <summary>Re-meshes every dirty chunk (in parallel) and updates the enclosed volume; results wait in _pending.</summary>
    void EnsureMeshed()
    {
        if (!_anyDirty) return;
        _anyDirty = false;
        var ids = new List<int>();
        for (var i = 0; i < _dirty.Length; i++)
            if (_dirty[i] != 0) { _dirty[i] = 0; ids.Add(i); }
        if (ids.Count == 0) return;
        var res = new ChunkMesh?[ids.Count];
        var vols = new double[ids.Count];
        void One(int k)
        {
            var id = ids[k];
            var sc = _scratch.Value!;
            MeshChunk(id, sc, null);
            vols[k] = sc.Volume;
            if (sc.OutI.Count == 0)
            {
                if (_hasMesh[id]) { _hasMesh[id] = false; res[k] = new ChunkMesh { Id = id }; }
                return;
            }
            _hasMesh[id] = true;
            res[k] = new ChunkMesh { Id = id, Vertices = sc.OutV.ToArray(), Indices = sc.OutI.ToArray() };
        }
        if (ids.Count < 4) for (var k = 0; k < ids.Count; k++) One(k);
        else Parallel.For(0, ids.Count, One);
        for (var k = 0; k < ids.Count; k++)
        {
            _meshVol += vols[k] - _chunkVol[ids[k]];
            _chunkVol[ids[k]] = vols[k];
            if (res[k] != null) _pending.Add(res[k]!);
        }
        _pendingChunks += ids.Count;
    }

    public MeshData ExportMesh()
    {
        var ctx = new ExportCtx();
        var sc = new Scratch();
        for (var id = 0; id < _dirty.Length; id++) MeshChunk(id, sc, ctx);
        return new MeshData(ctx.V.ToArray(), ctx.I.ToArray());
    }

    sealed class ExportCtx
    {
        public readonly Dictionary<long, int> Map = new();
        public readonly List<float> V = new();
        public readonly List<int> I = new();
    }

    struct SVert
    {
        public Vector3 P, N;
        public float Time;
        public bool Sharp;
        public int Out;
        public long Key;
    }

    sealed class Scratch
    {
        public readonly byte[] Occ = new byte[W3];
        public readonly byte[] Cfg = new byte[W3];
        public readonly int[][] HitLo = { new int[W3], new int[W3], new int[W3] };
        public readonly int[][] HitHi = { new int[W3], new int[W3], new int[W3] };
        public readonly int[] CellVert = new int[W3 * 4];
        public readonly List<Hit> Hits = new(4096);
        public readonly List<SVert> Verts = new(1024);
        public readonly List<float> OutV = new(8192);
        public readonly List<int> OutI = new(8192);
        public double Volume;
    }

    static readonly int[] Stride = { W * W, W, 1 };
    static readonly float[] QWeight = { 0.2f, 0.4f, 0.7f, 1f };
    static readonly sbyte[][] EdgeComp = BuildEdgeComp();

    /// <summary>For each of the 256 corner configurations: which surface sheet (0..3) each of the 12 cell edges belongs
    /// to, or -1 when the edge has no crossing. Edge id = axis*4 + bBit + 2*cBit with b=(axis+1)%3, c=(axis+2)%3.
    /// Sheets are the connected groups of crossing edges, linked across each face; on a face with four crossings the
    /// two inside corners are kept apart (the same rule is seen from both cells sharing the face, so the mesh is manifold).</summary>
    static sbyte[][] BuildEdgeComp()
    {
        var table = new sbyte[256][];
        var par = new int[12];
        int Find(int x) { while (par[x] != x) x = par[x]; return x; }
        void Union(int x, int y) { par[Find(x)] = Find(y); }
        for (var cfg = 0; cfg < 256; cfg++)
        {
            bool In(int corner) => (cfg >> corner & 1) != 0;
            var cross = new bool[12];
            for (var a = 0; a < 3; a++)
                for (var k = 0; k < 4; k++)
                {
                    int b = (a + 1) % 3, c = (a + 2) % 3;
                    var low = ((k & 1) << b) | ((k >> 1) << c);
                    cross[a * 4 + k] = In(low) != In(low | (1 << a));
                }
            for (var i = 0; i < 12; i++) par[i] = i;
            for (var a = 0; a < 3; a++)
                for (var s = 0; s < 2; s++)
                {
                    int b = (a + 1) % 3, c = (a + 2) % 3;
                    int e0 = b * 4 + 2 * s, e2 = b * 4 + 1 + 2 * s, e3 = c * 4 + s, e1 = c * 4 + s + 2;
                    var cnt = (cross[e0] ? 1 : 0) + (cross[e1] ? 1 : 0) + (cross[e2] ? 1 : 0) + (cross[e3] ? 1 : 0);
                    if (cnt == 2)
                    {
                        var first = -1;
                        foreach (var e in new[] { e0, e1, e2, e3 })
                            if (cross[e]) { if (first < 0) first = e; else Union(first, e); }
                    }
                    else if (cnt == 4)
                    {
                        if (In(s << a)) { Union(e3, e0); Union(e1, e2); }
                        else { Union(e0, e1); Union(e2, e3); }
                    }
                }
            var t = new sbyte[12];
            var roots = new List<int>();
            for (var e = 0; e < 12; e++)
            {
                if (!cross[e]) { t[e] = -1; continue; }
                var r = Find(e);
                var id = roots.IndexOf(r);
                if (id < 0) { id = roots.Count; roots.Add(r); }
                t[e] = (sbyte)id;
            }
            table[cfg] = t;
        }
        return table;
    }

    /// <summary>Dual-contours one chunk into sc.OutV / sc.OutI (display) or into ctx (welded export).</summary>
    void MeshChunk(int id, Scratch sc, ExportCtx? ctx)
    {
        sc.OutV.Clear(); sc.OutI.Clear(); sc.Hits.Clear(); sc.Verts.Clear(); sc.Volume = 0;
        var org = _box.Min;
        Span<int> w0 = stackalloc int[3];
        var cz = id % _nc[2]; var cy = id / _nc[2] % _nc[1]; var cx = id / (_nc[2] * _nc[1]);
        w0[0] = cx * ChunkSize - 1; w0[1] = cy * ChunkSize - 1; w0[2] = cz * ChunkSize - 1;

        var occ = sc.Occ;
        Array.Clear(occ);
        var hits = sc.Hits;
        var any = false;

        // ---- gather: node occupancy votes and the crossings on every grid edge of the window
        for (var f = 0; f < 3; f++)
        {
            int u = (f + 1) % 3, v = (f + 2) % 3;
            int[] hLo = sc.HitLo[f], hHi = sc.HitHi[f];
            Array.Fill(hLo, -1); Array.Fill(hHi, -1);
            var rays = _rays[f];
            float minF = _min[f], invH = 1f / _h[f];
            int sf = Stride[f], wf = w0[f];
            for (var lu = 0; lu < W; lu++)
            {
                var iu = w0[u] + lu;
                if (iu < 1 || iu > _n[u]) continue;
                for (var lv = 0; lv < W; lv++)
                {
                    var iv = w0[v] + lv;
                    if (iv < 1 || iv > _n[v]) continue;
                    var r = rays[(iu - 1) * _n[v] + (iv - 1)];
                    if (r == null) continue;
                    var bi = lu * Stride[u] + lv * Stride[v];
                    for (var k = 0; k < r.Length; k += 2)
                    {
                        float a = (r[k].T - minF) * invH + 0.5f, b = (r[k + 1].T - minF) * invH + 0.5f;
                        int ea = (int)MathF.Ceiling(a) - 1 - wf, eb = (int)MathF.Ceiling(b) - 1 - wf;   // edge whose two nodes straddle the hit
                        if (eb < 0) continue;
                        if (ea >= W) break;
                        if (ea >= 0) { hLo[bi + ea * sf] = hits.Count; hits.Add(r[k]); }
                        if (eb < W && hHi[bi + eb * sf] < 0) { hHi[bi + eb * sf] = hits.Count; hits.Add(r[k + 1]); }
                        int ka = Math.Max(0, (int)MathF.Ceiling(a) - wf), kb = Math.Min(W - 1, (int)MathF.Ceiling(b) - 1 - wf);
                        for (var kk = ka; kk <= kb; kk++) occ[bi + kk * sf]++;
                        any = true;
                    }
                }
            }
        }
        if (!any) return;

        // ---- one vertex per surface sheet per cell
        var cfgs = sc.Cfg; var cellVert = sc.CellVert; var verts = sc.Verts;
        Span<Vector3> ep = stackalloc Vector3[12], en = stackalloc Vector3[12];
        Span<float> et = stackalloc float[12], ew = stackalloc float[12];
        Span<float> nodePos = stackalloc float[3];
        const int C = W - 1;
        for (var lx = 0; lx < C; lx++)
            for (var ly = 0; ly < C; ly++)
                for (var lz = 0; lz < C; lz++)
                {
                    var i = (lx * W + ly) * W + lz;
                    var cfg = 0;
                    if (occ[i] >= 2) cfg |= 1;
                    if (occ[i + Stride[0]] >= 2) cfg |= 2;
                    if (occ[i + Stride[1]] >= 2) cfg |= 4;
                    if (occ[i + Stride[0] + Stride[1]] >= 2) cfg |= 8;
                    if (occ[i + 1] >= 2) cfg |= 16;
                    if (occ[i + Stride[0] + 1] >= 2) cfg |= 32;
                    if (occ[i + Stride[1] + 1] >= 2) cfg |= 64;
                    if (occ[i + Stride[0] + Stride[1] + 1] >= 2) cfg |= 128;
                    cfgs[i] = (byte)cfg;
                    if (cfg == 0 || cfg == 255) continue;

                    var comp = EdgeComp[cfg];
                    nodePos[0] = _min[0] + (w0[0] + lx - 0.5f) * _h[0];
                    nodePos[1] = _min[1] + (w0[1] + ly - 0.5f) * _h[1];
                    nodePos[2] = _min[2] + (w0[2] + lz - 0.5f) * _h[2];
                    var nComp = 0;
                    for (var e = 0; e < 12; e++)
                    {
                        if (comp[e] < 0) continue;
                        if (comp[e] >= nComp) nComp = comp[e] + 1;
                        int a = e >> 2, b = (a + 1) % 3, c = (a + 2) % 3, bb = e & 1, cb = e >> 1 & 1;
                        var li = i + bb * Stride[b] + cb * Stride[c];
                        var lowIn = occ[li] >= 2;
                        var hi = lowIn ? sc.HitHi[a][li] : sc.HitLo[a][li];
                        Vector3 p = default;
                        float along;
                        if (hi >= 0)
                        {
                            var h = hits[hi];
                            along = h.T; en[e] = Unpack(h.N); et[e] = h.Time; ew[e] = QWeight[h.N >> 30];
                        }
                        else
                        {   // the families disagree here: no stored crossing on this edge, take its middle
                            along = nodePos[a] + 0.5f * _h[a];
                            en[e] = lowIn ? Axis(a) : -Axis(a); et[e] = OldTime; ew[e] = QWeight[0];
                        }
                        SetComp(ref p, a, along);
                        SetComp(ref p, b, nodePos[b] + bb * _h[b]);
                        SetComp(ref p, c, nodePos[c] + cb * _h[c]);
                        ep[e] = p;
                    }
                    Vector3 cellMin = new(nodePos[0], nodePos[1], nodePos[2]);
                    var cellMax = cellMin + new Vector3(_h[0], _h[1], _h[2]);
                    for (var s = 0; s < nComp; s++)
                    {
                        Vector3 mass = default, nsum = default; var cnt = 0; var time = OldTime;
                        for (var e = 0; e < 12; e++)
                            if (comp[e] == s) { mass += ep[e]; nsum += en[e] * ew[e]; cnt++; if (et[e] > time) time = et[e]; }
                        mass /= cnt;
                        // regularised QEF: min sum (n.(x-p))^2 + lambda |x-mass|^2
                        float lam = 0.004f * cnt, a00 = lam, a01 = 0, a02 = 0, a11 = lam, a12 = 0, a22 = lam;
                        Vector3 rhs = default;
                        for (var e = 0; e < 12; e++)
                        {
                            if (comp[e] != s) continue;
                            var nn = en[e] * ew[e];       // planes with a blurred normal count less
                            a00 += nn.X * nn.X; a01 += nn.X * nn.Y; a02 += nn.X * nn.Z; a11 += nn.Y * nn.Y; a12 += nn.Y * nn.Z; a22 += nn.Z * nn.Z;
                            rhs += nn * Vector3.Dot(nn, ep[e] - mass);
                        }
                        float c00 = a11 * a22 - a12 * a12, c01 = a02 * a12 - a01 * a22, c02 = a01 * a12 - a02 * a11;
                        var det = a00 * c00 + a01 * c01 + a02 * c02;
                        var pos = mass;
                        if (det > 1e-12f)
                        {
                            float c11 = a00 * a22 - a02 * a02, c12 = a01 * a02 - a00 * a12, c22 = a00 * a11 - a01 * a01;
                            pos += new Vector3(c00 * rhs.X + c01 * rhs.Y + c02 * rhs.Z, c01 * rhs.X + c11 * rhs.Y + c12 * rhs.Z, c02 * rhs.X + c12 * rhs.Y + c22 * rhs.Z) / det;
                            pos = Vector3.Clamp(pos, cellMin, cellMax);
                        }
                        var nl = nsum.Length();
                        var nmean = nl > 1e-6f ? nsum / nl : Vector3.UnitZ;
                        var sharp = false;
                        for (var e = 0; e < 12; e++)
                            if (comp[e] == s && Vector3.Dot(en[e], nmean) < 0.9f) { sharp = true; break; }
                        cellVert[i * 4 + s] = verts.Count;
                        long key = 0;
                        if (ctx != null)
                            key = ((((long)(w0[0] + lx + 1) * (_n[1] + 3)) + (w0[1] + ly + 1)) * (_n[2] + 3) + (w0[2] + lz + 1)) * 4 + s;
                        verts.Add(new SVert { P = pos, N = nmean, Time = time, Sharp = sharp, Out = -1, Key = key });
                    }
                }

        // ---- one quad per sign-changing edge owned by this chunk
        Span<int> q = stackalloc int[4];
        var vspan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(verts);
        for (var lx = 1; lx <= ChunkSize; lx++)
            for (var ly = 1; ly <= ChunkSize; ly++)
                for (var lz = 1; lz <= ChunkSize; lz++)
                {
                    var i = (lx * W + ly) * W + lz;
                    var in1 = occ[i] >= 2;
                    for (var a = 0; a < 3; a++)
                    {
                        if ((occ[i + Stride[a]] >= 2) == in1) continue;
                        int b = (a + 1) % 3, c = (a + 2) % 3;
                        int cA = i - Stride[b] - Stride[c], cB = i - Stride[c], cD = i - Stride[b];
                        q[0] = cellVert[cA * 4 + EdgeComp[cfgs[cA]][a * 4 + 3]];
                        q[1] = cellVert[cB * 4 + EdgeComp[cfgs[cB]][a * 4 + 2]];
                        q[2] = cellVert[i * 4 + EdgeComp[cfgs[i]][a * 4]];
                        q[3] = cellVert[cD * 4 + EdgeComp[cfgs[cD]][a * 4 + 1]];
                        if (!in1) { (q[0], q[3]) = (q[3], q[0]); (q[1], q[2]) = (q[2], q[1]); }

                        if (ctx != null)
                        {
                            for (var k = 0; k < 4; k++)
                            {
                                ref var sv = ref vspan[q[k]];
                                if (!ctx.Map.TryGetValue(sv.Key, out var gi))
                                {
                                    ctx.Map[sv.Key] = gi = ctx.V.Count / 3;
                                    ctx.V.Add(sv.P.X); ctx.V.Add(sv.P.Y); ctx.V.Add(sv.P.Z);
                                }
                                q[k] = gi;
                            }
                            EmitQuad(ctx.I, q);
                            continue;
                        }

                        {   // signed volume under the quad (same split as EmitQuad), relative to the box corner
                            Vector3 p0 = vspan[q[0]].P - org, p1 = vspan[q[1]].P - org, p2 = vspan[q[2]].P - org, p3 = vspan[q[3]].P - org;
                            sc.Volume += (Triple(p0, p1, p2) + Triple(p0, p2, p3)) / 6.0;
                        }
                        var hid = in1 ? sc.HitHi[a][i] : sc.HitLo[a][i];
                        var qn = hid >= 0 ? Unpack(hits[hid].N) : in1 ? Axis(a) : -Axis(a);
                        for (var k = 0; k < 4; k++)
                        {
                            ref var sv = ref vspan[q[k]];
                            if (sv.Sharp) { q[k] = sc.OutV.Count / 7; AddVert(sc.OutV, sv.P, qn, sv.Time); }
                            else
                            {
                                if (sv.Out < 0) { sv.Out = sc.OutV.Count / 7; AddVert(sc.OutV, sv.P, sv.N, sv.Time); }
                                q[k] = sv.Out;
                            }
                        }
                        EmitQuad(sc.OutI, q);
                    }
                }
    }

    static double Triple(Vector3 a, Vector3 b, Vector3 c) =>
        (double)a.X * ((double)b.Y * c.Z - (double)b.Z * c.Y) + (double)a.Y * ((double)b.Z * c.X - (double)b.X * c.Z) + (double)a.Z * ((double)b.X * c.Y - (double)b.Y * c.X);

    static void EmitQuad(List<int> idx, Span<int> q)
    {
        if (q[0] != q[1] && q[1] != q[2] && q[0] != q[2]) { idx.Add(q[0]); idx.Add(q[1]); idx.Add(q[2]); }
        if (q[0] != q[2] && q[2] != q[3] && q[0] != q[3]) { idx.Add(q[0]); idx.Add(q[2]); idx.Add(q[3]); }
    }

    static void AddVert(List<float> o, Vector3 p, Vector3 n, float time)
    {
        o.Add(p.X); o.Add(p.Y); o.Add(p.Z); o.Add(n.X); o.Add(n.Y); o.Add(n.Z); o.Add(time);
    }

    // ====================================================================================== small helpers
    static float Comp(Vector3 v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;
    static void SetComp(ref Vector3 v, int a, float x) { if (a == 0) v.X = x; else if (a == 1) v.Y = x; else v.Z = x; }
    static Vector3 Axis(int a) => a == 0 ? Vector3.UnitX : a == 1 ? Vector3.UnitY : Vector3.UnitZ;
    static Vector3 MRow(in Matrix4x4 m, int r) => r == 0 ? new Vector3(m.M11, m.M12, m.M13) : r == 1 ? new Vector3(m.M21, m.M22, m.M23) : new Vector3(m.M31, m.M32, m.M33);

    static uint Pack(Vector3 n)
    {
        static uint Q(float x) => (uint)Math.Clamp((int)MathF.Round(x * 511f) + 512, 0, 1023);
        return Q(n.X) | Q(n.Y) << 10 | Q(n.Z) << 20;
    }

    static Vector3 Unpack(uint p)
    {
        var v = new Vector3((int)(p & 1023) - 512, (int)(p >> 10 & 1023) - 512, (int)(p >> 20 & 1023) - 512);
        var l = v.Length();
        return l > 0 ? v / l : Vector3.UnitZ;
    }
}
