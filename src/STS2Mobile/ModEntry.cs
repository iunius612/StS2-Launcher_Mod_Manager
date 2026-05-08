using System;
using System.IO;
using System.Runtime.InteropServices;
using Godot;
using Godot.Bridge;
using Godot.NativeInterop;
using HarmonyLib;
using STS2Mobile.Launcher;
using STS2Mobile.Patches;

namespace STS2Mobile;

// Entry point for the mobile patcher. Bootstraps GodotSharp, applies all Harmony
// patches, and falls back to standalone launcher mode if game files aren't present.
public static class ModEntry
{
    private static Harmony _harmony;
    private static bool _applied = false;

    // Bootstraps GodotSharp by setting up DLL import resolver, native interop,
    // and managed callbacks. Called from gd_mono.cpp before Apply().
    [UnmanagedCallersOnly]
    public static int InitializeGodotSharp(
        IntPtr godotDllHandle,
        IntPtr outManagedCallbacks,
        IntPtr unmanagedCallbacks,
        int unmanagedCallbacksSize
    )
    {
        try
        {
            DllImportResolver dllImportResolver = new GodotDllImportResolver(
                godotDllHandle
            ).OnResolveDllImport;
            var coreApiAssembly = typeof(GodotObject).Assembly;
            NativeLibrary.SetDllImportResolver(coreApiAssembly, dllImportResolver);

            NativeFuncs.Initialize(unmanagedCallbacks, unmanagedCallbacksSize);
            ManagedCallbacks.Create(outManagedCallbacks);

            Console.Error.WriteLine("[STS2Mobile] GodotSharp bootstrapped successfully");
            return 1;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[STS2Mobile] GodotSharp bootstrap failed: {e}");
            return 0;
        }
    }

    [UnmanagedCallersOnly]
    public static void Apply()
    {
        if (_applied)
            return;
        _applied = true;

        // Must run before any HarmonyLib touch — once HarmonySharedState's .cctor
        // throws, every subsequent access re-throws the cached TypeInitializationException.
        EnsureWritableTempPath();

        PatchHelper.Log("[Build] Y700_issue19 test build (TMPDIR fix probe)");
        PatchHelper.Log("Initializing STS2Mobile...");

        _harmony = new Harmony("com.sts2mobile");

        // Game-assembly-independent — must run in both standalone-launcher and game modes
        // (issue #11 affects the launcher UI too on Fold6).
        RenderDiagnosticPatches.Apply(_harmony);

        // Game patches require sts2.dll; if missing, fall through to standalone launcher.
        try
        {
            // Mobile-compat shim for BaseLib v3.x async state-machine patches.
            // BaseLib's AsyncMethodCall.Create transpiler crashes the game on Mono Android
            // (Godot StringName::unref BUG on _draw_rect). Until root cause in MonoMod/Cecil
            // emit is fixed, prefix-disable BaseLib's state-machine surgery so the rest of
            // BaseLib (node factories, content patches, etc.) can load.
            // See .repro/issue8_root_cause.md.
            BaseLibCompatPatches.Apply(_harmony);
            ModelDbInitPatch.Apply(_harmony);
            PlatformPatches.Apply(_harmony);
            ReleaseInfoPatches.Apply(_harmony);
            SettingsPatches.Apply(_harmony);
            UiScalePatches.Apply(_harmony);
            MobileLayoutPatches.Apply(_harmony);
            EventLayoutPatches.Apply(_harmony);
            MerchantLayoutPatches.Apply(_harmony);
            AppLifecyclePatches.Apply(_harmony);
            TouchInputPatches.Apply(_harmony);
            CardRewardPatches.Apply(_harmony);
            EarlyAccessDisclaimerPatches.Apply(_harmony);
            FeedbackScreenPatches.Apply(_harmony);
            CombatBackgroundPatches.Apply(_harmony);
            LanMultiplayerPatcher.Apply(_harmony);
            ModLoaderPatches.Apply(_harmony);
            LauncherPatches.Apply(_harmony);
            SaveDiagnosticPatches.Apply(_harmony);

            PatchHelper.Log("All game patches applied.");
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"Game patches skipped (files not present): {ex.Message}");
            ScheduleStandaloneLauncher();
        }
    }

    private static void ScheduleStandaloneLauncher()
    {
        PatchHelper.Log("Scheduling standalone launcher...");
        Callable.From(CreateStandaloneLauncher).CallDeferred();
    }

    private static void CreateStandaloneLauncher()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            Callable.From(CreateStandaloneLauncher).CallDeferred();
            return;
        }

        var launcher = new LauncherUI();
        tree.Root.AddChild(launcher);
        launcher.Initialize();
        PatchHelper.Log("Standalone launcher displayed");
    }

    // MonoMod (used by HarmonyLib for runtime IL emit) writes dynamic assemblies
    // to Path.GetTempPath(). On Unix that resolves via TMPDIR env var with "/tmp"
    // as the fallback. Some Android ROMs (Lenovo ZUI 14 on Y700, issue #19) launch
    // app processes without TMPDIR set, so MonoMod tries to write to "/tmp/MonoMod_DMD_*.dll"
    // and HarmonySharedState's .cctor throws DirectoryNotFoundException — every patch
    // then fails with a cached TypeInitializationException, the launcher falls into
    // standalone mode, and "RESTART APP" loops forever.
    //
    // Strategy: probe Path.GetTempPath() writability. If it works (Samsung et al.),
    // change nothing. Otherwise redirect TMPDIR to an app-private dir derived from
    // the assembly's own location — Godot.OS APIs are unsafe to call this early
    // (per the gd_mono.cpp init-context constraint), so we walk up from
    // Assembly.Location instead.
    private static void EnsureWritableTempPath()
    {
        try
        {
            var existing = Path.GetTempPath();
            var probe = Path.Combine(existing, $"sts2_tmpdir_probe_{Guid.NewGuid():N}");
            try
            {
                File.WriteAllBytes(probe, Array.Empty<byte>());
                File.Delete(probe);
                return;
            }
            catch
            {
                // existing temp path not writable — fall through to override
            }

            // Java's setupAssemblies() copies STS2Mobile.dll to
            // <files>/.godot/mono/publish/arm64/. Walk up 4 levels to reach <files>.
            var asmLoc = typeof(ModEntry).Assembly.Location;
            if (string.IsNullOrEmpty(asmLoc))
            {
                PatchHelper.Log("[Diag] EnsureWritableTempPath: Assembly.Location empty");
                return;
            }
            var dir = Path.GetDirectoryName(asmLoc);
            for (int i = 0; i < 4 && !string.IsNullOrEmpty(dir); i++)
                dir = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(dir))
            {
                PatchHelper.Log($"[Diag] EnsureWritableTempPath: cannot derive files dir from {asmLoc}");
                return;
            }

            var appPrivateTmp = Path.Combine(dir, ".tmp");
            Directory.CreateDirectory(appPrivateTmp);
            System.Environment.SetEnvironmentVariable("TMPDIR", appPrivateTmp);
            System.Environment.SetEnvironmentVariable("TMP", appPrivateTmp);
            System.Environment.SetEnvironmentVariable("TEMP", appPrivateTmp);
            PatchHelper.Log(
                $"[Diag] TMPDIR overridden -> {appPrivateTmp} (issue #19, was {existing})"
            );

            // App-private dir is persistent (unlike /tmp on stock devices), so stale
            // MonoMod_DMD_*.dll would otherwise accumulate session over session.
            try
            {
                int cleared = 0;
                foreach (var f in Directory.GetFiles(appPrivateTmp, "MonoMod_DMD_*.dll"))
                {
                    try
                    {
                        File.Delete(f);
                        cleared++;
                    }
                    catch { }
                }
                if (cleared > 0)
                    PatchHelper.Log($"[Diag] Cleared {cleared} stale MonoMod_DMD files");
            }
            catch { }
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Diag] EnsureWritableTempPath failed: {ex.Message}");
        }
    }
}
