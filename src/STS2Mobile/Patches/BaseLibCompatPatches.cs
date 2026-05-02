using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace STS2Mobile.Patches;

// Mobile-compat shim for BaseLib v3.x.
//
// BaseLib's BaseLib.Utils.Patching.AsyncMethodCall.Create transpiler injects new
// yield states into compiler-emitted async state-machine MoveNext methods. On the
// mobile launcher (Mono Android + MonoMod/Cecil-based emit) this transpiler crashes
// the renderer with a Godot StringName double-unref ("BUG: Unreferenced static
// string to 0: _draw_rect" at string_name.cpp:116). The crash happens regardless of
// MonoILFixup behavior — verified by leaving raw operands alone vs declaring new
// locals (both produce the same crash). Root cause appears to be in the IL emission
// pipeline (Cecil/MonoMod handling of BaseLib's state-machine surgery), not the
// raw-local fixup pass.
//
// Until the underlying emit issue is identified and fixed, this shim prefixes
// AsyncMethodCall.Create to return the original IL unchanged. Effect:
//   - BaseLib loads (DLL + PCK init succeeds)
//   - Node factories, config UI, CustomPile patch, content patches: WORK
//   - Async hooks (AfterCardPlayed, BeforePlay, etc.): DISABLED (no-op)
//   - Mods that depend on BaseLib's non-hook features: WORK
//   - Mods that depend on BaseLib's hook system: load but their hooks never fire
//
// This is a degraded-mode workaround, not a real fix.
public static class BaseLibCompatPatches
{
    private static Harmony _harmony;
    private static bool _patched;

    public static void Apply(Harmony harmony)
    {
        _harmony = harmony;
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        PatchHelper.Log("BaseLibCompatPatches: registered AssemblyLoad listener for BaseLib");
    }

    private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
    {
        if (_patched) return;
        var asmName = args.LoadedAssembly.GetName().Name;
        if (asmName != "BaseLib") return;
        try
        {
            var asyncMethodCallType = args.LoadedAssembly.GetType("BaseLib.Utils.Patching.AsyncMethodCall");
            if (asyncMethodCallType == null)
            {
                PatchHelper.Log("BaseLibCompat: AsyncMethodCall type not found in BaseLib assembly");
                return;
            }
            var createMethod = AccessTools.Method(asyncMethodCallType, "Create");
            if (createMethod == null)
            {
                PatchHelper.Log("BaseLibCompat: AsyncMethodCall.Create method not found");
                return;
            }
            var prefix = AccessTools.Method(typeof(BaseLibCompatPatches), nameof(AsyncMethodCallCreatePrefix));
            _harmony.Patch(createMethod, prefix: new HarmonyMethod(prefix));
            _patched = true;
            PatchHelper.Log("Patched BaseLib.Utils.Patching.AsyncMethodCall.Create (state-machine hooks disabled for mobile compat)");
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"BaseLibCompat: failed to patch on load: {ex.Message}");
        }
    }

    public static bool AsyncMethodCallCreatePrefix(IEnumerable<CodeInstruction> code, ref List<CodeInstruction> __result)
    {
        Console.WriteLine("[BaseLibCompat] Skipping AsyncMethodCall.Create (mobile workaround) — async hook will not fire");
        __result = code.ToList();
        return false;
    }
}
