using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using ViveTcpRecorder;

namespace ViveExchangeSetup;

internal static class Program
{
    /// <summary>Same folder the Grasshopper component and set_exchange_token.ps1 use.</summary>
    public static readonly string TokenDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Grasshopper", "Libraries", "ViveExchange");

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest") { Environment.Exit(SelfTest()); return; }
        ApplicationConfiguration.Initialize();
        RegisterLinkHandler();
        Application.Run(new MainForm(args.FirstOrDefault(a => a.StartsWith(Invite.Scheme + ":", StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>Makes viveexchange:// links open this app (current user only, no admin rights).</summary>
    private static void RegisterLinkHandler()
    {
        try
        {
            var exe = Environment.ProcessPath!;
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Invite.Scheme}");
            key.SetValue("", "URL:Vive Plane Exchange invite");
            key.SetValue("URL Protocol", "");
            using var cmd = key.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{exe}\" \"%1\"");
        }
        catch { /* links then just need pasting */ }
    }

    /// <summary>Checks the token against GitHub; returns a human-readable result and whether it can write.</summary>
    public static async Task<(bool Ok, bool CanWrite, string Message)> CheckToken(string token, string repo)
    {
        using var http = new HttpClient { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ViveExchangeSetup/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var res = await http.GetAsync($"repos/{repo}");
            if ((int)res.StatusCode == 401) return (false, false, "GitHub rejected the token (wrong or expired).");
            if ((int)res.StatusCode == 404) return (false, false, $"Token cannot see {repo}. Check the repo name and the token's repository access.");
            if (!res.IsSuccessStatusCode) return (false, false, $"GitHub answered {(int)res.StatusCode}.");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var push = doc.RootElement.TryGetProperty("permissions", out var perm) && perm.TryGetProperty("push", out var p) && p.GetBoolean();
            var admin = perm.ValueKind == JsonValueKind.Object && perm.TryGetProperty("admin", out var a) && a.GetBoolean();
            var msg = push ? $"OK: token can read and write {repo}." : $"OK: token can read {repo}, but NOT write (pull only).";
            // Fine-grained tokens report the owner's role here, so 'admin' does not mean the token itself has admin rights.
            if (admin) msg += " (Your account is admin of the repo; keep the token limited to Contents.)";
            return (true, push, msg);
        }
        catch (Exception ex) { return (false, false, "No connection to GitHub: " + ex.Message); }
    }

    private static int SelfTest()
    {
        var data = new InviteData("github_pat_TEST123", "org/repo", "plane-data", DateTime.UtcNow);
        var link = Invite.Create(data, "correct horse 42");
        var back = Invite.Open(link, "correct horse 42");
        var ok = back == data && link.StartsWith("viveexchange://invite/") && !link.Contains("TEST123");
        var wrong = false;
        try { Invite.Open(link, "wrong passphrase"); } catch (CryptographicException) { wrong = true; }
        var tampered = false;
        try { Invite.Open(link[..^2] + (link[^2] == 'A' ? "B" : "A") + link[^1], "correct horse 42"); } catch (Exception) { tampered = true; }
        var dir = Path.Combine(Path.GetTempPath(), "vxtest_" + Guid.NewGuid().ToString("N"));
        TokenStore.Save(dir, back.Token);
        var stored = TokenStore.Load(dir) == back.Token && !File.ReadAllText(Path.Combine(dir, "token.dat")).Contains("TEST123");
        File.SetAttributes(Path.Combine(dir, "token.dat"), FileAttributes.Normal);
        Directory.Delete(dir, true);
        Console.WriteLine($"roundtrip={ok} wrongPass={wrong} tampered={tampered} stored={stored} linkLength={link.Length}");
        return ok && wrong && tampered && stored ? 0 : 1;
    }
}
