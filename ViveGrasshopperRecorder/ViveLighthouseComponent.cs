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

/// <summary>
/// Single unified Lighthouse component. Automatically increments its index
/// based on existing lighthouse components on the canvas, or accepts an explicit Index input.
/// </summary>
public sealed class ViveLighthouseComponent : GH_Component
{
    private static readonly Bitmap[] LighthouseIcons = new[]
    {
        LoadIcon("ObjLighthouse1.png"),
        LoadIcon("ObjLighthouse2.png"),
        LoadIcon("ObjLighthouse3.png"),
        LoadIcon("ObjLighthouse4.png")
    };

    public bool Paused { get; set; }
    public int AssignedIndex { get; set; }
    private bool _userSetIndex;

    private Plane _oldPlane = Plane.WorldXY;
    private Transform _oldTransform = Transform.Identity;
    private GeometryBase? _cachedModel;

    public ViveLighthouseComponent()
        : base(
            "Vive Lighthouse",
            "Lighthouse",
            "Extracts HTC Vive Lighthouse base station plane, transform matrix, and 3D geometry.",
            "Vive",
            "Tracking Device")
    {
        AssignedIndex = 0;
        UpdateNickNameAndMessage();
    }

    public override Guid ComponentGuid => new("A215CF68-8E69-42E3-BFF5-6272C730A581");

    protected override Bitmap Icon
    {
        get
        {
            int idx = AssignedIndex;
            if (idx >= 0 && idx < LighthouseIcons.Length)
                return LighthouseIcons[idx];
            return LighthouseIcons[0];
        }
    }

    public override void AddedToDocument(GH_Document document)
    {
        base.AddedToDocument(document);

        if (!_userSetIndex)
        {
            AssignedIndex = DetermineNextLighthouseIndex(document);
            UpdateNickNameAndMessage();
        }
    }

    private int DetermineNextLighthouseIndex(GH_Document document)
    {
        var usedIndices = new HashSet<int>();
        foreach (var obj in document.Objects)
        {
            if (obj is ViveLighthouseComponent other && other != this)
            {
                usedIndices.Add(other.AssignedIndex);
            }
        }

        int next = 0;
        while (usedIndices.Contains(next))
        {
            next++;
        }
        return next;
    }

    private void UpdateNickNameAndMessage()
    {
        int displayNum = AssignedIndex + 1;
        NickName = $"Lighthouse{displayNum}";
        Message = $"Lighthouse{displayNum}";
    }

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddGenericParameter(
            "Vive", "V",
            "Passing Vive Runtime / TCP data to current component",
            GH_ParamAccess.item);

        pManager.AddIntegerParameter(
            "Index", "I",
            "Lighthouse index (0 for Lighthouse 1, 1 for Lighthouse 2, 2 for Lighthouse 3, 3 for Lighthouse 4). If unconnected, automatically uses the assigned index.",
            GH_ParamAccess.item,
            0);
        pManager[1].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddGeometryParameter(
            "Lighthouse", "L",
            "HTC Vive Lighthouse 3D model representation",
            GH_ParamAccess.item);

        pManager.AddPlaneParameter(
            "Plane", "P",
            "The Lighthouse plane representation (Y forward in optical beam emission direction, Z up)",
            GH_ParamAccess.item);

        pManager.AddGenericParameter(
            "Matrix", "M",
            "Transformation matrix of Lighthouse (Transform)",
            GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        object? rawVive = null;
        if (!DA.GetData(0, ref rawVive) || rawVive is null)
            return;

        int effectiveIndex = AssignedIndex;
        // Check if user connected an explicit input to Index
        if (Params.Input[1].SourceCount > 0)
        {
            int wiredIndex = 0;
            if (DA.GetData(1, ref wiredIndex))
            {
                effectiveIndex = wiredIndex < 0 ? 0 : wiredIndex;
            }
        }

        SolveLighthouse(
            DA,
            rawVive,
            effectiveIndex,
            Paused,
            ref _oldPlane,
            ref _oldTransform,
            ref _cachedModel);
    }

    private void SolveLighthouse(
        IGH_DataAccess DA,
        object rawVive,
        int index,
        bool paused,
        ref Plane oldPlane,
        ref Transform oldTransform,
        ref GeometryBase? cachedModel)
    {
        Plane currentPlane = Plane.Unset;
        Transform currentTransform = Transform.Identity;
        string serialOrName = $"Lighthouse{index + 1}";

        if (rawVive is IGH_Goo goo)
            rawVive = goo.ScriptVariable();

        // 1. Native ViveTrackData
        if (rawVive is ViveTrackData data)
        {
            var lighthouses = data.Lighthouses;
            if (lighthouses.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No Lighthouses detected in the Vive stream.");
                Message = "No BaseStation";
                return;
            }

            if (index >= lighthouses.Count)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Lighthouse index {index} exceeds detected lighthouses ({lighthouses.Count}).");
                Message = $"Idx {index} > {lighthouses.Count - 1}";
                return;
            }

            var lh = lighthouses[index];
            currentPlane = lh.Plane;
            currentTransform = lh.CorrectedMatrix4X4;
            serialOrName = string.IsNullOrEmpty(lh.SerialNumber) ? $"Lighthouse{index + 1}" : lh.SerialNumber;
        }
        // 2. Direct Plane support
        else if (rawVive is Plane p)
        {
            currentPlane = p;
            currentTransform = Transform.PlaneToPlane(Plane.WorldXY, p);
        }
        // 3. Fallback: ViveTrack.OpenvrWrapper reflection
        else if (TryExtractFromViveTrackWrapper(rawVive, index, out var vtPlane, out var vtXform, out var vtName))
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

        Message = serialOrName;

        if (!paused && currentPlane.IsValid)
        {
            oldPlane = currentPlane;
            oldTransform = currentTransform;
        }

        // Prepare 3D Geometry only if output 0 is wired to something (prevents Rhino lag)
        if (Params.Output[0].Recipients.Count > 0)
        {
            cachedModel ??= ViveGeometryStore.GetLighthouseGeometry();
            GeometryBase? geomInstance = cachedModel?.Duplicate();
            if (geomInstance != null && oldTransform.IsValid)
            {
                geomInstance.Transform(oldTransform);
            }
            DA.SetData(0, geomInstance);
        }
        else
        {
            DA.SetData(0, oldPlane.Origin);
        }

        DA.SetData(1, oldPlane);
        DA.SetData(2, new GH_Transform(oldTransform));
    }

    private static bool TryExtractFromViveTrackWrapper(
        object wrapper,
        int index,
        out Plane plane,
        out Transform transform,
        out string name)
    {
        plane = Plane.Unset;
        transform = Transform.Identity;
        name = $"Lighthouse{index + 1}";

        try
        {
            var trackedDevicesProp = wrapper.GetType().GetProperty("TrackedDevices");
            if (trackedDevicesProp is null) return false;
            var trackedDevices = trackedDevicesProp.GetValue(wrapper);
            if (trackedDevices is null) return false;

            var indexesByClassesProp = trackedDevices.GetType().GetProperty("IndexesByClasses");
            if (indexesByClassesProp is null) return false;
            var dict = indexesByClassesProp.GetValue(trackedDevices) as IDictionary;
            if (dict is null || !dict.Contains("Lighthouse")) return false;

            if (dict["Lighthouse"] is not IList list || index >= list.Count) return false;
            int key = Convert.ToInt32(list[index]);

            var allDevicesProp = trackedDevices.GetType().GetProperty("AllDevices");
            if (allDevicesProp is null) return false;
            var allDevs = allDevicesProp.GetValue(trackedDevices) as IDictionary;
            if (allDevs is null || !allDevs.Contains(key)) return false;

            var dev = allDevs[key];
            if (dev is null) return false;

            dev.GetType().GetMethod("ConvertPose")?.Invoke(dev, null);

            var xformProp = dev.GetType().GetProperty("CorrectedMatrix4X4")
                            ?? dev.GetType().GetField("CorrectedMatrix4X4") as MemberInfo;

            if (xformProp is PropertyInfo pi && pi.GetValue(dev) is Transform t)
                transform = t;
            else if (xformProp is FieldInfo fi && fi.GetValue(dev) is Transform tf)
                transform = tf;

            plane = Plane.WorldXY;
            plane.Transform(transform);
            name = $"Lighthouse{index + 1}";
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
            .ToolTipText = "Pause updates for this lighthouse.";

        Menu_AppendSeparator(menu);

        var indexMenu = (ToolStripMenuItem)Menu_AppendItem(menu, "Select Lighthouse Device");
        for (int i = 0; i < 4; i++)
        {
            int targetIndex = i;
            bool isCurrent = (AssignedIndex == targetIndex);
            Menu_AppendItem(
                indexMenu.DropDown,
                $"Lighthouse {i + 1} (Index {i})",
                (s, e) =>
                {
                    RecordUndoEvent("Select Lighthouse");
                    AssignedIndex = targetIndex;
                    _userSetIndex = true;
                    UpdateNickNameAndMessage();
                    ExpireSolution(true);
                },
                true,
                isCurrent);
        }
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
        writer.SetInt32("AssignedIndex", AssignedIndex);
        writer.SetBoolean("UserSetIndex", _userSetIndex);
        return base.Write(writer);
    }

    public override bool Read(GH_IReader reader)
    {
        if (reader.ItemExists("Pause"))
            Paused = reader.GetBoolean("Pause");
        if (reader.ItemExists("AssignedIndex"))
        {
            AssignedIndex = reader.GetInt32("AssignedIndex");
            _userSetIndex = true;
            UpdateNickNameAndMessage();
        }
        if (reader.ItemExists("UserSetIndex"))
            _userSetIndex = reader.GetBoolean("UserSetIndex");

        return base.Read(reader);
    }

    private static Bitmap LoadIcon(string name)
    {
        var stream = typeof(ViveLighthouseComponent).Assembly.GetManifestResourceStream($"ViveTcpRecorder.Resources.{name}");
        if (stream is null) return new Bitmap(24, 24);
        using (stream)
        {
            using var bmp = new Bitmap(stream);
            return new Bitmap(bmp, new Size(24, 24));
        }
    }
}
