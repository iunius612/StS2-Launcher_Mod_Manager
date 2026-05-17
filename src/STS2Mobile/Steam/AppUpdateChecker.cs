using System;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

namespace STS2Mobile.Steam;

// Checks for launcher updates by comparing the installed version against the
// latest GitHub release. Returns the download URL if an update is available.
public static class AppUpdateChecker
{
    private const string ReleasesUrl =
        "https://api.github.com/repos/iunius612/StS2-Launcher_Mod_Manager/releases/latest";

    public static async Task<AppUpdateResult> CheckAsync()
    {
        var currentVersion = GetInstalledVersion();
        if (currentVersion == null)
            return AppUpdateResult.None;

        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Add("User-Agent", "StS2-Launcher");

        var response = await http.GetStringAsync(ReleasesUrl).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;

        // Prefer tag_name (always clean like "v0.3.3") over name (often
        // decorated like "v0.3.3 — Description") so version parsing doesn't
        // trip over the suffix.
        var releaseTag = root.TryGetProperty("tag_name", out var tagProp)
            ? tagProp.GetString()
            : null;
        var releaseName = root.TryGetProperty("name", out var nameProp)
            ? nameProp.GetString()
            : null;
        var rawLatest = !string.IsNullOrEmpty(releaseTag) ? releaseTag : releaseName;

        if (rawLatest == null)
            return AppUpdateResult.None;

        // Skip debug-tagged releases (e.g. v0.3.12-debug). These are uploaded
        // for in-house dialog/UI testing and should never trigger self-update
        // for end users. The debug tester reaches them via direct sideload.
        if (rawLatest.IndexOf("-debug", StringComparison.OrdinalIgnoreCase) >= 0)
            return AppUpdateResult.None;

        var latestVersion = NormalizeVersion(rawLatest);
        var installedVersion = NormalizeVersion(currentVersion);

        if (latestVersion == null || installedVersion == null)
            return AppUpdateResult.None;

        if (CompareVersions(latestVersion, installedVersion) <= 0)
            return AppUpdateResult.None;

        var releaseBody = root.TryGetProperty("body", out var bodyProp)
            ? bodyProp.GetString()
            : null;
        var releaseNotes = ReleaseNotes.ExtractDialogBody(releaseBody);

        string downloadUrl = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name != null && name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.TryGetProperty("browser_download_url", out var url)
                        ? url.GetString()
                        : null;
                    break;
                }
            }
        }

        return new AppUpdateResult(latestVersion, downloadUrl, releaseNotes);
    }

    private static string GetInstalledVersion()
    {
        try
        {
            var jcw = Engine.GetSingleton("JavaClassWrapper");
            var wrapper = (GodotObject)
                jcw.Call("wrap", "com.game.sts2launcher.modmanager.GodotApp");
            var godotApp = (GodotObject)wrapper.Call("getInstance");
            return (string)godotApp.Call("getVersionName");
        }
        catch
        {
            return null;
        }
    }

    // Returns the leading semver-like portion of the input ("v0.3.3 — foo" → "0.3.3").
    private static string NormalizeVersion(string version)
    {
        if (string.IsNullOrEmpty(version))
            return null;
        var trimmed = version.TrimStart('v', 'V').TrimStart();
        int len = 0;
        while (len < trimmed.Length && (char.IsDigit(trimmed[len]) || trimmed[len] == '.'))
            len++;
        var head = trimmed.Substring(0, len).TrimEnd('.');
        return head.Length == 0 ? null : head;
    }

    private static int CompareVersions(string a, string b)
    {
        var aParts = a.Split('.');
        var bParts = b.Split('.');
        var len = Math.Max(aParts.Length, bParts.Length);

        for (int i = 0; i < len; i++)
        {
            int aVal = i < aParts.Length && int.TryParse(aParts[i], out var av) ? av : 0;
            int bVal = i < bParts.Length && int.TryParse(bParts[i], out var bv) ? bv : 0;
            if (aVal != bVal)
                return aVal - bVal;
        }

        return 0;
    }
}

public class AppUpdateResult
{
    public static readonly AppUpdateResult None = new(null, null, null);

    public string LatestVersion { get; }
    public string DownloadUrl { get; }
    public string ReleaseNotes { get; }
    public bool HasUpdate => LatestVersion != null;

    public AppUpdateResult(string latestVersion, string downloadUrl, string releaseNotes)
    {
        LatestVersion = latestVersion;
        DownloadUrl = downloadUrl;
        ReleaseNotes = releaseNotes;
    }
}
