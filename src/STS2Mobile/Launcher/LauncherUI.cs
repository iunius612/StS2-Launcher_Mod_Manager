using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.Patches;

namespace STS2Mobile.Launcher;

// Thin wrapper Control that initializes the MVC launcher components and
// processes a main-thread action queue so SteamKit callbacks can update the UI.
public class LauncherUI : Control
{
    private readonly ConcurrentQueue<Action> _mainThreadQueue = new();
    private LauncherModel _model;
    private LauncherView _view;
    private LauncherController _controller;
    private bool _inGameMode;
    private bool _windowScaleOverridden;
    private Vector2I _origScaleSize;
    private Window.ContentScaleModeEnum _origScaleMode;
    private Window.ContentScaleAspectEnum _origScaleAspect;

    // Logical canvas the launcher targets. Window.ContentScale is pinned to
    // these dims with CanvasItems + Expand, so widget scale is computed from
    // the base size (always 2.0) instead of from the visible rect — the visible
    // rect grows along the wider physical axis under Expand and would otherwise
    // give a wildly different scale on fold/unfold/rotate.
    public const int LogicalWidth = 1920;
    public const int LogicalHeight = 1080;
    public const float UiScale = LogicalHeight / 540f; // 2.0

    public void Initialize()
    {
        ZIndex = 100;

        // The game PCK's project.godot pins display/window/handheld/orientation to
        // landscape, which Godot applies at runtime and silently overrides the
        // activity's android:screenOrientation="sensorLandscape". Force sensor
        // landscape from C# so the user can flip the device 180° (USB-C charging
        // angle) and have the screen rotate.
        try
        {
            DisplayServer.ScreenSetOrientation(DisplayServer.ScreenOrientation.SensorLandscape);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Launcher] Failed to set sensor landscape: {ex.Message}");
        }

        // Pin Window content scale to a fixed logical 1920×1080 with canvas_items +
        // expand. Godot then auto-stretches the entire UI tree to whatever physical
        // viewport the foldable / rotation / window resize produces, so widget
        // sizes computed once at construction (font, button height, padding) keep
        // looking right after fold/unfold without rebuilding the tree.
        // Original game-set values are stashed and restored in OnExitTree.
        try
        {
            var window = GetWindow();
            if (window != null)
            {
                _origScaleSize = window.ContentScaleSize;
                _origScaleMode = window.ContentScaleMode;
                _origScaleAspect = window.ContentScaleAspect;
                window.ContentScaleSize = new Vector2I(1920, 1080);
                window.ContentScaleMode = Window.ContentScaleModeEnum.CanvasItems;
                window.ContentScaleAspect = Window.ContentScaleAspectEnum.Expand;
                _windowScaleOverridden = true;
                PatchHelper.Log(
                    $"[Launcher] Window ContentScale overridden (orig size={_origScaleSize}, mode={_origScaleMode}, aspect={_origScaleAspect})"
                );
            }
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Launcher] Failed to set Window ContentScale: {ex.Message}");
        }

        try
        {
            // ContentScaleAspect.Expand makes GetVisibleRect grow along whatever
            // physical axis exceeds the project aspect, so don't compute scale
            // from it (would give different values on fold/unfold/rotate). Use
            // the base logical size — scale stays a stable 2.0.
            var vpSize =
                GetViewport()?.GetVisibleRect().Size ?? new Vector2(LogicalWidth, LogicalHeight);
            SetAnchorsPreset(LayoutPreset.FullRect);
            // Required because LauncherUI's parent is the game's gameNode (a
            // plain Node, not a Control), so anchors don't drive auto-sizing —
            // we have to set Size explicitly. Without this, every child Control
            // sees a 0×0 parent and the launcher collapses into the corner.
            // (Removed in v0.3.7, restored in v0.3.8 — that removal was the
            // observed top-left-collapse regression.)
            Size = vpSize;
            var scale = UiScale;

            _model = new LauncherModel(OS.GetDataDir());
            _model.InGameMode = _inGameMode;
            _view = new LauncherView(this, scale);
            _controller = new LauncherController(_model, _view, a => _mainThreadQueue.Enqueue(a));

            PatchHelper.Log($"LauncherUI initialized. Viewport={vpSize}");
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"BuildUI FAILED: {ex}");
            return;
        }

        LauncherPatches.CloudSyncEnabled = LauncherModel.LoadCloudSyncPref();

        // Prevent Android back button from quitting while the launcher is active.
        GetTree().AutoAcceptQuit = false;

        GetTree().ProcessFrame += OnProcessFrame;
        TreeExiting += OnExitTree;
        _controller.Start();
    }

    public void SetGameMode(bool inGameMode) => _inGameMode = inGameMode;

    public Task WaitForLaunch() => _model.WaitForLaunch();

    // Mirrors the scale formula used in Initialize so other launcher-spawned
    // overlays (e.g. cloud conflict dialog) match the rest of the UI sizing.
    public static float ResolveScale(Node sceneRef)
    {
        // Pinned scale matches LauncherUI's UiScale so overlays sized off this
        // value (CloudConflictDialog etc.) stay visually consistent with the
        // launcher across fold/unfold/rotate.
        return UiScale;
    }

    // Used by overlays that need to know the actual viewport height (not the
    // scale) — e.g. CloudConflictDialog drops to compact font/padding when the
    // viewport is short, so foldable cover-screen / folded landscape doesn't
    // clip the buttons off the bottom.
    public static float ResolveViewportHeight(Node sceneRef)
    {
        try
        {
            return sceneRef?.GetViewport()?.GetVisibleRect().Size.Y ?? 1080f;
        }
        catch
        {
            return 1080f;
        }
    }

    private void OnProcessFrame()
    {
        while (_mainThreadQueue.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"UI update error: {ex.Message}");
            }
        }

        _view?.UpdateKeyboardOffset();
    }

    private void OnExitTree()
    {
        GetTree().ProcessFrame -= OnProcessFrame;
        GetTree().AutoAcceptQuit = true;

        // Hand the Window's content scale back to whatever the game set so the
        // launcher exit doesn't break the game's own UI sizing.
        if (_windowScaleOverridden)
        {
            try
            {
                var window = GetWindow();
                if (window != null)
                {
                    window.ContentScaleSize = _origScaleSize;
                    window.ContentScaleMode = _origScaleMode;
                    window.ContentScaleAspect = _origScaleAspect;
                }
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"[Launcher] Failed to restore Window ContentScale: {ex.Message}");
            }
            _windowScaleOverridden = false;
        }

        _model?.Dispose();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMGoBackRequest && _controller is { IsModManagerOpen: true })
            _controller.OnModManagerBackPressed();
    }
}
