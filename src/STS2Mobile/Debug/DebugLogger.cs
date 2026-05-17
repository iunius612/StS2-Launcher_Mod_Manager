using System;
using Godot;

namespace STS2Mobile.Debug;

// Thin wrapper over the Java-side logcat capture in GodotApp. The actual
// process management lives there because (a) the toggle has to survive across
// process restarts via SharedPreferences, and (b) we want capture to start in
// onCreate before any .NET code is loaded.
public static class DebugLogger
{
    public static bool IsEnabled()
    {
        var app = GetGodotApp();
        if (app == null)
            return false;
        try
        {
            return (bool)app.Call("isLogcatCaptureEnabled");
        }
        catch
        {
            return false;
        }
    }

    public static string Enable()
    {
        var app = GetGodotApp();
        if (app == null)
            return null;
        try
        {
            return (string)app.Call("startLogcatCapture");
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Debug] Enable failed: {ex.Message}");
            return null;
        }
    }

    public static void Disable()
    {
        var app = GetGodotApp();
        if (app == null)
            return;
        try
        {
            app.Call("stopLogcatCapture");
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Debug] Disable failed: {ex.Message}");
        }
    }

    public static string GetCurrentFilePath()
    {
        var app = GetGodotApp();
        if (app == null)
            return null;
        try
        {
            return (string)app.Call("getLogcatFilePath");
        }
        catch
        {
            return null;
        }
    }

    public static string GetLogsDirPath()
    {
        var app = GetGodotApp();
        if (app == null)
            return AppPaths.ExternalLogsDir;
        try
        {
            return (string)app.Call("getLogcatLogsDirPath");
        }
        catch
        {
            return AppPaths.ExternalLogsDir;
        }
    }

    private static GodotObject GetGodotApp()
    {
        try
        {
            var jcw = Engine.GetSingleton("JavaClassWrapper");
            var wrapper = (GodotObject)
                jcw.Call("wrap", "com.game.sts2launcher.modmanager.GodotApp");
            return (GodotObject)wrapper.Call("getInstance");
        }
        catch
        {
            return null;
        }
    }
}
