// Binary message protocol between the Grasshopper component (client) and ClayViewer.exe (pipe server).
// This file is SOURCE-LINKED into both projects (Compile Include ... Link), so it must stay
// dependency-free and compile on net7.0 and net8.0.
//
// Wire format (little-endian):
//   frame   = uint32 length | byte type | body[length - 1]
//   string  = int32 byteCount | UTF-8 bytes
//   mesh    = int32 vertexCount | float32[3*vertexCount] xyz | int32 indexCount | int32[indexCount] (triangles)
//   matrix  = double[16], ROW-MAJOR, column-vector convention: p' = M * p, translation in elements 3, 7, 11
//             (the same layout as Rhino.Geometry.Transform's M00..M33).
// All geometry is in MILLIMETRES. Unknown message types are skipped (forward compatible); a change of
// ClayWire.Version is bumped for every
// addition; peers older than ClayWire.MinCompatibleVersion are incompatible.
using System.Text;

namespace ViveClay.Protocol;

public enum MsgType : byte
{
    Hello = 1,       // both directions, first message sent by the server after connect
    SetStock = 2,    // client -> viewer: stock box (+ optional mesh) and resolution
    SetTool = 3,     // client -> viewer: tool mesh in the tool-local frame
    Pose = 4,        // client -> viewer: tool-local -> world transform
    Reset = 5,       // client -> viewer: restore the uncarved stock
    RequestMesh = 6, // client -> viewer: please reply with CarvedMesh
    CarvedMesh = 7,  // viewer -> client
    Status = 8,      // viewer -> client: human readable state + running removed volume
    Close = 9,       // either direction: orderly shutdown
    SetCutter = 10,  // v2, client -> viewer: the part of the tool that removes material (blade / loop), tool-local frame.
                     //     An empty mesh clears it (the SetTool mesh then cuts). v1 viewers skip this message.
    RecordedPlanes = 11, // v3, client -> viewer: recording flag + all recorded planes of the active Take Recorder (full snapshot,
                     //     replaces the previous one; sent at most 5 Hz and only on change). v1/v2 viewers skip it.
}

/// <summary>Triangle mesh. Vertices are xyz triplets; Normals (optional, not serialised) are xyz per vertex.</summary>
public sealed class MeshData
{
    public float[] Vertices;
    public int[] Indices;
    public float[]? Normals;
    public MeshData(float[] vertices, int[] indices, float[]? normals = null)
    { Vertices = vertices; Indices = indices; Normals = normals; }
    public int VertexCount => Vertices.Length / 3;
    public int TriangleCount => Indices.Length / 3;
}

public abstract class ClayMessage { public abstract MsgType Type { get; } }

public sealed class HelloMsg : ClayMessage
{
    public override MsgType Type => MsgType.Hello;
    public int Version = ClayWire.Version;
    public string Name = "";
}

public sealed class SetStockMsg : ClayMessage
{
    public override MsgType Type => MsgType.SetStock;
    public double[] Min = new double[3];
    public double[] Max = new double[3];
    public double ResolutionMm = 0.5;
    /// <summary>Only set when the stock is not an axis-aligned box.</summary>
    public MeshData? Mesh;
}

public sealed class SetToolMsg : ClayMessage
{
    public override MsgType Type => MsgType.SetTool;
    public MeshData Mesh = new(Array.Empty<float>(), Array.Empty<int>());
}

/// <summary>v2. The cutting geometry (blade, wire loop, rib edge) in the tool-local frame. Only this removes material;
/// the SetTool mesh (handle, tracker) is then display-only. Thin, open and non-convex meshes are allowed.</summary>
public sealed class SetCutterMsg : ClayMessage
{
    public override MsgType Type => MsgType.SetCutter;
    public MeshData Mesh = new(Array.Empty<float>(), Array.Empty<int>());
}

/// <summary>v3. Snapshot of the recorded planes (only frames actually recorded). Body layout:
///   bool recording (1 byte) | int32 currentTake | int32 count |
///   count x [ int32 take | float32 ox, oy, oz (mm) | float32 ax, ay, az (unit tool axis = plane Z) | float64 timestamp (s) ]
/// Take indices are 0-based (same as the Take Recorder's take index). currentTake = the take being recorded, or the index the
/// next take will get when not recording.</summary>
public sealed class RecordedPlanesMsg : ClayMessage
{
    public override MsgType Type => MsgType.RecordedPlanes;
    public bool Recording;
    public int CurrentTake;
    public int[] Take = Array.Empty<int>();
    public float[] Origin = Array.Empty<float>();   // 3 per plane
    public float[] Axis = Array.Empty<float>();     // 3 per plane
    public double[] Time = Array.Empty<double>();
    public int Count => Take.Length;
}

public sealed class PoseMsg : ClayMessage
{
    public override MsgType Type => MsgType.Pose;
    public double[] Matrix = new double[16];
    public double Timestamp;      // seconds, sender's clock (monotonic is fine)
    public bool CuttingEnabled = true;
    /// <summary>Optional TCP plane Z axis (world). Appended to the frame; older readers ignore it.</summary>
    public double[]? TcpAxis;
}

public sealed class ResetMsg : ClayMessage { public override MsgType Type => MsgType.Reset; }
public sealed class RequestMeshMsg : ClayMessage { public override MsgType Type => MsgType.RequestMesh; }
public sealed class CloseMsg : ClayMessage { public override MsgType Type => MsgType.Close; }

public sealed class CarvedMeshMsg : ClayMessage
{
    public override MsgType Type => MsgType.CarvedMesh;
    public MeshData Mesh = new(Array.Empty<float>(), Array.Empty<int>());
    public double RemovedVolumeMm3;
}

public sealed class StatusMsg : ClayMessage
{
    public override MsgType Type => MsgType.Status;
    public string Text = "";
    public double RemovedVolumeMm3;
}

/// <summary>A type this build does not know; returned so callers can ignore it.</summary>
public sealed class UnknownMsg : ClayMessage
{
    public byte Raw;
    public override MsgType Type => (MsgType)Raw;
}

public static class ClayWire
{
    public const string PipeName = "ViveClayViewer";
    /// <summary>2 = adds SetCutter, 3 = adds RecordedPlanes. Purely additive: older peers skip unknown messages
    /// (a v1 viewer ignores SetCutter and cuts with the tool mesh; v1/v2 viewers ignore RecordedPlanes).</summary>
    public const int Version = 3;
    /// <summary>Oldest peer version this build still talks to.</summary>
    public const int MinCompatibleVersion = 1;
    const int MaxFrame = 512 * 1024 * 1024;

    // ---------------------------------------------------------------- write
    /// <summary>Serialises and writes one frame with a single Write call (so frames never interleave if callers lock).</summary>
    public static void Write(Stream s, ClayMessage m)
    {
        var ms = new MemoryStream(256);
        var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(0u);                      // length placeholder
        w.Write((byte)m.Type);
        switch (m)
        {
            case HelloMsg h: w.Write(h.Version); WriteString(w, h.Name); break;
            case SetStockMsg ss:
                for (var i = 0; i < 3; i++) w.Write(ss.Min[i]);
                for (var i = 0; i < 3; i++) w.Write(ss.Max[i]);
                w.Write(ss.ResolutionMm);
                w.Write(ss.Mesh != null);
                if (ss.Mesh != null) WriteMesh(w, ss.Mesh);
                break;
            case SetToolMsg st: WriteMesh(w, st.Mesh); break;
            case SetCutterMsg sc: WriteMesh(w, sc.Mesh); break;
            case PoseMsg p:
                for (var i = 0; i < 16; i++) w.Write(p.Matrix[i]);
                w.Write(p.Timestamp); w.Write(p.CuttingEnabled);
                if (p.TcpAxis is { Length: 3 }) for (var i = 0; i < 3; i++) w.Write(p.TcpAxis[i]);
                break;
            case RecordedPlanesMsg rp:
                w.Write(rp.Recording); w.Write(rp.CurrentTake); w.Write(rp.Count);
                for (var i = 0; i < rp.Count; i++)
                {
                    w.Write(rp.Take[i]);
                    for (var k = 0; k < 3; k++) w.Write(rp.Origin[i * 3 + k]);
                    for (var k = 0; k < 3; k++) w.Write(rp.Axis[i * 3 + k]);
                    w.Write(rp.Time[i]);
                }
                break;
            case CarvedMeshMsg cm: w.Write(cm.RemovedVolumeMm3); WriteMesh(w, cm.Mesh); break;
            case StatusMsg sm: w.Write(sm.RemovedVolumeMm3); WriteString(w, sm.Text); break;
            case ResetMsg or RequestMeshMsg or CloseMsg: break;
            default: throw new InvalidOperationException("Cannot serialise " + m.GetType().Name);
        }
        w.Flush();
        var buf = ms.GetBuffer();
        var len = (int)ms.Length;
        BitConverter.TryWriteBytes(new Span<byte>(buf, 0, 4), (uint)(len - 4));
        s.Write(buf, 0, len);
        s.Flush();
    }

    static void WriteString(BinaryWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s ?? "");
        w.Write(b.Length); w.Write(b);
    }

    static void WriteMesh(BinaryWriter w, MeshData m)
    {
        w.Write(m.Vertices.Length / 3);
        var vb = new byte[m.Vertices.Length * 4];
        Buffer.BlockCopy(m.Vertices, 0, vb, 0, vb.Length);
        w.Write(vb);
        w.Write(m.Indices.Length);
        var ib = new byte[m.Indices.Length * 4];
        Buffer.BlockCopy(m.Indices, 0, ib, 0, ib.Length);
        w.Write(ib);
    }

    // ----------------------------------------------------------------- read
    /// <summary>Reads one frame. Returns null on a clean end of stream between frames; throws on a truncated frame.</summary>
    public static ClayMessage? Read(Stream s)
    {
        var head = new byte[4];
        var n = ReadFully(s, head, 4);
        if (n == 0) return null;
        if (n < 4) throw new EndOfStreamException("Truncated frame header.");
        var len = BitConverter.ToUInt32(head, 0);
        if (len < 1 || len > MaxFrame) throw new InvalidDataException("Bad frame length " + len);
        var body = new byte[len];
        if (ReadFully(s, body, (int)len) < len) throw new EndOfStreamException("Truncated frame body.");

        var type = (MsgType)body[0];
        using var r = new BinaryReader(new MemoryStream(body, 1, body.Length - 1), Encoding.UTF8);
        switch (type)
        {
            case MsgType.Hello: return new HelloMsg { Version = r.ReadInt32(), Name = ReadString(r) };
            case MsgType.SetStock:
                var ss = new SetStockMsg();
                for (var i = 0; i < 3; i++) ss.Min[i] = r.ReadDouble();
                for (var i = 0; i < 3; i++) ss.Max[i] = r.ReadDouble();
                ss.ResolutionMm = r.ReadDouble();
                if (r.ReadBoolean()) ss.Mesh = ReadMesh(r);
                return ss;
            case MsgType.SetTool: return new SetToolMsg { Mesh = ReadMesh(r) };
            case MsgType.SetCutter: return new SetCutterMsg { Mesh = ReadMesh(r) };
            case MsgType.Pose:
                var p = new PoseMsg();
                for (var i = 0; i < 16; i++) p.Matrix[i] = r.ReadDouble();
                p.Timestamp = r.ReadDouble(); p.CuttingEnabled = r.ReadBoolean();
                if (r.BaseStream.Length - r.BaseStream.Position >= 24) p.TcpAxis = new[] { r.ReadDouble(), r.ReadDouble(), r.ReadDouble() };
                return p;
            case MsgType.RecordedPlanes:
                {
                    var rp = new RecordedPlanesMsg { Recording = r.ReadBoolean(), CurrentTake = r.ReadInt32() };
                    var cnt = r.ReadInt32();
                    if (cnt < 0 || cnt > (body.Length - 10) / 36) throw new InvalidDataException("Bad plane count " + cnt);
                    rp.Take = new int[cnt]; rp.Origin = new float[cnt * 3]; rp.Axis = new float[cnt * 3]; rp.Time = new double[cnt];
                    for (var i = 0; i < cnt; i++)
                    {
                        rp.Take[i] = r.ReadInt32();
                        for (var k = 0; k < 3; k++) rp.Origin[i * 3 + k] = r.ReadSingle();
                        for (var k = 0; k < 3; k++) rp.Axis[i * 3 + k] = r.ReadSingle();
                        rp.Time[i] = r.ReadDouble();
                    }
                    return rp;
                }
            case MsgType.Reset: return new ResetMsg();
            case MsgType.RequestMesh: return new RequestMeshMsg();
            case MsgType.CarvedMesh: { var vol = r.ReadDouble(); return new CarvedMeshMsg { RemovedVolumeMm3 = vol, Mesh = ReadMesh(r) }; }
            case MsgType.Status: { var vol = r.ReadDouble(); return new StatusMsg { RemovedVolumeMm3 = vol, Text = ReadString(r) }; }
            case MsgType.Close: return new CloseMsg();
            default: return new UnknownMsg { Raw = body[0] };
        }
    }

    static string ReadString(BinaryReader r)
    {
        var n = r.ReadInt32();
        return Encoding.UTF8.GetString(r.ReadBytes(n));
    }

    static MeshData ReadMesh(BinaryReader r)
    {
        var vc = r.ReadInt32();
        var v = new float[vc * 3];
        var vb = r.ReadBytes(v.Length * 4);
        Buffer.BlockCopy(vb, 0, v, 0, vb.Length);
        var ic = r.ReadInt32();
        var idx = new int[ic];
        var ib = r.ReadBytes(ic * 4);
        Buffer.BlockCopy(ib, 0, idx, 0, ib.Length);
        return new MeshData(v, idx);
    }

    static int ReadFully(Stream s, byte[] buf, int count)
    {
        var got = 0;
        while (got < count)
        {
            var n = s.Read(buf, got, count - got);
            if (n <= 0) break;
            got += n;
        }
        return got;
    }
}
