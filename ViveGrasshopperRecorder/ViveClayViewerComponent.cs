#pragma warning disable CA1416

using System.Drawing;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace ViveTcpRecorder;

/// <summary>
/// OBSOLETE: the Clay Viewer is now part of the Vive Take Recorder (Tool input, Open Visualiser / Reset Stock / Get Mesh buttons).
/// This class is only kept (hidden from the toolbar) so definitions saved with it still open and keep carving.
/// </summary>
public sealed class ViveClayViewerComponent : GH_Component, IClayButtons
{
    private static readonly Lazy<Bitmap> ComponentIcon = new(() => PlaneIcons.Load("ClayViewer.png"));
    private readonly ClayLink _clay;

    public ViveClayViewerComponent()
        : base("Vive Clay Viewer", "Clay",
            "OBSOLETE - the viewer is now built into the Vive Take Recorder (Tool input and Open Visualiser / Reset Stock / Get Mesh buttons). Replace this component with the Take Recorder.",
            "Vive", "Utilities")
    {
        _clay = new ClayLink(this);
    }

    public override Guid ComponentGuid => new("B2D6E3A4-5C17-4F89-A0E1-7D3C94B8F052");
    public override GH_Exposure Exposure => GH_Exposure.hidden;
    public override bool Obsolete => true;
    protected override Bitmap Icon => ComponentIcon.Value;

    public override void CreateAttributes() => m_attributes = new ClayViewerAttributes(this, this);

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddMeshParameter("Tool", "T", "Tool mesh already placed in world by the Vive Tracker component.", GH_ParamAccess.item);
        p.AddGenericParameter("Stock", "S", "Clay block: Box, Brep or Mesh.", GH_ParamAccess.item);
        for (var i = 0; i < p.ParamCount; i++) p[i].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddMeshParameter("Carved Mesh", "M", "Carved stock (after the Get Mesh button), in document units.", GH_ParamAccess.item);
        p.AddTextParameter("Analytics", "A", "Removed volume (take analytics moved to Vive Take Analytics).", GH_ParamAccess.item);
        p.AddTextParameter("Status", "St", "Connection and viewer state.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        if (da.Iteration > 0) return;
        Mesh? tool = null;
        IGH_Goo? stockGoo = null;
        da.GetData(0, ref tool);
        da.GetData(1, ref stockGoo);
        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Obsolete: use the Vive Take Recorder, which now includes the Clay Viewer.");

        var stock = stockGoo?.ScriptVariable();
        if (stock is Extrusion ex) stock = ex.ToBrep();
        _clay.Sync(stock, tool);

        Message = _clay.StateText();
        da.SetData(0, _clay.Carved);
        da.SetData(1, $"Removed volume: {_clay.RemovedMm3:0} mm3");
        da.SetData(2, Message);
    }

    void IClayButtons.OpenVisualiser() => _clay.OpenVisualiser();
    void IClayButtons.ResetStock() => _clay.ResetStock();
    void IClayButtons.GetMesh() => _clay.GetMesh();

    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);
        _clay.AppendResolutionMenu(menu);
    }

    public override bool Write(GH_IO.Serialization.GH_IWriter writer)
    {
        _clay.Write(writer);
        return base.Write(writer);
    }

    public override bool Read(GH_IO.Serialization.GH_IReader reader)
    {
        _clay.Read(reader);
        return base.Read(reader);
    }

    public override void RemovedFromDocument(GH_Document document)
    {
        _clay.Detach();
        base.RemovedFromDocument(document);
    }
}
