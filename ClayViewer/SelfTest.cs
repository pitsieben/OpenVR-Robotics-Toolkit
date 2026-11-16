using System.Globalization;
using System.Numerics;
using ClayViewer.Stock;
using ViveClay.Protocol;

namespace ClayViewer;

/// <summary>--selftest: a handful of analytic carving cases (expected vs measured volume, closed-mesh check).
/// A quick sanity run for developers, not the acceptance suite.</summary>
public static class SelfTest
{
    public static void Run(double res)
    {
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "[selftest] resolution {0} mm, min cutter thickness {1} mm", res, res * TriDexelStock.MinThicknessFactor));
        var th = MathF.Max(0.4f, (float)(res * TriDexelStock.MinThicknessFactor)); // 0.4 mm blade is only thickened below the minimum
        var tm = (float)(res * TriDexelStock.MinThicknessFactor); // open sheets always get exactly the minimum thickness
        var box = new StockBox(Vector3.Zero, new Vector3(100, 60, 40));
        MeshData Box(float x0, float y0, float z0, float x1, float y1, float z1)
        { var v = new List<float>(); var i = new List<int>(); DemoDriver.AddBox(v, i, x0, y0, z0, x1, y1, z1); return new MeshData(v.ToArray(), i.ToArray()); }
        Matrix4x4 At(float x, float y, float z, float yaw = 0) => Matrix4x4.CreateRotationZ(yaw) * Matrix4x4.CreateTranslation(x, y, z);

        void Case(string name, MeshData cutter, double expected, Action<TriDexelStock> carve, MeshData? stockMesh = null, double tolPct = 1.0)
        {
            var s = new TriDexelStock();
            s.Init(box, res, stockMesh);
            var vol0 = MeshCheck.Analyse(s.ExportMesh()).volume;
            s.SetCutter(cutter);
            carve(s);
            var m = s.ExportMesh();
            var (open, non, vol) = MeshCheck.Analyse(m);
            var dump = Environment.GetEnvironmentVariable("CLAY_SELFTEST_DUMP");
            if (!string.IsNullOrEmpty(dump)) MeshCheck.WriteObj(Path.Combine(dump, new string(name.Where(char.IsLetterOrDigit).ToArray()) + ".obj"), m);
            var err = expected > 0 ? (s.RemovedVolume - expected) / expected * 100 : 0;
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "[selftest] {0,-38} expected {1,9:0.0}  removed {2,9:0.0}  err {3,6:0.00}%  (ray estimate {4,9:0.0}) | export: {5:0.0} mm3 lost, open {6}, non-manifold {7}  {8}",
                name, expected, s.RemovedVolume, err, s.RemovedVolumeDexel, vol0 - vol, open, non, Math.Abs(err) <= tolPct && open == 0 && non == 0 ? "OK" : "CHECK"));
        }

        var punch = Box(-5, -5, 0, 5, 5, 60);
        Case("static plunge 10x10x5", punch, 500, s => s.CarveSweep(At(50, 30, 35), At(50, 30, 35)));
        Case("slot sweep 10 wide, 5 deep, 50 long", punch, 3000, s => { s.CarveSweep(At(25, 30, 35), At(25, 30, 35)); s.CarveSweep(At(25, 30, 35), At(75, 30, 35)); });
        Case("slot swept at 30 deg (diagonal)", punch, 5 * (100 + 50 * 10 * (Math.Cos(Math.PI / 6) + Math.Sin(Math.PI / 6))), s => { s.CarveSweep(At(25, 15, 35), At(25, 15, 35)); s.CarveSweep(At(25, 15, 35), At(68.3f, 40, 35)); });
        var blade = Box(-9, -0.2f, 0, 9, 0.2f, 60);     // 0.4 mm thick closed blade
        Case("thin blade static (18 x t x 10)", blade, 18 * th * 10, s => s.CarveSweep(At(50, 30, 30), At(50, 30, 30)), tolPct: 3);
        Case("thin blade slicing 60 mm in-plane", blade, (60 + 18) * th * 10, s => { s.CarveSweep(At(20, 30, 30), At(20, 30, 30)); s.CarveSweep(At(20, 30, 30), At(80, 30, 30)); }, tolPct: 3);
        Case("thin blade scraping 30 mm broadside", blade, 18 * 10 * (30 + th), s => { s.CarveSweep(At(50, 15, 30), At(50, 15, 30)); s.CarveSweep(At(50, 15, 30), At(50, 45, 30)); });
        Case("thin blade yawed 37 deg, static", blade, 18 * th * 10, s => s.CarveSweep(At(50, 30, 30, 0.6458f), At(50, 30, 30, 0.6458f)), tolPct: 3);
        Case("thin blade spun 180 deg (disc r=9)", blade, Math.PI * 81 * 10, s => { s.CarveSweep(At(50, 30, 30), At(50, 30, 30)); s.CarveSweep(At(50, 30, 30), At(50, 30, 30, 1.5f)); s.CarveSweep(At(50, 30, 30, 1.5f), At(50, 30, 30, 3.14159f)); }, tolPct: 2);
        var sheet = new MeshData(new float[] { -10, 0, 0, 10, 0, 0, 10, 0, 60, -10, 0, 60 }, new[] { 0, 1, 2, 0, 2, 3 });   // open single quad
        Case("open sheet static (20 x t x 10 + rim)", sheet, tm * (20 + tm) * (10 + tm / 2) - (1 - Math.PI / 4) * tm * tm / 2 * (20 + 2 * 10), s => s.CarveSweep(At(50, 30, 30), At(50, 30, 30)), tolPct: 4);
        Case("sphere r=8 half sunk", Sphere(8), 2.0 / 3 * Math.PI * 512, s => s.CarveSweep(At(50, 30, 40), At(50, 30, 40)));
        Case("sphere r=8 dragged 40 mm (half sunk)", Sphere(8), 2.0 / 3 * Math.PI * 512 + Math.PI * 64 / 2 * 40, s => { s.CarveSweep(At(30, 30, 40), At(30, 30, 40)); s.CarveSweep(At(30, 30, 40), At(70, 30, 40)); });
        var brick = DemoDriver.ChamferedBrick();
        box = new StockBox(Vector3.Zero, new Vector3(226.2f, 88, 100));
        Case("mesh stock (chamfered brick) volume", punch, 500, s =>
        {
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "[selftest]   mesh stock volume {0:0.0}, analytic {1:0.0}", s.RemainingVolume(), DemoDriver.ChamferedBrickVolume));
            s.CarveSweep(At(113, 44, 95), At(113, 44, 95));
        }, brick);
    }

    static MeshData Sphere(float r, int n = 48)
    {
        var v = new List<float>(); var idx = new List<int>();
        for (var i = 0; i <= n; i++)
            for (var j = 0; j < 2 * n; j++)
            {
                float a = MathF.PI * i / n, b = MathF.PI * j / n;
                v.Add(r * MathF.Sin(a) * MathF.Cos(b)); v.Add(r * MathF.Sin(a) * MathF.Sin(b)); v.Add(r * MathF.Cos(a));
            }
        for (var i = 0; i < n; i++)
            for (var j = 0; j < 2 * n; j++)
            {
                int p = i * 2 * n + j, q = i * 2 * n + (j + 1) % (2 * n), p2 = p + 2 * n, q2 = q + 2 * n;
                idx.AddRange(new[] { p, p2, q2, p, q2, q });
            }
        return new MeshData(v.ToArray(), idx.ToArray());
    }
}
