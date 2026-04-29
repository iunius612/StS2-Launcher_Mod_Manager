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

    // Drives which side gets visual emphasis ("most recent" highlight). When one
    // side is empty, the side with data is the de-facto "most recent" — there's
    // nothing to compare timestamps against.
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
                return LocalSummary.LastModified > CloudSummary.LastModified;
            return false;
        }
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

                    bool hasLocal = localSize > 0;
                    bool hasCloud = cloudSize > 0;

                    if (hasLocal)
                        anyLocal = true;
                    if (hasCloud)
                        anyCloud = true;

                    if (hasLocal && hasCloud)
                    {
                        // Quick size-mismatch shortcut: different sizes ⇒ different content.
                        if (localSize != cloudSize)
                        {
                            anyDiff = true;
                        }
                        else
                        {
                            // Same size — read both to compare exact bytes.
                            try
                            {
                                var localContent = local.ReadFile(path);
                                var cloudContent = await cloud
                                    .ReadFileAsync(path)
                                    .ConfigureAwait(false);
                                if (localContent != cloudContent)
                                    anyDiff = true;
                            }
                            catch (Exception ex)
                            {
                                PatchHelper.Log(
                                    $"[Cloud] Decision: read failed for {path}, treating as diff: {ex.Message}"
                                );
                                anyDiff = true;
                            }
                        }
                    }

                    // Build summaries from the FIRST non-empty progress.save
                    // we encounter on each side. profile1 (vanilla) takes
                    // priority since 99% of users only have that one — falling
                    // through to other profiles only matters for power users.
                    if (hasLocal && aggregateLocal == null)
                    {
                        aggregateLocal = TryReadSummary(() => local.ReadFile(path), localSize, local.GetLastModifiedTime(path));
                    }
                    if (hasCloud && aggregateCloud == null)
                    {
                        try
                        {
                            var cc = await cloud.ReadFileAsync(path).ConfigureAwait(false);
                            aggregateCloud = SaveProgressSummary.FromContent(
                                cc,
                                cloudSize,
                                cloud.GetLastModifiedTime(path)
                            );
                        }
                        catch (Exception ex)
                        {
                            PatchHelper.Log(
                                $"[Cloud] Decision: cloud read failed for {path}: {ex.Message}"
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

        if (!anyLocal && !anyCloud)
            return new SyncDecisionResult { Decision = SyncDecision.NoData };
        if (anyLocal && !anyCloud)
            return new SyncDecisionResult
            {
                Decision = SyncDecision.MobileOnly,
                LocalSummary = aggregateLocal,
                CloudSummary = new SaveProgressSummary(),
            };
        if (!anyLocal && anyCloud)
            return new SyncDecisionResult
            {
                Decision = SyncDecision.CloudOnly,
                LocalSummary = new SaveProgressSummary(),
                CloudSummary = aggregateCloud,
            };
        if (anyDiff)
            return new SyncDecisionResult
            {
                Decision = SyncDecision.Conflict,
                LocalSummary = aggregateLocal,
                CloudSummary = aggregateCloud,
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

    private static SaveProgressSummary TryReadSummary(
        Func<string> readContent,
        int rawSize,
        DateTimeOffset lastModified
    )
    {
        try
        {
            return SaveProgressSummary.FromContent(readContent(), rawSize, lastModified);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Cloud] Decision: local read failed: {ex.Message}");
            return new SaveProgressSummary();
        }
    }
}
