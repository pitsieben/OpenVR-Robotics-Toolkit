using System.Security.Cryptography;
using ViveTcpRecorder;

namespace ViveExchangeSetup;

/// <summary>Three tabs: save my token, create an invite link, accept an invite link.</summary>
public sealed class MainForm : Form
{
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };

    // My token
    private readonly TextBox _token = Secret();
    private readonly TextBox _repo = new() { Text = "pitsieben/OpenVR-Robotics-Toolkit" };
    private readonly Label _tokenStatus = StatusLabel();

    // Create invite
    private readonly TextBox _invToken = Secret();
    private readonly TextBox _invRepo = new() { Text = "pitsieben/OpenVR-Robotics-Toolkit" };
    private readonly TextBox _invBranch = new() { Text = "plane-data" };
    private readonly TextBox _invPass = new();
    private readonly TextBox _invLink = new() { ReadOnly = true, Multiline = true, Height = 70, ScrollBars = ScrollBars.Vertical };
    private readonly Label _invStatus = StatusLabel();

    // Accept invite
    private readonly TextBox _accLink = new() { Multiline = true, Height = 70, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _accPass = Secret();
    private readonly Label _accStatus = StatusLabel();

    public MainForm(string? link)
    {
        Text = "Vive Plane Exchange - Token & Invites";
        Width = 620;
        Height = 520;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        _tabs.TabPages.Add(BuildTokenTab());
        _tabs.TabPages.Add(BuildInviteTab());
        _tabs.TabPages.Add(BuildAcceptTab());
        Controls.Add(_tabs);
        RefreshSavedState();

        if (link != null)
        {
            _accLink.Text = link;
            _tabs.SelectedIndex = 2;
            _accStatus.Text = "Invite received. Enter the passphrase you were given separately, then click Accept.";
        }
    }

    // ---------- tabs ----------

    private TabPage BuildTokenTab()
    {
        var save = new Button { Text = "Check && save", AutoSize = true };
        save.Click += async (_, _) => await SaveToken(_token.Text, _repo.Text, _tokenStatus, clearBox: _token);
        var remove = new Button { Text = "Remove saved token", AutoSize = true };
        remove.Click += (_, _) =>
        {
            var f = Path.Combine(Program.TokenDir, "token.dat");
            if (File.Exists(f)) { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); }
            RefreshSavedState();
        };
        return Page("My token",
            Info("Paste the GitHub token for THIS site. It is checked against GitHub and saved encrypted (Windows DPAPI, only your Windows account on this PC can read it) next to the Grasshopper plugin. Restart Rhino afterwards."),
            Row("Token", _token), Row("Repo (owner/name)", _repo), Buttons(save, remove), _tokenStatus);
    }

    private TabPage BuildInviteTab()
    {
        var gen = new Button { Text = "Generate", AutoSize = true };
        gen.Click += (_, _) => _invPass.Text = NewPassphrase();
        var create = new Button { Text = "Create invite link", AutoSize = true };
        create.Click += (_, _) => CreateInvite();
        var copy = new Button { Text = "Copy link", AutoSize = true };
        copy.Click += (_, _) => { if (_invLink.Text.Length > 0) { Clipboard.SetText(_invLink.Text); _invStatus.Text = "Link copied. Now send the passphrase over a DIFFERENT channel (e.g. phone/Signal)."; } };
        var copyPass = new Button { Text = "Copy passphrase", AutoSize = true };
        copyPass.Click += (_, _) => { if (_invPass.Text.Length > 0) Clipboard.SetText(_invPass.Text); };
        _invPass.Text = NewPassphrase();
        _invToken.PlaceholderText = "empty = use the token saved on this PC";
        return Page("Create invite",
            Info("Makes an encrypted link that gives someone the token. The link alone is useless: the recipient also needs the passphrase, so send it separately."),
            Row("Token to share", _invToken), Row("Repo", _invRepo), Row("Branch", _invBranch),
            Row("Passphrase", _invPass, gen),
            Buttons(create, copy, copyPass), _invLink, _invStatus);
    }

    private TabPage BuildAcceptTab()
    {
        var accept = new Button { Text = "Accept && save", AutoSize = true };
        accept.Click += async (_, _) => await AcceptInvite();
        var paste = new Button { Text = "Paste link", AutoSize = true };
        paste.Click += (_, _) => { if (Clipboard.ContainsText()) _accLink.Text = Clipboard.GetText(); };
        return Page("Accept invite",
            Info("Paste the invite link you received (or click it, which opens this app) and type the passphrase that was sent to you separately."),
            Row("Invite link", _accLink, paste), Row("Passphrase", _accPass), Buttons(accept), _accStatus);
    }

    // ---------- actions ----------

    private async Task SaveToken(string token, string repo, Label status, TextBox? clearBox)
    {
        token = token.Trim();
        repo = repo.Trim();
        if (token.Length < 20) { status.Text = "That does not look like a GitHub token."; return; }
        status.Text = "Checking with GitHub...";
        var (ok, canWrite, msg) = await Program.CheckToken(token, repo);
        if (!ok) { status.Text = msg + " Not saved."; return; }
        TokenStore.Save(Program.TokenDir, token);
        clearBox?.Clear();
        status.Text = msg + " Saved encrypted." + (canWrite ? "" : " Push will not work with this token.");
        RefreshSavedState();
    }

    private void CreateInvite()
    {
        try
        {
            var token = _invToken.Text.Trim();
            if (token.Length == 0) token = TokenStore.Load(Program.TokenDir) ?? "";
            if (token.Length == 0) { _invStatus.Text = "No token: paste one or save one in 'My token' first."; return; }
            var data = new InviteData(token, _invRepo.Text.Trim(), _invBranch.Text.Trim(), DateTime.UtcNow);
            _invLink.Text = Invite.Create(data, _invPass.Text);
            _invToken.Clear();
            _invStatus.Text = "Invite created. Copy the link and send it; send the passphrase separately.";
        }
        catch (Exception ex) { _invStatus.Text = ex.Message; }
    }

    private async Task AcceptInvite()
    {
        InviteData data;
        try { data = Invite.Open(_accLink.Text, _accPass.Text); }
        catch (Exception ex) { _accStatus.Text = ex.Message; return; }

        var info = $"Repo: {data.Repo} (branch {data.Branch})\nCreated: {data.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}\n\nSave this token on this PC?";
        if (MessageBox.Show(this, info, "Accept invite", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        await SaveToken(data.Token, data.Repo, _accStatus, clearBox: null);
        _accLink.Clear();
        _accPass.Clear();
        _accStatus.Text += $"\nIn Grasshopper set Repo = {data.Repo} and Branch = {data.Branch}; Site = your own site name, Remote Site = the partner's.";
    }

    private void RefreshSavedState()
    {
        var saved = File.Exists(Path.Combine(Program.TokenDir, "token.dat"));
        _tokenStatus.Text = saved ? $"A token is saved (encrypted) in {Program.TokenDir}." : "No token saved on this PC yet.";
    }

    private static string NewPassphrase()
    {
        // 5 words of 4-6 letters from a small built-in list + 2 digits: easy to read out over the phone.
        string[] words = { "clay", "brick", "knife", "robot", "tile", "kiln", "glaze", "wire", "loop", "slab", "carve", "press", "stone", "river", "maple", "cedar", "amber", "linz", "lemans", "orbit", "pixel", "tower", "frame", "plane", "track", "light", "north", "delta", "sigma", "omega", "lunar", "solar" };
        var picks = Enumerable.Range(0, 5).Select(_ => words[RandomNumberGenerator.GetInt32(words.Length)]);
        return string.Join("-", picks) + "-" + RandomNumberGenerator.GetInt32(10, 100);
    }

    // ---------- tiny layout helpers ----------

    private static TextBox Secret() => new() { UseSystemPasswordChar = true };
    private static Label StatusLabel() => new() { AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Color.DarkSlateBlue, Padding = new Padding(0, 6, 0, 0) };
    private static Label Info(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(560, 0), Padding = new Padding(0, 0, 0, 8) };

    private static Control Row(string label, Control box, Control? extra = null)
    {
        var row = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Width = 570 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, extra == null ? 430 : 340));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        box.Dock = DockStyle.Fill;
        row.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        row.Controls.Add(box, 1, 0);
        if (extra != null) row.Controls.Add(extra, 2, 0);
        return row;
    }

    private static Control Buttons(params Button[] buttons)
    {
        var flow = new FlowLayoutPanel { AutoSize = true, Padding = new Padding(130, 4, 0, 0) };
        flow.Controls.AddRange(buttons);
        return flow;
    }

    private static TabPage Page(string title, params Control[] controls)
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12) };
        foreach (var c in controls)
        {
            if (c is TextBox { Multiline: true } t) t.Width = 560;
            flow.Controls.Add(c);
        }
        var page = new TabPage(title);
        page.Controls.Add(flow);
        return page;
    }
}
