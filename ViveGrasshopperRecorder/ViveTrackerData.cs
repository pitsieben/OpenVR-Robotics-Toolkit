using System;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace ViveTcpRecorder;

/// <summary>
/// Composite data payload representing a tracked HTC Vive Tracker pose and its physical button/trigger state.
/// Can be fed directly into recorder components (Vive Take Recorder, Vive Calibration Recorder)
/// or unpacked into Grasshopper geometry and boolean components.
/// </summary>
public sealed class ViveTrackerData
{
    public Plane Plane { get; set; } = Plane.WorldXY;
    public Point3d Origin => Plane.Origin;
    public Transform Transform { get; set; } = Transform.Identity;

    // Trigger & Button states (from physical pogo pins or controller input)
    public bool Trigger { get; set; }
    public double TriggerValue { get; set; }
    public bool Grip { get; set; }
    public bool Menu { get; set; }
    public bool Trackpad { get; set; }
    public ulong ButtonMask { get; set; }

    public string SerialNumber { get; set; } = string.Empty;
    public uint DeviceIndex { get; set; }
    public double Timestamp { get; set; }
    public bool Tracked { get; set; }
    public GeometryBase? CustomGeometry { get; set; }

    public ViveTrackerData()
    {
    }

    public ViveTrackerData(ViveDevicePose pose, double timestamp = 0, GeometryBase? customGeometry = null)
    {
        if (pose == null) return;
        Plane = pose.Plane;
        Transform = pose.CorrectedMatrix4X4;
        Trigger = pose.Trigger;
        TriggerValue = pose.TriggerValue;
        Grip = pose.Grip;
        Menu = pose.Menu;
        Trackpad = pose.Trackpad;
        ButtonMask = pose.ButtonMask;
        SerialNumber = pose.SerialNumber;
        DeviceIndex = pose.DeviceIndex;
        Tracked = pose.Tracked;
        Timestamp = timestamp;
        CustomGeometry = customGeometry;
    }

    public ViveTrackerData Duplicate()
    {
        return new ViveTrackerData
        {
            Plane = Plane,
            Transform = Transform,
            Trigger = Trigger,
            TriggerValue = TriggerValue,
            Grip = Grip,
            Menu = Menu,
            Trackpad = Trackpad,
            ButtonMask = ButtonMask,
            SerialNumber = SerialNumber,
            DeviceIndex = DeviceIndex,
            Timestamp = Timestamp,
            Tracked = Tracked,
            CustomGeometry = CustomGeometry?.Duplicate()
        };
    }

    public override string ToString() =>
        $"TrackerData [{SerialNumber}] Trigger={(Trigger ? "ON" : "OFF")} ({TriggerValue:F2}) Pos=({Origin.X:F1}, {Origin.Y:F1}, {Origin.Z:F1})";
}

/// <summary>
/// Grasshopper Goo wrapper for ViveTrackerData, enabling native conversions to Plane, Point, Transform, Boolean, Number, and Geometry.
/// </summary>
public class GH_ViveTrackerData : GH_Goo<ViveTrackerData>
{
    public GH_ViveTrackerData() { }
    public GH_ViveTrackerData(ViveTrackerData data) : base(data) { }

    public override bool IsValid => Value != null && Value.Plane.IsValid;
    public override string TypeName => "ViveTrackerData";
    public override string TypeDescription => "Vive Tracker pose, plane, and trigger data";

    public override IGH_Goo Duplicate() => new GH_ViveTrackerData(Value?.Duplicate() ?? new ViveTrackerData());

    public override bool CastTo<Q>(ref Q target)
    {
        if (Value == null) return false;

        if (typeof(Q).IsAssignableFrom(typeof(Plane)))
        {
            target = (Q)(object)Value.Plane;
            return true;
        }
        if (typeof(Q).IsAssignableFrom(typeof(GH_Plane)))
        {
            target = (Q)(object)new GH_Plane(Value.Plane);
            return true;
        }
        if (typeof(Q).IsAssignableFrom(typeof(Point3d)))
        {
            target = (Q)(object)Value.Origin;
            return true;
        }
        if (typeof(Q).IsAssignableFrom(typeof(GH_Point)))
        {
            target = (Q)(object)new GH_Point(Value.Origin);
            return true;
        }
        if (typeof(Q).IsAssignableFrom(typeof(Transform)))
        {
            target = (Q)(object)Value.Transform;
            return true;
        }
        if (typeof(Q).IsAssignableFrom(typeof(GH_Transform)))
        {
            target = (Q)(object)new GH_Transform(Value.Transform);
            return true;
        }
        if (typeof(Q).IsAssignableFrom(typeof(bool)))
        {
            target = (Q)(object)Value.Trigger;
            return true;
        }
        if (typeof(Q).IsAssignableFrom(typeof(GH_Boolean)))
        {
            target = (Q)(object)new GH_Boolean(Value.Trigger);
            return true;
        }
        if (typeof(Q).IsAssignableFrom(typeof(double)))
        {
            target = (Q)(object)Value.TriggerValue;
            return true;
        }
        if (typeof(Q).IsAssignableFrom(typeof(GH_Number)))
        {
            target = (Q)(object)new GH_Number(Value.TriggerValue);
            return true;
        }
        if (typeof(Q).IsAssignableFrom(typeof(GeometryBase)) && Value.CustomGeometry != null)
        {
            target = (Q)(object)Value.CustomGeometry;
            return true;
        }

        return false;
    }

    public override bool CastFrom(object source)
    {
        if (source is ViveTrackerData td)
        {
            Value = td;
            return true;
        }
        if (source is GH_ViveTrackerData gtd)
        {
            Value = gtd.Value;
            return true;
        }
        if (source is Plane pl)
        {
            Value = new ViveTrackerData { Plane = pl, Transform = Transform.PlaneToPlane(Plane.WorldXY, pl) };
            return true;
        }
        return false;
    }

    public override string ToString() => Value?.ToString() ?? "Empty ViveTrackerData";
}
