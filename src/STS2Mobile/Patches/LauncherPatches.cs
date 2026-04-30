using System;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using STS2Mobile.Launcher;
using STS2Mobile.Launcher.Components;
using STS2Mobile.Steam;

namespace STS2Mobile.Patches;

// Core patches for the mobile launcher flow. Intercepts GameStartupWrapper to show
// the Steam login UI before the game starts, injects cloud save support via SteamKit2,
// and delegates sync logic to CloudSyncCoordinator. On first PLAY, verifies that
// the cloud file cache loads before letting the game's save layer touch the cloud
// store — protects against issue #4 where a stale/unauthenticated cache would let
// freshly defaulted local saves silently overwrite real cloud progress.
public static class LauncherPatches
{
    internal static bool CloudSyncEnabled = true;
    internal static string SavedAccountName;
    internal static string SavedRefreshToken;

    // Set true after a successful cache preload during RunLauncherThenGame. Drives
    // ConstructDefaultPrefix's fork: cloud-wrapped SaveManager when true, local-only
    // SaveManager (no cloud writes possible) when false.
    private static bool _cloudCacheReady;

    public static void Apply(Harmony harmony)
    {
        PatchHelper.PatchCritical(
            harmony,
            typeof(NGame),
            "GameStartupWrapper",
            prefix: PatchHelper.Method(typeof(LauncherPatches), nameof(GameStartupWrapperPrefix))
        );

        PatchHelper.Patch(
            harmony,
            typeof(SaveManager),
            "ConstructDefault",
            prefix: PatchHelper.Method(typeof(LauncherPatches), nameof(ConstructDefaultPrefix))
        );

        PatchHelper.PatchCritical(
            harmony,
            typeof(CloudSaveStore),
            "SyncCloudToLocal",
            prefix: PatchHelper.Method(typeof(LauncherPatches), nameof(SyncCloudToLocalPrefix))
        );
    }

    public static bool GameStartupWrapperPrefix(object __instance, ref Task __result)
    {
        __result = RunLauncherThenGame(__instance);
        return false;
    }

    public static bool ConstructDefaultPrefix(ref SaveManager __result)
    {
        PatchHelper.Log(
            $"[Cloud] ConstructDefaultPrefix called. HasToken={SavedRefreshToken != null}, "
                + $"CloudSync={CloudSyncEnabled}, CacheReady={_cloudCacheReady}"
        );

        if (!CloudSyncEnabled)
        {
            PatchHelper.Log("[Cloud] Cloud sync disabled by user — using local-only SaveManager");
            return true;
        }

        if (SavedAccountName == null || SavedRefreshToken == null)
        {
            PatchHelper.Log("[Cloud] No saved credentials — using local-only SaveManager");
            return true;
        }

        // Inline blocking preload: NGame and other game systems access
        // SaveManager.Instance during early initialization, BEFORE our launcher
        // gets a chance to run RunLauncherThenGame's async preload. If we just
        // returned local-only here, the game would cache default in-memory state
        // (no progress, etc.) which later cloud pulls cannot dislodge — verified
        // 2026-04-29 in 0.3.0-rc2: pulls succeeded but main menu still showed
        // "no save". So when we don't yet have a cache, we synchronously wait
        // up to 15s here. The first SaveManager.Instance access on cold start
        // pays this latency, but the game then sees real cloud-derived state
        // from the very first read.
        if (!_cloudCacheReady)
        {
            try
            {
                var preloadStore =
                    SteamKit2CloudSaveStore.Instance
                    ?? new SteamKit2CloudSaveStore(SavedAccountName, SavedRefreshToken);
                _cloudCacheReady = preloadStore
                    .WaitForCacheReadyAsync(15_000)
                    .GetAwaiter()
                    .GetResult();
                PatchHelper.Log(
                    $"[Cloud] Inline cache preload from ConstructDefault: ready={_cloudCacheReady}"
                );
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"[Cloud] Inline cache preload threw: {ex.Message}");
                _cloudCacheReady = false;
            }
        }

        // After the inline attempt, if cache is still not ready, fall back to
        // local-only — same protection as before (no cloud writes, so no
        // destructive overwrite even if the game writes defaults).
        if (!_cloudCacheReady)
        {
            PatchHelper.Log(
                "[Cloud] Cloud cache not ready after inline preload — using local-only SaveManager"
            );
            return true;
        }

        try
        {
            var localStore = new GodotFileIo(UserDataPathProvider.GetAccountScopedBasePath(null));
            // Reuse the singleton from the preload so we don't open a second SteamConnection.
            var cloudStore =
                SteamKit2CloudSaveStore.Instance
                ?? new SteamKit2CloudSaveStore(SavedAccountName, SavedRefreshToken);
            var wrappedStore = new CloudSaveStore(localStore, cloudStore);

            __result = new SaveManager(wrappedStore);
            PatchHelper.Log("[Cloud] Created SaveManager with SteamKit2 cloud store");
            return false;
        }
        catch (Exception ex)
        {
            PatchHelper.Log(
                $"[Cloud] Cloud store injection failed, falling back to local: {ex.Message}"
            );
            return true;
        }
    }

    public static bool SyncCloudToLocalPrefix(
        CloudSaveStore __instance,
        string path,
        ref Task __result
    )
    {
        __result = CloudSyncCoordinator.AutoSyncFileAsync(
            __instance.LocalStore,
            __instance.CloudStore,
            path
        );
        return false;
    }

    private static async Task RunLauncherThenGame(object game)
    {
        var gameNode = (Node)game;

        var launcher = new LauncherUI();
        gameNode.AddChild(launcher);
        launcher.SetGameMode(true);
        launcher.Initialize();
        PatchHelper.Log("Launcher UI displayed");

        await launcher.WaitForLaunch();
        PatchHelper.Log("User launched game, proceeding to startup...");

        var instanceField = typeof(SaveManager).GetField(
            "_instance",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        if (instanceField != null)
        {
            instanceField.SetValue(null, null);
            PatchHelper.Log("[Cloud] Reset SaveManager._instance for cloud store re-injection");
        }

        // Free the launcher BEFORE running the conflict dialog. The launcher is
        // ZIndex=100, so a dialog added underneath would be hidden — and even if
        // the z-ordering were fixed, leaving the launcher's PLAY button live
        // during a sync decision invites accidental re-entry. Once the user has
        // pressed PLAY, the launcher's job is done.
        launcher.QueueFree();

        // Pre-PLAY cloud handshake: load the file cache, classify the local/cloud
        // state, and resolve any conflict via the dialog BEFORE InitSettingsData runs.
        // After this completes, ConstructDefaultPrefix has the info it needs to pick
        // the right SaveManager flavor.
        _cloudCacheReady = false;
        if (CloudSyncEnabled && SavedAccountName != null && SavedRefreshToken != null)
        {
            try
            {
                var existing = SteamKit2CloudSaveStore.Instance;
                var cloudStore =
                    existing
                    ?? new SteamKit2CloudSaveStore(SavedAccountName, SavedRefreshToken);

                _cloudCacheReady = await cloudStore.WaitForCacheReadyAsync(15_000);
                if (_cloudCacheReady)
                {
                    PatchHelper.Log("[Cloud] Cache preload OK — running sync decision");
                    var localStore = new GodotFileIo(
                        UserDataPathProvider.GetAccountScopedBasePath(null)
                    );
                    await ResolveSyncDecisionAsync(gameNode, localStore, cloudStore);
                }
                else
                {
                    PatchHelper.Log(
                        "[Cloud] Cache preload failed — entering local-only mode this session"
                    );
                }
            }
            catch (Exception ex)
            {
                PatchHelper.Log(
                    $"[Cloud] Cache preload threw, entering local-only mode: {ex.Message}"
                );
                _cloudCacheReady = false;
            }
        }
        else
        {
            PatchHelper.Log("[Cloud] Skipping cloud preload (sync disabled or no credentials)");
        }

        if (ShaderWarmupScreen.NeedsWarmup())
        {
            var warmup = new ShaderWarmupScreen();
            gameNode.AddChild(warmup);
            warmup.Initialize();
            await warmup.WaitForCompletion();
            warmup.QueueFree();
        }

        SaveManager.Instance.InitSettingsData();

        var gameStartup = game.GetType()
            .GetMethod("GameStartup", BindingFlags.NonPublic | BindingFlags.Instance);

        try
        {
            await (Task)gameStartup.Invoke(game, null);
        }
        catch (TargetInvocationException ex)
        {
            PatchHelper.Log($"Game startup failed: {ex.InnerException?.Message}");
            throw ex.InnerException ?? ex;
        }
    }

    // Public entry point used by the Save Manager launcher button. Always shows
    // the dialog (even on Identical) so the user can inspect both sides and
    // explicitly trigger a re-sync if they want. Decision is forced to Conflict
    // for the dialog rendering — the underlying CloudSyncDecisions still runs
    // so summaries are accurate.
    public static async Task OpenSaveSyncDialogAsync(Node parent)
    {
        if (!CloudSyncEnabled)
        {
            PatchHelper.Log("[Cloud] Save Manager: cloud sync disabled by user");
            return;
        }
        if (SavedAccountName == null || SavedRefreshToken == null)
        {
            PatchHelper.Log("[Cloud] Save Manager: no saved credentials");
            return;
        }

        var cloudStore =
            SteamKit2CloudSaveStore.Instance
            ?? new SteamKit2CloudSaveStore(SavedAccountName, SavedRefreshToken);

        bool cacheLoaded = await cloudStore.WaitForCacheReadyAsync(15_000);
        if (!cacheLoaded)
        {
            PatchHelper.Log("[Cloud] Save Manager: cloud cache failed to load");
            return;
        }

        var localStore = new GodotFileIo(UserDataPathProvider.GetAccountScopedBasePath(null));
        var rawDecision = await CloudSyncDecisions.DetermineAsync(localStore, cloudStore);

        // Pass the real decision through — the dialog adapts (Identical /
        // NoData show informational close-only UI; non-Identical show the
        // local/cloud choice). No need to force Conflict semantics anymore.
        var displayDecision = new SyncDecisionResult
        {
            Decision = rawDecision.Decision,
            LocalSummary = rawDecision.LocalSummary ?? new SaveProgressSummary(),
            CloudSummary = rawDecision.CloudSummary ?? new SaveProgressSummary(),
        };
        PatchHelper.Log(
            $"[Cloud] Save Manager: opening dialog (decision={rawDecision.Decision})"
        );
        Issue7Diagnostics.LogDialogSummary(
            "SaveManagerButton",
            displayDecision.Decision,
            displayDecision.LocalSummary,
            displayDecision.CloudSummary
        );
        await HandleConflictAsync(parent, localStore, cloudStore, displayDecision);
    }

    private static async Task ResolveSyncDecisionAsync(
        Node gameNode,
        ISaveStore localStore,
        SteamKit2CloudSaveStore cloudStore
    )
    {
        var result = await CloudSyncDecisions.DetermineAsync(localStore, cloudStore);
        PatchHelper.Log($"[Cloud] Sync decision: {result.Decision}");
        Issue7Diagnostics.LogDialogSummary(
            "FirstPlayAuto",
            result.Decision,
            result.LocalSummary,
            result.CloudSummary
        );

        switch (result.Decision)
        {
            case SyncDecision.NoData:
            case SyncDecision.Identical:
                // Truly nothing to surface — both sides agree (or neither has anything).
                return;

            case SyncDecision.MobileOnly:
            case SyncDecision.CloudOnly:
            case SyncDecision.Conflict:
                // Anything other than full agreement gets a dialog so the user
                // can confirm the direction of the sync. Without this, a fresh
                // mobile install with cloud progress would silently pull (mostly
                // safe) but a fresh PC install with mobile progress would push
                // (also mostly safe) — and a true conflict could pick the wrong
                // side. Surfacing the choice removes both risk and surprise.
                await HandleConflictAsync(gameNode, localStore, cloudStore, result);
                return;
        }
    }

    private static async Task HandleConflictAsync(
        Node gameNode,
        ISaveStore localStore,
        SteamKit2CloudSaveStore cloudStore,
        SyncDecisionResult decision
    )
    {
        // The dialog deliberately has no "remember my choice" option — which
        // side is correct depends on context (which device played last,
        // whether the user just restored from backup, etc.) so blanket
        // auto-apply would silently destroy data on the next conflict that
        // should have gone the other way.
        var dialog = new CloudConflictDialog(
            decision.LocalSummary,
            decision.CloudSummary,
            decision.LocalIsMoreRecent,
            LauncherUI.ResolveScale(gameNode),
            decision.Decision,
            LauncherUI.ResolveViewportHeight(gameNode),
            decision.DiffSlotCount
        );
        gameNode.AddChild(dialog);
        var choice = await dialog.Result;
        PatchHelper.Log($"[Cloud] Conflict resolved by user: choice={choice}");

        switch (choice)
        {
            case CloudConflictChoice.KeepLocal:
                await ApplyChosenSideAsync(localStore, cloudStore, keepLocal: true);
                if (!await FlushAndVerifyAsync(localStore, cloudStore, keepLocal: true))
                {
                    PatchHelper.Log(
                        "[Cloud] Verification failed after KeepLocal — falling back to local-only this session"
                    );
                    _cloudCacheReady = false;
                }
                break;
            case CloudConflictChoice.KeepCloud:
                await ApplyChosenSideAsync(localStore, cloudStore, keepLocal: false);
                if (!await FlushAndVerifyAsync(localStore, cloudStore, keepLocal: false))
                {
                    PatchHelper.Log(
                        "[Cloud] Verification failed after KeepCloud — falling back to local-only this session"
                    );
                    _cloudCacheReady = false;
                }
                break;
            case CloudConflictChoice.Cancel:
                // User aborted. Force local-only mode for this session — the game still
                // boots, but no cloud writes happen. They can retry next launch.
                _cloudCacheReady = false;
                PatchHelper.Log("[Cloud] Conflict cancelled — falling back to local-only");
                break;
        }
    }

    // Blocks until queued cloud writes drain, then verifies the chosen side has
    // landed on both ends. Without this, the user could press a button and the
    // launcher would fall through to InitSettingsData while a push is still in
    // flight — risking the game's first save call racing with a half-written
    // cloud copy. We verify only progress.save (the one save the user mostly
    // cares about); other files lean on the post-decision AutoSyncFileAsync
    // path, which now sees both sides aligned for progress and either pulls or
    // pushes the rest by content comparison.
    private static async Task<bool> FlushAndVerifyAsync(
        ISaveStore localStore,
        SteamKit2CloudSaveStore cloudStore,
        bool keepLocal
    )
    {
        try
        {
            // Same rationale as QuitPrefix: wait for actual upload completion
            // signaled by CloudWriteQueue's _actionInProgress + queue depth.
            // 5-min ceiling is for unreachable-Steam scenarios; healthy uploads
            // finish in 1-15 s. The user is sitting on the dialog waiting for
            // verify and explicitly chose to commit a sync direction, so the
            // wait is expected.
            cloudStore.Flush(timeoutMs: 300_000);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Cloud] Flush failed: {ex.Message}");
            return false;
        }

        // Re-read progress.save from both sides for the active profile layout.
        // We only sample profile1 — verifying every profile/path would balloon
        // the wait time and progress.save is the one file that actually carries
        // career state worth losing sleep over.
        var path = ProgressSaveManager.GetProgressPathForProfile(1);
        try
        {
            bool localExists = localStore.FileExists(path);
            bool cloudExists = cloudStore.FileExists(path);

            if (!localExists && !cloudExists)
            {
                PatchHelper.Log($"[Cloud] Verify: {path} absent on both sides — accepted");
                return true;
            }

            if (localExists != cloudExists)
            {
                PatchHelper.Log(
                    $"[Cloud] Verify: existence mismatch local={localExists} cloud={cloudExists}"
                );
                return false;
            }

            string localContent = localStore.ReadFile(path);
            string cloudContent = await cloudStore.ReadFileAsync(path);

            if (localContent.Length != cloudContent.Length)
            {
                PatchHelper.Log(
                    $"[Cloud] Verify: size mismatch local={localContent.Length} "
                        + $"cloud={cloudContent.Length}"
                );
                return false;
            }

            if (localContent != cloudContent)
            {
                PatchHelper.Log(
                    $"[Cloud] Verify: content mismatch (same size, different bytes)"
                );
                return false;
            }

            PatchHelper.Log(
                $"[Cloud] Verify OK: {path} matches on both sides ({localContent.Length} bytes)"
            );
            return true;
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Cloud] Verify threw: {ex.Message}");
            return false;
        }
    }

    // Pre-syncs progress.save across all profiles so InitSettingsData's later
    // SyncCloudToLocal calls see equal content on both sides (BRANCH-A → skip).
    private static async Task ApplyChosenSideAsync(
        ISaveStore local,
        SteamKit2CloudSaveStore cloud,
        bool keepLocal
    )
    {
        // Issue #7 fix scope:
        // 1. Also process current_run.save / current_run_mp.save — previously
        //    only progress.save was synced here, leaving the in-progress run
        //    one resolved-conflict away from being silently overwritten by
        //    AutoSyncFileAsync's "cloud wins" fallback when floors match.
        // 2. After local writes (KeepCloud branch), call SetLastModifiedTime
        //    to align local mtime with cloud's, otherwise local mtime = NOW
        //    (file write timestamp) and cloud mtime stays at original cloud
        //    value, leaving the next launch with a permanent mtime asymmetry
        //    that re-triggers conflict and points the "최근" badge at the
        //    wrong side.
        // 3. After cloud writes (KeepLocal branch), align local mtime to NOW
        //    too — SteamKit2CloudSaveStore.WriteFile sets cloud mtime to NOW
        //    automatically, so matching local to NOW keeps both sides equal.
        var wasModded = UserDataPathProvider.IsRunningModded;
        try
        {
            foreach (bool modded in new[] { false, true })
            {
                UserDataPathProvider.IsRunningModded = modded;
                for (int profile = 1; profile <= 3; profile++)
                {
                    await ApplyOneAsync(
                        local, cloud, keepLocal,
                        ProgressSaveManager.GetProgressPathForProfile(profile)
                    );
                    await ApplyOneAsync(
                        local, cloud, keepLocal,
                        RunSaveManager.GetRunSavePath(profile, "current_run.save")
                    );
                    await ApplyOneAsync(
                        local, cloud, keepLocal,
                        RunSaveManager.GetRunSavePath(profile, "current_run_mp.save")
                    );
                }
            }
        }
        finally
        {
            UserDataPathProvider.IsRunningModded = wasModded;
        }
    }

    private static async Task ApplyOneAsync(
        ISaveStore local,
        SteamKit2CloudSaveStore cloud,
        bool keepLocal,
        string path
    )
    {
        try
        {
            if (keepLocal)
            {
                if (local.FileExists(path))
                {
                    var content = local.ReadFile(path);
                    cloud.WriteFile(path, content);
                    // SteamKit2CloudSaveStore stamps cloud mtime to NOW on
                    // write; sync local to NOW too so DetermineAsync sees
                    // identical mtimes on the next pass.
                    local.SetLastModifiedTime(path, DateTimeOffset.UtcNow);
                    PatchHelper.Log($"[Cloud] Conflict apply: pushed {path}");
                }
            }
            else
            {
                if (cloud.FileExists(path))
                {
                    var content = await cloud.ReadFileAsync(path);
                    var cloudTime = cloud.GetLastModifiedTime(path);
                    await local.WriteFileAsync(path, content);
                    local.SetLastModifiedTime(path, cloudTime);
                    PatchHelper.Log($"[Cloud] Conflict apply: pulled {path}");
                }
            }
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Cloud] Conflict apply failed for {path}: {ex.Message}");
        }
    }
}
