using System.Globalization;
using System.IO.Pipes;
using ClayViewer;
using ClayViewer.Rendering;
using ClayViewer.Stock;
using ViveClay.Protocol;

// Vive Clay Viewer - external visualiser for the Grasshopper "Vive Clay Viewer" component.
//   ClayViewer.exe                  wait for Grasshopper on pipe "ViveClayViewer"
//   ClayViewer.exe --demo           synthetic knife + loop tool carving the workshop brick at 90 Hz (no pipe)
//   --demo-tool knife|loop|both     which demo tool (default both, switching every 8 s)
//   --demo-stock box|mesh           plain box, or the brick as a closed mesh with chamfered edges (default box)
//   --demo-seconds N                close automatically after N seconds and print a summary
//   --res MM                        demo stock resolution (default 0.5)
//   --bench                         demo + timing report (ms per carve step, ms per remesh, dropped poses, fps, volume)
//   --min-thick F                   minimum cutter thickness as a multiple of the resolution (default 1.25)
//   --headless                      no window (engine + pipe only)
//   --screenshot FILE.png           save the last frame before closing (with --demo-seconds); key P saves one any time
//   --zoom F  --yaw RAD  --pitch RAD camera distance factor (e.g. 0.5 = closer) and initial orbit angles
//   --export FILE.obj               write the carved mesh at the end and print a watertightness check
//   --selftest                      analytic carving cases (expected vs measured volume) at --res, then exit
//   --select-take N                 highlight recorded take N at start (screenshots)
//   --ui-scale F                    panel scale override (default: window DPI / 96)
//   --pipe NAME                     override pipe name
var a = args.ToList();
bool Has(string f) => a.Contains(f);
string? Val(string f) { var i = a.IndexOf(f); return i >= 0 && i + 1 < a.Count ? a[i + 1] : null; }
double Num(string f, double def) => double.TryParse(Val(f), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : def;

var bench = Has("--bench");
var demo = Has("--demo") || bench;
var headless = Has("--headless");
var seconds = Num("--demo-seconds", bench ? 20 : 0);
var demoRes = Num("--res", 0.5);
var pipeName = Val("--pipe") ?? ClayWire.PipeName;
var exportPath = Val("--export");
TriDexelStock.MinThicknessFactor = Math.Max(0.5, Num("--min-thick", TriDexelStock.MinThicknessFactor));

if (Has("--selftest")) { SelfTest.Run(demoRes); return 0; }

AppDomain.CurrentDomain.UnhandledException += (_, e) => Console.Error.WriteLine("[fatal] " + e.ExceptionObject);

using var engine = new ClayEngine();
PipeServer? pipe = null;
if (!demo)
{
    pipe = new PipeServer(engine, pipeName);
    try { pipe.Start(); }
    catch (IOException)
    {
        Console.Error.WriteLine("Another Vive Clay Viewer is already running (pipe '" + pipeName + "' is in use).");
        return 2;
    }
    Console.WriteLine("[viewer] pipe server listening on '" + pipeName + "'");
}

using var stop = new CancellationTokenSource();
if (demo) DemoDriver.Start(engine, demoRes, Val("--demo-tool") ?? "both", Val("--demo-stock") == "mesh", stop.Token);

Viewer? viewer = null;
if (headless)
{
    var end = seconds > 0 ? DateTime.UtcNow.AddSeconds(seconds) : DateTime.MaxValue;
    while (DateTime.UtcNow < end) Thread.Sleep(50);
    stop.Cancel();
}
else
{
    var shotPath = Val("--screenshot");
    var winSize = shotPath != null ? new Silk.NET.Maths.Vector2D<int>(1600, 900) : (Silk.NET.Maths.Vector2D<int>?)null;
    viewer = new Viewer(engine, winSize) { UiScale = (float)Num("--ui-scale", 0), Zoom = (float)Num("--zoom", 1), CloseAfterSeconds = seconds, ScreenshotPath = shotPath, LogSlowFrames = bench };
    viewer.SelectTake = (int)Num("--select-take", -1);
    viewer.Yaw = (float)Num("--yaw", viewer.Yaw); viewer.Pitch = (float)Num("--pitch", viewer.Pitch);
    try { viewer.Run(); }
    catch (Exception ex) { Console.Error.WriteLine("[fatal] " + ex); return 1; }
    finally { stop.Cancel(); pipe?.Dispose(); }
}

// let the carver finish what is queued, then report
for (var i = 0; i < 200 && engine.Backlog > 0; i++) Thread.Sleep(25);
Thread.Sleep(50);
var st = engine.Stats;
var fps = viewer != null ? $"frames {viewer.FramesRendered}, avg fps {viewer.AvgFps:0.0}, " : "";
Console.WriteLine($"[summary] {fps}poses received {st.PosesReceived}, carved {st.PosesCarved}, dropped {st.PosesDropped}, removed {engine.RemovedVolume:0.0} mm3");
if (bench)
{
    var inv = CultureInfo.InvariantCulture;
    Console.WriteLine("[bench] " + engine.StockInfo + string.Format(inv, ", {0:N0} cells, requested res {1} mm", engine.CellCount, demoRes));
    Console.WriteLine(string.Format(inv, "[bench] poses: received {0}, carved {1} ({2} merged into longer sweeps), dropped {3}, max queue {4}",
        st.PosesReceived, st.PosesCarved, st.PosesCoalesced, st.PosesDropped, st.MaxBacklog));
    Console.WriteLine(string.Format(inv, "[bench] carve: {0:0.000} ms per pose avg, {1:0.00} ms max, {2:0.0} sub-steps per pose, {3:0.000} ms per sub-step",
        st.CarveMsTotal / Math.Max(1, st.Sweeps), st.CarveMsMax, st.SubSteps / (double)Math.Max(1, st.Sweeps), st.CarveMsTotal / Math.Max(1, st.SubSteps)));
    Console.WriteLine(string.Format(inv, "[bench] remesh: {0:0.000} ms avg, {1:0.00} ms max, {2:0.0} chunks per remesh, {3} remeshes",
        st.RemeshMsTotal / Math.Max(1, st.Remeshes), st.RemeshMsMax, st.ChunksRemeshed / (double)Math.Max(1, st.Remeshes), st.Remeshes));
    if (viewer != null)
        Console.WriteLine(string.Format(inv, "[bench] render: avg {0:0.0} fps over {1} frames, worst frame {2:0.0} ms, {3} frames over 34 ms", viewer.AvgFps, viewer.FramesRendered, viewer.WorstFrameMs, viewer.SlowFrames));
    Console.WriteLine(string.Format(inv, "[bench] removed volume {0:0.0} mm3", engine.RemovedVolume));
}

if (exportPath != null)
{
    using var done = new ManualResetEventSlim();
    MeshData? mesh = null; double remaining = 0;
    engine.Post(() =>
    {
        mesh = engine.Stock?.ExportMesh();
        remaining = (engine.Stock as TriDexelStock)?.RemainingVolume() ?? 0;
        done.Set();
    });
    done.Wait(TimeSpan.FromSeconds(120));
    if (mesh != null)
    {
        MeshCheck.WriteObj(exportPath, mesh);
        var (open, nonManifold, volume) = MeshCheck.Analyse(mesh);
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "[export] {0}: {1} vertices, {2} triangles, open edges {3}, non-manifold edges {4}, mesh volume {5:0.0} mm3, dexel volume {6:0.0} mm3",
            exportPath, mesh.VertexCount, mesh.TriangleCount, open, nonManifold, volume, remaining));
    }
}
return 0;

/// <summary>Export helpers: OBJ writer and a closed/manifold/volume check of a triangle mesh.</summary>
public static class MeshCheck
{
    public static void WriteObj(string path, MeshData m)
    {
        using var w = new StreamWriter(path);
        var inv = CultureInfo.InvariantCulture;
        for (var i = 0; i < m.Vertices.Length; i += 3)
            w.WriteLine(string.Format(inv, "v {0:0.####} {1:0.####} {2:0.####}", m.Vertices[i], m.Vertices[i + 1], m.Vertices[i + 2]));
        for (var i = 0; i + 2 < m.Indices.Length; i += 3)
            w.WriteLine($"f {m.Indices[i] + 1} {m.Indices[i + 1] + 1} {m.Indices[i + 2] + 1}");
    }

    /// <summary>Directed-edge check: a closed oriented manifold has every directed edge exactly once with its reverse
    /// exactly once. Returns (edges without a partner, edges used more than once per direction, enclosed volume).</summary>
    public static (int open, int nonManifold, double volume) Analyse(MeshData m)
    {
        var edges = new Dictionary<(int, int), int>();
        double vol = 0;
        var v = m.Vertices;
        for (var i = 0; i + 2 < m.Indices.Length; i += 3)
        {
            int a = m.Indices[i], b = m.Indices[i + 1], c = m.Indices[i + 2];
            edges[(a, b)] = edges.GetValueOrDefault((a, b)) + 1;
            edges[(b, c)] = edges.GetValueOrDefault((b, c)) + 1;
            edges[(c, a)] = edges.GetValueOrDefault((c, a)) + 1;
            double ax = v[a * 3], ay = v[a * 3 + 1], az = v[a * 3 + 2], bx = v[b * 3], by = v[b * 3 + 1], bz = v[b * 3 + 2], cx = v[c * 3], cy = v[c * 3 + 1], cz = v[c * 3 + 2];
            vol += (ax * (by * cz - bz * cy) + ay * (bz * cx - bx * cz) + az * (bx * cy - by * cx)) / 6.0;
        }
        int open = 0, non = 0;
        foreach (var (k, n) in edges)
        {
            if (n > 1) non++;
            if (!edges.ContainsKey((k.Item2, k.Item1))) open++;
        }
        return (open, non, vol);
    }
}
