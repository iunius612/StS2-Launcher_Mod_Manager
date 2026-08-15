using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace STS2Mobile.Patches;

// EXPERIMENTAL mod-guard: attributes runtime C# exceptions to the mod that
// caused them, so the user can be told "mod X is crashing" instead of digging
// through logcat. Two attribution signals:
//   1. a stack frame whose assembly is a tracked mod assembly, or
//   2. a patched-method frame whose Harmony patch owners include a mod.
// Indirect damage (a mod corrupting state that the game trips over later)
// leaves no mod frame and is NOT attributable — by design we stay silent then.
//
// Observe-only: hooks Godot's central managed-exception logger with a postfix
// (original logging behavior untouched) plus AppDomain.UnhandledException.
// Fail-safe: attribution errors are swallowed; worst case is no alert, never
// a behavior change.
public static class ModExceptionAttributionPatches
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, int> _logCounts = new();
    private static bool _firstObserved;

    // issue #87 A/B test: patching LogException re-emits Godot's central
    // managed-exception logger as a Harmony DMD — the same emit class that
    // corrupted engine StringNames in issue #8. Reporter's chest-room crash
    // (`BUG: Unreferenced static string to 0` storm) started 11s after this
    // postfix first fired, so this build ships with the postfix OFF to isolate
    // it. AppDomain.UnhandledException observation below stays on (no re-emit
    // involved). Flip back to true (or delete the gate) once verdict is in.
    private const bool InstallLogExceptionPostfix = false;

    public static void Apply(Harmony harmony)
    {
#pragma warning disable CS0162 // A/B const gate above makes the install branch unreachable
        try
        {
            var type = AccessTools.TypeByName("Godot.NativeInterop.ExceptionUtils");
            var target = AccessTools.Method(type, "LogException");
            if (!InstallLogExceptionPostfix)
            {
                PatchHelper.Log(
                    "[Issue87] A/B: LogException postfix DISABLED (AppDomain observer only)"
                );
            }
            else if (target == null)
            {
                PatchHelper.Log("[ModGuard] ExceptionUtils.LogException not found — observer skipped");
            }
            else
            {
                harmony.Patch(
                    target,
                    postfix: new HarmonyMethod(
                        typeof(ModExceptionAttributionPatches),
                        nameof(LogExceptionPostfix)
                    )
                );
                PatchHelper.Log("[ModGuard] Exception observer installed (ExceptionUtils.LogException)");
            }
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[ModGuard] Exception observer install failed: {ex.Message}");
        }

        try
        {
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Observe(args.ExceptionObject as Exception, "unhandled");
        }
        catch { }
#pragma warning restore CS0162
    }

    private static void LogExceptionPostfix(Exception e) => Observe(e, "godot");

    private static void Observe(Exception e, string channel)
    {
        try
        {
            if (e == null)
                return;
            if (!_firstObserved)
            {
                _firstObserved = true;
                PatchHelper.Log(
                    $"[ModGuard] Exception observer active (first: {e.GetType().Name} via {channel})"
                );
            }

            var (mod, via) = Attribute(e);
            if (mod == null)
                return;

            // Cap identical (mod, exception type) log lines so a postfix that
            // throws every frame during combat can't flood logcat.
            var key = mod + "|" + e.GetType().Name;
            lock (_lock)
            {
                _logCounts.TryGetValue(key, out var n);
                _logCounts[key] = n + 1;
                if (n >= 3)
                    return;
            }

            PatchHelper.Log(
                $"[ModGuard] Exception attributed to mod '{mod}' ({via}): "
                    + $"{e.GetType().Name}: {e.Message}"
            );

            // issue #76 — the alert itself is gated on the launcher's Debug
            // toggle inside ModGuardAlert (Debug: OFF → never shown, crash-grade
            // included). Attribution and the log line above are unconditional,
            // so nothing is lost for diagnosis either way.
            ModGuardAlert.ShowForMod(mod, e.GetType().Name);
        }
        catch { }
    }

    private static (string mod, string via) Attribute(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            StackFrame[] frames;
            try
            {
                frames = new StackTrace(e, false).GetFrames();
            }
            catch
            {
                continue;
            }
            if (frames == null)
                continue;

            foreach (var frame in frames)
            {
                MethodBase method = null;
                try
                {
                    method = frame.GetMethod();
                }
                catch { }
                if (method == null)
                    continue;

                // Signal 1: the throwing frame IS mod code.
                var asm = method.DeclaringType?.Assembly ?? method.Module?.Assembly;
                if (asm != null && ModAssemblyRegistry.IsModAssembly(asm))
                    return (
                        asm.GetName().Name,
                        $"in {method.DeclaringType?.Name}.{method.Name}"
                    );

                // Signal 2: the frame is a Harmony-patched method — map back to
                // the original and see whether a mod owns any patch on it.
                try
                {
                    var original = Harmony.GetOriginalMethodFromStackframe(frame);
                    if (original == null)
                        continue;
                    var info = Harmony.GetPatchInfo(original);
                    if (info == null)
                        continue;
                    var owner = info
                        .Prefixes.Concat(info.Postfixes)
                        .Concat(info.Transpilers)
                        .Concat(info.Finalizers)
                        .Select(p => p.PatchMethod?.DeclaringType?.Assembly)
                        .FirstOrDefault(a => a != null && ModAssemblyRegistry.IsModAssembly(a));
                    if (owner != null)
                        return (
                            owner.GetName().Name,
                            $"patch on {original.DeclaringType?.Name}.{original.Name}"
                        );
                }
                catch { }
            }
        }
        return (null, null);
    }
}
