using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace STS2Mobile.Modding;

// Bridges Godot C# to GodotApp.java's SAF (Storage Access Framework) picker.
// Java copies each chosen URI into the app cache and exposes the absolute paths;
// this class polls until the picker closes and returns all picked paths.
public static class SafBridge
{
    public static async Task<string[]> PickZipsToCacheAsync(CancellationToken ct = default)
    {
        var godotApp = GetGodotApp();
        if (godotApp == null)
        {
            PatchHelper.Log("[Mods] SafBridge: GodotApp singleton unavailable");
            return Array.Empty<string>();
        }

        PatchHelper.Log("[Mods] SafBridge: launching zip picker...");
        try
        {
            godotApp.Call("openZipPicker");
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Mods] SafBridge: openZipPicker call threw: {ex}");
            return Array.Empty<string>();
        }

        var deadline = DateTime.UtcNow.AddMinutes(5);
        var polls = 0;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await Task.Delay(150, ct);
            }
            catch (OperationCanceledException)
            {
                PatchHelper.Log("[Mods] SafBridge: cancelled while waiting");
                return Array.Empty<string>();
            }

            bool active;
            try
            {
                active = (bool)godotApp.Call("isPickerActive");
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"[Mods] SafBridge: isPickerActive failed: {ex.Message}");
                return Array.Empty<string>();
            }

            // First poll gives us whether the Intent actually started.
            if (polls == 0)
                PatchHelper.Log($"[Mods] SafBridge: first poll active={active}");
            polls++;

            if (!active)
            {
                try
                {
                    var joined = (string)godotApp.Call("consumePickedZipPaths");
                    if (string.IsNullOrEmpty(joined))
                    {
                        PatchHelper.Log("[Mods] SafBridge: picker closed without a selection");
                        return Array.Empty<string>();
                    }
                    var paths = joined.Split(
                        '\n',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    );
                    PatchHelper.Log($"[Mods] SafBridge: picked {paths.Length} file(s)");
                    return paths;
                }
                catch (Exception ex)
                {
                    PatchHelper.Log($"[Mods] SafBridge: consume failed: {ex.Message}");
                    return Array.Empty<string>();
                }
            }
        }

        PatchHelper.Log("[Mods] SafBridge: picker timeout after 5 minutes");
        return Array.Empty<string>();
    }

    // Back-compat single-file helper; most callers should use the plural form.
    public static async Task<string> PickZipToCacheAsync(CancellationToken ct = default)
    {
        var paths = await PickZipsToCacheAsync(ct);
        return paths.Length == 0 ? null : paths[0];
    }

    private static GodotObject GetGodotApp()
    {
        try
        {
            var jcw = Engine.GetSingleton("JavaClassWrapper");
            var wrapper = (GodotObject)jcw.Call("wrap", "com.game.sts2launcher.GodotApp");
            return (GodotObject)wrapper.Call("getInstance");
        }
        catch
        {
            return null;
        }
    }
}
