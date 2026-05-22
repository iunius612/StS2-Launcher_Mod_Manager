using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace STS2Mobile.Steam;

// Issue #36 Part A (redesign) — Local Backup as full-tree snapshots, segregated
// by origin. Replaces the old per-sync victim backups (which copied one file at a
// time on each push/pull/autosync — far too often). Now a "backup" is one snapshot
// of the entire save tree, taken at most once per launch (pre-PLAY handshake) or
// on the user's manual action button.
//
//   Saves/manual/<ts>/<tree>           — user pressed Local Backup (BackupNow).
//   Saves/auto/<ts>_match/<tree>       — handshake: both sides agreed.
//   Saves/auto/<ts>_conflict/kept/<tree>      — handshake conflict: resolved local.
//   Saves/auto/<ts>_conflict/discarded/<tree> — handshake conflict: thrown-away side.
//
// A snapshot walks every save path (CloudSyncCoordinator.GetSaveFilePaths over all
// profiles × modded layouts) and copies each non-empty file, preserving the real
// on-disk tree steam/{userId}/[modded/]profile{N}/saves/{file}. Empty/trivial files
// are skipped so a snapshot is never an all-empty folder.
//
// No on/off gate (the UI toggle is gone — GAME replaced it with an action button):
// backups always run when storage permission is granted. Work runs on a background
// thread so it never blocks the game/launcher main thread.
public static class LocalBackupService
{
    private const string Tag = "[Issue36-Backup]";

    // Files at/below this byte count aren't real saves — don't copy them (so a
    // snapshot folder is never all-empty) and don't count them.
    private const int TrivialBytes = 2;

    // Newest N auto handshake sets kept; older auto sets evicted FIFO. Manual sets
    // are never auto-evicted (a deliberate backup must be durable).
    private const int MaxAutoSets = 10;

    // Fixed contract consumed by GAME (LauncherController.OnLocalBackupPressed).
    // FROZEN contract — aligned to GAME's live consumer (LauncherController:841,
    // 866-871) for fastest convergence per team-lead's "GAME stays, you align"
    // directive. Synchronous BackupNow() + this field set. DO NOT change this API
    // surface again without the lead's explicit instruction.
    // NOTE: the lead's frozen *message* pasted an async `BackupNowAsync()/SetDir`
    // block, but GAME's actual code calls sync `BackupNow()` and reads
    // TotalBytes/DestPath/NeedsPermission — those two disagree. Matching GAME's live
    // code is the only thing that makes the build green and honors "GAME은 이미 A".
    // Flagged to the lead to confirm/correct if the async block was intentional.
    // Mutable public fields by design — a plain DTO unpacked by GAME.
    public class BackupResult
    {
        public bool Success;
        public int FileCount;
        public long TotalBytes; // total bytes copied
        public string DestPath; // e.g. .../Saves/manual/20260522_223015
        public string Error; // failure reason, null on success
        public bool NeedsPermission; // true when blocked by missing storage permission
    }

    // ---- Manual (action button) -------------------------------------------------

    // Snapshots the current LOCAL save tree into Saves/manual/<ts>. Synchronous and
    // background-safe: GAME calls it inside its own Task.Run and shows the returned
    // BackupResult in a modal. No credentials needed (account-scoped local path).
    // Manual sets are FIFO-protected, so nothing is pruned here. Missing storage
    // permission → Success=false, NeedsPermission=true (GAME prompts/guides).
    public static BackupResult BackupNow()
    {
        try
        {
            if (!AppPaths.HasStoragePermission())
                return new BackupResult
                {
                    Success = false,
                    NeedsPermission = true,
                    Error = "저장소 권한이 없습니다.",
                };

            var local = new GodotFileIo(UserDataPathProvider.GetAccountScopedBasePath(null));
            var setDir = Path.Combine(AppPaths.ExternalManualBackupsDir, MakeTimestamp());
            var (count, bytes) = SnapshotLocalTree(local, setDir);

            if (count == 0)
            {
                TryDeleteDir(setDir); // no non-empty saves — don't leave an empty folder
                PatchHelper.Log($"{Tag} manual: no non-empty saves found, skipped");
                return new BackupResult
                {
                    Success = false,
                    Error = "백업할 세이브가 없습니다.",
                };
            }

            PatchHelper.Log($"{Tag} manual OK: {count} files, {bytes}B → {setDir}");
            return new BackupResult
            {
                Success = true,
                FileCount = count,
                TotalBytes = bytes,
                DestPath = setDir,
            };
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"{Tag} manual FAIL: {ex.Message}");
            return new BackupResult { Success = false, Error = ex.Message };
        }
    }

    // ---- Auto (pre-PLAY handshake) ---------------------------------------------

    // match: both sides agree. Snapshot the local tree once into Saves/auto/<ts>_match.
    // Fire-and-forget on a background thread — nothing is being discarded, so the
    // handshake needn't wait.
    public static void BackupHandshakeMatch(ISaveStore local)
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (!AppPaths.HasStoragePermission())
                    return;

                var setDir = Path.Combine(
                    AppPaths.ExternalAutoBackupsDir,
                    MakeTimestamp() + "_match"
                );
                var (count, bytes) = SnapshotLocalTree(local, setDir);
                if (count == 0)
                {
                    TryDeleteDir(setDir);
                    return;
                }
                PatchHelper.Log($"{Tag} auto-match OK: {count} files, {bytes}B → {setDir}");
                PruneAutoSets();
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"{Tag} auto-match FAIL: {ex.Message}");
            }
        });
    }

    // conflict — captured in TWO phases around the user's resolution:
    //   1. BEFORE apply: snapshot the DISCARDED side (the old state about to be
    //      thrown away) into auto/<ts>_conflict/discarded. keepLocal → discarded is
    //      the old cloud content; keepCloud → discarded is the old local content.
    //   2. AFTER apply: snapshot the KEPT side = the resolved LOCAL tree into
    //      auto/<ts>_conflict/kept (after ApplyChosenSide, local holds whichever
    //      side won).
    // BeginConflictBackupAsync returns a handle that ties both phases to one
    // <ts>_conflict folder. The handshake awaits phase 1 (so the discarded data is
    // on disk before it's overwritten) and fires phase 2 after apply.
    //
    // Efficiency (lead's note): the discarded-cloud capture reads only the known
    // save-file paths (GetSaveFilePaths), not a blind full-cloud re-download. Cloud
    // reads still go over the network, but they're scoped to the save set the
    // handshake already deals with.
    public sealed class ConflictBackupHandle
    {
        internal string SetRoot;
        internal bool Enabled;
    }

    // Phase 1 — discarded side, awaited before ApplyChosenSide overwrites it.
    public static async Task<ConflictBackupHandle> BackupConflictDiscardedAsync(
        ISaveStore local,
        ICloudSaveStore cloud,
        bool keepLocal
    )
    {
        var handle = new ConflictBackupHandle();
        try
        {
            if (!AppPaths.HasStoragePermission())
                return handle;

            handle.SetRoot = Path.Combine(
                AppPaths.ExternalAutoBackupsDir,
                MakeTimestamp() + "_conflict"
            );
            handle.Enabled = true;

            var discardedDir = Path.Combine(handle.SetRoot, "discarded");
            int count;
            long bytes;
            if (keepLocal)
                (count, bytes) = await SnapshotCloudTreeAsync(cloud, discardedDir)
                    .ConfigureAwait(false); // discarding cloud
            else
                (count, bytes) = SnapshotLocalTree(local, discardedDir); // discarding local

            PatchHelper.Log(
                $"{Tag} auto-conflict discarded ({(keepLocal ? "cloud" : "local")}): "
                    + $"{count} files, {bytes}B → {discardedDir}"
            );
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"{Tag} auto-conflict discarded FAIL: {ex.Message}");
        }
        return handle;
    }

    // Phase 2 — kept side = resolved LOCAL tree, after ApplyChosenSide. Background.
    public static void BackupConflictKept(ConflictBackupHandle handle, ISaveStore local)
    {
        if (handle == null || !handle.Enabled || string.IsNullOrEmpty(handle.SetRoot))
            return;

        _ = Task.Run(() =>
        {
            try
            {
                var keptDir = Path.Combine(handle.SetRoot, "kept");
                var (count, bytes) = SnapshotLocalTree(local, keptDir);
                PatchHelper.Log(
                    $"{Tag} auto-conflict kept (resolved local): {count} files, {bytes}B → {keptDir}"
                );

                // If both phases ended up empty (no real saves anywhere), drop the
                // whole conflict set so an empty folder doesn't occupy a FIFO slot.
                if (IsSetEmpty(handle.SetRoot))
                {
                    TryDeleteDir(handle.SetRoot);
                    return;
                }
                PruneAutoSets();
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"{Tag} auto-conflict kept FAIL: {ex.Message}");
            }
        });
    }

    // ---- snapshot core ----------------------------------------------------------

    // Copies every non-empty local save file into setDir, mirroring its full
    // account-scoped relative tree. Returns (file count, total bytes).
    private static (int count, long bytes) SnapshotLocalTree(ISaveStore local, string setDir)
    {
        int count = 0;
        long bytes = 0;
        foreach (var path in CloudSyncCoordinator.GetSaveFilePaths(local))
        {
            try
            {
                if (!local.FileExists(path))
                    continue;
                var content = local.ReadFile(path);
                if (WriteSnapshotFile(setDir, path, content))
                {
                    count++;
                    bytes += Encoding.UTF8.GetByteCount(content);
                }
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"{Tag} snapshot(local) skip {path}: {ex.Message}");
            }
        }
        return (count, bytes);
    }

    // Cloud variant — reads are async (network), scoped to the known save paths.
    private static async Task<(int count, long bytes)> SnapshotCloudTreeAsync(
        ICloudSaveStore cloud,
        string setDir
    )
    {
        int count = 0;
        long bytes = 0;
        foreach (var path in CloudSyncCoordinator.GetSaveFilePaths(cloud))
        {
            try
            {
                if (!cloud.FileExists(path))
                    continue;
                var content = await cloud.ReadFileAsync(path).ConfigureAwait(false);
                if (WriteSnapshotFile(setDir, path, content))
                {
                    count++;
                    bytes += Encoding.UTF8.GetByteCount(content);
                }
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"{Tag} snapshot(cloud) skip {path}: {ex.Message}");
            }
        }
        return (count, bytes);
    }

    // Writes one snapshot file. Skips empty/trivial content so a set never contains
    // all-empty folders. Normalizes to the full account-scoped user:// tree first.
    private static bool WriteSnapshotFile(string setDir, string path, string content)
    {
        if (string.IsNullOrEmpty(content) || content.Length <= TrivialBytes)
            return false;

        var fullPath = NormalizeToFullPath(path);
        var backupPath = AppPaths.BuildBackupPath(setDir, fullPath);
        var dir = Path.GetDirectoryName(backupPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(backupPath, content);
        return true;
    }

    // Mirrors GodotFileIo.GetFullPath: resolves a profile-relative save path to the
    // full account-scoped user:// form (steam/{userId}/...). The store hands us
    // profile-relative paths; without this the steam/{userId}/ scope is lost and
    // backups from different accounts collide. Falls back to the raw path on error.
    private static string NormalizeToFullPath(string path)
    {
        try
        {
            if (path.StartsWith("user://"))
                return path;
            var baseDir = UserDataPathProvider.GetAccountScopedBasePath(null);
            if (string.IsNullOrEmpty(baseDir) || path.StartsWith(baseDir))
                return path;
            return baseDir + "/" + path;
        }
        catch
        {
            return path;
        }
    }

    // ---- FIFO retention ---------------------------------------------------------

    // Keeps the newest MaxAutoSets sets under Saves/auto, deleting older ones. A
    // "set" is a top-level child of auto/ (a <ts>_match or <ts>_conflict folder;
    // conflict's kept+discarded count as ONE set). manual/ is never touched
    // (FIFO-protected). Names start with yyyyMMdd_HHmmss so ordinal == chronological.
    private static void PruneAutoSets()
    {
        try
        {
            if (!Directory.Exists(AppPaths.ExternalAutoBackupsDir))
                return;

            var sets = Directory.GetDirectories(AppPaths.ExternalAutoBackupsDir);
            if (sets.Length <= MaxAutoSets)
                return;

            var oldest = sets.OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal)
                .Take(sets.Length - MaxAutoSets);

            foreach (var dir in oldest)
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                    PatchHelper.Log($"{Tag} pruned auto set {Path.GetFileName(dir)}");
                }
                catch (Exception ex)
                {
                    PatchHelper.Log(
                        $"{Tag} prune failed for {Path.GetFileName(dir)}: {ex.Message}"
                    );
                }
            }
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"{Tag} auto prune failed: {ex.Message}");
        }
    }

    // A backup set is "empty" if it holds no file larger than the trivial threshold.
    private static bool IsSetEmpty(string setRoot)
    {
        try
        {
            if (!Directory.Exists(setRoot))
                return true;
            foreach (
                var file in Directory.EnumerateFiles(setRoot, "*", SearchOption.AllDirectories)
            )
            {
                if (new FileInfo(file).Length > TrivialBytes)
                    return false;
            }
            return true;
        }
        catch
        {
            return false; // can't inspect — don't delete it
        }
    }

    private static string MakeTimestamp() =>
        DateTimeOffset.UtcNow.ToLocalTime().ToString("yyyyMMdd_HHmmss");

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch { }
    }
}
