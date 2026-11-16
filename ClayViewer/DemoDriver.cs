using System.Diagnostics;
using System.Numerics;
using ViveClay.Protocol;

namespace ClayViewer;

/// <summary>
/// --demo: feeds the engine directly (no pipe) with the workshop brick (226.2 x 88 x 100 mm) and two synthetic hand
/// tools at 90 Hz, so the viewer can be checked on its own:
///   knife - a 0.4 mm thick pointed steel blade (closed mesh, thinner than a cell) slicing long curved strokes;
///   loop  - a ribbon/wire loop tool; its Cutter is the filled loop face (an OPEN single-surface mesh), because the
///           clay that passes through the loop comes away as a ribbon. Scoops channels across the brick.
/// Tools are modelled in the tool-local frame: tracker origin at the handle top, working end pointing down -Z,
/// tip at z = -130. Handle and tracker go in the display Tool mesh, only the Cutter mesh removes material.
/// </summary>
public static class DemoDriver
{
    public const double PoseHz = 90.0;

    public static Thread Start(ClayEngine engine, double resolutionMm, string tool, bool meshStock, CancellationToken stop)
    {
        var t = new Thread(() => Run(engine, resolutionMm, tool, meshStock, stop)) { IsBackground = true, Name = "ClayDemo" };
        t.Start();
        return t;
    }

    static void Run(ClayEngine engine, double res, string tool, bool meshStock, CancellationToken stop)
    {
        engine.SetConnection("demo");
        engine.Submit(new SetStockMsg
        {
            Min = new[] { 0.0, 0, 0 }, Max = new[] { 226.2, 88, 100 }, ResolutionMm = res,
            Mesh = meshStock ? ChamferedBrick() : null,
        });
        var useLoop = tool == "loop";
        SendTool(engine, useLoop);

        var sw = Stopwatch.StartNew();
        const double dt = 1.0 / PoseHz;
        const double switchEvery = 8.0;
        long k = 0;
        var rec = new DemoRecorder();
        while (!stop.IsCancellationRequested)
        {
            if (sw.Elapsed.TotalSeconds < k * dt) { Thread.Sleep(1); continue; }
            var t = k * dt;                                     // path depends on the pose index only (repeatable)
            k++;
            if (tool == "both")
            {
                var wantLoop = (int)(t / switchEvery) % 2 == 1;
                if (wantLoop != useLoop) { useLoop = wantLoop; SendTool(engine, useLoop); }
            }
            var m = useLoop ? LoopPose(t) : KnifePose(t);
            engine.Submit(new PoseMsg { Matrix = m, Timestamp = t, CuttingEnabled = true });
            rec.Step(engine, t, m);
        }
    }

    /// <summary>Synthetic Take Recorder: three takes recorded at 30 Hz along the demo tool's tip with a growing tool tilt
    /// (take 1 near-vertical, take 2 moderate, take 3 sweeping past 30 degrees), published as RecordedPlanes snapshots at 5 Hz.</summary>
    sealed class DemoRecorder
    {
        static readonly (double from, double to, double tilt0, double tilt1)[] Takes =
        {
            (0.4, 2.6, 4, 14), (3.0, 5.0, 12, 26), (5.4, 7.6, 18, 44),
        };
        readonly List<int> _take = new(); readonly List<float> _o = new(), _a = new(); readonly List<double> _t = new();
        double _lastRec = -1, _lastSend = -1; bool _dirty, _wasRec;

        public void Step(ClayEngine engine, double t, double[] m)
        {
            var cur = -1;
            for (var i = 0; i < Takes.Length; i++) if (t >= Takes[i].from && t < Takes[i].to) cur = i;
            if (cur >= 0 && t - _lastRec >= 1.0 / 30)
            {
                _lastRec = t;
                var (from, to, a0, a1) = Takes[cur];
                var u = (t - from) / (to - from);
                var tilt = (a0 + (a1 - a0) * u + 3 * Math.Sin(7.3 * t)) * Math.PI / 180;
                var az = 0.8 * t + cur * 2.1;
                // tip of the demo tool (local (0,0,-130)) as origin, synthetic axis with the scripted tilt
                _take.Add(cur + 1);
                _o.Add((float)m[3]); _o.Add((float)m[7]); _o.Add((float)(m[11] - 130));      // tip below the tracker (ignoring the small blade tilt)
                _a.Add((float)(Math.Sin(tilt) * Math.Cos(az))); _a.Add((float)(Math.Sin(tilt) * Math.Sin(az))); _a.Add((float)Math.Cos(tilt));
                _t.Add(t);
                _dirty = true;
            }
            var recording = cur >= 0;
            if (t - _lastSend >= 0.2 && (_dirty || recording != _wasRec))
            {
                _lastSend = t; _dirty = false; _wasRec = recording;
                engine.Submit(new RecordedPlanesMsg
                {
                    Recording = recording, CurrentTake = recording ? cur + 1 : _take.Count > 0 ? _take[^1] : 0,
                    Take = _take.ToArray(), Origin = _o.ToArray(), Axis = _a.ToArray(), Time = _t.ToArray(),
                });
            }
        }
    }

    static void SendTool(ClayEngine engine, bool loop)
    {
        engine.Submit(new SetToolMsg { Mesh = loop ? LoopToolMesh() : KnifeToolMesh() });
        engine.Submit(new SetCutterMsg { Mesh = loop ? LoopCutterMesh() : KnifeCutterMesh() });
    }

    // ------------------------------------------------------------------ paths
    /// <summary>Knife: long strokes along the brick, blade plane following the direction of travel, lifted out at the turns.</summary>
    public static double[] KnifePose(double t)
    {
        const double w = 0.8;
        double x = 113.1 + 92 * Math.Sin(w * t), y = 44 + 30 * Math.Sin(0.23 * t) + 5 * Math.Sin(1.9 * t);
        double vx = 92 * w * Math.Cos(w * t), vy = 30 * 0.23 * Math.Cos(0.23 * t) + 5 * 1.9 * Math.Cos(1.9 * t);
        var yaw = Math.Atan2(vy * Math.Sign(vx), Math.Abs(vx) + 1e-6);      // blade (local X) along the stroke
        var depth = 14 * Smooth(0.15, 0.5, Math.Abs(Math.Cos(w * t))) - 4;  // mm below the top face; negative = lifted
        return Pose(x, y, 100 - depth + 130, yaw, 0.12 * Math.Sin(0.5 * t));
    }

    /// <summary>Loop: scoops across the brick (entering and leaving through the long sides), loop face square to the travel.</summary>
    public static double[] LoopPose(double t)
    {
        const double w = 1.15;
        double x = 113.1 + 88 * Math.Sin(0.17 * t + 0.6), y = 44 + 58 * Math.Sin(w * t);
        double vx = 88 * 0.17 * Math.Cos(0.17 * t + 0.6), vy = 58 * w * Math.Cos(w * t);
        var yaw = Math.Atan2(-vx * Math.Sign(vy), Math.Abs(vy) + 1e-6);     // loop normal (local Y) along the travel
        var depth = 8 + 4 * Math.Sin(0.45 * t);
        return Pose(x, y, 100 - depth + 130, yaw, 0);
    }

    static double Smooth(double a, double b, double x)
    {
        var s = Math.Clamp((x - a) / (b - a), 0, 1);
        return s * s * (3 - 2 * s);
    }

    /// <summary>Row-major column-vector pose: yaw about Z, then a small tilt about the tool's own X.</summary>
    static double[] Pose(double x, double y, double z, double yaw, double tilt)
    {
        double c = Math.Cos(yaw), s = Math.Sin(yaw), ct = Math.Cos(tilt), st = Math.Sin(tilt);
        return new[]
        {
            c, -s * ct,  s * st, x,
            s,  c * ct, -c * st, y,
            0,  st,      ct,     z,
            0,  0,       0,      1,
        };
    }

    // ------------------------------------------------------------------ meshes (flat shaded, unshared vertices)
    /// <summary>The blade only: pointed profile in the XZ plane, 0.4 mm thick, closed.</summary>
    public static MeshData KnifeCutterMesh()
    {
        var b = new Builder();
        var profile = new[] { new Vector2(-9, -66), new Vector2(-9, -112), new Vector2(-3, -127), new Vector2(2, -130), new Vector2(9, -118), new Vector2(9, -66) };
        b.Prism(profile, 0.2f, (p, h) => new Vector3(p.X, h, p.Y));
        return b.Build();
    }

    public static MeshData KnifeToolMesh()
    {
        var b = new Builder();
        b.Cylinder(new Vector3(0, 0, -68), new Vector3(0, 0, -4), 7.5f, 14);       // handle
        b.Cylinder(new Vector3(0, 0, -4), new Vector3(0, 0, 6), 22f, 24);          // tracker puck
        return b.Build();
    }

    /// <summary>Filled loop face in the XZ plane (open, single surface): radius 11, lowest point at z = -130.</summary>
    public static MeshData LoopCutterMesh()
    {
        const int n = 40; const float r = 11f;
        var v = new List<float> { 0, 0, -119 }; var idx = new List<int>();
        for (var i = 0; i < n; i++)
        {
            var a = i * MathF.Tau / n;
            v.Add(r * MathF.Cos(a)); v.Add(0); v.Add(-119 + r * MathF.Sin(a));
            idx.Add(0); idx.Add(1 + i); idx.Add(1 + (i + 1) % n);
        }
        return new MeshData(v.ToArray(), idx.ToArray());
    }

    public static MeshData LoopToolMesh()
    {
        var b = new Builder();
        b.Cylinder(new Vector3(0, 0, -100), new Vector3(0, 0, -4), 6.5f, 14);
        b.Cylinder(new Vector3(0, 0, -4), new Vector3(0, 0, 6), 22f, 24);
        b.Cylinder(new Vector3(0, 0, -108.5f), new Vector3(0, 0, -100), 2.2f, 8);  // ferrule
        return b.Build();
    }

    /// <summary>The brick as a closed mesh with its four vertical edges chamfered by 12 mm (exercises the mesh-stock path).</summary>
    public static MeshData ChamferedBrick()
    {
        const float L = 226.2f, Wd = 88f, H = 100f, c = 12f;
        var b = new Builder();
        var poly = new[]
        {
            new Vector2(c, 0), new Vector2(L - c, 0), new Vector2(L, c), new Vector2(L, Wd - c),
            new Vector2(L - c, Wd), new Vector2(c, Wd), new Vector2(0, Wd - c), new Vector2(0, c),
        };
        b.Prism(poly, H / 2, (p, h) => new Vector3(p.X, p.Y, h + H / 2));
        return b.Build();
    }

    /// <summary>Volume of ChamferedBrick in mm^3 (box minus four triangular prisms).</summary>
    public const double ChamferedBrickVolume = 226.2 * 88 * 100 - 4 * (12.0 * 12.0 / 2) * 100;

    public static void AddBox(List<float> v, List<int> idx, float x0, float y0, float z0, float x1, float y1, float z1)
    {
        var b = v.Count / 3;
        for (var i = 0; i < 8; i++)
        { v.Add((i & 1) == 0 ? x0 : x1); v.Add((i & 2) == 0 ? y0 : y1); v.Add((i & 4) == 0 ? z0 : z1); }
        int[] f = { 0, 2, 3, 0, 3, 1, 4, 5, 7, 4, 7, 6, 0, 1, 5, 0, 5, 4, 2, 6, 7, 2, 7, 3, 0, 4, 6, 0, 6, 2, 1, 3, 7, 1, 7, 5 };
        foreach (var i in f) idx.Add(b + i);
    }

    /// <summary>Flat-shaded convex solids with outward faces.</summary>
    sealed class Builder
    {
        readonly List<float> _v = new(), _n = new(); readonly List<int> _i = new();
        public MeshData Build() => new(_v.ToArray(), _i.ToArray(), _n.ToArray());

        void Face(Vector3 solidCentre, params Vector3[] p)
        {
            var c = Vector3.Zero; foreach (var q in p) c += q; c /= p.Length;
            var n = Vector3.Zero;
            for (var k = 0; k < p.Length; k++) n += Vector3.Cross(p[k] - c, p[(k + 1) % p.Length] - c);
            n = Vector3.Normalize(n);
            if (Vector3.Dot(n, c - solidCentre) < 0) { Array.Reverse(p); n = -n; }
            var b = _v.Count / 3;
            foreach (var q in p) { _v.Add(q.X); _v.Add(q.Y); _v.Add(q.Z); _n.Add(n.X); _n.Add(n.Y); _n.Add(n.Z); }
            for (var k = 1; k + 1 < p.Length; k++) { _i.Add(b); _i.Add(b + k); _i.Add(b + k + 1); }
        }

        /// <summary>Convex polygon extruded from -half to +half along the axis given by map(point, height).</summary>
        public void Prism(Vector2[] poly, float half, Func<Vector2, float, Vector3> map)
        {
            var centre = Vector3.Zero;
            foreach (var p in poly) centre += map(p, 0);
            centre /= poly.Length;
            Face(centre, poly.Select(p => map(p, -half)).ToArray());
            Face(centre, poly.Select(p => map(p, half)).ToArray());
            for (var k = 0; k < poly.Length; k++)
            {
                Vector2 a = poly[k], b = poly[(k + 1) % poly.Length];
                Face(centre, map(a, -half), map(b, -half), map(b, half), map(a, half));
            }
        }

        public void Cylinder(Vector3 a, Vector3 b, float r, int sides)
        {
            var axis = Vector3.Normalize(b - a);
            var u = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX));
            var w = Vector3.Cross(axis, u);
            var mid = (a + b) / 2; var half = Vector3.Distance(a, b) / 2;
            var poly = new Vector2[sides];
            for (var k = 0; k < sides; k++) poly[k] = new Vector2(r * MathF.Cos(k * MathF.Tau / sides), r * MathF.Sin(k * MathF.Tau / sides));
            Prism(poly, half, (p, h) => mid + u * p.X + w * p.Y + axis * h);
        }
    }
}
