using System;
using System.Collections.Generic;
using Godot;
using STS2Mobile.Launcher.Components;
using STS2Mobile.Launcher.Sections;
using STS2Mobile.Steam;

namespace STS2Mobile.Launcher;

// Builds the launcher UI layout programmatically with a split panel:
// left side has login/download/action controls, right side has a console log.
public class LauncherView
{
    public LoginSection Login { get; }
    public CodeSection Code { get; }
    public DownloadSection Download { get; }
    public ActionSection Actions { get; }
    public ModManagerSection ModManager { get; }
    public StyledButton ModManagerButton { get; }
    public LogView Log { get; }
    public StyledButton DebugButton { get; }

    private readonly StyledLabel _statusLabel;
    private readonly Control _parent;
    private readonly StyledPanel _panel;
    private float _panelBaseY;

    // Exposed so the controller can use this Control as a parent when adding
    // overlays (e.g. CloudConflictDialog opened from the Save Manager button).
    public Control RootControl => _parent;

    public LauncherView(Control parent, float scale)
    {
        _parent = parent;
        _scale = scale;
        parent.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        var vpSize = parent.GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920, 1080);

        var bg = new ScreenBackground();
        bg.GuiInput += DismissKeyboard;
        parent.AddChild(bg);

        _panel = new StyledPanel(scale, widthRatio: 0.95f, heightRatio: 0.92f);
        _panel.UpdateSizeFromViewport(vpSize);
        _panel.Panel.GuiInput += DismissKeyboard;
        parent.AddChild(_panel);
        _panelBaseY = _panel.Position.Y;

        // Widget scale stays fixed (Window ContentScale handles physical mapping),
        // but the logical visible rect extends along the wider axis under
        // ContentScaleAspect.Expand. Without recomputing the panel min-size, it
        // stays centered at its original 1824×994 logical with black bars on
        // any axis that grew (most visible after a foldable hinge transition).
        // The parent.Size update is essential — LauncherUI's parent in the
        // running game is `gameNode` (a Node, not a Control), so anchors don't
        // drive auto-sizing. Without setting Size, every child sees a stale
        // size from the previous viewport and the panel snaps to the corner.
        // (parent.Size update was present in v0.3.5, dropped in v0.3.6 along
        // with the hook itself, hook re-added in v0.3.6 but missing this line —
        // restored in v0.3.8.)
        var vp = parent.GetViewport();
        if (vp != null)
            vp.SizeChanged += () =>
            {
                var newSize = vp.GetVisibleRect().Size;
                parent.Size = newSize;
                _panel.UpdateSizeFromViewport(newSize);
                // Don't recapture _panelBaseY here. The virtual keyboard appearing
                // also fires SizeChanged (viewport shrinks for the keyboard), and
                // by the time we run, UpdateKeyboardOffset has already moved
                // _panel.Position.Y up by the offset. Capturing now would lock the
                // base at the offset-up position, leaving the panel stuck high
                // after the keyboard dismisses. The panel is a FullRect-anchored
                // CenterContainer so its natural position stays (0,0) regardless
                // of viewport size — the initial capture is enough.
                PatchHelper.Log($"[Launcher] Viewport SizeChanged -> {newSize}; panel resized");
            };

        var hbox = new HBoxContainer();
        hbox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        hbox.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        hbox.AddThemeConstantOverride("separation", (int)(16 * scale));
        _panel.Content.AddChild(hbox);

        var leftCenter = new CenterContainer();
        leftCenter.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        leftCenter.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        leftCenter.SizeFlagsStretchRatio = 1f;
        hbox.AddChild(leftCenter);

        var left = new VBoxContainer();
        left.CustomMinimumSize = new Vector2((int)(200 * scale), 0);
        left.AddThemeConstantOverride("separation", (int)(10 * scale));
        leftCenter.AddChild(left);

        var title = new StyledLabel("StS2 Launcher", scale, fontSize: 26);
        left.AddChild(title);
        left.AddChild(new HSeparator());

        _statusLabel = new StyledLabel("Initializing...", scale);
        _statusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        left.AddChild(_statusLabel);

        Login = new LoginSection(scale);
        left.AddChild(Login);

        Code = new CodeSection(scale);
        left.AddChild(Code);

        Download = new DownloadSection(scale);
        left.AddChild(Download);

        Actions = new ActionSection(scale);
        left.AddChild(Actions);

        ModManager = new ModManagerSection(scale);
        ModManager.ConfirmationRequested += (message, onOk, onCancel) =>
            ShowConfirmation(message, onOk, onCancel);
        left.AddChild(ModManager);

        // Repurposed in 0.3.0: opens the Save Sync dialog instead of the WIP
        // mod manager screen. The ModManagerSection above is still constructed
        // for future use but no longer reachable from this button.
        ModManagerButton = new StyledButton("SAVE MANAGER", scale, fontSize: 14, height: 40);
        ModManagerButton.Visible = false;
        left.AddChild(ModManagerButton);

        // FMOD attribution (required by FMOD EULA).
        var fmodContainer = new VBoxContainer();
        fmodContainer.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        fmodContainer.Alignment = BoxContainer.AlignmentMode.End;
        left.AddChild(fmodContainer);

        var fmodLogo = LoadFmodLogo(scale);
        if (fmodLogo != null)
            fmodContainer.AddChild(fmodLogo);

        var fmodCredit = new StyledLabel(
            "Made using FMOD Studio by Firelight Technologies Pty Ltd.",
            scale,
            fontSize: 8
        );
        fmodCredit.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.55f));
        fmodContainer.AddChild(fmodCredit);

        var right = new VBoxContainer();
        right.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        right.SizeFlagsStretchRatio = 1f;
        hbox.AddChild(right);

        var logHeader = new HBoxContainer();
        logHeader.AddThemeConstantOverride("separation", (int)(8 * scale));
        right.AddChild(logHeader);

        var logTitle = new StyledLabel("Console", scale, fontSize: 14);
        logTitle.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.65f));
        logTitle.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        logHeader.AddChild(logTitle);

        DebugButton = new StyledButton("Debug: OFF", scale, fontSize: 11, height: 28);
        DebugButton.CustomMinimumSize = new Vector2(
            (int)(110 * scale),
            DebugButton.CustomMinimumSize.Y
        );
        logHeader.AddChild(DebugButton);

        Log = new LogView(scale);
        Log.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        Log.GuiInput += DismissKeyboard;
        right.AddChild(Log);
    }

    private readonly float _scale;

    public void SetStatus(string text) => _statusLabel.Text = text;

    public void AppendLog(string msg) => Log.AppendLog(msg);

    public void AppendColoredLog(string msg, Godot.Color color) => Log.AppendColoredLog(msg, color);

    public void HideAllSections()
    {
        Login.Visible = false;
        Code.Visible = false;
        Download.Visible = false;
        Actions.HideAll();
        ModManager.Visible = false;
        ModManagerButton.Visible = false;
    }

    public void ShowModManager()
    {
        HideAllSections();
        ModManager.Visible = true;
        ModManager.Refresh();
    }

    public void UpdateKeyboardOffset()
    {
        var kbHeight = DisplayServer.VirtualKeyboardGetHeight();
        if (kbHeight > 0)
        {
            var windowSize = DisplayServer.WindowGetSize();
            var vpSize = _parent.GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920, 1080);
            var scale = vpSize.Y / windowSize.Y;
            var offset = kbHeight * scale * 0.5f;
            _panel.Position = new Vector2(_panel.Position.X, _panelBaseY - offset);
        }
        else
        {
            _panel.Position = new Vector2(_panel.Position.X, _panelBaseY);
        }
    }

    // Loads the FMOD logo extracted by GodotApp from internal storage.
    private static TextureRect LoadFmodLogo(float scale)
    {
        try
        {
            var logoPath = System.IO.Path.Combine(OS.GetDataDir(), "fmod_logo.png");
            if (!System.IO.File.Exists(logoPath))
            {
                PatchHelper.Log($"FMOD logo not found at {logoPath}");
                return null;
            }

            var bytes = System.IO.File.ReadAllBytes(logoPath);
            var image = new Image();
            image.LoadPngFromBuffer(bytes);

            var tex = ImageTexture.CreateFromImage(image);
            var rect = new TextureRect();
            rect.Texture = tex;
            rect.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
            rect.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
            rect.CustomMinimumSize = new Vector2((int)(120 * scale), (int)(30 * scale));
            return rect;
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"Failed to load FMOD logo: {ex.Message}");
            return null;
        }
    }

    public void ShowConfirmation(
        string message,
        Action onConfirmed,
        Action onCancelled = null,
        string okLabel = "OK",
        string cancelLabel = "Cancel"
    )
    {
        var dialog = new StyledDialog(message, _scale, okLabel, cancelLabel);
        dialog.Confirmed += onConfirmed;
        if (onCancelled != null)
            dialog.Cancelled += onCancelled;
        _parent.AddChild(dialog);
    }

    public LauncherUpdateDialog ShowLauncherUpdateDialog(string version)
    {
        var dialog = new LauncherUpdateDialog(version, _scale);
        _parent.AddChild(dialog);
        return dialog;
    }

    // Issue #36 Part A: outcome modal for a manual Local Backup run.
    public void ShowBackupResult(
        bool success,
        int fileCount,
        long totalBytes,
        string backupPath,
        string failureReason
    )
    {
        var dialog = new BackupResultDialog(
            success,
            fileCount,
            totalBytes,
            backupPath,
            failureReason,
            _scale
        );
        _parent.AddChild(dialog);
    }

    public void ShowBranchPicker(
        IReadOnlyList<SteamBranchInfo> branches,
        string currentBranch,
        Action<string> onConfirmed,
        Action onCancelled = null,
        Action onAtlasWipeRequested = null
    )
    {
        var dialog = new BranchPickerDialog(branches, currentBranch, _scale);
        dialog.BranchConfirmed += onConfirmed;
        if (onCancelled != null)
            dialog.Cancelled += onCancelled;
        if (onAtlasWipeRequested != null)
            dialog.AtlasWipeRequested += onAtlasWipeRequested;
        _parent.AddChild(dialog);
    }

    private void DismissKeyboard(InputEvent ev)
    {
        if (
            ev is InputEventMouseButton { Pressed: true } or InputEventScreenTouch { Pressed: true }
        )
            _parent.GetViewport()?.GuiReleaseFocus();
    }
}
