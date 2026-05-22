using System;
using Godot;
using STS2Mobile.Launcher.Components;

namespace STS2Mobile.Launcher.Sections;

public class ActionSection : VBoxContainer
{
    public event Action LaunchPressed;
    public event Action RetryPressed;
    // Issue #36 Part A: Local Backup is now a one-shot action, not a persisted
    // on/off mode. Pressing it triggers a manual full-tree snapshot.
    public event Action LocalBackupPressed;
    public event Action<bool> CloudSyncToggled;
    public event Action CloudPushPressed;
    public event Action CloudPullPressed;
    public event Action CheckGameUpdatePressed;
    public event Action CheckLauncherUpdatePressed;

    private readonly Button _launchButton;
    private readonly Button _retryButton;
    private readonly StyledButton _localBackupButton;
    private readonly StyledButton _cloudSyncToggle;
    private readonly Button _pushButton;
    private readonly Button _pullButton;
    private readonly Button _gameUpdateButton;
    private readonly Button _launcherUpdateButton;
    private readonly StyleBoxFlat _offStyle;
    private readonly StyleBoxFlat _onStyle;

    public ActionSection(float scale)
    {
        _retryButton = new StyledButton("RETRY", scale);
        _retryButton.Visible = false;
        _retryButton.Pressed += () => RetryPressed?.Invoke();
        AddChild(_retryButton);

        var r = (int)(4 * scale);
        var bw = System.Math.Max(1, (int)(2 * scale));
        _offStyle = StyledButton.MakeOutline(new Color(0.7f, 0.25f, 0.25f), r, bw);
        _onStyle = StyledButton.MakeOutline(new Color(0.25f, 0.65f, 0.3f), r, bw);

        _localBackupButton = new StyledButton("Local Backup", scale, fontSize: 14, height: 44);
        _localBackupButton.Visible = false;
        _localBackupButton.Pressed += () => LocalBackupPressed?.Invoke();
        AddChild(_localBackupButton);

        _cloudSyncToggle = new StyledButton("Auto Sync: OFF", scale, fontSize: 14, height: 44);
        _cloudSyncToggle.ToggleMode = true;
        _cloudSyncToggle.Visible = false;
        ApplyToggleStyle(_cloudSyncToggle, false);
        _cloudSyncToggle.Toggled += pressed =>
        {
            _cloudSyncToggle.Text = pressed ? "Auto Sync: ON" : "Auto Sync: OFF";
            ApplyToggleStyle(_cloudSyncToggle, pressed);
            CloudSyncToggled?.Invoke(pressed);
        };
        AddChild(_cloudSyncToggle);

        var pushPullRow = new HBoxContainer();
        pushPullRow.Visible = false;
        pushPullRow.AddThemeConstantOverride("separation", (int)(6 * scale));

        _pushButton = new StyledButton("Push to Cloud", scale, fontSize: 14, height: 44);
        _pushButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _pushButton.Pressed += () => CloudPushPressed?.Invoke();
        pushPullRow.AddChild(_pushButton);

        _pullButton = new StyledButton("Pull from Cloud", scale, fontSize: 14, height: 44);
        _pullButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _pullButton.Pressed += () => CloudPullPressed?.Invoke();
        pushPullRow.AddChild(_pullButton);

        AddChild(pushPullRow);

        _gameUpdateButton = new StyledButton("CHECK GAME UPDATE", scale, fontSize: 16, height: 48);
        _gameUpdateButton.Visible = false;
        _gameUpdateButton.Pressed += () => CheckGameUpdatePressed?.Invoke();
        AddChild(_gameUpdateButton);

        _launcherUpdateButton = new StyledButton(
            "CHECK LAUNCHER UPDATE",
            scale,
            fontSize: 16,
            height: 48
        );
        _launcherUpdateButton.Visible = false;
        _launcherUpdateButton.Pressed += () => CheckLauncherUpdatePressed?.Invoke();
        AddChild(_launcherUpdateButton);

        _launchButton = new StyledButton("LAUNCH", scale, fontSize: 16, height: 48);
        _launchButton.Visible = false;
        _launchButton.Pressed += () => LaunchPressed?.Invoke();
        AddChild(_launchButton);
    }

    public void SetCloudSyncChecked(bool value)
    {
        _cloudSyncToggle.ButtonPressed = value;
        _cloudSyncToggle.Text = value ? "Auto Sync: ON" : "Auto Sync: OFF";
        ApplyToggleStyle(_cloudSyncToggle, value);
    }

    private void ApplyToggleStyle(Button button, bool on)
    {
        var style = on ? _onStyle : _offStyle;
        button.AddThemeStyleboxOverride("normal", style);
        button.AddThemeStyleboxOverride("hover", style);
        button.AddThemeStyleboxOverride("pressed", style);
        button.AddThemeStyleboxOverride("disabled", style);
    }

    private HBoxContainer PushPullRow => (HBoxContainer)_pushButton.GetParent();

    public void ShowLaunch(string text, bool showCloudSync, bool showUpdate)
    {
        _launchButton.Text = text;
        _launchButton.Visible = true;
        _localBackupButton.Visible = showCloudSync;
        _cloudSyncToggle.Visible = showCloudSync;
        PushPullRow.Visible = showCloudSync;
        _gameUpdateButton.Visible = showUpdate;
        _gameUpdateButton.Disabled = false;
        _gameUpdateButton.Text = "CHECK GAME UPDATE";
        _launcherUpdateButton.Visible = showUpdate;
        _launcherUpdateButton.Disabled = false;
        _launcherUpdateButton.Text = "CHECK LAUNCHER UPDATE";
        _retryButton.Visible = false;
    }

    public void ShowRetry()
    {
        _retryButton.Visible = true;
        _launchButton.Visible = false;
        _localBackupButton.Visible = false;
        _cloudSyncToggle.Visible = false;
        PushPullRow.Visible = false;
        _gameUpdateButton.Visible = false;
        _launcherUpdateButton.Visible = false;
    }

    public void HideAll()
    {
        _launchButton.Visible = false;
        _retryButton.Visible = false;
        _localBackupButton.Visible = false;
        _cloudSyncToggle.Visible = false;
        PushPullRow.Visible = false;
        _gameUpdateButton.Visible = false;
        _launcherUpdateButton.Visible = false;
    }

    // Locks every sync-affecting button while a cloud operation is in flight.
    // Issue #7: previously only push/pull were disabled — PLAY was still
    // pressable, so a user who tapped Save Manager and then quickly hit PLAY
    // could enter the game's GameStartupWrapper concurrently with the cloud
    // handshake (race against ConstructDefaultPrefix's cache preload). PLAY
    // now stays disabled until the in-flight sync resolves.
    public void SetSyncBusy(bool busy)
    {
        _pushButton.Disabled = busy;
        _pullButton.Disabled = busy;
        _launchButton.Disabled = busy;
        // A manual backup snapshots the same save tree the sync touches, so
        // keep it locked while a cloud op (or another backup) is in flight.
        _localBackupButton.Disabled = busy;
    }

    public void SetGameUpdateButtonText(string text) => _gameUpdateButton.Text = text;

    public void SetGameUpdateButtonDisabled(bool disabled) => _gameUpdateButton.Disabled = disabled;

    public void SetLauncherUpdateButtonText(string text) => _launcherUpdateButton.Text = text;

    public void SetLauncherUpdateButtonDisabled(bool disabled) =>
        _launcherUpdateButton.Disabled = disabled;
}
