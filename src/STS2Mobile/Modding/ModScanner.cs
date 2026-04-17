using System.Collections.Generic;
using System.IO;

namespace STS2Mobile.Modding;

// Walks AppPaths.ExternalModsDir and returns one ModEntryInfo per subfolder that
// contains a parseable mod_manifest.json with a non-empty id.
public static class ModScanner
{
    public static List<ModEntryInfo> Scan()
    {
        var results = new List<ModEntryInfo>();
        if (!Directory.Exists(AppPaths.ExternalModsDir))
            return results;

        foreach (var dir in Directory.EnumerateDirectories(AppPaths.ExternalModsDir))
        {
            var manifestPath = Path.Combine(dir, "mod_manifest.json");
            if (!File.Exists(manifestPath))
                continue;

            var manifest = ModManifest.TryParse(manifestPath);
            if (manifest == null || !manifest.IsValid())
                continue;

            results.Add(
                new ModEntryInfo
                {
                    Path = dir,
                    Manifest = manifest,
                    ReadmeSnippet = LoadReadmeSnippet(dir),
                }
            );
        }

        return results;
    }

    private static string LoadReadmeSnippet(string modDir)
    {
        foreach (var name in new[] { "README.md", "readme.md", "README.txt", "readme.txt" })
        {
            var path = Path.Combine(modDir, name);
            if (!File.Exists(path))
                continue;
            try
            {
                var text = File.ReadAllText(path).Trim();
                return text.Length > 300 ? text.Substring(0, 300) + "..." : text;
            }
            catch { }
        }
        return null;
    }
}
