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
        "https://api.github.com/repos/Ekyso/StS2-Launcher/releases/latest";

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

        var releaseName = root.TryGetProperty("name", out var nameProp)
            ? nameProp.GetString()
            : null;

        if (releaseName == null)
            return AppUpdateResult.None;

        var latestVersion = NormalizeVersion(releaseName);
        var installedVersion = NormalizeVersion(currentVersion);

        if (latestVersion == null || installedVersion == null)
            return AppUpdateResult.None;

        if (CompareVersions(latestVersion, installedVersion) <= 0)
            return AppUpdateResult.None;

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

        return new AppUpdateResult(latestVersion, downloadUrl);
    }

    private static string GetInstalledVersion()
    {
        try
        {
            var jcw = Engine.GetSingleton("JavaClassWrapper");
            var wrapper = (GodotObject)jcw.Call("wrap", "com.game.sts2launcher.GodotApp");
            var godotApp = (GodotObject)wrapper.Call("getInstance");
            return (string)godotApp.Call("getVersionName");
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeVersion(string version)
    {
        if (string.IsNullOrEmpty(version))
            return null;
        return version.TrimStart('v', 'V');
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
    public static readonly AppUpdateResult None = new(null, null);

    public string LatestVersion { get; }
    public string DownloadUrl { get; }
    public bool HasUpdate => LatestVersion != null;

    public AppUpdateResult(string latestVersion, string downloadUrl)
    {
        LatestVersion = latestVersion;
        DownloadUrl = downloadUrl;
    }
}
