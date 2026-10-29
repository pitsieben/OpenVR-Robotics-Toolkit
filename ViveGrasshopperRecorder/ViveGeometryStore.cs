using System;
using System.Globalization;
using System.IO;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace ViveTcpRecorder;

/// <summary>
/// Provides cached 3D geometry representations for HTC Vive Trackers and Lighthouses.
/// Uses the official Vive Tracker 3.0 OBJ mesh (scaled to millimetres and aligned with Rhino Z-UP).
/// </summary>
public static class ViveGeometryStore
{
    private static readonly Lazy<GeometryBase?> TrackerModel = new(LoadTrackerModel);
    private static readonly Lazy<GeometryBase?> LighthouseModel = new(LoadLighthouseModel);

    public static GeometryBase? GetTrackerGeometry()
    {
        var geom = TrackerModel.Value;
        return geom?.Duplicate();
    }

    public static GeometryBase? GetLighthouseGeometry()
    {
        var geom = LighthouseModel.Value;
        return geom?.Duplicate();
    }

    private static GeometryBase? LoadTrackerModel()
    {
        try
        {
            using var stream = typeof(ViveGeometryStore).Assembly.GetManifestResourceStream("ViveTcpRecorder.Resources.vr_tracker_vive_3_0.obj");
            if (stream != null)
            {
                var mesh = ParseObjStream(stream, scale: 1000.0, flipZ: true);
                if (mesh != null && mesh.IsValid)
                    return mesh;
            }
        }
        catch
        {
            // Fallback to procedural puck
        }

        return CreateFallbackTrackerMesh();
    }

    private static GeometryBase? LoadLighthouseModel()
    {
        try
        {
            using var stream = typeof(ViveGeometryStore).Assembly.GetManifestResourceStream("ViveTcpRecorder.Resources.lighthouse.b64");
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                string b64 = reader.ReadToEnd().Trim();
                if (!string.IsNullOrEmpty(b64))
                {
                    byte[] bytes = Convert.FromBase64String(b64);
                    var obj = GH_Convert.ByteArrayToCommonObject<GeometryBase>(bytes);
                    if (obj != null && obj.IsValid)
                        return obj;
                }
            }
        }
        catch
        {
            // Fallback to procedural box
        }

        return CreateFallbackLighthouseMesh();
    }

    private static Mesh? ParseObjStream(Stream stream, double scale, bool flipZ)
    {
        var mesh = new Mesh();
        using var reader = new StreamReader(stream);
        var culture = CultureInfo.InvariantCulture;

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var span = line.AsSpan().TrimStart();
            if (span.StartsWith("v "))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4 &&
                    double.TryParse(parts[1], NumberStyles.Float, culture, out var x) &&
                    double.TryParse(parts[2], NumberStyles.Float, culture, out var y) &&
                    double.TryParse(parts[3], NumberStyles.Float, culture, out var z))
                {
                    mesh.Vertices.Add(x * scale, y * scale, (flipZ ? -z : z) * scale);
                }
            }
            else if (span.StartsWith("f "))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 4) // Triangle
                {
                    if (TryParseFaceIndex(parts[1], out var i0) &&
                        TryParseFaceIndex(parts[2], out var i1) &&
                        TryParseFaceIndex(parts[3], out var i2))
                    {
                        if (flipZ)
                            mesh.Faces.AddFace(i0, i2, i1);
                        else
                            mesh.Faces.AddFace(i0, i1, i2);
                    }
                }
                else if (parts.Length == 5) // Quad
                {
                    if (TryParseFaceIndex(parts[1], out var i0) &&
                        TryParseFaceIndex(parts[2], out var i1) &&
                        TryParseFaceIndex(parts[3], out var i2) &&
                        TryParseFaceIndex(parts[4], out var i3))
                    {
                        if (flipZ)
                            mesh.Faces.AddFace(i0, i3, i2, i1);
                        else
                            mesh.Faces.AddFace(i0, i1, i2, i3);
                    }
                }
            }
        }

        mesh.Normals.ComputeNormals();
        mesh.Compact();
        return mesh;
    }

    private static bool TryParseFaceIndex(string token, out int index)
    {
        index = 0;
        int slash = token.IndexOf('/');
        var s = slash >= 0 ? token[..slash] : token;
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawIndex))
        {
            index = rawIndex - 1; // OBJ is 1-based
            return index >= 0;
        }
        return false;
    }

    private static Mesh CreateFallbackTrackerMesh()
    {
        var cylinder = new Cylinder(new Circle(Plane.WorldXY, 45.0), 40.0);
        var mesh = Mesh.CreateFromCylinder(cylinder, 2, 24);
        return mesh ?? new Mesh();
    }

    private static Mesh CreateFallbackLighthouseMesh()
    {
        var box = new Box(Plane.WorldXY, new Interval(-37.5, 37.5), new Interval(-37.5, 37.5), new Interval(-37.5, 37.5));
        var mesh = Mesh.CreateFromBox(box, 1, 1, 1);
        return mesh ?? new Mesh();
    }
}
