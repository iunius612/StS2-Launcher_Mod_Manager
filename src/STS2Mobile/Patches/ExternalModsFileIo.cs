using System;
using System.IO;
using MegaCrit.Sts2.Core.Modding;
using GodotFileAccess = Godot.FileAccess;

namespace STS2Mobile.Patches;

// Wraps the IModManagerFileIo the game hands ModManager.Initialize so any path
// pointing into the executable-adjacent "mods" directory is transparently
// redirected to AppPaths.ExternalModsDir (the launcher's external storage
// folder, e.g. /storage/emulated/0/StS2LauncherMM/Mods). All other paths are
// delegated to the original implementation so non-"mods" access (e.g. future
// steam/workshop probes) still works.
//
// This replaces the previous ldstr "mods" transpiler in ModLoaderPatches: as of
// sts2 v0.107.0, ModManager.Initialize is async (Task) so the compiler hoists
// the Path.Combine(..., "mods") call into a generated state-machine MoveNext
// and the main-body transpiler can no longer find the ldstr. Swapping the
// fileIo argument via prefix is signature-stable across that lowering.
public sealed class ExternalModsFileIo : IModManagerFileIo
{
    private readonly string _externalRoot;
    private readonly IModManagerFileIo _inner;

    public ExternalModsFileIo(string externalRoot, IModManagerFileIo inner)
    {
        _externalRoot = externalRoot ?? throw new ArgumentNullException(nameof(externalRoot));
        _inner = inner;
    }

    // Returns the rewritten external-storage path if `path` targets the game's
    // "mods" directory or anything beneath it; otherwise null (delegate to inner).
    // Matching is purely suffix-based on the final "mods" segment so we don't
    // care what the game's exe directory happens to be.
    private string TryRedirect(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        var normalized = path.Replace('\\', '/');
        const string needle = "/mods";
        var idx = normalized.LastIndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            // Bare "mods" with no parent separator — treat as the root itself.
            if (normalized.Equals("mods", StringComparison.OrdinalIgnoreCase))
                return _externalRoot;
            return null;
        }

        // Must be either the trailing segment or followed by a path separator,
        // so we don't accidentally redirect "/modsetting" or similar.
        var tail = normalized.Substring(idx + needle.Length);
        if (tail.Length > 0 && tail[0] != '/')
            return null;

        return tail.Length == 0 ? _externalRoot : _externalRoot + tail;
    }

    public string[] GetFilesAt(string path)
    {
        var redirected = TryRedirect(path);
        if (redirected == null)
            return _inner?.GetFilesAt(path) ?? Array.Empty<string>();
        try
        {
            return Directory.Exists(redirected)
                ? Directory.GetFiles(redirected)
                : Array.Empty<string>();
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Mods] GetFilesAt({redirected}) failed: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    public string[] GetDirectoriesAt(string path)
    {
        var redirected = TryRedirect(path);
        if (redirected == null)
            return _inner?.GetDirectoriesAt(path) ?? Array.Empty<string>();
        try
        {
            return Directory.Exists(redirected)
                ? Directory.GetDirectories(redirected)
                : Array.Empty<string>();
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Mods] GetDirectoriesAt({redirected}) failed: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    public bool FileExists(string path)
    {
        var redirected = TryRedirect(path);
        if (redirected == null)
            return _inner != null && _inner.FileExists(path);
        try
        {
            return File.Exists(redirected);
        }
        catch
        {
            return false;
        }
    }

    public bool DirectoryExists(string path)
    {
        var redirected = TryRedirect(path);
        if (redirected == null)
            return _inner != null && _inner.DirectoryExists(path);
        try
        {
            return Directory.Exists(redirected);
        }
        catch
        {
            return false;
        }
    }

    public Stream OpenStream(string path, GodotFileAccess.ModeFlags mode)
    {
        var redirected = TryRedirect(path);
        if (redirected == null)
            return _inner?.OpenStream(path, mode);

        // Map Godot ModeFlags to standard FileMode/FileAccess. Godot's flags are
        // Read=1, Write=2, ReadWrite=3, WriteRead=7 — only Read/Write matter for
        // mod file ingestion (manifest read, asset open). Default to Read.
        FileMode fileMode;
        FileAccess fileAccess;
        switch (mode)
        {
            case GodotFileAccess.ModeFlags.Write:
                fileMode = FileMode.Create;
                fileAccess = FileAccess.Write;
                break;
            case GodotFileAccess.ModeFlags.ReadWrite:
                fileMode = FileMode.OpenOrCreate;
                fileAccess = FileAccess.ReadWrite;
                break;
            case GodotFileAccess.ModeFlags.WriteRead:
                fileMode = FileMode.Create;
                fileAccess = FileAccess.ReadWrite;
                break;
            case GodotFileAccess.ModeFlags.Read:
            default:
                fileMode = FileMode.Open;
                fileAccess = FileAccess.Read;
                break;
        }

        try
        {
            var dir = Path.GetDirectoryName(redirected);
            if (!string.IsNullOrEmpty(dir) && fileAccess != FileAccess.Read)
                Directory.CreateDirectory(dir);
            return new FileStream(redirected, fileMode, fileAccess, FileShare.Read);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Mods] OpenStream({redirected}, {mode}) failed: {ex.Message}");
            return null;
        }
    }
}
