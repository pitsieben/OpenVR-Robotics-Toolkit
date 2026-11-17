using System.Text;

namespace ViveTcpRecorder;

/// <summary>Pure France/Austria folder logic of the Plane Exchange component (unit-testable).</summary>
public static class ExchangeSites
{
    /// <summary>Folder-safe lower-case name: letters, digits, '-' and '_' are kept, everything else becomes '_'.</summary>
    public static string Folder(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.Trim())
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        return sb.Length == 0 ? "planes" : sb.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// Maps the Site text to the folder to push to and the folder to pull from, case-insensitively:
    /// France pushes to "france" and pulls from "austria", Austria the reverse. Anything else (or empty) falls back to France and,
    /// when it was not empty, returns a warning. A legacy Remote Site (files saved by earlier versions), when not empty, overrides the
    /// pairing: Site is then the push folder as typed and Remote Site the pull folder.
    /// </summary>
    public static void Resolve(string? site, string? remoteSite, out string push, out string pull, out string? warning)
    {
        warning = null;
        var s = (site ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(remoteSite))
        {
            push = Folder(s.Length == 0 ? "france" : s);
            pull = Folder(remoteSite);
            return;
        }
        var l = s.ToLowerInvariant();
        if (l.Contains("austria")) { push = "austria"; pull = "france"; return; }
        if (l.Contains("france")) { push = "france"; pull = "austria"; return; }
        push = "france"; pull = "austria";
        if (s.Length > 0) warning = $"Site '{s}' is not France or Austria: using France.";
    }
}
