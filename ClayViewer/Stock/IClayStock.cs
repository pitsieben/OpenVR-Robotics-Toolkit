using System.Numerics;
using ViveClay.Protocol;

namespace ClayViewer.Stock;

/// <summary>Axis-aligned stock box in millimetres.</summary>
public readonly record struct StockBox(Vector3 Min, Vector3 Max)
{
    public Vector3 Size => Max - Min;
    public Vector3 Center => (Min + Max) * 0.5f;
}

/// <summary>Surface mesh of one chunk of the stock. Vertices are 7 floats: position xyz, normal xyz, cut time
/// (seconds on the stock clock at which that piece of surface was cut; very negative for the original skin).
/// Empty arrays mean "this chunk has no surface any more".</summary>
public sealed class ChunkMesh
{
    public int Id;
    public float[] Vertices = Array.Empty<float>();
    public int[] Indices = Array.Empty<int>();
}

/// <summary>
/// === CARVING CORE INTERFACE ===
/// ClayEngine owns ONE instance and calls every member from a single dedicated carving thread (never the render
/// thread, never the pipe thread), so implementations need no locking against callers (they may use worker threads
/// internally). All units are millimetres, world frame is Rhino's (Z up).
/// Matrices are System.Numerics row-vector convention: worldPoint = Vector3.Transform(toolLocalPoint, matrix).
/// </summary>
public interface IClayStock
{
    /// <summary>(Re)create the stock. <paramref name="mesh"/> is non-null when the stock is not a plain box
    /// (the box is then the mesh's bounding box). Resets RemovedVolume to 0.</summary>
    void Init(StockBox box, double resolutionMm, MeshData? mesh);

    /// <summary>Set the geometry that removes material (tool-local frame). May be thin, open or non-convex.</summary>
    void SetCutter(MeshData cutter);

    /// <summary>Remove the volume swept by the cutter moving from pose <paramref name="from"/> to pose
    /// <paramref name="to"/> (rotation and translation are interpolated). from == to carves the static pose.
    /// Returns the number of interpolation sub-steps used.</summary>
    int CarveSweep(Matrix4x4 from, Matrix4x4 to);

    /// <summary>Sub-step length as a fraction of the cell size (0.3 = default). The engine raises it when it is behind.</summary>
    double StepFraction { get; set; }

    /// <summary>Re-mesh every chunk whose surface changed since the last call and append the results.</summary>
    int RemeshDirty(List<ChunkMesh> output);

    /// <summary>Total removed material in mm^3 since Init/Reset.</summary>
    double RemovedVolume { get; }

    /// <summary>Seconds since Init/Reset on the clock used for the per-vertex cut time.</summary>
    float Now { get; }

    /// <summary>Current carved surface as one closed, welded triangle mesh (mm), for Grasshopper "Get Mesh".</summary>
    MeshData ExportMesh();

    /// <summary>Restore the uncarved stock (same box/resolution/mesh).</summary>
    void Reset();

    /// <summary>One-line description for the HUD.</summary>
    string Describe();

    /// <summary>Number of grid cells (for the bench report).</summary>
    long CellCount { get; }
}
