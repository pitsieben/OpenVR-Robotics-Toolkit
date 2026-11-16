using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rhino.Geometry;

namespace ViveTcpRecorder;

/// <summary>One recorded plane set as stored on the exchange branch.</summary>
public sealed class PlaneSet
{
    public int Schema { get; set; } = 1;
    public string Site { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Rhino unit system name the coordinates are written in (e.g. "Millimeters").</summary>
    public string Units { get; set; } = "Millimeters";
    public string Note { get; set; } = "";
    public List<PlaneRecord> Planes { get; set; } = new();
}

public sealed class PlaneRecord
{
    public double[] O { get; set; } = new double[3];
    public double[] X { get; set; } = new double[3];
    public double[] Y { get; set; } = new double[3];

    public static PlaneRecord From(Plane p) => new()
    {
        O = new[] { p.OriginX, p.OriginY, p.OriginZ },
        X = new[] { p.XAxis.X, p.XAxis.Y, p.XAxis.Z },
        Y = new[] { p.YAxis.X, p.YAxis.Y, p.YAxis.Z },
    };

    public Plane ToPlane(double scale) => new(
        new Point3d(O[0] * scale, O[1] * scale, O[2] * scale),
        new Vector3d(X[0], X[1], X[2]),
        new Vector3d(Y[0], Y[1], Y[2]));
}

/// <summary>Minimal GitHub contents-API client: no git install needed on workshop PCs.</summary>
public static class PlaneExchangeClient
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ViveTcpRecorder-PlaneExchange/1.0");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return c;
    }

    public static string SafeName(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.Trim())
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        return sb.Length == 0 ? "planes" : sb.ToString();
    }

    public static string PathFor(string site, string name) => $"planes/{SafeName(site)}/{SafeName(name)}.json";

    /// <summary>Folder next to the installed .gha that holds the token.</summary>
    public static string InstallTokenFolder =>
        Path.Combine(Path.GetDirectoryName(typeof(PlaneExchangeClient).Assembly.Location) ?? ".", "ViveExchange");

    /// <summary>
    /// Token lookup order: input, env var VIVE_EXCHANGE_TOKEN, then ViveExchange\token.dat / token.txt
    /// next to the installed .gha, then %APPDATA%\ViveExchange\. A plain token.txt is encrypted on
    /// first use with Windows DPAPI (current user only) into token.dat and the plain file is deleted.
    /// </summary>
    public static string? ResolveToken(string? input)
    {
        if (!string.IsNullOrWhiteSpace(input)) return input.Trim();
        var env = Environment.GetEnvironmentVariable("VIVE_EXCHANGE_TOKEN");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ViveExchange");
        var libraries = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Grasshopper", "Libraries", "ViveExchange");
        foreach (var dir in new[] { InstallTokenFolder, libraries, appData })
        {
            var t = TokenStore.Load(dir);
            if (t != null) return t;
        }
        return null;
    }

    private static HttpRequestMessage Req(HttpMethod m, string url, string? token)
    {
        var r = new HttpRequestMessage(m, url);
        if (!string.IsNullOrEmpty(token))
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return r;
    }

    private static async Task<string> Fail(HttpResponseMessage res, string what)
    {
        var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
        string msg = body;
        try { msg = JsonDocument.Parse(body).RootElement.GetProperty("message").GetString() ?? body; } catch { }
        var hint = res.StatusCode switch
        {
            HttpStatusCode.Unauthorized => " (token invalid or expired)",
            HttpStatusCode.Forbidden => " (token lacks Contents: write, or branch/path is protected)",
            HttpStatusCode.NotFound => " (repo/branch/file not found, or token cannot see the repo)",
            _ => "",
        };
        return $"{what} failed: {(int)res.StatusCode} {msg}{hint}";
    }

    /// <summary>Returns the blob sha of a file, or null if it does not exist.</summary>
    private static async Task<string?> GetSha(string repo, string branch, string path, string? token)
    {
        using var res = await Http.SendAsync(Req(HttpMethod.Get, $"repos/{repo}/contents/{path}?ref={Uri.EscapeDataString(branch)}", token)).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(await Fail(res, "Lookup"));
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync().ConfigureAwait(false));
        return doc.RootElement.GetProperty("sha").GetString();
    }

    /// <summary>Creates the branch from the repo's default branch if it does not exist yet.</summary>
    private static async Task EnsureBranch(string repo, string branch, string token)
    {
        using (var head = await Http.SendAsync(Req(HttpMethod.Get, $"repos/{repo}/git/ref/heads/{branch}", token)).ConfigureAwait(false))
            if (head.IsSuccessStatusCode) return;

        using var info = await Http.SendAsync(Req(HttpMethod.Get, $"repos/{repo}", token)).ConfigureAwait(false);
        if (!info.IsSuccessStatusCode) throw new InvalidOperationException(await Fail(info, "Repo lookup"));
        string def;
        using (var d = JsonDocument.Parse(await info.Content.ReadAsStringAsync().ConfigureAwait(false)))
            def = d.RootElement.GetProperty("default_branch").GetString()!;

        using var defRef = await Http.SendAsync(Req(HttpMethod.Get, $"repos/{repo}/git/ref/heads/{def}", token)).ConfigureAwait(false);
        if (!defRef.IsSuccessStatusCode) throw new InvalidOperationException(await Fail(defRef, "Default branch lookup"));
        string sha;
        using (var d = JsonDocument.Parse(await defRef.Content.ReadAsStringAsync().ConfigureAwait(false)))
            sha = d.RootElement.GetProperty("object").GetProperty("sha").GetString()!;

        var create = Req(HttpMethod.Post, $"repos/{repo}/git/refs", token);
        create.Content = new StringContent(JsonSerializer.Serialize(new { @ref = $"refs/heads/{branch}", sha }), Encoding.UTF8, "application/json");
        using var res = await Http.SendAsync(create).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode && res.StatusCode != HttpStatusCode.UnprocessableEntity)
            throw new InvalidOperationException(await Fail(res, "Branch creation"));
    }

    private static async Task PutFile(string repo, string branch, string path, string content, string message, string token)
    {
        var sha = await GetSha(repo, branch, path, token).ConfigureAwait(false);
        var payload = new Dictionary<string, object>
        {
            ["message"] = message,
            ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
            ["branch"] = branch,
        };
        if (sha != null) payload["sha"] = sha;
        var put = Req(HttpMethod.Put, $"repos/{repo}/contents/{path}", token);
        put.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var res = await Http.SendAsync(put).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(await Fail(res, $"Upload of {path}"));
    }

    /// <summary>Writes planes/&lt;site&gt;/&lt;name&gt;.json and planes/&lt;site&gt;/latest.json.</summary>
    public static async Task<string> Push(string repo, string branch, string token, PlaneSet set)
    {
        await EnsureBranch(repo, branch, token).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(set, Json);
        var msg = $"[{set.Site}] {set.Name}: {set.Planes.Count} planes";
        var path = PathFor(set.Site, set.Name);
        await PutFile(repo, branch, path, json, msg, token).ConfigureAwait(false);
        if (!string.Equals(SafeName(set.Name), "latest", StringComparison.OrdinalIgnoreCase))
            await PutFile(repo, branch, PathFor(set.Site, "latest"), json, msg + " (latest)", token).ConfigureAwait(false);
        return path;
    }

    public static async Task<PlaneSet> Pull(string repo, string branch, string? token, string site, string name)
    {
        var path = PathFor(site, string.IsNullOrWhiteSpace(name) ? "latest" : name);
        var req = Req(HttpMethod.Get, $"repos/{repo}/contents/{path}?ref={Uri.EscapeDataString(branch)}", token);
        req.Headers.Accept.Clear();
        req.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
        using var res = await Http.SendAsync(req).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(await Fail(res, $"Download of {path}"));
        var text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
        return JsonSerializer.Deserialize<PlaneSet>(text, Json) ?? throw new InvalidOperationException($"{path} is empty or invalid.");
    }

    /// <summary>Names of plane sets available for a site.</summary>
    public static async Task<List<string>> List(string repo, string branch, string? token, string site)
    {
        using var res = await Http.SendAsync(Req(HttpMethod.Get, $"repos/{repo}/contents/planes/{SafeName(site)}?ref={Uri.EscapeDataString(branch)}", token)).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.NotFound) return new List<string>();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(await Fail(res, "Listing"));
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync().ConfigureAwait(false));
        return doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("name").GetString() ?? "")
            .Where(n => n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(n => n[..^5])
            .OrderBy(n => n)
            .ToList();
    }
}
