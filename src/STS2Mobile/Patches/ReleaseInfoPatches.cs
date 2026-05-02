using System;
using System.IO;
using System.Text.Json.Nodes;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Debug;

namespace STS2Mobile.Patches;

// Issue #9: game v0.103.x propagates null ReleaseInfo into three visible
// symptoms — main-menu version "????", run-history "NON-RELEASE-VERSION",
// and LAN multiplayer "Version mismatch" handshake rejection.
//
// Root cause: ReleaseInfoManager.LoadConfig only looks next to
// OS.GetExecutablePath(), which on Android is the APK path — release_info.json
// never sits there. PC builds ship the file next to the game executable so the
// game is happy on PC but blank on mobile.
//
// Fix: postfix LoadConfig. When the original returns null, retry from the
// launcher's downloaded game files dir (OS.GetDataDir()/game/release_info.json),
// which is the same JSON Megacrit ships in the game depot.
public static class ReleaseInfoPatches
{
    public static void Apply(Harmony harmony)
    {
        // Apply must stay free of Godot OS calls — patches Apply runs from
        // gd_mono.cpp init context where OS singleton may not be ready, and
        // a native fault here aborts the whole patch chain. Diagnostic dump
        // lives inside the postfix instead, which fires from the main thread.
        PatchHelper.Patch(
            harmony,
            typeof(ReleaseInfoManager),
            "LoadConfig",
            postfix: PatchHelper.Method(typeof(ReleaseInfoPatches), nameof(LoadConfigPostfix))
        );
    }

    public static void LoadConfigPostfix(ref ReleaseInfo __result)
    {
        try
        {
            var execPath = OS.GetExecutablePath();
            var dataDir = OS.GetDataDir();
            var execDirCandidate = Path.Combine(
                Path.GetDirectoryName(execPath) ?? string.Empty,
                "release_info.json"
            );
            var gameDirCandidate = Path.Combine(dataDir, "game", "release_info.json");

            PatchHelper.Log($"[ReleaseInfo] OS.GetExecutablePath()='{execPath}'");
            PatchHelper.Log($"[ReleaseInfo] OS.GetDataDir()='{dataDir}'");
            PatchHelper.Log(
                $"[ReleaseInfo] exec-dir candidate='{execDirCandidate}' exists={File.Exists(execDirCandidate)}"
            );
            PatchHelper.Log(
                $"[ReleaseInfo] game-dir candidate='{gameDirCandidate}' exists={File.Exists(gameDirCandidate)}"
            );
            PatchHelper.Log(
                $"[ReleaseInfo] original LoadConfig returned: {(__result == null ? "null" : $"populated (Version={__result.Version})")}"
            );

            if (__result != null)
                return;

            if (!File.Exists(gameDirCandidate))
            {
                PatchHelper.Log(
                    "[ReleaseInfo] no fallback file at game dir — main-menu version, run-history BuildId, and LAN handshake will all use the broken default"
                );
                return;
            }

            var text = File.ReadAllText(gameDirCandidate);
            var head = text.Length > 300 ? text.Substring(0, 300) : text;
            PatchHelper.Log(
                $"[ReleaseInfo] game-dir file size={text.Length}B, head: {head.Replace("\n", " ")}"
            );

            var node = JsonNode.Parse(text);
            if (node == null)
            {
                PatchHelper.Log("[ReleaseInfo] fallback JSON parse returned null root");
                return;
            }

            var commit = (string)node["commit"] ?? string.Empty;
            var version = (string)node["version"] ?? string.Empty;
            var branch = (string)node["branch"] ?? string.Empty;
            var dateStr = (string)node["date"];
            DateTime date = DateTime.TryParse(dateStr, out var parsed)
                ? parsed
                : DateTime.UtcNow;

            __result = new ReleaseInfo
            {
                Commit = commit,
                Version = version,
                Date = date,
                Branch = branch,
            };

            PatchHelper.Log(
                $"[ReleaseInfo] fallback succeeded: Version='{version}' Branch='{branch}' Commit='{commit}' Date='{date:yyyy-MM-dd}'"
            );
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[ReleaseInfo] postfix failed: {ex}");
        }
    }
}
