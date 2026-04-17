using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace STS2Mobile.Patches;

// Redirects the game's built-in mod loader to scan AppPaths.ExternalModsDir
// (/storage/emulated/0/StS2Launcher/Mods) instead of the "mods" folder next to
// the game executable. A Harmony transpiler rewrites the relevant IL inside
// ModManager.Initialize so the game's own recursive scanner walks our path;
// the Steam-only enumerator is short-circuited because Android has no
// Steamworks runtime.
public static class ModLoaderPatches
{
    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(
            harmony,
            typeof(ModManager),
            "Initialize",
            transpiler: PatchHelper.Method(typeof(ModLoaderPatches), nameof(InitializeTranspiler))
        );
        PatchHelper.Patch(
            harmony,
            typeof(ModManager),
            "ReadSteamMods",
            prefix: PatchHelper.Method(typeof(ModLoaderPatches), nameof(ReadSteamModsPrefix))
        );
    }

    // Rewrites `Path.Combine(directoryName, "mods")` inside ModManager.Initialize
    // to push AppPaths.ExternalModsDir directly. No reflection on private fields,
    // so the patch survives rebuilds that rename backing fields.
    public static IEnumerable<CodeInstruction> InitializeTranspiler(
        IEnumerable<CodeInstruction> instructions
    )
    {
        var matcher = new CodeMatcher(instructions)
            .MatchStartForward(new CodeMatch(OpCodes.Ldstr, "mods"));

        if (matcher.IsValid)
        {
            // IL pattern is: ldloc directoryName, ldstr "mods", call Path.Combine.
            // Drop all three and push the external path literal instead.
            matcher.Advance(-1);
            matcher.RemoveInstructions(3);
            matcher.InsertAndAdvance(
                new CodeInstruction(OpCodes.Ldstr, AppPaths.ExternalModsDir)
            );
            PatchHelper.Log($"[Mods] Redirected ModManager.Initialize to {AppPaths.ExternalModsDir}");
        }
        else
        {
            PatchHelper.Log(
                "[Mods] Warning: could not locate \"mods\" ldstr in ModManager.Initialize; "
                    + "external mods will be ignored."
            );
        }

        return matcher.InstructionEnumeration();
    }

    // Skip the Steam-backed mod enumeration on Android (no Steamworks runtime).
    public static bool ReadSteamModsPrefix() => false;
}
