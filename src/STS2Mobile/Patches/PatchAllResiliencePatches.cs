using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace STS2Mobile.Patches;

// Issue #55 defense-in-depth. BaseLib isolates each of its own patch classes in
// a per-class try/catch (TryPatchAll), but third-party mods usually call plain
// Harmony.PatchAll(), where a single patch class that fails to apply throws out
// of PatchClassProcessor.Patch() and aborts the caller's whole loop. On Mono
// Android the dominant failure is a method that can't be imported/JITted into a
// DynamicMethod (init-only setter modreq — see InitSetterEmitPatches — but also
// version skew or a bad image). InitSetterEmitPatches fixes the init-setter root
// cause generally; this finalizer is the safety net for any residual import
// failure: it degrades the offending patch class to a no-op instead of letting
// one mod's failure cascade into a boot-time softlock, generalizing BaseLib's
// own per-class isolation to every PatchAll caller.
//
// PatchClassProcessor.Patch() (Harmony 2.4.2) always rethrows on failure via
// ReportException — either the HarmonyException it built (InnerException = the
// real cause) or a fresh HarmonyException wrapping it. So a finalizer here
// reliably receives the failure. We only swallow import-failure exceptions;
// anything else (argument errors, our own bugs, unexpected states) is rethrown
// unchanged so it stays visible.
public static class PatchAllResiliencePatches
{
    public static void Apply(Harmony harmony)
    {
        try
        {
            var pcpType = typeof(Harmony).Assembly.GetType("HarmonyLib.PatchClassProcessor");
            if (pcpType == null)
            {
                PatchHelper.Log(
                    "PatchAllResilience: HarmonyLib.PatchClassProcessor not found — resilience inactive"
                );
                return;
            }
            var patchMethod = AccessTools.Method(pcpType, "Patch", Type.EmptyTypes);
            if (patchMethod == null)
            {
                PatchHelper.Log(
                    "PatchAllResilience: PatchClassProcessor.Patch() not found — resilience inactive"
                );
                return;
            }
            var finalizer = AccessTools.Method(
                typeof(PatchAllResiliencePatches),
                nameof(PatchFinalizer)
            );
            harmony.Patch(patchMethod, finalizer: new HarmonyMethod(finalizer));
            PatchHelper.Log(
                "Patched HarmonyLib.PatchClassProcessor.Patch (import-failure resilience finalizer)"
            );
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"PatchAllResilience: finalizer wiring failed: {ex.Message}");
        }
    }

    // Finalizer on PatchClassProcessor.Patch(). Returning null suppresses the
    // exception; returning it (or any Exception) lets it propagate. On an import
    // failure we suppress and hand back an empty replacement list so the caller's
    // PatchAll continues with the remaining classes.
    public static Exception PatchFinalizer(
        Exception __exception,
        ref List<MethodInfo> __result,
        object __instance
    )
    {
        if (__exception == null)
            return null;
        if (!IsImportFailure(__exception))
            return __exception; // not our concern — rethrow unchanged

        var container = TryGetContainerName(__instance);
        PatchHelper.Log(
            $"[Issue55-Resilience] Degraded patch class '{container}' — import failure swallowed: "
                + DescribeImportFailure(__exception)
        );
        __result = new List<MethodInfo>();
        return null; // suppress
    }

    // Walks the InnerException chain (which unwraps HarmonyException) for a
    // method/type import failure.
    private static bool IsImportFailure(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (
                e is MissingMethodException
                || e is TypeLoadException
                || e is BadImageFormatException
            )
                return true;
        }
        return false;
    }

    private static string DescribeImportFailure(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (
                e is MissingMethodException
                || e is TypeLoadException
                || e is BadImageFormatException
            )
                return $"{e.GetType().Name}: {e.Message}";
        }
        return ex.GetType().Name;
    }

    private static string TryGetContainerName(object patchClassProcessor)
    {
        try
        {
            var f = AccessTools.Field(patchClassProcessor?.GetType(), "containerType");
            if (f?.GetValue(patchClassProcessor) is Type t)
                return t.FullName ?? t.Name;
        }
        catch
        {
            // best-effort
        }
        return "<unknown>";
    }
}
