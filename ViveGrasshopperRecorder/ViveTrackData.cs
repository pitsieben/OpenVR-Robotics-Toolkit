using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace ViveTcpRecorder;

/// <summary>
/// Represents a single tracked OpenVR device (Tracker, Lighthouse, Controller, or HMD)
/// with poses converted directly into Rhino's world coordinate space.
/// </summary>
public sealed class ViveDevicePose
{
    public uint DeviceIndex { get; set; }
    public string SerialNumber { get; set; } = string.Empty;
    public string ModelNumber { get; set; } = string.Empty;
    public string DeviceClass { get; set; } = "Tracker"; // "Tracker", "Lighthouse", "Controller", "HMD"
    public bool Connected { get; set; }
    public bool Tracked { get; set; }
    public string TrackingResult { get; set; } = "Running_OK";

    public double[] MatrixOpenVr { get; set; } = new double[12];
    public Point3d Origin { get; set; }
    public Plane Plane { get; set; } = Plane.WorldXY;
    public Transform CorrectedMatrix4X4 { get; set; } = Transform.Identity;
    public GeometryBase? Geometry { get; set; }

    // Button & Trigger states (from OpenVR / physical pogo pins)
    public bool Trigger { get; set; }
    public double TriggerValue { get; set; }
    public bool Grip { get; set; }
    public bool Menu { get; set; }
    public bool Trackpad { get; set; }
    public ulong ButtonMask { get; set; }

    public override string ToString() =>
        $"{DeviceClass} [{SerialNumber}] Index={DeviceIndex} Tracked={Tracked} Pos={Origin:F1}";
}

/// <summary>
/// Complete Vive tracking context holding all detected devices.
/// Structured to be fully compatible with both the TCP stream and ViveTrack workflows.
/// </summary>
public sealed class ViveTrackData
{
    public double Timestamp { get; set; }
    public long Sequence { get; set; }
    public string Status { get; set; } = "Active";

    public List<ViveDevicePose> Devices { get; } = new();

    // Lookups by device class
    public List<ViveDevicePose> Trackers =>
        Devices.Where(d => string.Equals(d.DeviceClass, "Tracker", StringComparison.OrdinalIgnoreCase)).ToList();

    public List<ViveDevicePose> Lighthouses =>
        Devices.Where(d => string.Equals(d.DeviceClass, "Lighthouse", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(d.DeviceClass, "TrackingReference", StringComparison.OrdinalIgnoreCase)).ToList();

    public List<ViveDevicePose> Controllers =>
        Devices.Where(d => string.Equals(d.DeviceClass, "Controller", StringComparison.OrdinalIgnoreCase)).ToList();

    public List<ViveDevicePose> HMDs =>
        Devices.Where(d => string.Equals(d.DeviceClass, "HMD", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// Provides ViveTrack-compatible indexing for downstream interoperability.
    /// </summary>
    public TrackedDevicesAdapter TrackedDevices { get; }

    public ViveTrackData()
    {
        TrackedDevices = new TrackedDevicesAdapter(this);
    }

    public string Summary()
    {
        int trk = Trackers.Count;
        int lh = Lighthouses.Count;
        int ctrl = Controllers.Count;
        int hmd = HMDs.Count;

        var parts = new List<string>();
        if (trk > 0) parts.Add($"{trk} Tracker{(trk > 1 ? "s" : "")}");
        if (lh > 0) parts.Add($"{lh} Lighthouse{(lh > 1 ? "s" : "")}");
        if (ctrl > 0) parts.Add($"{ctrl} Controller{(ctrl > 1 ? "s" : "")}");
        if (hmd > 0) parts.Add($"{hmd} HMD");

        if (parts.Count == 0)
            return $"Vive: No devices detected ({Status})";

        return $"Vive: {string.Join(", ", parts)} ({Status})";
    }

    public override string ToString() => Summary();

    public sealed class TrackedDevicesAdapter
    {
        private readonly ViveTrackData _parent;

        public TrackedDevicesAdapter(ViveTrackData parent)
        {
            _parent = parent;
        }

        public Dictionary<string, List<int>> IndexesByClasses
        {
            get
            {
                var dict = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Tracker"] = new List<int>(),
                    ["Lighthouse"] = new List<int>(),
                    ["Controller"] = new List<int>(),
                    ["HMD"] = new List<int>()
                };

                for (int i = 0; i < _parent.Devices.Count; i++)
                {
                    var dev = _parent.Devices[i];
                    string cls = dev.DeviceClass;
                    if (cls.Equals("TrackingReference", StringComparison.OrdinalIgnoreCase))
                        cls = "Lighthouse";

                    if (!dict.ContainsKey(cls))
                        dict[cls] = new List<int>();

                    dict[cls].Add((int)dev.DeviceIndex);
                }

                return dict;
            }
        }

        public Dictionary<int, ViveDevicePose> AllDevices
        {
            get
            {
                var dict = new Dictionary<int, ViveDevicePose>();
                foreach (var dev in _parent.Devices)
                {
                    dict[(int)dev.DeviceIndex] = dev;
                }
                return dict;
            }
        }
    }
}
