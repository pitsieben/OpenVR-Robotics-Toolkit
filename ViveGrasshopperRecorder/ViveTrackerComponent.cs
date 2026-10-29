#pragma warning disable CA1416 // Validate platform compatibility for System.Drawing.Bitmap

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace ViveTcpRecorder;

public sealed class ViveTrackerComponent : GH_Component
{
    private static readonly Lazy<Bitmap> ComponentIcon = new(LoadIcon);

    public bool Paused { get; set; }

    private Plane _oldPlane = Plane.WorldXY;
    private Transform _oldTransform = Transform.Identity;
    private GeometryBase? _cachedModel;

    public ViveTrackerComponent()
        : base(
            "Vive Tracker",
            "Tracker",
            "Extracts HTC Vive Tracker plane (Z UP), transform matrix, and 3D geometry from the Vive stream.",
            "Vive",
            "Tracking Device")
    {
    }

    public override Guid ComponentGuid => new("8D60C03A-A4D0-4F5B-9720-B570932B1A22");

    protected override Bitmap Icon => ComponentIcon.Value;

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddGenericParameter(
            "Vive", "V",
            "Passing Vive Runtime / TCP data to current component",
            GH_ParamAccess.item);

        pManager.AddIntegerParameter(
            "Index", "I",
            "If you have more than one tracker, indicate the index (0, 1, 2...).",
            GH_ParamAccess.item,
            0);
        pManager[1].Optional = true;

        pManager.AddGeometryParameter(
            "Mesh", "M",
            "Optional custom tool geometry referenced at (0,0,0) with tracking center point at origin. If provided, transforms and visualizes this tool mesh attached to the tracker.",
            GH_ParamAccess.item);
        pManager[2].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddGeometryParameter(
            "Tracker", "T",
            "HTC Vive Tracker 3D model representation or custom tool geometry",
            GH_ParamAccess.item);

        pManager.AddPlaneParameter(
            "Plane", "P",
            "The Tracker plane representation (Rhino Z facing UP out of top dome)",
            GH_ParamAccess.item);

        pManager.AddGenericParameter(
            "Matrix", "M",
            "Transformation matrix of Tracker (Transform)",
            GH_ParamAccess.item);

        pManager.AddGenericParameter(
            "TrackerData", "TD",
            "Composite tracker data payload (plane, trigger, and button states) for record components",
            GH_ParamAccess.item);

        pManager.AddBooleanParameter(
            "Trigger", "Trig",
            "Boolean trigger state from tracker pogo pins (True when active)",
            GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        object? rawVive = null;
        int index = 0;

        if (!DA.GetData(0, ref rawVive) || rawVive is null)
            return;

        DA.GetData(1, ref index);
        if (index < 0) index = 0;

        Plane currentPlane = Plane.Unset;
        Transform currentTransform = Transform.Identity;
        string serialOrName = $"Tracker{index}";
        bool trigger = false;
        double triggerVal = 0.0;
        bool grip = false;
        bool menu = false;
        bool trackpad = false;
        ulong buttonMask = 0;
        bool isTracked = true;
        double timestamp = 0.0;

        // Unwrap Goo if necessary
        if (rawVive is IGH_Goo goo)
            rawVive = goo.ScriptVariable();

        // 1. Check if input is our native ViveTrackData
        if (rawVive is ViveTrackData data)
        {
            var trackers = data.Trackers;
            if (trackers.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No Trackers detected in the Vive stream.");
                Message = "No Tracker";
                return;
            }

            if (index >= trackers.Count)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Tracker index {index} exceeds detected trackers ({trackers.Count}).");
                Message = $"Idx {index} > {trackers.Count - 1}";
                return;
            }

            var trk = trackers[index];
            currentPlane = trk.Plane;
            currentTransform = trk.CorrectedMatrix4X4;
            serialOrName = string.IsNullOrEmpty(trk.SerialNumber) ? $"Tracker{index}" : trk.SerialNumber;
            trigger = trk.Trigger;
            triggerVal = trk.TriggerValue;
            grip = trk.Grip;
            menu = trk.Menu;
            trackpad = trk.Trackpad;
            buttonMask = trk.ButtonMask;
            isTracked = trk.Tracked;
            timestamp = data.Timestamp;
        }
        // 2. Direct Plane input support (fallback/convenience)
        else if (rawVive is Plane p)
        {
            currentPlane = p;
            currentTransform = Transform.PlaneToPlane(Plane.WorldXY, p);
        }
        // 3. Fallback: Check if input is ViveTrack.OpenvrWrapper from original ViveTrack plugin via reflection
        else if (TryExtractFromViveTrackWrapper(rawVive, "Tracker", index, out var vtPlane, out var vtXform, out var vtName))
        {
            currentPlane = vtPlane;
            currentTransform = vtXform;
            serialOrName = vtName;
        }
        else
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Unsupported input type for 'Vive'. Expecting ViveTrackData or OpenvrWrapper.");
            return;
        }

        // Get optional custom tool mesh referenced at (0,0,0)
        GeometryBase? customGeom = null;
        object? rawMesh = null;
        if (DA.GetData(2, ref rawMesh) && rawMesh != null)
        {
            if (rawMesh is IGH_GeometricGoo geomGoo)
            {
                customGeom = GH_Convert.ToGeometryBase(geomGoo);
            }
            else if (rawMesh is IGH_Goo gooObj)
            {
                var unwrapped = gooObj.ScriptVariable();
                if (unwrapped is GeometryBase gb) customGeom = gb;
            }
            else if (rawMesh is GeometryBase gbDirect)
            {
                customGeom = gbDirect;
            }
        }

        Message = trigger ? $"{serialOrName} [TRIG]" : serialOrName;

        if (!Paused && currentPlane.IsValid)
        {
            _oldPlane = currentPlane;
            _oldTransform = currentTransform;
        }

        // Prepare 3D Geometry only if output 0 is wired to something (prevents Rhino lag)
        if (Params.Output[0].Recipients.Count > 0)
        {
            if (customGeom != null && customGeom.IsValid)
            {
                var toolInstance = customGeom.Duplicate();
                if (_oldTransform.IsValid)
                {
                    toolInstance.Transform(_oldTransform);
                }
                DA.SetData(0, toolInstance);
            }
            else
            {
                _cachedModel ??= ViveGeometryStore.GetTrackerGeometry();
                GeometryBase? geomInstance = _cachedModel?.Duplicate();
                if (geomInstance != null && _oldTransform.IsValid)
                {
                    geomInstance.Transform(_oldTransform);
                }
                DA.SetData(0, geomInstance);
            }
        }
        else
        {
            DA.SetData(0, _oldPlane.Origin);
        }

        DA.SetData(1, _oldPlane);
        DA.SetData(2, new GH_Transform(_oldTransform));

        var trackerData = new ViveTrackerData
        {
            Plane = _oldPlane,
            Transform = _oldTransform,
            Trigger = trigger,
            TriggerValue = triggerVal,
            Grip = grip,
            Menu = menu,
            Trackpad = trackpad,
            ButtonMask = buttonMask,
            SerialNumber = serialOrName,
            DeviceIndex = (uint)index,
            Timestamp = timestamp,
            Tracked = isTracked,
            CustomGeometry = customGeom
        };

        DA.SetData(3, new GH_ViveTrackerData(trackerData));
        DA.SetData(4, trigger);
    }

    private static bool TryExtractFromViveTrackWrapper(
        object wrapper,
        string deviceClass,
        int index,
        out Plane plane,
        out Transform transform,
        out string name)
    {
        plane = Plane.Unset;
        transform = Transform.Identity;
        name = $"{deviceClass}{index}";

        try
        {
            var trackedDevicesProp = wrapper.GetType().GetProperty("TrackedDevices");
            if (trackedDevicesProp is null) return false;
            var trackedDevices = trackedDevicesProp.GetValue(wrapper);
            if (trackedDevices is null) return false;

            var indexesByClassesProp = trackedDevices.GetType().GetProperty("IndexesByClasses");
            if (indexesByClassesProp is null) return false;
            var dict = indexesByClassesProp.GetValue(trackedDevices) as IDictionary;
            if (dict is null || !dict.Contains(deviceClass)) return false;

            if (dict[deviceClass] is not IList list || index >= list.Count) return false;
            int key = Convert.ToInt32(list[index]);

            var allDevicesProp = trackedDevices.GetType().GetProperty("AllDevices");
            if (allDevicesProp is null) return false;
            var allDevs = allDevicesProp.GetValue(trackedDevices) as IDictionary;
            if (allDevs is null || !allDevs.Contains(key)) return false;

            var dev = allDevs[key];
            if (dev is null) return false;

            // Trigger ConvertPose() and GetTrackerCorrectedMatrix4X4() if available
            dev.GetType().GetMethod("ConvertPose")?.Invoke(dev, null);
            dev.GetType().GetMethod("GetTrackerCorrectedMatrix4X4")?.Invoke(dev, null);

            var xformProp = dev.GetType().GetProperty("CorrectedMatrix4X4")
                            ?? dev.GetType().GetField("CorrectedMatrix4X4") as MemberInfo;

            if (xformProp is PropertyInfo pi && pi.GetValue(dev) is Transform t)
                transform = t;
            else if (xformProp is FieldInfo fi && fi.GetValue(dev) is Transform tf)
                transform = tf;

            plane = Plane.WorldXY;
            plane.Transform(transform);
            name = $"{deviceClass}{index}";
            return true;
        }
        catch
        {
            return false;
        }
    }

    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);
        Menu_AppendItem(menu, "Pause", Menu_Click_Pause, true, Paused)
            .ToolTipText = "Pause updates for this tracker.";
    }

    private void Menu_Click_Pause(object? sender, EventArgs e)
    {
        RecordUndoEvent("Pause");
        Paused = !Paused;
        ExpireSolution(true);
    }

    public override bool Write(GH_IWriter writer)
    {
        writer.SetBoolean("Pause", Paused);
        return base.Write(writer);
    }

    public override bool Read(GH_IReader reader)
    {
        if (reader.ItemExists("Pause"))
            Paused = reader.GetBoolean("Pause");
        return base.Read(reader);
    }

    private static Bitmap LoadIcon()
    {
        var stream = typeof(ViveTrackerComponent).Assembly.GetManifestResourceStream("ViveTcpRecorder.Resources.ObjTracker.png");
        if (stream is null) return new Bitmap(24, 24);
        using (stream)
        {
            using var bmp = new Bitmap(stream);
            return new Bitmap(bmp, new Size(24, 24));
        }
    }
}
