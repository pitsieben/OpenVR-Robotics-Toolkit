#pragma warning disable CA1416

using System.Drawing;
using Grasshopper;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Special;
using Rhino;
using Rhino.Geometry;

namespace ViveTcpRecorder;

/// <summary>
/// Pushes recorded planes to, and pulls the partner site's planes from, a GitHub data branch.
/// Network calls run in the background so the canvas never freezes.
/// Visible inputs: Planes, Site (France / Austria), Push, Pull, Push Name, Pull Name. Repo, Pull Repo, Branch, Token, List and Note are
/// optional parameters hidden by default (zoom in on the component and click the + / - at the edge of its inputs to add or remove them).
/// Site France pushes to the folder "france" and pulls from "austria"; Austria is the reverse.
/// </summary>
public sealed class VivePlaneExchangeComponent : GH_Component, IGH_VariableParameterComponent
{
    private static readonly Lazy<Bitmap> ComponentIcon = new(() => PlaneIcons.Load("PlaneExchange.png"));

    private sealed record ParamDef(string Key, string Name, string Nick, string Description, Func<IGH_Param> Create);

    private static readonly string[] DefaultLayout = { "Planes", "Site", "Push", "Pull", "PushName", "PullName" };
    /// <summary>Layout of files saved before the simplification (13 inputs in this order).</summary>
    private static readonly string[] LegacyLayout =
        { "Planes", "Push", "Pull", "Site", "RemoteSite", "PushName", "PullName", "Repo", "PullRepo", "Branch", "Token", "List", "Note" };
    /// <summary>Parameters the user can add with the + button, in the order they are offered.</summary>
    private static readonly string[] Extras = { "Repo", "PullRepo", "Branch", "Token", "List", "Note" };

    private static readonly Dictionary<string, ParamDef> Defs = BuildDefs();

    private readonly object _gate = new();
    private List<string> _layout = new(DefaultLayout);
    private bool _fromFile, _siteListChecked;
    private bool _wasPush, _wasPull, _wasList;
    private bool _busy;
    private string _status = "Idle. Click Push to upload, Pull to download.";
    private DataTree<Plane> _pulled = new();
    private string _pulledInfo = "";
    private List<string> _available = new();

    public VivePlaneExchangeComponent()
        : base("Vive Plane Exchange", "PlaneX",
            "Push recorded planes to a GitHub data branch and pull the partner site's planes, so a calibration recorded at one site can be used at the other. Site France pushes to 'france' and pulls from 'austria'; Austria is the reverse. Zoom in and use the + / - on the inputs to show Repo, Pull Repo, Branch, Token, List and Note.",
            "Vive", "Utilities")
    {
    }

    public override Guid ComponentGuid => new("7F3B6C21-9D4E-4A58-B1C7-2E8F05A3D964");
    protected override Bitmap Icon => ComponentIcon.Value;

    // ------------------------------------------------------------------ parameters
    private static Dictionary<string, ParamDef> BuildDefs()
    {
        static Param_String Text(string def) { var p = new Param_String { Access = GH_ParamAccess.item }; p.SetPersistentData(def); return p; }
        static Param_Boolean Flag() { var p = new Param_Boolean { Access = GH_ParamAccess.item }; p.SetPersistentData(false); return p; }
        var list = new[]
        {
            new ParamDef("Planes", "Planes", "P", "Planes to push. Data-tree branches are kept (e.g. one branch per take) and come back the same way on Pull.", () => new Param_Plane { Access = GH_ParamAccess.tree }),
            new ParamDef("Site", "Site", "S", "This site: France or Austria (a Value List with both is created for you; text 'france' / 'austria' works too). France pushes to 'france' and pulls from 'austria'; Austria the reverse.", () => Text("France")),
            new ParamDef("Push", "Push", "Push", "Button: upload Planes as <site>/<Push Name>.json (and <site>/latest.json).", Flag),
            new ParamDef("Pull", "Pull", "Pull", "Button: download <other site>/<Pull Name>.json.", Flag),
            new ParamDef("PushName", "Push Name", "PN", "Name of the plane set to push. Empty = timestamp (latest is written as well).", () => Text("")),
            new ParamDef("PullName", "Pull Name", "PuN", "Plane set to pull. Empty = latest.", () => Text("latest")),
            new ParamDef("Repo", "Repo", "R", "GitHub repo to push to, as owner/name.", () => Text("pitsieben/OpenVR-Robotics-Toolkit")),
            new ParamDef("PullRepo", "Pull Repo", "PR", "Repo to pull from. Empty = same as Repo. Use when each site has its own data repo.", () => Text("")),
            new ParamDef("Branch", "Branch", "B", "Data branch name.", () => Text("plane-data")),
            new ParamDef("Token", "Token", "T", "Leave EMPTY (recommended): the token is read encrypted from the install folder (run set_exchange_token.ps1 once). A token typed here is saved inside the .gh file.", () => Text("")),
            new ParamDef("List", "List", "L", "Button: list plane sets available at the other site.", Flag),
            new ParamDef("Note", "Note", "Nt", "Optional note stored with the pushed set.", () => Text("")),
            // only present in files saved by earlier versions
            new ParamDef("RemoteSite", "Remote Site", "RS", "Partner site's folder to pull from (legacy input; overrides the automatic France/Austria pairing).", () => Text("austria")),
        };
        return list.ToDictionary(d => d.Key);
    }

    private static IGH_Param Make(string key)
    {
        var d = Defs[key];
        var p = d.Create();
        p.Name = d.Name; p.NickName = d.Nick; p.Description = d.Description; p.Optional = true;
        return p;
    }

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        foreach (var key in _layout)
        {
            var prm = Make(key);
            p.AddParameter(prm, prm.Name, prm.NickName, prm.Description, prm.Access);
        }
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddPlaneParameter("Pulled Planes", "P", "Planes downloaded from the other site, scaled to this document's units, in the same branches they were pushed in.", GH_ParamAccess.tree);
        p.AddTextParameter("Info", "I", "Site, name, time and units of the pulled set.", GH_ParamAccess.item);
        p.AddTextParameter("Available", "A", "Plane sets available at the other site (after List).", GH_ParamAccess.list);
        p.AddTextParameter("Status", "St", "Result of the last action.", GH_ParamAccess.item);
    }

    private IEnumerable<string> HiddenExtras() => Extras.Where(k => !_layout.Contains(k));

    bool IGH_VariableParameterComponent.CanInsertParameter(GH_ParameterSide side, int index) => side == GH_ParameterSide.Input && HiddenExtras().Any();
    bool IGH_VariableParameterComponent.CanRemoveParameter(GH_ParameterSide side, int index) =>
        side == GH_ParameterSide.Input && index >= 0 && index < _layout.Count && !DefaultLayout.Contains(_layout[index]);

    IGH_Param IGH_VariableParameterComponent.CreateParameter(GH_ParameterSide side, int index)
    {
        var key = HiddenExtras().First();
        _layout.Insert(Math.Clamp(index, 0, _layout.Count), key);
        return Make(key);
    }

    bool IGH_VariableParameterComponent.DestroyParameter(GH_ParameterSide side, int index)
    {
        if (index < 0 || index >= _layout.Count) return false;
        _layout.RemoveAt(index);
        return true;
    }

    void IGH_VariableParameterComponent.VariableParameterMaintenance() { }

    /// <summary>Replaces the input parameters by the ones in <see cref="_layout"/> (used while loading a file, before the parameter data is read).</summary>
    private void RebuildInputs()
    {
        while (Params.Input.Count > 0) Params.UnregisterInputParameter(Params.Input[0]);
        foreach (var key in _layout) Params.RegisterInputParam(Make(key));
    }

    // ------------------------------------------------------------------ persistence
    public override bool Write(GH_IO.Serialization.GH_IWriter writer)
    {
        writer.SetString("InputLayout", string.Join(",", _layout));
        return base.Write(writer);
    }

    public override bool Read(GH_IO.Serialization.GH_IReader reader)
    {
        _fromFile = true;
        var want = reader.ItemExists("InputLayout")
            ? reader.GetString("InputLayout").Split(',', StringSplitOptions.RemoveEmptyEntries).Where(Defs.ContainsKey).ToList()
            : new List<string>(LegacyLayout);   // saved before the simplification: keep all 13 inputs so wires and values still line up
        if (want.Count == 0) want = new List<string>(DefaultLayout);
        if (!want.SequenceEqual(_layout))
        {
            _layout = want;
            RebuildInputs();
        }
        var ok = base.Read(reader);
        var pi = _layout.IndexOf("Planes");
        if (pi >= 0 && pi < Params.Input.Count) Params.Input[pi].Access = GH_ParamAccess.tree;   // older files saved it as a list
        return ok;
    }

    // ------------------------------------------------------------------ site value list
    public override void AddedToDocument(GH_Document document)
    {
        base.AddedToDocument(document);
        if (_fromFile || _siteListChecked) return;
        _siteListChecked = true;
        document.ScheduleSolution(5, doc => CreateSiteList(doc));
    }

    private void CreateSiteList(GH_Document doc)
    {
        var index = _layout.IndexOf("Site");
        if (index < 0 || index >= Params.Input.Count) return;
        var param = Params.Input[index];
        if (param.SourceCount > 0) return;

        Attributes.ExpireLayout();
        Attributes.PerformLayout();
        var vl = new GH_ValueList();
        vl.CreateAttributes();
        vl.Name = "Site";
        vl.NickName = "Site";
        vl.ListMode = GH_ValueListMode.DropDown;
        vl.ListItems.Clear();
        vl.ListItems.Add(new GH_ValueListItem("France", "\"France\""));
        vl.ListItems.Add(new GH_ValueListItem("Austria", "\"Austria\""));
        vl.SelectItem(0);
        var left = param.Attributes.Bounds.Left;
        vl.Attributes.Pivot = new PointF(left - 140, param.Attributes.Pivot.Y - 10);
        doc.AddObject(vl, false);
        param.AddSource(vl);
        param.ExpireSolution(false);
    }

    // ------------------------------------------------------------------ solve
    private T Get<T>(IGH_DataAccess da, string key, T fallback)
    {
        var i = _layout.IndexOf(key);
        if (i < 0) return fallback;
        var v = fallback;
        return da.GetData(i, ref v) && v != null ? v : fallback;
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        if (da.Iteration > 0) return;

        var records = new List<PlaneRecord>();
        var pi = _layout.IndexOf("Planes");
        if (pi >= 0 && da.GetDataTree(pi, out GH_Structure<GH_Plane> inTree) && inTree != null)
            for (var b = 0; b < inTree.PathCount; b++)
                foreach (var gp in inTree.Branches[b])
                    if (gp != null && gp.Value.IsValid)
                    {
                        var r = PlaneRecord.From(gp.Value);
                        r.Path = inTree.Paths[b].ToString();
                        records.Add(r);
                    }
        var push = Get(da, "Push", false);
        var pull = Get(da, "Pull", false);
        var list = Get(da, "List", false);
        var siteText = Get(da, "Site", "France");
        var remoteText = Get(da, "RemoteSite", "");
        var name = Get(da, "PushName", "");
        var pullName = Get(da, "PullName", "latest");
        var repo = Get(da, "Repo", "pitsieben/OpenVR-Robotics-Toolkit");
        var pullRepo = Get(da, "PullRepo", "");
        var branch = Get(da, "Branch", "plane-data");
        var tokenIn = Get(da, "Token", "");
        var note = Get(da, "Note", "");

        ExchangeSites.Resolve(siteText, remoteText, out var pushSite, out var pullSite, out var siteWarning);
        if (siteWarning != null) AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, siteWarning);

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
                else if (pushEdge && records.Count == 0)
                    _status = "Nothing to push: Planes is empty.";
                else if (pushEdge)
                {
                    var set = new PlaneSet
                    {
                        Site = pushSite,
                        Name = string.IsNullOrWhiteSpace(name) ? DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") : name,
                        Units = docUnits.ToString(),
                        Note = note,
                        Planes = records,
                    };
                    Run($"Pushing {records.Count} planes to {pushSite}...", async () =>
                    {
                        var path = await PlaneExchangeClient.Push(repo, branch, token!, set);
                        _status = $"Pushed {set.Planes.Count} planes to {repo}@{branch}:{path} ({DateTime.Now:HH:mm:ss}).";
                    });
                }
                else if (pullEdge)
                {
                    Run($"Pulling {pullSite}/{pullName}...", async () =>
                    {
                        var set = await PlaneExchangeClient.Pull(pullRepo, branch, token, pullSite, pullName);
                        var from = Enum.TryParse<UnitSystem>(set.Units, true, out var u) ? u : UnitSystem.Millimeters;
                        var scale = RhinoMath.UnitScale(from, docUnits);
                        var tree = new DataTree<Plane>();
                        foreach (var r in set.Planes)
                        {
                            var path = new GH_Path(0);
                            if (!string.IsNullOrEmpty(r.Path)) { var gp = new GH_Path(); if (gp.FromString(r.Path)) path = gp; }
                            tree.Add(r.ToPlane(scale), path);
                        }
                        _pulled = tree;
                        _pulledInfo = $"{set.Site}/{set.Name} | {set.Planes.Count} planes | recorded {set.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} | {set.Units}"
                                      + (scale != 1 ? $" -> {docUnits} (x{scale:G6})" : "")
                                      + (string.IsNullOrWhiteSpace(set.Note) ? "" : $" | {set.Note}");
                        _status = $"Pulled {_pulled.DataCount} planes in {_pulled.BranchCount} branches from {pullRepo}@{branch} ({DateTime.Now:HH:mm:ss}).";
                    });
                }
                else
                {
                    Run($"Listing {pullSite}...", async () =>
                    {
                        _available = await PlaneExchangeClient.List(pullRepo, branch, token, pullSite);
                        _status = $"{_available.Count} plane sets at {pullSite}.";
                    });
                }
            }

            if (_status.Contains("failed") || _status.StartsWith("Push needs") || _status.StartsWith("Repo must") || _status.StartsWith("Nothing"))
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, _status);

            da.SetDataTree(0, _pulled);
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
}
