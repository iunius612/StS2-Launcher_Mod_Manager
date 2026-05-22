using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace STS2Mobile.Steam;

// Stateless cloud sync coordinator: auto sync and manual push/pull.
//
// Issue #36 Part A redesign: per-sync victim backups were REMOVED from this class.
// Backups are now full-tree snapshots owned by LocalBackupService — taken once per
// pre-PLAY handshake (auto) or on the user's action button (manual), not on every
// push/pull/autosync. The old LocalBackupEnabled gate and Begin/EndBackupSession
// machinery are gone with them.
public static class CloudSyncCoordinator
{
    private const int HistoryFileLimit = 100;

    public static async Task PushFileAsync(ISaveStore local, ICloudSaveStore cloud, string path)
    {
        if (!local.FileExists(path))
            return;

        string content = local.ReadFile(path);

        if (cloud.FileExists(path))
        {
            string cloudContent = await cloud.ReadFileAsync(path);
            if (content == cloudContent)
            {
                PatchHelper.Log($"[Cloud] Push: skipping {path} (identical)");
                return;
            }
        }

        cloud.WriteFile(path, content);
        PatchHelper.Log($"[Cloud] Push: uploaded {path}");
    }

    public static async Task PullFileAsync(ISaveStore local, ICloudSaveStore cloud, string path)
    {
        if (!cloud.FileExists(path))
            return;

        string cloudContent = await cloud.ReadFileAsync(path);

        if (local.FileExists(path))
        {
            string localContent = local.ReadFile(path);
            if (localContent == cloudContent)
            {
                PatchHelper.Log($"[Cloud] Pull: skipping {path} (identical)");
                return;
            }
        }

        var pullTime = cloud.GetLastModifiedTime(path);
        await local.WriteFileAsync(path, cloudContent);
        local.SetLastModifiedTime(path, pullTime);
        PatchHelper.Log($"[Cloud] Pull: downloaded {path}");
    }

    // Uses content comparison only — timestamps are unreliable on mobile (game init
    // rewrites files, OS touches metadata). Progress/run files use SaveProgressComparer;
    // non-progress files default to cloud wins; history files sync bidirectionally.
    public static async Task AutoSyncFileAsync(ISaveStore local, ICloudSaveStore cloud, string path)
    {
        try
        {
            bool cloudExists = cloud.FileExists(path);
            bool localExists = local.FileExists(path);

            if (cloudExists && localExists)
            {
                string localContent = local.ReadFile(path);
                string cloudContent = await cloud.ReadFileAsync(path);

                if (IsCorrupt(localContent))
                {
                    PatchHelper.Log($"[Cloud] Sync: local {path} is corrupt, pulling from cloud");
                    Issue7Diagnostics.LogIsCorruptDetected(path, localContent);
                    var cloudTime = cloud.GetLastModifiedTime(path);
                    await local.WriteFileAsync(path, cloudContent);
                    local.SetLastModifiedTime(path, cloudTime);
                    return;
                }

                if (localContent == cloudContent)
                {
                    PatchHelper.Log($"[Cloud] Sync: {path} identical, skipping");
                    return;
                }

                var result = SaveProgressComparer.Compare(path, localContent, cloudContent);

                if (result == CompareResult.CloudWins)
                {
                    PatchHelper.Log($"[Cloud] Sync: cloud wins for {path}");
                    Issue7Diagnostics.LogCurrentRunSyncDetail(
                        path,
                        localContent,
                        cloudContent,
                        "CloudWins"
                    );
                    var cloudTime = cloud.GetLastModifiedTime(path);
                    await local.WriteFileAsync(path, cloudContent);
                    local.SetLastModifiedTime(path, cloudTime);
                }
                else if (result == CompareResult.LocalWins)
                {
                    PatchHelper.Log($"[Cloud] Sync: local wins for {path}, uploading");
                    Issue7Diagnostics.LogCurrentRunSyncDetail(
                        path,
                        localContent,
                        cloudContent,
                        "LocalWins"
                    );
                    cloud.WriteFile(path, localContent);
                }
                else
                {
                    // Cloud wins on equal progress or non-progress files to preserve PC as primary.
                    PatchHelper.Log($"[Cloud] Sync: contents differ for {path}, cloud wins");
                    Issue7Diagnostics.LogCurrentRunSyncDetail(
                        path,
                        localContent,
                        cloudContent,
                        "EqualOrNonProgress→CloudWins"
                    );
                    var cloudTime = cloud.GetLastModifiedTime(path);
                    await local.WriteFileAsync(path, cloudContent);
                    local.SetLastModifiedTime(path, cloudTime);
                }
            }
            else if (cloudExists)
            {
                Issue7Diagnostics.LogCurrentRunSyncDetail(path, null, null, "CloudOnly→Pull");
                await PullFileAsync(local, cloud, path);
            }
            else if (localExists)
            {
                Issue7Diagnostics.LogCurrentRunSyncDetail(path, null, null, "LocalOnly→Push");
                await PushFileAsync(local, cloud, path);
            }
            // (neither exists — no-op)
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Cloud] Sync failed for {path}: {ex.Message}");
        }
    }

    // Returns Task (not async): the per-sync cloud backup loop that needed an await
    // was removed in the Part A redesign, so the body is now fully synchronous.
    public static Task ManualPushAllAsync(string accountName, string refreshToken)
    {
        var localStore = new GodotFileIo(UserDataPathProvider.GetAccountScopedBasePath(null));
        var cloudStore =
            SteamKit2CloudSaveStore.Instance
            ?? new SteamKit2CloudSaveStore(accountName, refreshToken);

        var paths = GetSaveFilePaths(localStore);
        PatchHelper.Log($"[Cloud] Push: starting ({paths.Count} files)");

        cloudStore.BeginSaveBatch();
        int count = 0;
        int deletedCloud = 0;
        foreach (var path in paths)
        {
            try
            {
                if (!localStore.FileExists(path))
                {
                    if (IsEphemeralRunFile(path) && cloudStore.FileExists(path))
                    {
                        cloudStore.DeleteFile(path);
                        PatchHelper.Log($"[Cloud] Push: deleted cloud {path} (local cleared run)");
                        deletedCloud++;
                    }
                    continue;
                }

                string content = localStore.ReadFile(path);
                PatchHelper.Log($"[Cloud] Push: queuing {path} ({content.Length} bytes)");
                cloudStore.WriteFile(path, content);
                count++;
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"[Cloud] Push: failed for {path}: {ex.Message}");
            }
        }
        cloudStore.EndSaveBatch();

        PatchHelper.Log(
            $"[Cloud] Push complete: {count} files batched for upload, {deletedCloud} cloud files mirror-deleted"
        );
        return Task.CompletedTask;
    }

    public static async Task ManualPullAllAsync(string accountName, string refreshToken)
    {
        var localStore = new GodotFileIo(UserDataPathProvider.GetAccountScopedBasePath(null));
        var cloudStore =
            SteamKit2CloudSaveStore.Instance
            ?? new SteamKit2CloudSaveStore(accountName, refreshToken);

        var paths = GetSaveFilePaths(cloudStore);
        PatchHelper.Log($"[Cloud] Pull: starting ({paths.Count} files)");

        int downloaded = 0;
        int skipped = 0;
        int deletedLocal = 0;
        foreach (var path in paths)
        {
            try
            {
                if (!cloudStore.FileExists(path))
                {
                    if (IsEphemeralRunFile(path) && localStore.FileExists(path))
                    {
                        DeleteEphemeralLocalWithBackup(localStore, path);
                        PatchHelper.Log($"[Cloud] Pull: deleted local {path} (cloud cleared run)");
                        deletedLocal++;
                    }
                    else
                    {
                        skipped++;
                    }
                    continue;
                }
                PatchHelper.Log($"[Cloud] Pull: downloading {path}");
                var pullTime = cloudStore.GetLastModifiedTime(path);
                string content = await cloudStore.ReadFileAsync(path);
                await localStore.WriteFileAsync(path, content);
                localStore.SetLastModifiedTime(path, pullTime);
                PatchHelper.Log($"[Cloud] Pull: wrote {path} ({content.Length} bytes)");
                downloaded++;
            }
            catch (Exception ex)
            {
                // Issue #31: stale-cache fallback. Steam's EnumerateUserFiles RPC
                // keeps remotely-deleted files in the manifest for a while after
                // the actual storage is wiped, so cloudStore.FileExists can return
                // true while ClientFileDownload returns FileNotFound. The download
                // failure is the authoritative signal that cloud is empty — mirror
                // that locally for ephemeral run files.
                if (
                    IsEphemeralRunFile(path)
                    && ex.Message.Contains("FileNotFound", StringComparison.OrdinalIgnoreCase)
                    && localStore.FileExists(path)
                )
                {
                    try
                    {
                        DeleteEphemeralLocalWithBackup(localStore, path);
                        deletedLocal++;
                        PatchHelper.Log(
                            $"[Cloud] Pull: deleted local {path} (cloud stale-cache, actually gone)"
                        );
                    }
                    catch (Exception delEx)
                    {
                        PatchHelper.Log(
                            $"[Cloud] Pull: stale-cache delete failed for {path}: {delEx.Message}"
                        );
                    }
                }
                else
                {
                    PatchHelper.Log($"[Cloud] Pull: failed for {path}: {ex.Message}");
                }
            }
        }

        PatchHelper.Log(
            $"[Cloud] Pull complete: {downloaded} downloaded, {skipped} not in cloud, {deletedLocal} local files mirror-deleted"
        );
    }

    public static List<string> GetSaveFilePaths(ISaveStore store)
    {
        var paths = new List<string>();
        CollectProfilePaths(paths, store.GetFilesInDirectory, store.DirectoryExists);
        return paths;
    }

    public static List<string> GetSaveFilePaths(ICloudSaveStore store)
    {
        var paths = new List<string>();
        CollectProfilePaths(paths, store.GetFilesInDirectory, store.DirectoryExists);
        return paths;
    }

    // Collects save paths for both vanilla and modded profile directories.
    private static void CollectProfilePaths(
        List<string> paths,
        Func<string, string[]> getFiles,
        Func<string, bool> dirExists
    )
    {
        var wasModded = UserDataPathProvider.IsRunningModded;
        try
        {
            foreach (bool modded in new[] { false, true })
            {
                UserDataPathProvider.IsRunningModded = modded;
                for (int i = 1; i <= 3; i++)
                {
                    paths.Add(ProgressSaveManager.GetProgressPathForProfile(i));
                    paths.Add(RunSaveManager.GetRunSavePath(i, "current_run.save"));
                    paths.Add(RunSaveManager.GetRunSavePath(i, "current_run_mp.save"));
                    paths.Add(PrefsSaveManager.GetPrefsPath(i));
                    AddHistoryFiles(paths, getFiles, dirExists, i);
                }
            }
        }
        finally
        {
            UserDataPathProvider.IsRunningModded = wasModded;
        }
    }

    private static void AddHistoryFiles(
        List<string> paths,
        Func<string, string[]> getFiles,
        Func<string, bool> dirExists,
        int profileId
    )
    {
        var historyDir = RunHistorySaveManager.GetHistoryPath(profileId);
        if (!dirExists(historyDir))
            return;

        var runFiles = getFiles(historyDir)
            .Where(f => f.EndsWith(".run") && !f.EndsWith(".backup") && !f.EndsWith(".tmp"))
            .OrderByDescending(f => f) // Filenames are Unix timestamps — descending = newest first
            .Take(HistoryFileLimit);

        foreach (var file in runFiles)
            paths.Add($"{historyDir}/{file}");
    }

    // Save files are JSON; a non-JSON opener indicates corruption (e.g., unencrypted write).
    private static bool IsCorrupt(string content)
    {
        if (string.IsNullOrEmpty(content))
            return false;
        return content[0] != '{' && content[0] != '[';
    }

    // Issue #31: ephemeral per-run save files. The game deletes these from cloud
    // when a run ends (clear/abandon) — manual Pull/Push must mirror that deletion
    // to the other side so completed runs don't reappear as "Continue" zombies.
    // progress.save is intentionally excluded: it's persistent meta progress and
    // mirror-deleting it would risk catastrophic data loss on fresh-install pushes.
    internal static bool IsEphemeralRunFile(string path)
    {
        var lower = path.Replace("user://", "").Replace("\\", "/").ToLowerInvariant();
        return lower.EndsWith("/current_run.save") || lower.EndsWith("/current_run_mp.save");
    }

    // RunSaveManager keeps a .backup sibling per save and falls back to it when
    // the primary is missing. Mirror-deleting the primary alone leaves the game
    // restoring the run from the backup — we must remove both.
    internal static void DeleteEphemeralLocalWithBackup(ISaveStore local, string path)
    {
        local.DeleteFile(path);
        var backupPath = path + ".backup";
        if (local.FileExists(backupPath))
        {
            try
            {
                local.DeleteFile(backupPath);
                PatchHelper.Log($"[Cloud] Mirror-delete: also removed local {backupPath}");
            }
            catch (Exception ex)
            {
                PatchHelper.Log(
                    $"[Cloud] Mirror-delete: backup removal failed for {backupPath}: {ex.Message}"
                );
            }
        }
    }

}
