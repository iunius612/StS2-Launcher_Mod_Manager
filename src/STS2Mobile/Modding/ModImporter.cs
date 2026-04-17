using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Godot;

namespace STS2Mobile.Modding;

// Converts a user-supplied zip into a normalized mod folder under
// AppPaths.ExternalModsDir. Handles the two common zip layouts (manifest at root,
// or one level deep inside a wrapping folder) to match how mods are distributed
// on Nexus/GitHub.
public static class ModImporter
{
    public class ImportResult
    {
        public bool Success;
        public string ModId;
        public string Error;
        public bool AlreadyExists;
    }

    public static Task<ImportResult> ImportZipAsync(string zipPath, bool overwrite) =>
        Task.Run(() => ImportZip(zipPath, overwrite));

    private static ImportResult ImportZip(string zipPath, bool overwrite)
    {
        var tempRoot = Path.Combine(
            OS.GetCacheDir(),
            "mod_import_" + Guid.NewGuid().ToString("N")
        );
        // Keep the cached zip around when the caller needs to retry with overwrite;
        // otherwise delete it so we don't leak zips in /cache.
        bool keepZip = false;
        try
        {
            Directory.CreateDirectory(tempRoot);
            SafeExtract(zipPath, tempRoot);

            var modRoot = FindModRoot(tempRoot);
            if (modRoot == null)
                return Fail("Selected zip is not a StS2 mod (mod_manifest.json not found).");

            var manifest = ModManifest.TryParse(Path.Combine(modRoot, "mod_manifest.json"));
            if (manifest == null || !manifest.IsValid())
                return Fail("mod_manifest.json is missing or has no 'id' field.");

            if (!IsValidId(manifest.Id))
                return Fail($"Invalid mod id: '{manifest.Id}'");

            Directory.CreateDirectory(AppPaths.ExternalModsDir);
            var dest = Path.Combine(AppPaths.ExternalModsDir, manifest.Id);
            if (Directory.Exists(dest))
            {
                if (!overwrite)
                {
                    keepZip = true;
                    return new ImportResult
                    {
                        Success = false,
                        ModId = manifest.Id,
                        AlreadyExists = true,
                    };
                }
                Directory.Delete(dest, recursive: true);
            }

            CopyDirectory(modRoot, dest);

            var cfg = ModConfig.Load();
            cfg.Add(manifest.Id, enabled: true);
            cfg.Save();

            return new ImportResult { Success = true, ModId = manifest.Id };
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Mods] Import failed: {ex}");
            return Fail("Import failed: " + ex.Message);
        }
        finally
        {
            TryDeleteDir(tempRoot);
            if (!keepZip)
                TryDeleteFile(zipPath);
        }
    }

    public static void CleanupImportZip(string zipPath) => TryDeleteFile(zipPath);

    private static string FindModRoot(string tempRoot)
    {
        if (File.Exists(Path.Combine(tempRoot, "mod_manifest.json")))
            return tempRoot;

        var subdirs = Directory.GetDirectories(tempRoot);
        if (subdirs.Length == 1 && File.Exists(Path.Combine(subdirs[0], "mod_manifest.json")))
            return subdirs[0];

        foreach (
            var path in Directory.EnumerateFiles(
                tempRoot,
                "mod_manifest.json",
                SearchOption.AllDirectories
            )
        )
            return Path.GetDirectoryName(path);

        return null;
    }

    // Extracts with Zip Slip protection — any entry whose resolved path escapes
    // the destination root is rejected.
    private static void SafeExtract(string zipPath, string destRoot)
    {
        var fullRoot = Path.GetFullPath(destRoot);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(fullRoot, entry.FullName));
            AssertWithin(fullRoot, target);

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void AssertWithin(string root, string target)
    {
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar.ToString())
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!target.StartsWith(rootWithSep, StringComparison.Ordinal) && target != root)
            throw new InvalidOperationException("Zip entry escapes extraction root: " + target);
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(src))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var sub in Directory.EnumerateDirectories(src))
            CopyDirectory(sub, Path.Combine(dest, Path.GetFileName(sub)));
    }

    private static bool IsValidId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;
        foreach (var c in id)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.'))
                return false;
        }
        return true;
    }

    public static bool DeleteMod(string modId)
    {
        if (!IsValidId(modId))
            return false;
        try
        {
            var dir = Path.Combine(AppPaths.ExternalModsDir, modId);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);

            var cfg = ModConfig.Load();
            cfg.Remove(modId);
            cfg.Save();
            return true;
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Mods] DeleteMod failed: {ex}");
            return false;
        }
    }

    private static ImportResult Fail(string error) => new() { Success = false, Error = error };

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }
}
