using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace STS2Mobile.Patches;

// Issue #55: BaseLib v3.3.5 + HextechRunes softlock at character select.
//
// Root cause (confirmed by decompile): Mono Android's DynamicMethod JIT cannot
// bind a call to an init-only property setter — its signature carries
// modreq(System.Runtime.CompilerServices.IsExternalInit) on the return, and the
// token fails to resolve at JIT time ("MissingMethodException: Method not found:
// void ...set_X(...)"). This ONLY affects DYNAMICALLY emitted methods; normal
// AOT/JIT handles init setters fine (the game uses them everywhere). So any
// Harmony patch that copies an original method whose body sets an init-only
// property re-emits that setter call into a DMD and crashes on Mono Android:
//   #1 BaseLib ExtendedSavePatches postfixes the game's source-generated
//      MegaCritSerializerContext.<Serializable*>PropInit, whose body builds
//      new JsonPropertyInfoValues<T>{ IsProperty = ..., ... } (System.Text.Json
//      9.0.7, all { get; init; }).
//   #2 HextechRunes patches a method that sets DamageResult.UnblockedDamage
//      (game type, { get; init; }).
//   Same wall previously hit issue #32 (set_ShouldResumeParentEventAfterCombat).
//
// Fix: prefix MonoMod.Utils' DMD emit leaf (ILGeneratorShimExt.DynEmit). When it
// is about to emit a call/callvirt to a TRIVIAL init-only auto-property setter,
// rewrite it to a direct `stfld <backingField>`. This is stack-neutral
// (`call void set_X` and `stfld field` both consume this+value and push
// nothing) and the backing field carries no modreq, so the DMD JITs cleanly on
// Mono Android. We only rewrite when the setter body is verified to be exactly
// `ldarg.0; ldarg.1; stfld <field>; ret` (a plain auto-property), so semantics
// are identical; anything else falls through to the original emit unchanged.
//
// Registered before BaseLibCompatPatches/game patches so it intercepts every
// subsequent Harmony wrapper generation (launcher, BaseLib, and third-party
// mods alike). Target is resolved by reflection — MonoMod.Utils is not a compile
// reference; it is loaded alongside Harmony at runtime.
public static class InitSetterEmitPatches
{
    private const string IsExternalInitFullName =
        "System.Runtime.CompilerServices.IsExternalInit";

    // setter -> backing field to store into, or null when not a rewritable
    // trivial init-only auto-property. Doubles as a per-setter analysis cache so
    // the reflection work runs once and logging is de-duplicated.
    private static readonly Dictionary<MethodInfo, FieldInfo> _rewriteCache = new();

    // ---- (A) DynEmit init-setter -> stfld rewrite -----------------------------

    public static void Apply(Harmony harmony)
    {
        try
        {
            var shimExtType = AccessTools.TypeByName("MonoMod.Utils.Cil.ILGeneratorShimExt");
            if (shimExtType == null)
            {
                PatchHelper.Log(
                    "InitSetterEmit: MonoMod.Utils.Cil.ILGeneratorShimExt not found — init-setter fix inactive"
                );
                return;
            }

            var dynEmit = AccessTools.Method(
                shimExtType,
                "DynEmit",
                new[] { typeof(ILGenerator), typeof(OpCode), typeof(object) }
            );
            if (dynEmit == null)
            {
                PatchHelper.Log(
                    "InitSetterEmit: ILGeneratorShimExt.DynEmit(ILGenerator,OpCode,object) not found — init-setter fix inactive"
                );
                return;
            }

            var prefix = AccessTools.Method(typeof(InitSetterEmitPatches), nameof(DynEmitPrefix));
            harmony.Patch(dynEmit, prefix: new HarmonyMethod(prefix));
            PatchHelper.Log(
                "Patched MonoMod.Utils.Cil.ILGeneratorShimExt.DynEmit (init-only setter -> backing-field store for Mono Android)"
            );
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"InitSetterEmit: DynEmit patch failed: {ex.Message}");
        }
    }

    // Prefix on DynEmit(ILGenerator il, OpCode opcode, object operand). Positional
    // injection (__0/__1/__2) avoids depending on parameter names. Returns false
    // (skip original) only after emitting the equivalent stfld; otherwise true so
    // the original emit runs unchanged. Never throws into the emit pipeline.
    public static bool DynEmitPrefix(object __0, object __1, object __2, ref object __result)
    {
        try
        {
            if (__0 is not ILGenerator ilgen)
                return true;
            if (__1 is not OpCode opcode)
                return true;
            if (__2 is not MethodInfo setter)
                return true;
            if (opcode != OpCodes.Call && opcode != OpCodes.Callvirt)
                return true;
            if (!setter.IsSpecialName || !setter.Name.StartsWith("set_", StringComparison.Ordinal))
                return true;

            var backing = ResolveRewrite(setter);
            if (backing == null)
                return true;

            ilgen.Emit(OpCodes.Stfld, backing);
            __result = null;
            return false; // skip original DynEmit(call/callvirt set_X)
        }
        catch (Exception ex)
        {
            // Emit must never be broken by this shim — fall back to original.
            PatchHelper.Log($"InitSetterEmit: prefix error, falling through: {ex.Message}");
            return true;
        }
    }

    // Returns the backing field to store into when `setter` is a trivial
    // init-only auto-property setter (ldarg.0; ldarg.1; stfld <field>; ret with a
    // modreq(IsExternalInit) return), otherwise null. Cached per setter; logs the
    // first time each distinct setter is accepted for rewrite.
    private static FieldInfo ResolveRewrite(MethodInfo setter)
    {
        if (_rewriteCache.TryGetValue(setter, out var cached))
            return cached;

        FieldInfo backing = null;
        try
        {
            if (IsInitOnly(setter) && IsTrivialAutoPropertySetter(setter))
            {
                var propName = setter.Name.Substring(4); // strip "set_"
                backing = setter.DeclaringType?.GetField(
                    "<" + propName + ">k__BackingField",
                    BindingFlags.Instance | BindingFlags.NonPublic
                );
                if (backing != null)
                {
                    PatchHelper.Log(
                        $"InitSetterEmit: rewriting init setter -> stfld for {setter.DeclaringType?.FullName}.{setter.Name}"
                    );
                }
            }
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"InitSetterEmit: analysis failed for {setter.Name}: {ex.Message}");
            backing = null;
        }

        _rewriteCache[setter] = backing;
        return backing;
    }

    private static bool IsInitOnly(MethodInfo setter)
    {
        var mods = setter.ReturnParameter?.GetRequiredCustomModifiers();
        if (mods == null)
            return false;
        foreach (var m in mods)
        {
            if (m.FullName == IsExternalInitFullName)
                return true;
        }
        return false;
    }

    // True only when the body is exactly `ldarg.0; ldarg.1; stfld <token>; ret`,
    // the canonical compiler-generated auto-property setter. Verifying this makes
    // the call->stfld rewrite provably semantics-preserving.
    private static bool IsTrivialAutoPropertySetter(MethodInfo setter)
    {
        var body = setter.GetMethodBody();
        var il = body?.GetILAsByteArray();
        if (il == null || il.Length != 8)
            return false;
        return il[0] == 0x02 // ldarg.0
            && il[1] == 0x03 // ldarg.1
            && il[2] == 0x7D // stfld <4-byte token il[3..6]>
            && il[7] == 0x2A; // ret
    }

    // ---- (B) One-shot #3 trigger diagnostic (observe-only) --------------------

    // #3 symptom: KeyNotFoundException 'ACT.OVERGROWTH' from UnlockState..cctor ->
    // ModelDb.get_AllEncounters -> ModelDb.Acts -> Get<Overgrowth>() ->
    // _contentById["ACT.OVERGROWTH"]. Overgrowth is a base act registered by
    // ModelDb.Init's own body, so its absence means Init's body never populated
    // _contentById before AllEncounters was read. This trace records, exactly
    // once, whether Init's body started/completed and whether AllEncounters was
    // read before Init completed (with the calling stack). Pure logging — every
    // hook is void (original always runs) and fully guarded.
    private static bool _initStarted;
    private static bool _initCompleted;
    private static bool _earlyAccessLogged;

    public static void ApplyInitTrace(Harmony harmony)
    {
        try
        {
            var modelDb = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.ModelDb");
            if (modelDb == null)
            {
                PatchHelper.Log("InitSetterEmit: ModelDb not found — #3 trace inactive");
                return;
            }

            var init = AccessTools.Method(modelDb, "Init");
            if (init != null)
            {
                harmony.Patch(
                    init,
                    prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(InitSetterEmitPatches), nameof(InitPrefix))
                    ),
                    postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(InitSetterEmitPatches), nameof(InitPostfix))
                    )
                );
                PatchHelper.Log("Patched ModelDb.Init (#3 trace: start/complete)");
            }

            var allEnc = modelDb.GetProperty(
                "AllEncounters",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
            );
            var getter = allEnc?.GetGetMethod(true);
            if (getter != null)
            {
                harmony.Patch(
                    getter,
                    prefix: new HarmonyMethod(
                        AccessTools.Method(
                            typeof(InitSetterEmitPatches),
                            nameof(AllEncountersPrefix)
                        )
                    )
                );
                PatchHelper.Log("Patched ModelDb.AllEncounters getter (#3 trace: early access)");
            }
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"InitSetterEmit: #3 trace wiring failed: {ex.Message}");
        }
    }

    public static void InitPrefix()
    {
        if (_initStarted)
            return;
        _initStarted = true;
        PatchHelper.Log("[Issue55-Trace] ModelDb.Init prefix reached (body about to run)");
    }

    public static void InitPostfix()
    {
        if (_initCompleted)
            return;
        _initCompleted = true;
        PatchHelper.Log("[Issue55-Trace] ModelDb.Init body completed");
    }

    public static void AllEncountersPrefix()
    {
        try
        {
            if (_initCompleted || _earlyAccessLogged)
                return;
            _earlyAccessLogged = true;
            PatchHelper.Log(
                "[Issue55-Trace] ModelDb.AllEncounters read BEFORE Init completed "
                    + $"(initStarted={_initStarted}). Stack:\n{Environment.StackTrace}"
            );
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Issue55-Trace] AllEncounters trace failed: {ex.Message}");
        }
    }
}
