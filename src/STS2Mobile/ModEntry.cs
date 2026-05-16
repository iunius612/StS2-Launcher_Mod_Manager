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

        // issue #19/#27: .NET Path.GetTempPath() falls back to "/tmp" on Unix
        // when TMPDIR is unset; some Android ROMs (Lenovo ZUI 14, certain OneUI
        // builds) don't set TMPDIR for app processes and /tmp/ doesn't exist on
        // Android. MonoMod (Harmony's IL emitter) writes temp DLLs there, so
        // HarmonySharedState.cctor throws, every Harmony patch fails, and the
        // launcher is stuck in "RESTART APP" forever. Redirect TMPDIR to the
        // app's private cache dir before any Harmony work. Hardcoded package id
        // must match android/gradle.properties export_package_name.
        try
        {
            const string tmpDir =
                "/data/user/0/com.game.sts2launcher.modmanager/cache/MonoMod";
            Directory.CreateDirectory(tmpDir);
            System.Environment.SetEnvironmentVariable("TMPDIR", tmpDir);
            int cleaned = TryCleanStaleMonoModFiles(tmpDir);
            Console.Error.WriteLine(
                $"[STS2Mobile] [Diag] TMPDIR -> {tmpDir} (cleaned {cleaned} stale)"
            );
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[STS2Mobile] [Diag] TMPDIR override failed: {ex.Message}"
            );
        }

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

    private static int TryCleanStaleMonoModFiles(string dir)
    {
        int cleaned = 0;
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-7);
            foreach (var f in Directory.EnumerateFiles(dir, "MonoMod_*"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(f) < cutoff)
                    {
                        File.Delete(f);
                        cleaned++;
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
        return cleaned;
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
}
