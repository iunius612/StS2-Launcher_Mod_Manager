using System;
using System.Collections.Generic;
using Godot;
using STS2Mobile.Launcher;
using STS2Mobile.Launcher.Components;

namespace STS2Mobile.Steam;

// Part B (issue #36) — prevention layer. The guard lives at the single cloud
// write funnel: SteamKit2CloudSaveStore.WriteFile(string, byte[]). EVERY cloud
// write converges there — the game's direct path (game CloudSaveStore →
// CloudStore.WriteFile = our store), the coordinator's manual push
// (cloud.WriteFile), and the string/async overloads (all delegate to the byte[]
// form). Guarding that one point covers all of them (verified by code
// cross-check + sts2.dll decompile, issue #4).
//
// The store calls ShouldBlockWrite BEFORE it touches the cache (_cache.Set) or
// enqueues the upload, so a blocked write leaves the cache uncorrupted and never
// uploads — the good cloud copy survives. Critically, the cloud's CURRENT size
// must be read before _cache.Set overwrites it with the new (empty) length.
//
// Always on, independent of the Local Backup toggle (B-3): prevention can't be
// opted out of. The rule is deliberately conservative to avoid blocking
// legitimate saves — see ShouldBlockWrite.
public static class CloudWriteGuard
{
    // Dedicated tag so device-test-qa can grep a deterministic PASS/FAIL signal.
    private const string Tag = "[Issue36-GuardB]";

    // Bytes at or below this count are treated as "empty" even if non-zero — a
    // couple of stray bytes (e.g. "{}", a stray newline) can't be a real save and
    // would still destroy a populated cloud copy. Kept tiny so we never reject a
    // genuinely small-but-valid file.
    private const int EmptyByteThreshold = 2;

    // Block surfaced to the user once per path to avoid dialog spam when the game
    // retries a write in a tight loop.
    private static readonly HashSet<string> _notifiedPaths = new();
    private static readonly object _notifyLock = new();

    // Swappable for tests. Defaults to the in-game CloudConflictDialog notifier.
    public static Action<string, string> Notifier = DefaultNotify;

    // Primary entry point, called from inside SteamKit2CloudSaveStore.WriteFile
    // (byte[]) before _cache.Set and before enqueue. Returns true to BLOCK — the
    // store then early-returns, so neither the cache nor the upload queue is
    // touched and the existing cloud copy is preserved.
    //
    // `cache` is the store's own CloudFileCache (size/timestamp metadata, no
    // network). `canonPath` is the already-canonicalized cloud key. `newByteLength`
    // is the length of the bytes about to be written.
    //
    // Rule (conservative, no false positives on real saves):
    //   - New content non-empty (> threshold)        → ALLOW (never block real data).
    //   - New content empty AND cache not loaded      → BLOCK (can't verify cloud is
    //         safe; issue #4 protection — don't let an unverified empty write
    //         through). Loaded state is only trustworthy when IsLoaded == true.
    //   - New content empty AND cloud non-empty       → BLOCK (the destructive case).
    //   - New content empty AND cloud empty/absent     → ALLOW (nothing to protect).
    public static bool ShouldBlockWrite(
        CloudFileCache cache,
        string canonPath,
        int newByteLength,
        out string reason
    )
    {
        reason = null;
        try
        {
            bool newIsEmpty = newByteLength <= EmptyByteThreshold;
            if (!newIsEmpty)
                return false; // Real content — never blocked (오탐 0).

            if (cache == null)
                return false; // No cache to consult — fail open.

            // Cache metadata is only authoritative once EnumerateUserFiles has
            // completed. Until then, FileExists/GetFileSize can't be trusted, so a
            // conservative block is the safe choice for an empty write (issue #4).
            if (!cache.IsLoaded)
            {
                reason =
                    $"빈 내용({newByteLength} bytes) 쓰기를 차단했습니다. "
                    + "클라우드 상태를 아직 확인하지 못해 안전을 위해 보류합니다.";
                PatchHelper.Log(
                    $"{Tag} BLOCK(cache-not-loaded) {canonPath}: new={newByteLength}B"
                );
                return true;
            }

            if (!cache.FileExists(canonPath))
            {
                PatchHelper.Log($"{Tag} ALLOW(cloud-absent) {canonPath}: new={newByteLength}B");
                return false; // Fresh file — empty write harmless.
            }

            int cloudSize = cache.GetFileSize(canonPath);
            if (cloudSize <= EmptyByteThreshold)
            {
                PatchHelper.Log(
                    $"{Tag} ALLOW(cloud-empty) {canonPath}: new={newByteLength}B cloud={cloudSize}B"
                );
                return false; // Cloud also empty/trivial — nothing to protect.
            }

            reason =
                $"빈 내용({newByteLength} bytes)이 클라우드의 기존 저장({cloudSize} bytes)을 "
                + "덮어쓰려 해 차단했습니다.";
            PatchHelper.Log(
                $"{Tag} BLOCK(empty-overwrite) {canonPath}: new={newByteLength}B cloud={cloudSize}B"
            );
            return true;
        }
        catch (Exception ex)
        {
            // A guard that throws must never break the save path. Fail OPEN — the
            // prevention layer is best-effort; Part A backup is the safety net.
            PatchHelper.Log($"{Tag} ERROR {canonPath}, allowing write: {ex.Message}");
            return false;
        }
    }

    // Fires a user-facing notification for a blocked write. Throttled per path so
    // a retry loop doesn't stack dialogs. Safe to call from the game's save
    // thread — the dialog itself is marshalled onto the main thread.
    public static void NotifyBlocked(string canonPath, string reason)
    {
        lock (_notifyLock)
        {
            if (!_notifiedPaths.Add(canonPath))
                return; // Already surfaced this path this session.
        }

        try
        {
            Notifier?.Invoke(canonPath, reason);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"{Tag} notify failed for {canonPath}: {ex.Message}");
        }
    }

    // Default notifier: shows an informational CloudConflictDialog on the main
    // thread. The block already preserved the cloud copy, so there's no choice to
    // make — the dialog is purely informational (close-only). Reuses the existing
    // dialog so styling/sizing stay consistent across foldable form factors.
    private static void DefaultNotify(string canonPath, string reason)
    {
        // CallDeferred marshals onto the main thread (the dialog touches the
        // scene tree). Mirrors RenderDiagnosticPatches' self-retry pattern.
        Callable.From(() => ShowDialog(canonPath, reason)).CallDeferred();
    }

    private static void ShowDialog(string canonPath, string reason)
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
                return;

            var parent = tree.Root;
            float scale = LauncherUI.ResolveScale(parent);
            float vh = LauncherUI.ResolveViewportHeight(parent);

            // Informational, close-only dialog (SyncDecision.Identical hides the
            // local/cloud choice buttons). customTitle / customSubtitle override
            // the default text with the block reason.
            var dialog = new CloudConflictDialog(
                new SaveProgressSummary(),
                new SaveProgressSummary(),
                localIsMoreRecent: false,
                scale,
                SyncDecision.Identical,
                vh,
                customSubtitle: reason,
                customTitle: "세이브 보호 — 클라우드 덮어쓰기 차단"
            );
            parent.AddChild(dialog);
            PatchHelper.Log($"{Tag} notified user for {canonPath}");
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"{Tag} dialog failed for {canonPath}: {ex.Message}");
        }
    }
}
