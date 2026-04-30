using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace STS2Mobile.Steam;

// Read-only diagnostic logging for issue #7 (Save Manager not recognizing
// in-progress run / current_run.save). Enabled by the marker file
// /storage/emulated/0/StS2LauncherMM/.diagnose_issue7 — drop with
// `adb shell touch /storage/emulated/0/StS2LauncherMM/.diagnose_issue7`.
//
// Strictly observation: nothing here changes save flow. Adds verbose [Diag-i7]
// log lines so we can tell, post-hoc, which CloudSyncDecisions branch fired,
// which files were on each side at the moment of decision, and what
// CompareCurrentRun saw for floor counts. Logcat ring buffer is 5 MB so the
// marker gates the noise — without it the audit code is a no-op.
public static class Issue7Diagnostics
{
    private static readonly string MarkerPath = AppPaths.ExternalRoot + "/.diagnose_issue7";
    private const int CacheTtlMs = 1000;

    private static long _lastCheckTicks;
    private static bool _lastEnabled;
    private static readonly object _cacheLock = new();

    // 1s cache: AutoSyncFileAsync runs once per save file per game write, so
    // hitting the FS each time would be wasteful. The marker is sticky during
    // a session anyway — user drops/removes it at the launcher boundary.
    public static bool Enabled
    {
        get
        {
            lock (_cacheLock)
            {
                var nowTicks = Environment.TickCount;
                if (nowTicks - _lastCheckTicks < CacheTtlMs && _lastCheckTicks != 0)
                    return _lastEnabled;
                try
                {
                    _lastEnabled = File.Exists(MarkerPath);
                }
                catch
                {
                    _lastEnabled = false;
                }
                _lastCheckTicks = nowTicks == 0 ? 1 : nowTicks;
                return _lastEnabled;
            }
        }
    }

    // Walks every profile×modded combination and logs size of each save file
    // (progress.save / current_run.save / current_run_mp.save) on local and
    // cloud. Called at the top of DetermineAsync so we can tell whether the
    // "anyLocal=false" outcome came from genuinely empty disk or from the
    // decision logic ignoring current_run.save. Side-effect free.
    public static async Task AuditDecisionStateAsync(
        ISaveStore local,
        ICloudSaveStore cloud,
        string callSite
    )
    {
        if (!Enabled)
            return;

        PatchHelper.Log($"[Diag-i7] === Audit start (callSite={callSite}) ===");

        var wasModded = UserDataPathProvider.IsRunningModded;
        try
        {
            foreach (bool modded in new[] { false, true })
            {
                UserDataPathProvider.IsRunningModded = modded;
                for (int profile = 1; profile <= 3; profile++)
                {
                    var progressPath = ProgressSaveManager.GetProgressPathForProfile(profile);
                    var runPath = RunSaveManager.GetRunSavePath(profile, "current_run.save");
                    var runMpPath = RunSaveManager.GetRunSavePath(profile, "current_run_mp.save");

                    var progress = ProbePair(local, cloud, progressPath);
                    var run = ProbePair(local, cloud, runPath);
                    var runMp = ProbePair(local, cloud, runMpPath);

                    PatchHelper.Log(
                        $"[Diag-i7] modded={modded} profile{profile}: "
                            + $"progress.save L={progress.localSize}/C={progress.cloudSize} | "
                            + $"current_run.save L={run.localSize}/C={run.cloudSize} | "
                            + $"current_run_mp.save L={runMp.localSize}/C={runMp.cloudSize}"
                    );

                    if (run.localSize > 0)
                    {
                        try
                        {
                            var floors = CountRunFloors(local.ReadFile(runPath));
                            PatchHelper.Log(
                                $"[Diag-i7]   local current_run.save: floors={floors} path={runPath}"
                            );
                        }
                        catch (Exception ex)
                        {
                            PatchHelper.Log(
                                $"[Diag-i7]   local current_run.save read failed: {ex.Message}"
                            );
                        }
                    }
                    if (run.cloudSize > 0)
                    {
                        try
                        {
                            var content = await cloud.ReadFileAsync(runPath).ConfigureAwait(false);
                            var floors = CountRunFloors(content);
                            PatchHelper.Log(
                                $"[Diag-i7]   cloud current_run.save: floors={floors} path={runPath}"
                            );
                        }
                        catch (Exception ex)
                        {
                            PatchHelper.Log(
                                $"[Diag-i7]   cloud current_run.save read failed: {ex.Message}"
                            );
                        }
                    }
                }
            }
        }
        finally
        {
            UserDataPathProvider.IsRunningModded = wasModded;
        }

        PatchHelper.Log($"[Diag-i7] === Audit end (callSite={callSite}) ===");
    }

    // Logged by AutoSyncFileAsync. For current_run.save files we dump floor
    // counts on both sides so we can tell whether the comparer saw a legitimate
    // diff and which side it picked. For other files we just emit a short
    // outcome line so the timeline of every sync is reconstructable from logs.
    public static void LogCurrentRunSyncDetail(
        string path,
        string localContent,
        string cloudContent,
        string outcome
    )
    {
        if (!Enabled)
            return;
        try
        {
            var lower = path?.ToLowerInvariant() ?? "";
            bool isRun = lower.Contains("current_run");
            if (isRun)
            {
                int localFloors = string.IsNullOrEmpty(localContent)
                    ? -1
                    : CountRunFloors(localContent);
                int cloudFloors = string.IsNullOrEmpty(cloudContent)
                    ? -1
                    : CountRunFloors(cloudContent);
                PatchHelper.Log(
                    $"[Diag-i7] AutoSync current_run: path={path} "
                        + $"localFloors={localFloors} cloudFloors={cloudFloors} outcome={outcome}"
                );
            }
            else
            {
                PatchHelper.Log($"[Diag-i7] AutoSync: path={path} outcome={outcome}");
            }
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Diag-i7] AutoSync log failed: {ex.Message}");
        }
    }

    // Logged when CloudConflictDialog opens via Save Manager button — captures
    // the SaveProgressSummary fields the dialog will render so we can correlate
    // user-visible content with on-disk reality.
    public static void LogDialogSummary(
        string source,
        SyncDecision decision,
        SaveProgressSummary local,
        SaveProgressSummary cloud
    )
    {
        if (!Enabled)
            return;
        PatchHelper.Log(
            $"[Diag-i7] Dialog open ({source}): decision={decision} "
                + $"localEmpty={local?.IsEmpty} localSize={local?.RawSize} "
                + $"localFloors={local?.FloorsClimbed} localWins={local?.TotalWins} "
                + $"localPlay={local?.TotalPlaytimeSeconds}s | "
                + $"cloudEmpty={cloud?.IsEmpty} cloudSize={cloud?.RawSize} "
                + $"cloudFloors={cloud?.FloorsClimbed} cloudWins={cloud?.TotalWins} "
                + $"cloudPlay={cloud?.TotalPlaytimeSeconds}s"
        );
    }

    private static (int localSize, int cloudSize) ProbePair(
        ISaveStore local,
        ICloudSaveStore cloud,
        string path
    )
    {
        int localSize = 0;
        int cloudSize = 0;
        try
        {
            if (local.FileExists(path))
            {
                var content = local.ReadFile(path);
                localSize = content?.Length ?? 0;
                // For non-empty files, dump first byte to spot partial-write corruption
                // (force-stop scenarios where JSON file ends mid-write — first char
                // not '{'/'[' is the IsCorrupt trigger; we want to see the actual
                // first byte to confirm vs alternative failure modes).
                if (localSize > 0 && (path.Contains("current_run") || path.Contains("progress")))
                {
                    var firstByte = (byte)content[0];
                    var firstChar = char.IsControl((char)firstByte) ? '?' : (char)firstByte;
                    PatchHelper.Log(
                        $"[Diag-i7]   local first-byte: '{firstChar}' (0x{firstByte:X2}) path={path}"
                    );
                }
            }
        }
        catch { }
        try
        {
            if (cloud.FileExists(path))
                cloudSize = cloud.GetFileSize(path);
        }
        catch { }
        return (localSize, cloudSize);
    }

    // Logged from CloudSyncCoordinator.AutoSyncFileAsync when IsCorrupt branch
    // fires. This is the destructive-prone moment — local content is about to
    // be overwritten by cloud. Capturing the actual content shape proves whether
    // it's a true partial-write (truncated mid-JSON) or some other corruption.
    public static void LogIsCorruptDetected(string path, string content)
    {
        if (!Enabled)
            return;
        try
        {
            int len = content?.Length ?? 0;
            if (len == 0)
            {
                PatchHelper.Log($"[Diag-i7] IsCorrupt: path={path} len=0 (empty)");
                return;
            }
            int dumpLen = Math.Min(200, len);
            var prefix = content.Substring(0, dumpLen);
            // Replace control characters for log readability
            var safe = new System.Text.StringBuilder();
            foreach (char c in prefix)
            {
                if (c == '\n')
                    safe.Append("\\n");
                else if (c == '\r')
                    safe.Append("\\r");
                else if (c == '\t')
                    safe.Append("\\t");
                else if (char.IsControl(c) || c > 127)
                    safe.AppendFormat("\\x{0:X2}", (int)c);
                else
                    safe.Append(c);
            }
            PatchHelper.Log(
                $"[Diag-i7] IsCorrupt: path={path} len={len} firstByte=0x{(byte)content[0]:X2} "
                    + $"prefix=\"{safe}\""
            );
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Diag-i7] LogIsCorruptDetected failed: {ex.Message}");
        }
    }

    // Logged from SteamKit2CloudSaveStore.WriteFile entry — captures the queue
    // depth at the moment a write is enqueued. If the user force-stops shortly
    // after a flurry of saves, the last logged depth tells us how many uploads
    // were pending when the process died (cap H1 — cloud upload queue lost).
    public static void LogWriteEnqueue(string path, int byteSize, int queueDepth)
    {
        if (!Enabled)
            return;
        var lower = path?.ToLowerInvariant() ?? "";
        // Only log saves we care about — current_run, progress, prefs, settings.
        // Skip history and other noise.
        if (
            !lower.Contains("current_run")
            && !lower.Contains("progress.save")
            && !lower.Contains("prefs.save")
            && !lower.Contains("settings.save")
        )
            return;
        PatchHelper.Log(
            $"[Diag-i7] WriteFile enqueue: path={path} bytes={byteSize} queueDepth={queueDepth}"
        );
    }

    // Mirrors SaveProgressComparer.CountRunFloors so the audit can tag a run
    // file with its floor count without coupling to that internal helper.
    private static int CountRunFloors(string content)
    {
        if (string.IsNullOrEmpty(content))
            return 0;
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            int count = 0;
            if (
                root.TryGetProperty("map_point_history", out var history)
                && history.ValueKind == JsonValueKind.Array
            )
            {
                foreach (var act in history.EnumerateArray())
                {
                    if (act.ValueKind == JsonValueKind.Array)
                        count += act.GetArrayLength();
                }
            }
            else if (
                root.TryGetProperty("acts", out var acts) && acts.ValueKind == JsonValueKind.Array
            )
            {
                count = acts.GetArrayLength();
            }
            return count;
        }
        catch
        {
            return -1;
        }
    }
}
