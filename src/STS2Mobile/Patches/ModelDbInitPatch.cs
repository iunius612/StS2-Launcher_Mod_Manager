using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

namespace STS2Mobile.Patches;

// Replaces ModelDb.Init() with a two-phase initialization to avoid circular dependency
// crashes. Phase 1 pre-populates the registry with uninitialized objects so cross-type
// references resolve during construction. Phase 2 runs the actual constructors.
public static class ModelDbInitPatch
{
    private static bool _suppressContains = false;

    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(
            harmony,
            typeof(ModelDb),
            "Init",
            prefix: PatchHelper.Method(typeof(ModelDbInitPatch), nameof(InitPrefix))
        );
    }

    public static bool ContainsPrefix(ref bool __result)
    {
        if (_suppressContains)
        {
            __result = false;
            return false;
        }
        return true;
    }

    public static bool InitPrefix()
    {
        PatchHelper.Log("Running patched ModelDb.Init()");

        var modelDbType = typeof(ModelDb);

        var allSubtypesProp = modelDbType.GetProperty(
            "AllAbstractModelSubtypes",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
        );
        var types = (Type[])allSubtypesProp.GetValue(null);

        var getIdMethod = modelDbType.GetMethod(
            "GetId",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            null,
            new[] { typeof(Type) },
            null
        );

        var contentByIdField = modelDbType.GetField(
            "_contentById",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        var contentById = contentByIdField.GetValue(null);

        var dictType = contentById.GetType();
        var setItemMethod = dictType.GetMethod("set_Item");
        var keyType = dictType.GetGenericArguments()[0];
        var removeMethod = dictType.GetMethod("Remove", new[] { keyType });

        // AbstractModel.Id became a get-only autoprop in 0.108.0 that is only set
        // inside the constructor, and the constructor's duplicate check now reads
        // the dictionary directly (GetByIdOrNull) instead of the Contains() we
        // suppress below. We resolve the base type via the assembly so a namespace
        // move doesn't break the reflection.
        var abstractModelType = modelDbType.Assembly.GetType(
            "MegaCrit.Sts2.Core.Models.AbstractModel"
        );
        var idProp = abstractModelType?.GetProperty(
            "Id",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
        );
        var idBackingField = abstractModelType?.GetField(
            "<Id>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance
        );

        // Phase 1: Pre-populate dictionary with uninitialized objects
        PatchHelper.Log(
            $"Phase 1: Pre-registering {types.Length} types with uninitialized objects"
        );

        var typeObjects = new Dictionary<Type, object>();
        var typeIds = new Dictionary<Type, object>();
        int preRegCount = 0;

        for (int i = 0; i < types.Length; i++)
        {
            try
            {
                var type = types[i];
                var id = getIdMethod.Invoke(null, new object[] { type });
                var model = RuntimeHelpers.GetUninitializedObject(type);
                setItemMethod.Invoke(contentById, new[] { id, model });
                typeObjects[type] = model;
                typeIds[type] = id;
                preRegCount++;
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"Phase 1 - Failed to pre-register {types[i].Name}: {ex.Message}");
            }
        }

        PatchHelper.Log($"Phase 1 complete: {preRegCount} types pre-registered");

        // Temporarily suppress Contains() during Phase 2 so constructors don't
        // short-circuit when they check if their type is already registered.
        var harmony = new Harmony("com.sts2mobile.modeldb");
        var containsMethod = modelDbType.GetMethod(
            "Contains",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            null,
            new[] { typeof(Type) },
            null
        );
        var containsPrefix = typeof(ModelDbInitPatch).GetMethod(
            nameof(ContainsPrefix),
            BindingFlags.Public | BindingFlags.Static
        );
        harmony.Patch(containsMethod, new HarmonyMethod(containsPrefix));

        // Phase 2: Run constructors on pre-allocated objects
        PatchHelper.Log("Phase 2: Running constructors");

        _suppressContains = true;

        int successCount = 0;
        var failed = new List<Type>();

        foreach (var type in types)
        {
            if (!typeObjects.ContainsKey(type))
                continue;

            var id = typeIds[type];
            var model = typeObjects[type];

            // 0.108.0's constructor throws DuplicateModelException if the id is
            // already present (via GetByIdOrNull). Remove the pre-registered entry
            // so the constructor sees an empty slot, sets its get-only Id, then
            // re-register the same in-place instance. The finally guarantees the
            // entry is restored even if the constructor throws — a missing entry
            // would surface later as ModelNotFoundException.
            try
            {
                removeMethod.Invoke(contentById, new[] { id });

                RuntimeHelpers.RunClassConstructor(type.TypeHandle);

                var ctor = type.GetConstructor(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null
                );
                if (ctor != null)
                {
                    ctor.Invoke(model, null);
                }

                successCount++;
            }
            catch (Exception ex)
            {
                failed.Add(type);
                var inner = ex;
                while (inner.InnerException != null)
                    inner = inner.InnerException;
                PatchHelper.Log(
                    $"Phase 2 - Failed {type.Name}: {inner.GetType().Name}: {inner.Message}"
                );
            }
            finally
            {
                setItemMethod.Invoke(contentById, new[] { id, model });
            }
        }

        _suppressContains = false;
        harmony.Unpatch(containsMethod, containsPrefix);

        // Defense: any instance whose constructor failed still has a null get-only
        // Id, and 0.108.0's ModelDb.InitIds() dereferences Id.Category -> NRE that
        // kills GameStartup (black screen). Set the backing field directly from the
        // key we already computed so a partial failure can't brick the whole boot.
        if (idProp != null && idBackingField != null)
        {
            int patchedIds = 0;
            foreach (var kvp in typeIds)
            {
                if (!typeObjects.TryGetValue(kvp.Key, out var model))
                    continue;
                try
                {
                    if (idProp.GetValue(model) == null)
                    {
                        idBackingField.SetValue(model, kvp.Value);
                        patchedIds++;
                    }
                }
                catch (Exception ex)
                {
                    PatchHelper.Log($"Id backfill failed for {kvp.Key.Name}: {ex.Message}");
                }
            }
            if (patchedIds > 0)
                PatchHelper.Log($"Backfilled Id on {patchedIds} models with failed constructors");
        }
        else
        {
            PatchHelper.Log(
                "Id backing field not found (older game version?); skipping Id backfill"
            );
        }

        if (failed.Count > 0)
        {
            PatchHelper.Log(
                $"WARNING: {failed.Count}/{types.Length} types had constructor errors:"
            );
            foreach (var type in failed)
                PatchHelper.Log($"  - {type.FullName}");
        }
        else
        {
            PatchHelper.Log($"All {successCount} model types registered successfully");
        }

        return false;
    }
}
