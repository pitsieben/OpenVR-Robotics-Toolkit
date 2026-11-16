using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;
using System.Drawing;

namespace ViveTcpRecorder;

/// <summary>
/// Clay Viewer layout in the CamCore style: full-width buttons under the component body, then GH's dark status bubble
/// (the component's Message, one or two lines) below them. Mouse buttons are not inspected (any click on a button fires it).
/// </summary>
internal sealed class ClayViewerAttributes : GH_ComponentAttributes
{
    private const float ButtonHeight = 22, Gap = 4, Inset = 4;
    private static readonly string[] Labels = { "Open Visualiser", "Reset Stock", "Get Mesh" };
    private readonly RectangleF[] _buttons = new RectangleF[3];
    private int _pressed = -1;

    public ClayViewerAttributes(ViveClayViewerComponent owner) : base(owner) { }

    protected override void Layout()
    {
        base.Layout();
        var body = GH_Convert.ToRectangle(Bounds);
        var y = body.Bottom + Gap;
        for (var i = 0; i < _buttons.Length; i++)
        {
            var h = i == 0 ? ButtonHeight : ButtonHeight - 4;
            _buttons[i] = new RectangleF(body.X + Inset, y, body.Width - 2 * Inset, h);
            y += h + Gap;
        }
        Bounds = RectangleF.Union(Bounds, new RectangleF(body.X, body.Y, body.Width, _buttons[^1].Bottom - body.Y + Inset));
    }

    protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
    {
        base.Render(canvas, graphics, channel);
        if (channel != GH_CanvasChannel.Objects) return;
        for (var i = 0; i < _buttons.Length; i++)
        {
            using var capsule = GH_Capsule.CreateTextCapsule(_buttons[i], _buttons[i], _pressed == i ? GH_Palette.Grey : GH_Palette.Black, Labels[i], 2, 0);
            capsule.Render(graphics, Selected, Owner.Locked, false);
        }
    }

    public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
    {
        for (var i = 0; i < _buttons.Length; i++)
            if (_buttons[i].Contains(e.CanvasLocation))
            {
                _pressed = i;
                global::Grasshopper.Instances.InvalidateCanvas();
                return GH_ObjectResponse.Capture;
            }
        return base.RespondToMouseDown(sender, e);
    }

    public override GH_ObjectResponse RespondToMouseUp(GH_Canvas sender, GH_CanvasMouseEvent e)
    {
        if (_pressed < 0) return base.RespondToMouseUp(sender, e);
        var i = _pressed;
        _pressed = -1;
        global::Grasshopper.Instances.InvalidateCanvas();
        if (_buttons[i].Contains(e.CanvasLocation))
        {
            var owner = (ViveClayViewerComponent)Owner;
            if (i == 0) owner.OpenVisualiser();
            else if (i == 1) owner.ResetStock();
            else owner.GetMesh();
        }
        return GH_ObjectResponse.Release;
    }
}
