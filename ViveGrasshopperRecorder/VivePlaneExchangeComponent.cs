#pragma warning disable CA1416

using System.Drawing;
using Grasshopper.Kernel;
using Rhino;
using Rhino.Geometry;

namespace ViveTcpRecorder;

/// <summary>
/// Pushes recorded planes to, and pulls a partner site's planes from, a GitHub data branch.
/// Network calls run in the background so the canvas never freezes.
/// </summary>
public sealed class VivePlaneExchangeComponent : GH_Component
{
    private static readonly Lazy<Bitmap> ComponentIcon = new(LoadIcon);

    private readonly object _gate = new();
    private bool _wasPush, _wasPull, _wasList;
    private bool _busy;
    private string _status = "Idle. Click Push to upload, Pull to download.";
    private List<Plane> _pulled = new();
    private string _pulledInfo = "";
    private List<string> _available = new();

    public VivePlaneExchangeComponent()
        : base("Vive Plane Exchange", "PlaneX",
            "Push recorded planes to a GitHub data branch and pull the partner site's planes, so a calibration recorded at one site can be used at the other.",
            "Vive", "TCP")
    {
    }

    public override Guid ComponentGuid => new("7F3B6C21-9D4E-4A58-B1C7-2E8F05A3D964");
    protected override Bitmap Icon => ComponentIcon.Value;

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddPlaneParameter("Planes", "P", "Planes to push (e.g. from Vive Calibration Recorder).", GH_ParamAccess.list);
        p.AddBooleanParameter("Push", "Push", "Button: upload Planes as <Site>/<Name>.json (and <Site>/latest.json).", GH_ParamAccess.item, false);
        p.AddBooleanParameter("Pull", "Pull", "Button: download <Remote Site>/<Pull Name>.json.", GH_ParamAccess.item, false);
        p.AddTextParameter("Site", "S", "This site's folder name, e.g. france.", GH_ParamAccess.item, "france");
        p.AddTextParameter("Remote Site", "RS", "Partner site's folder to pull from, e.g. austria.", GH_ParamAccess.item, "austria");
        p.AddTextParameter("Name", "N", "Name of the plane set to push. Empty = timestamp.", GH_ParamAccess.item, "");
        p.AddTextParameter("Pull Name", "PN", "Plane set to pull. Empty = latest.", GH_ParamAccess.item, "latest");
        p.AddTextParameter("Repo", "R", "GitHub repo to push to, as owner/name.", GH_ParamAccess.item, "pitsieben/OpenVR-Robotics-Toolkit");
        p.AddTextParameter("Pull Repo", "PR", "Repo to pull from. Empty = same as Repo. Use when each site has its own data repo.", GH_ParamAccess.item, "");
        p.AddTextParameter("Branch", "B", "Data branch name.", GH_ParamAccess.item, "plane-data");
        p.AddTextParameter("Token", "T",
            "Leave EMPTY (recommended): the token is read encrypted from the install folder (run set_exchange_token.ps1 once). A token typed here is saved inside the .gh file.",
            GH_ParamAccess.item, "");
        p.AddBooleanParameter("List", "L", "Button: list plane sets available at Remote Site.", GH_ParamAccess.item, false);
        p.AddTextParameter("Note", "Nt", "Optional note stored with the pushed set.", GH_ParamAccess.item, "");
        for (var i = 0; i < p.ParamCount; i++) p[i].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddPlaneParameter("Pulled Planes", "P", "Planes downloaded from the remote site, scaled to this document's units.", GH_ParamAccess.list);
        p.AddTextParameter("Info", "I", "Site, name, time and units of the pulled set.", GH_ParamAccess.item);
        p.AddTextParameter("Available", "A", "Plane sets available at Remote Site (after List).", GH_ParamAccess.list);
        p.AddTextParameter("Status", "St", "Result of the last action.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        if (da.Iteration > 0) return;

        var planes = new List<Plane>();
        da.GetDataList(0, planes);
        bool push = false, pull = false, list = false;
        da.GetData(1, ref push);
        da.GetData(2, ref pull);
        string site = "france", remote = "austria", name = "", pullName = "latest", repo = "", pullRepo = "", branch = "plane-data", tokenIn = "", note = "";
        da.GetData(3, ref site);
        da.GetData(4, ref remote);
        da.GetData(5, ref name);
        da.GetData(6, ref pullName);
        da.GetData(7, ref repo);
        da.GetData(8, ref pullRepo);
        da.GetData(9, ref branch);
        da.GetData(10, ref tokenIn);
        da.GetData(11, ref list);
        da.GetData(12, ref note);

        repo = repo.Trim();
        pullRepo = string.IsNullOrWhiteSpace(pullRepo) ? repo : pullRepo.Trim();
        branch = string.IsNullOrWhiteSpace(branch) ? "plane-data" : branch.Trim();
        var token = PlaneExchangeClient.ResolveToken(tokenIn);
        if (!string.IsNullOrWhiteSpace(tokenIn))
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Token is typed into the definition and will be saved in the .gh file. Leave it empty and run set_exchange_token.ps1 instead.");

        var pushEdge = push && !_wasPush;
        var pullEdge = pull && !_wasPull;
        var listEdge = list && !_wasList;
        _wasPush = push; _wasPull = pull; _wasList = list;

        var docUnits = RhinoDoc.ActiveDoc?.ModelUnitSystem ?? UnitSystem.Millimeters;

        lock (_gate)
        {
            if (!_busy && (pushEdge || pullEdge || listEdge))
            {
                if (!repo.Contains('/'))
                    _status = "Repo must be owner/name.";
                else if (pushEdge && token is null)
                    _status = "Push needs a token: run set_exchange_token.ps1 once on this PC.";
                else if (pushEdge && planes.Count == 0)
                    _status = "Nothing to push: Planes is empty.";
                else if (pushEdge)
                {
                    var set = new PlaneSet
                    {
                        Site = site,
                        Name = string.IsNullOrWhiteSpace(name) ? DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") : name,
                        Units = docUnits.ToString(),
                        Note = note,
                        Planes = planes.Select(PlaneRecord.From).ToList(),
                    };
                    Run($"Pushing {planes.Count} planes...", async () =>
                    {
                        var path = await PlaneExchangeClient.Push(repo, branch, token!, set);
                        _status = $"Pushed {set.Planes.Count} planes to {repo}@{branch}:{path} ({DateTime.Now:HH:mm:ss}).";
                    });
                }
                else if (pullEdge)
                {
                    Run($"Pulling {remote}/{pullName}...", async () =>
                    {
                        var set = await PlaneExchangeClient.Pull(pullRepo, branch, token, remote, pullName);
                        var from = Enum.TryParse<UnitSystem>(set.Units, true, out var u) ? u : UnitSystem.Millimeters;
                        var scale = RhinoMath.UnitScale(from, docUnits);
                        _pulled = set.Planes.Select(r => r.ToPlane(scale)).ToList();
                        _pulledInfo = $"{set.Site}/{set.Name} | {set.Planes.Count} planes | recorded {set.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} | {set.Units}"
                                      + (scale != 1 ? $" -> {docUnits} (x{scale:G6})" : "")
                                      + (string.IsNullOrWhiteSpace(set.Note) ? "" : $" | {set.Note}");
                        _status = $"Pulled {_pulled.Count} planes from {pullRepo}@{branch} ({DateTime.Now:HH:mm:ss}).";
                    });
                }
                else
                {
                    Run($"Listing {remote}...", async () =>
                    {
                        _available = await PlaneExchangeClient.List(pullRepo, branch, token, remote);
                        _status = $"{_available.Count} plane sets at {remote}.";
                    });
                }
            }

            if (_status.Contains("failed") || _status.StartsWith("Push needs") || _status.StartsWith("Repo must") || _status.StartsWith("Nothing"))
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, _status);

            da.SetDataList(0, _pulled);
            da.SetData(1, _pulledInfo);
            da.SetDataList(2, _available);
            da.SetData(3, _busy ? "Working... " + _status : _status);
        }
    }

    private void Run(string startMsg, Func<Task> work)
    {
        _busy = true;
        _status = startMsg;
        Task.Run(async () =>
        {
            try { await work(); }
            catch (Exception ex) { lock (_gate) _status = ex is InvalidOperationException ? ex.Message : $"Network action failed: {ex.Message}"; }
            finally
            {
                lock (_gate) _busy = false;
                RhinoApp.InvokeOnUiThread(new Action(() => ExpireSolution(true)));
            }
        });
    }

    private static Bitmap LoadIcon()
    {
        using var stream = typeof(VivePlaneExchangeComponent).Assembly.GetManifestResourceStream("ViveTcpRecorder.Resources.Calibration.png");
        if (stream is null) return new Bitmap(24, 24);
        using var source = new Bitmap(stream);
        return new Bitmap(source, new Size(24, 24));
    }
}
