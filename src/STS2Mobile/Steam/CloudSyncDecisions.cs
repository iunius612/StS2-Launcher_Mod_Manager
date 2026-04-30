using System;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace STS2Mobile.Steam;

// Cross-store progress comparison for the launcher's first-PLAY decision.
// Inspects progress.save in profile1/2/3 (vanilla and modded layouts) and
// classifies the situation into a single state — drives whether we silently
// auto-sync or ask the user (issue #4 follow-up: avoid both data loss and
// over-prompting). Only the canonical progress.save is consulted; other save
// files (settings, prefs, history runs) follow the post-decision sync path.
public enum SyncDecision
{
    // Neither side has a meaningful progress.save anywhere. New install on both
    // ends. No prompt — let normal AutoSync handle the empty case.
    NoData,

    // Local has progress, cloud is empty for every profile. Mobile is the
    // first device. Silently push without prompting.
    MobileOnly,

    // Cloud has progress, local is empty for every profile. PC user installing
    // launcher for the first time. Silently pull.
    CloudOnly,

    // Both sides have progress, but at least one profile differs. Needs a user
    // decision — ConflictDialog must be shown.
    Conflict,

    // Both sides have content and every profile matches byte-for-byte. Treat as
    // "everything is fine" — fall through to the existing AutoSync logic.
    Identical,
}

public class SyncDecisionResult
{
    public SyncDecision Decision { get; init; }

    // For Conflict only: the per-profile summaries are aggregated for the
    // dialog. Local takes the union of every non-empty profile; cloud likewise.
    public SaveProgressSummary LocalSummary { get; init; }
    public SaveProgressSummary CloudSummary { get; init; }

    // Total number of (profile × modded) slots that differ between local and
    // cloud. The dialog renders only ONE profile's stats but ApplyChosenSide
    // pushes/pulls every diff. This counter lets the dialog flag the rest:
    // "프로필 3 (외 K개 차이)". 0 when there's no conflict at all.
    public int DiffSlotCount { get; init; }

    // Drives which side gets visual emphasis ("most recent" highlight). When one
    // side is empty, the side with data is the de-facto "most recent" — there's
    // nothing to compare timestamps against.
    //
    // Issue #7 fix: compare the *latest* timestamp on each side across BOTH
    // progress.save and current_run.save. Pre-fix only progress.save mtime was
    // considered, which mispointed the badge whenever the in-progress run was
    // newer than the accumulator file (verified: strict-swipe scenario where
    // local current_run.save was 21:49 but its progress.save was a stale
    // 21:14 from a prior KeepCloud, while cloud progress.save was a fresh
    // 21:55 from a manual KeepLocal — old code said cloud, true newer = local).
    public bool LocalIsMoreRecent
    {
        get
        {
            bool localHas = LocalSummary != null && !LocalSummary.IsEmpty;
            bool cloudHas = CloudSummary != null && !CloudSummary.IsEmpty;
            if (localHas && !cloudHas)
                return true;
            if (!localHas && cloudHas)
                return false;
            if (localHas && cloudHas)
                return Latest(LocalSummary) > Latest(CloudSummary);
            return false;
        }
    }

    private static DateTimeOffset Latest(SaveProgressSummary s)
    {
        var p = s.LastModified;
        var r = s.HasCurrentRun ? s.CurrentRunLastModified : DateTimeOffset.MinValue;
        return p > r ? p : r;
    }
}

public static class CloudSyncDecisions
{
    private const int MaxProfiles = 3;

    public static async Task<SyncDecisionResult> DetermineAsync(
        ISaveStore local,
        ICloudSaveStore cloud
    )
    {
        await Issue7Diagnostics.AuditDecisionStateAsync(local, cloud, "DetermineAsync");

        bool anyLocal = false;
        bool anyCloud = false;
        bool anyDiff = false;

        SaveProgressSummary aggregateLocal = null;
        SaveProgressSummary aggregateCloud = null;

        // Walk both vanilla and modded layouts so a player who plays modded on
        // PC and vanilla on mobile (or vice versa) gets the same conflict
        // handling. UserDataPathProvider.IsRunningModded is process-wide state;
        // restore it after we're done so we don't leak the toggle into the
        // game's own startup logic.
        // Track which profile triggered anyDiff so we can pick the most
        // user-relevant summary for the dialog (issue #7: previously we always
        // showed profile1's stats even when profile2/3 was the one that
        // differed, leaving the user with no idea what triggered the conflict).
        SaveProgressSummary firstDiffLocal = null;
        SaveProgressSummary firstDiffCloud = null;
        SaveProgressSummary firstRunLocal = null;
        SaveProgressSummary firstRunCloud = null;
        int diffSlots = 0;

        var wasModded = UserDataPathProvider.IsRunningModded;
        try
        {
            foreach (bool modded in new[] { false, true })
            {
                UserDataPathProvider.IsRunningModded = modded;
                for (int profile = 1; profile <= MaxProfiles; profile++)
                {
                    var path = ProgressSaveManager.GetProgressPathForProfile(profile);

                    int localSize = local.FileExists(path) ? GetSize(local, path) : 0;
                    int cloudSize = cloud.FileExists(path) ? cloud.GetFileSize(path) : 0;

                    // Issue #7: also check current_run.save. progress.save alone
                    // misses the most common cross-device case — an in-progress
                    // run on one device with empty/identical progress on the
                    // other. Without this, decision falls to NoData/Identical
                    // and the user is never prompted to sync the in-progress run.
                    var runPath = RunSaveManager.GetRunSavePath(profile, "current_run.save");
                    int localRunSize = local.FileExists(runPath) ? GetSize(local, runPath) : 0;
                    int cloudRunSize = cloud.FileExists(runPath) ? cloud.GetFileSize(runPath) : 0;

                    bool hasLocal = localSize > 0 || localRunSize > 0;
                    bool hasCloud = cloudSize > 0 || cloudRunSize > 0;

                    if (hasLocal)
                        anyLocal = true;
                    if (hasCloud)
                        anyCloud = true;

                    bool profileDiffers = false;

                    // Compare progress.save first (cheap size check, byte
                    // comparison only when sizes match).
                    if (localSize > 0 && cloudSize > 0)
                    {
                        if (localSize != cloudSize)
                        {
                            profileDiffers = true;
                        }
                        else
                        {
                            try
                            {
                                var localContent = local.ReadFile(path);
                                var cloudContent = await cloud
                                    .ReadFileAsync(path)
                                    .ConfigureAwait(false);
                                if (localContent != cloudContent)
                                    profileDiffers = true;
                            }
                            catch (Exception ex)
                            {
                                PatchHelper.Log(
                                    $"[Cloud] Decision: read failed for {path}, treating as diff: {ex.Message}"
                                );
                                profileDiffers = true;
                            }
                        }
                    }
                    else if (localSize > 0 || cloudSize > 0)
                    {
                        // One side has progress.save, other doesn't.
                        profileDiffers = true;
                    }

                    // Same comparison for current_run.save. Size-only check
                    // is sufficient for triggering Conflict — actual sync
                    // direction is decided later by SaveProgressComparer.
                    if (localRunSize > 0 && cloudRunSize > 0)
                    {
                        if (localRunSize != cloudRunSize)
                            profileDiffers = true;
                    }
                    else if (localRunSize > 0 || cloudRunSize > 0)
                    {
                        profileDiffers = true;
                    }

                    if (profileDiffers)
                        anyDiff = true;

                    // Aggregate summaries: build a per-profile summary so we
                    // can pick the most-relevant one to render. Priority:
                    //   1. profile that triggered anyDiff (matches what
                    //      Conflict actually means)
                    //   2. profile with an in-progress run
                    //   3. first non-empty progress.save (legacy fallback)
                    SaveProgressSummary localSummary = null;
                    SaveProgressSummary cloudSummary = null;

                    if (localSize > 0 || localRunSize > 0)
                    {
                        localSummary = BuildSummary(
                            () => localSize > 0 ? local.ReadFile(path) : null,
                            localSize,
                            localSize > 0 ? local.GetLastModifiedTime(path) : DateTimeOffset.MinValue,
                            () => localRunSize > 0 ? local.ReadFile(runPath) : null,
                            localRunSize,
                            localRunSize > 0
                                ? local.GetLastModifiedTime(runPath)
                                : DateTimeOffset.MinValue,
                            "local"
                        );
                        localSummary.ProfileNumber = profile;
                        localSummary.IsModded = modded;
                    }
                    if (cloudSize > 0 || cloudRunSize > 0)
                    {
                        cloudSummary = await BuildCloudSummaryAsync(
                            cloud,
                            path,
                            cloudSize,
                            runPath,
                            cloudRunSize
                        );
                        cloudSummary.ProfileNumber = profile;
                        cloudSummary.IsModded = modded;
                    }

                    if (profileDiffers)
                    {
                        diffSlots++;
                        firstDiffLocal ??= localSummary ?? new SaveProgressSummary { ProfileNumber = profile, IsModded = modded };
                        firstDiffCloud ??= cloudSummary ?? new SaveProgressSummary { ProfileNumber = profile, IsModded = modded };
                    }
                    if (localSummary?.HasCurrentRun == true)
                        firstRunLocal ??= localSummary;
                    if (cloudSummary?.HasCurrentRun == true)
                        firstRunCloud ??= cloudSummary;

                    if (aggregateLocal == null && localSummary != null)
                        aggregateLocal = localSummary;
                    if (aggregateCloud == null && cloudSummary != null)
                        aggregateCloud = cloudSummary;
                }
            }
        }
        finally
        {
            UserDataPathProvider.IsRunningModded = wasModded;
        }

        // Resolution priority: show the user the data that actually changed.
        // 1) profile that triggered anyDiff — the user can see *which* save is
        //    the source of conflict, not generic profile1 stats.
        // 2) profile with an in-progress run — even when sides match, surface
        //    the run state so "Identical" doesn't visually contradict the
        //    obvious presence of a current run.
        // 3) first non-empty (legacy) — for boring NoData / Identical cases
        //    where there's no in-progress run on either side.
        var resolvedLocal = firstDiffLocal ?? firstRunLocal ?? aggregateLocal;
        var resolvedCloud = firstDiffCloud ?? firstRunCloud ?? aggregateCloud;

        if (Issue7Diagnostics.Enabled)
            PatchHelper.Log(
                $"[Diag-i7] DetermineAsync result: anyLocal={anyLocal} anyCloud={anyCloud} anyDiff={anyDiff} "
                    + $"summarySource={(firstDiffLocal != null ? "diff" : firstRunLocal != null ? "run" : "first-nonempty")}"
            );

        if (!anyLocal && !anyCloud)
            return new SyncDecisionResult { Decision = SyncDecision.NoData };
        if (anyLocal && !anyCloud)
            return new SyncDecisionResult
            {
                Decision = SyncDecision.MobileOnly,
                LocalSummary = resolvedLocal,
                CloudSummary = new SaveProgressSummary(),
            };
        if (!anyLocal && anyCloud)
            return new SyncDecisionResult
            {
                Decision = SyncDecision.CloudOnly,
                LocalSummary = new SaveProgressSummary(),
                CloudSummary = resolvedCloud,
            };
        if (anyDiff)
            return new SyncDecisionResult
            {
                Decision = SyncDecision.Conflict,
                LocalSummary = resolvedLocal,
                CloudSummary = resolvedCloud,
                DiffSlotCount = diffSlots,
            };

        // Identical: still attach summaries so the Save Manager button can
        // display the (matching) state with real numbers instead of placeholders.
        return new SyncDecisionResult
        {
            Decision = SyncDecision.Identical,
            LocalSummary = aggregateLocal,
            CloudSummary = aggregateCloud,
        };
    }

    private static int GetSize(ISaveStore store, string path)
    {
        try
        {
            // ISaveStore doesn't expose a size primitive, but reading is cheap
            // for save files which are <1MB. Used only to decide whether a
            // file is non-empty.
            var content = store.ReadFile(path);
            return content?.Length ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static SaveProgressSummary BuildSummary(
        Func<string> readProgress,
        int progressSize,
        DateTimeOffset progressLastModified,
        Func<string> readRun,
        int runSize,
        DateTimeOffset runLastModified,
        string sideLabel
    )
    {
        SaveProgressSummary summary;
        try
        {
            var progressContent = progressSize > 0 ? readProgress() : null;
            summary = SaveProgressSummary.FromContent(
                progressContent ?? string.Empty,
                progressSize,
                progressLastModified
            );
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Cloud] Decision: {sideLabel} progress read failed: {ex.Message}");
            summary = new SaveProgressSummary();
        }

        if (runSize > 0)
        {
            try
            {
                summary.MergeCurrentRun(readRun(), runSize, runLastModified);
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"[Cloud] Decision: {sideLabel} current_run read failed: {ex.Message}");
            }
        }
        return summary;
    }

    private static async Task<SaveProgressSummary> BuildCloudSummaryAsync(
        ICloudSaveStore cloud,
        string progressPath,
        int progressSize,
        string runPath,
        int runSize
    )
    {
        SaveProgressSummary summary;
        try
        {
            string progressContent = string.Empty;
            DateTimeOffset progressMtime = DateTimeOffset.MinValue;
            if (progressSize > 0)
            {
                progressContent = await cloud.ReadFileAsync(progressPath).ConfigureAwait(false);
                progressMtime = cloud.GetLastModifiedTime(progressPath);
            }
            summary = SaveProgressSummary.FromContent(progressContent, progressSize, progressMtime);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Cloud] Decision: cloud progress read failed: {ex.Message}");
            summary = new SaveProgressSummary();
        }

        if (runSize > 0)
        {
            try
            {
                var runContent = await cloud.ReadFileAsync(runPath).ConfigureAwait(false);
                var runMtime = cloud.GetLastModifiedTime(runPath);
                summary.MergeCurrentRun(runContent, runSize, runMtime);
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"[Cloud] Decision: cloud current_run read failed: {ex.Message}");
            }
        }
        return summary;
    }
}
