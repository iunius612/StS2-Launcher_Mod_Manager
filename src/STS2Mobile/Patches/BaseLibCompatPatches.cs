using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace STS2Mobile.Patches;

// Mobile-compat shim for BaseLib v3.x. Three independent workarounds for
// MonoMod/Cecil/Mono-Android emit limitations that BaseLib trips over on
// the launcher's Mono Android runtime:
//
// 1) AsyncMethodCall.Create transpiler (issue #8): injects new yield states
//    into compiler-emitted async state-machine MoveNext methods. On Mono
//    Android this corrupts a Godot static StringName ("BUG: Unreferenced
//    static string to 0: _draw_rect"). Prefix-return the original IL so the
//    state-machine surgery never happens. Degrades async hooks
//    (AfterCardPlayed etc.) to no-op; rest of BaseLib works. See
//    .repro/issue8_root_cause.md.
//
// 2) CombatRoomFromSerializableRewardExtPatch (issue #32): a plain Prefix on
//    CombatRoom.FromSerializable whose wrapper generation throws
//    MissingMethodException on set_ShouldResumeParentEventAfterCombat — an
//    init-only setter (modreq IsExternalInit) in the original method body
//    that MonoMod's import path can't resolve on Mono Android. The setter
//    exists in the loaded sts2.dll bytes (PC and mobile use the same Steam
//    depot 2868840/public). Skip the whole patch class so PatchClassProcessor
//    never reaches UpdateWrapper. Degrades: RewardExtData (de)serialization
//    for mid-combat saves with custom-pool rewards. Most mods unaffected.
//
// 3) CustomEnum static-field fixup (issue #32): BaseLib's GenEnumValues
//    Prefix on ModelDb.Init is supposed to FieldInfo.SetValue unique IDs
//    onto 11 [CustomEnum] static TargetType fields in CustomTargetType. On
//    mobile the prefix never logs and the fields stay at default
//    TargetType.None (0). The Postfix ModelDbTargetTypeInitPatch then calls
//    Dictionary.Add(None, ...) 11 times -> second Add throws
//    ArgumentException ("Key: None") -> ModelDb.Init aborts -> black
//    screen. We run the same field assignment ourselves with Priority.First
//    on ModelDb.Init, using BaseLib's own CustomEnums.GenerateKey via
//    reflection so unique keys are produced regardless of whether BaseLib's
//    own prefix later runs.
public static class BaseLibCompatPatches
{
    private static Harmony _harmony;
    private static bool _wired;
    private static bool _customEnumFixupDone;
    private static Type _skipPatchContainerType;

    public static void Apply(Harmony harmony)
    {
        _harmony = harmony;
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        PatchHelper.Log("BaseLibCompatPatches: registered AssemblyLoad listener for BaseLib");
    }

    private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
    {
        if (_wired)
            return;
        if (args.LoadedAssembly.GetName().Name != "BaseLib")
            return;

        var asm = args.LoadedAssembly;
        TryPatchAsyncMethodCallCreate(asm);
        TryRegisterSkipFromSerializablePatch(asm);
        TryRegisterCustomEnumFixupOnModelDbInit(asm);
        _wired = true;
    }

    // ---- (1) AsyncMethodCall.Create skip --------------------------------------

    private static void TryPatchAsyncMethodCallCreate(Assembly baseLibAsm)
    {
        try
        {
            var asyncMethodCallType = baseLibAsm.GetType("BaseLib.Utils.Patching.AsyncMethodCall");
            if (asyncMethodCallType == null)
            {
                PatchHelper.Log(
                    "BaseLibCompat: AsyncMethodCall type not found in BaseLib assembly"
                );
                return;
            }
            var createMethod = AccessTools.Method(asyncMethodCallType, "Create");
            if (createMethod == null)
            {
                PatchHelper.Log("BaseLibCompat: AsyncMethodCall.Create method not found");
                return;
            }
            var prefix = AccessTools.Method(
                typeof(BaseLibCompatPatches),
                nameof(AsyncMethodCallCreatePrefix)
            );
            _harmony.Patch(createMethod, prefix: new HarmonyMethod(prefix));
            PatchHelper.Log(
                "Patched BaseLib.Utils.Patching.AsyncMethodCall.Create (state-machine hooks disabled for mobile compat)"
            );
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"BaseLibCompat: AsyncMethodCall.Create patch failed: {ex.Message}");
        }
    }

    public static bool AsyncMethodCallCreatePrefix(
        IEnumerable<CodeInstruction> code,
        ref List<CodeInstruction> __result
    )
    {
        Console.WriteLine(
            "[BaseLibCompat] Skipping AsyncMethodCall.Create (mobile workaround) — async hook will not fire"
        );
        __result = code.ToList();
        return false;
    }

    // ---- (2) Skip CombatRoomFromSerializableRewardExtPatch registration -------

    private static void TryRegisterSkipFromSerializablePatch(Assembly baseLibAsm)
    {
        try
        {
            _skipPatchContainerType =
                baseLibAsm.GetType(
                    "BaseLib.Patches.Rewards.CombatRoomFromSerializableRewardExtPatch"
                )
                ?? baseLibAsm
                    .GetTypes()
                    .FirstOrDefault(t => t.Name == "CombatRoomFromSerializableRewardExtPatch");
            if (_skipPatchContainerType == null)
            {
                PatchHelper.Log(
                    "BaseLibCompat: CombatRoomFromSerializableRewardExtPatch type not found, skip-shim inactive"
                );
                return;
            }

            var pcpType = typeof(Harmony).Assembly.GetType("HarmonyLib.PatchClassProcessor");
            if (pcpType == null)
            {
                PatchHelper.Log("BaseLibCompat: HarmonyLib.PatchClassProcessor type not found");
                return;
            }
            var patchMethod = AccessTools.Method(pcpType, "Patch");
            if (patchMethod == null)
            {
                PatchHelper.Log("BaseLibCompat: PatchClassProcessor.Patch method not found");
                return;
            }
            var prefix = AccessTools.Method(
                typeof(BaseLibCompatPatches),
                nameof(PatchClassProcessorPatchPrefix)
            );
            _harmony.Patch(patchMethod, prefix: new HarmonyMethod(prefix));
            PatchHelper.Log(
                $"Patched HarmonyLib.PatchClassProcessor.Patch (will skip {_skipPatchContainerType.FullName})"
            );
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"BaseLibCompat: FromSerializable skip patch failed: {ex.Message}");
        }
    }

    public static bool PatchClassProcessorPatchPrefix(
        object __instance,
        ref List<MethodInfo> __result
    )
    {
        if (_skipPatchContainerType == null)
            return true;
        try
        {
            var f = AccessTools.Field(__instance.GetType(), "containerType");
            if (f?.GetValue(__instance) is Type ctype && ctype == _skipPatchContainerType)
            {
                Console.WriteLine(
                    $"[BaseLibCompat] Skipping {ctype.FullName} (mobile workaround — init-setter modreq not importable by MonoMod on Android)"
                );
                __result = new List<MethodInfo>();
                return false;
            }
        }
        catch
        {
            // best-effort; fall through to normal path
        }
        return true;
    }

    // ---- (3) CustomEnum static-field fixup on ModelDb.Init -------------------

    private static void TryRegisterCustomEnumFixupOnModelDbInit(Assembly baseLibAsm)
    {
        try
        {
            var modelDbType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.ModelDb");
            if (modelDbType == null)
            {
                PatchHelper.Log("BaseLibCompat: ModelDb type not found, CustomEnum fixup inactive");
                return;
            }
            var initMethod = AccessTools.Method(modelDbType, "Init");
            if (initMethod == null)
            {
                PatchHelper.Log("BaseLibCompat: ModelDb.Init method not found");
                return;
            }
            var prefix = AccessTools.Method(
                typeof(BaseLibCompatPatches),
                nameof(ModelDbInitCustomEnumFixupPrefix)
            );
            var hm = new HarmonyMethod(prefix) { priority = Priority.First };
            _harmony.Patch(initMethod, prefix: hm);
            PatchHelper.Log("Patched ModelDb.Init with CustomEnum fixup prefix (Priority.First)");
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"BaseLibCompat: CustomEnum fixup wiring failed: {ex.Message}");
        }
    }

    // Manual replay of BaseLib's GenEnumValues.FindAndGenerate. On Mono Android
    // that prefix never executes (still under investigation — possibly Harmony
    // prefix-chain truncation after launcher's InitPrefix returns false, or
    // attribute/field reflection gap). Without it, every [CustomEnum] TargetType
    // field stays at default value 0 (TargetType.None), and BaseLib's
    // RegisterTargetTypes postfix crashes on duplicate-key Add.
    public static void ModelDbInitCustomEnumFixupPrefix()
    {
        if (_customEnumFixupDone)
            return;
        _customEnumFixupDone = true;

        try
        {
            var baseLibAsm = AppDomain
                .CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "BaseLib");
            if (baseLibAsm == null)
            {
                PatchHelper.Log("BaseLibCompat: CustomEnum fixup skipped (BaseLib not loaded)");
                return;
            }

            // Resolve BaseLib's CustomEnumAttribute / CustomEnums by short name —
            // namespace varies across BaseLib versions (3.1.3 ships them under
            // BaseLib.Patches.Content). GetType("BaseLib.CustomEnumAttribute") was
            // null on 3.1.3 and produced "fixup skipped" -> still got Key:None.
            Type customEnumAttr = null;
            Type customEnums = null;
            try
            {
                foreach (var t in baseLibAsm.GetTypes())
                {
                    if (customEnumAttr == null && t.Name == "CustomEnumAttribute")
                        customEnumAttr = t;
                    if (customEnums == null && t.Name == "CustomEnums")
                        customEnums = t;
                    if (customEnumAttr != null && customEnums != null)
                        break;
                }
            }
            catch (ReflectionTypeLoadException rtle)
            {
                foreach (var t in rtle.Types)
                {
                    if (t == null)
                        continue;
                    if (customEnumAttr == null && t.Name == "CustomEnumAttribute")
                        customEnumAttr = t;
                    if (customEnums == null && t.Name == "CustomEnums")
                        customEnums = t;
                }
            }
            var generateKey =
                customEnums == null
                    ? null
                    : AccessTools.Method(customEnums, "GenerateKey", new[] { typeof(FieldInfo) });
            if (customEnumAttr == null || generateKey == null)
            {
                PatchHelper.Log(
                    $"BaseLibCompat: CustomEnum fixup skipped — attr={customEnumAttr?.FullName ?? "null"} generateKey={(generateKey != null)}"
                );
                return;
            }
            PatchHelper.Log(
                $"BaseLibCompat: CustomEnum fixup resolved attr={customEnumAttr.FullName} generateKey={customEnums.FullName}.GenerateKey"
            );

            int assigned = 0;
            int skippedAlreadySet = 0;
            int failedPerField = 0;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch
                {
                    continue;
                }
                foreach (var type in types)
                {
                    FieldInfo[] fields;
                    try
                    {
                        fields = type.GetFields(
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                        );
                    }
                    catch
                    {
                        continue;
                    }
                    foreach (var field in fields)
                    {
                        try
                        {
                            if (!Attribute.IsDefined(field, customEnumAttr))
                                continue;
                            if (!field.FieldType.IsEnum)
                                continue;

                            var current = field.GetValue(null);
                            var defaultVal = Activator.CreateInstance(field.FieldType);
                            if (!Equals(current, defaultVal))
                            {
                                skippedAlreadySet++;
                                continue;
                            }
                            var key = generateKey.Invoke(null, new object[] { field });
                            field.SetValue(null, key);
                            assigned++;
                        }
                        catch (Exception inner)
                        {
                            failedPerField++;
                            PatchHelper.Log(
                                $"BaseLibCompat: CustomEnum fixup failed for {type.FullName}.{field.Name}: {inner.Message}"
                            );
                        }
                    }
                }
            }
            PatchHelper.Log(
                $"BaseLibCompat: CustomEnum fixup -> assigned={assigned} alreadySet={skippedAlreadySet} failed={failedPerField}"
            );
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"BaseLibCompat: CustomEnum fixup failed: {ex.Message}");
        }
    }
}
