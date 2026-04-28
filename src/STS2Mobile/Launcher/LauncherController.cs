using System;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.Patches;
using STS2Mobile.Steam;

namespace STS2Mobile.Launcher;

// Wires model events to view updates and handles the launcher UI state machine.
// All model callbacks are marshalled to the main thread before updating the view.
public class LauncherController
{
    private readonly LauncherModel _model;
    private readonly LauncherView _view;
    private readonly Action<Action> _runOnMainThread;
    private volatile bool _checkingForUpdates;
    private bool _launchStageShown;
    private string _lastLaunchText = "LAUNCH";
    private bool _lastShowCloudSync;
    private bool _lastShowUpdate;

    public LauncherController(
        LauncherModel model,
        LauncherView view,
        Action<Action> runOnMainThread
    )
    {
        _model = model;
        _view = view;
        _runOnMainThread = runOnMainThread;
    }

    public void Start()
    {
        _model.SessionStateChanged += s => _runOnMainThread(() => UpdateUI(s));
        _model.LogReceived += msg => _runOnMainThread(() => _view.AppendLog(msg));
        PatchHelper.LogEmitted += msg =>
        {
            if (msg.StartsWith("[Cloud]"))
                _runOnMainThread(() => _view.AppendLog(msg));
        };
        _model.CodeNeeded += wasIncorrect =>
            _runOnMainThread(() =>
            {
                _view.Login.Visible = false;
                _view.Code.Show(wasIncorrect);
            });
        _model.DownloadProgressChanged += p =>
            _runOnMainThread(() =>
            {
                _view.Download.SetProgress(
                    p.Percentage,
                    $"{LauncherModel.FormatSize(p.DownloadedBytes)} / {LauncherModel.FormatSize(p.TotalBytes)} ({p.Percentage:F1}%)"
                );
                _view.AppendLog(p.CurrentFile);
            });
        _model.DownloadLogReceived += msg => _runOnMainThread(() => _view.AppendLog(msg));
        _model.DownloadCompleted += () =>
            _runOnMainThread(() =>
            {
                _view.SetStatus("Download complete! Restart to play.");
                _view.Download.Visible = false;
                if (LauncherModel.GameFilesReady())
                {
                    var text = _model.InGameMode ? "PLAY" : "RESTART APP";
                    ShowLaunchStage(text, showCloudSync: false, showUpdate: false);
                }
                else
                    _view.Actions.ShowRetry();
            });
        _model.DownloadFailed += msg =>
            _runOnMainThread(() =>
            {
                if (msg == null)
                {
                    _view.Download.Reset();
                    return;
                }
                _view.SetStatus($"Download failed: {msg}");
                _view.Download.Reset("RETRY DOWNLOAD");
            });
        _model.DownloadCancelled += () =>
            _runOnMainThread(() =>
            {
                _view.SetStatus("Download cancelled");
                _view.Download.SetButtonDisabled(false);
            });
        _model.UpdateCheckCompleted += hasUpdate =>
            _runOnMainThread(() =>
            {
                if (hasUpdate)
                {
                    _view.Actions.HideAll();
                    _view.Download.Visible = true;
                    _view.Download.Reset("UPDATE GAME FILES");
                    _view.SetStatus("Update available!");
                }
                else
                {
                    _view.Actions.SetUpdateButtonText("UP TO DATE");
                }
            });
        _model.UpdateCheckFailed += msg =>
            _runOnMainThread(() =>
            {
                _view.Actions.SetUpdateButtonText("CHECK FAILED");
                _view.Actions.SetUpdateButtonDisabled(false);
                _view.AppendLog($"Update check failed: {msg}");
            });

        _view.Login.LoginRequested += OnLoginPressed;
        _view.Code.CodeSubmitted += OnCodeSubmitPressed;
        _view.Download.DownloadRequested += OnDownloadPressed;
        _view.Actions.LaunchPressed += OnLaunchPressed;
        _view.Actions.RetryPressed += OnRetryPressed;
        _view.Actions.LocalBackupToggled += OnLocalBackupToggled;
        _view.Actions.CloudSyncToggled += OnCloudSyncToggled;
        _view.Actions.CloudPushPressed += OnCloudPushPressed;
        _view.Actions.CloudPullPressed += OnCloudPullPressed;
        _view.Actions.CheckForUpdatesPressed += OnCheckForUpdatesPressed;
        _view.ModManagerButton.Pressed += OnModManagerPressed;
        _view.ModManager.BackPressed += OnModManagerBackPressed;

        var localBackupPref = LauncherModel.LoadLocalBackupPref();
        _view.Actions.SetLocalBackupChecked(localBackupPref);
        CloudSyncCoordinator.LocalBackupEnabled = localBackupPref;
        if (localBackupPref)
            AppPaths.EnsureExternalDirectories();
        _view.Actions.SetCloudSyncChecked(LauncherModel.LoadCloudSyncPref());

        var result = _model.StartSession();
        HandleFastPath(result);
        MaybePromptStoragePermission();
    }

    // Asks once, on first launch, for "All Files Access". Mods, save backup, and
    // future launcher features all live under /storage/emulated/0/StS2Launcher/,
    // so we surface the request up front instead of hiding it inside the (still
    // WIP) Mod Manager flow. A marker file ensures we never re-prompt.
    private void MaybePromptStoragePermission()
    {
        if (AppPaths.HasStoragePermission())
            return;

        var markerPath = System.IO.Path.Combine(
            OS.GetDataDir(),
            "storage_permission_prompted"
        );
        if (System.IO.File.Exists(markerPath))
            return;

        try
        {
            System.IO.File.WriteAllText(markerPath, "1");
        }
        catch { }

        _view.ShowConfirmation(
            "Allow 'All Files Access'?\n\nNeeded for installing mods and saving local game backups under /storage/emulated/0/StS2Launcher/.",
            onConfirmed: AppPaths.RequestStoragePermission,
            onCancelled: null
        );
    }

    private void HandleFastPath(FastPathResult result)
    {
        PatchHelper.Log($"[Mods] HandleFastPath result={result}");
        switch (result)
        {
            case FastPathResult.ReadyToLaunch:
                _view.SetStatus($"Welcome back, {_model.AccountName}");
                var text = _model.InGameMode ? "PLAY" : "RESTART APP";
                ShowLaunchStage(text, showCloudSync: true, showUpdate: true);
                break;

            case FastPathResult.AutoConnect:
                _model.Connect();
                StartConnectionTimeout();
                break;

            case FastPathResult.ShowLogin:
                ShowLoginStage("Enter your Steam credentials");
                break;
        }
    }

    private void ShowLoginStage(string status)
    {
        _view.SetStatus(status);
        _view.Login.Visible = true;
        _view.Login.SetDisabled(false);
    }

    private void ShowLaunchStage(string text, bool showCloudSync, bool showUpdate)
    {
        PatchHelper.Log($"[Mods] ShowLaunchStage fired (text='{text}', inGameMode={_model.InGameMode})");
        _launchStageShown = true;
        _lastLaunchText = text;
        _lastShowCloudSync = showCloudSync;
        _lastShowUpdate = showUpdate;
        _view.Actions.ShowLaunch(text, showCloudSync, showUpdate);
        _view.ModManagerButton.Visible = true;
    }

    private void OnModManagerPressed()
    {
        PatchHelper.Log("[Mods] Mod Manager button tapped");
        _view.SetStatus("Mod Manager");
        _view.ShowModManager();
    }

    public void OnModManagerBackPressed()
    {
        PatchHelper.Log($"[Mods] Back pressed (launchStageShown={_launchStageShown}, sessionState={_model.SessionState})");
        // Must hide mod manager first, otherwise UpdateUI's ModManager.Visible guard
        // refuses to redraw — that was making BACK a no-op.
        _view.ModManager.Visible = false;
        _view.ModManagerButton.Visible = false;

        // Fast path (ReadyToLaunch) shows the launch UI without changing SessionState,
        // so we can't rely on SessionState==LoggedIn to know if we were on the launch screen.
        if (_launchStageShown)
        {
            _view.SetStatus($"Welcome back, {_model.AccountName}");
            ShowLaunchStage(_lastLaunchText, _lastShowCloudSync, _lastShowUpdate);
        }
        else
        {
            ShowLoginStage("Enter your Steam credentials");
        }
    }

    public bool IsModManagerOpen => _view.ModManager.Visible;

    private async void StartConnectionTimeout()
    {
        await Task.Delay(10000);

        if (_model.ConnectionResolved)
            return;

        var state = _model.SessionState;
        if (
            state
            is SessionState.Connecting
                or SessionState.Authenticating
                or SessionState.VerifyingOwnership
        )
        {
            if (_model.HasOwnershipMarker() && LauncherModel.GameFilesReady())
            {
                _runOnMainThread(() =>
                {
                    _view.SetStatus("No connection — saved credentials will be used");
                    _view.AppendLog("Connection timed out. Valid ownership marker found.");
                    var text = _model.InGameMode ? "PLAY" : "RESTART APP";
                    ShowLaunchStage(text, showCloudSync: true, showUpdate: false);
                });
            }
            else
            {
                _runOnMainThread(() =>
                {
                    _view.SetStatus("Connection failed. Internet required for first launch.");
                    _view.Actions.ShowRetry();
                });
            }
        }
    }

    // Updates visible sections and status text based on session state transitions.
    private void UpdateUI(SessionState state)
    {
        if (
            _model.AwaitingCode
            && state
                is SessionState.Connecting
                    or SessionState.WaitingForCredentials
                    or SessionState.Authenticating
        )
            return;

        if (_checkingForUpdates)
            return;

        // After successful login, ignore session disconnects — cloud ops use
        // their own token-based connections, so the launcher session dropping is expected.
        if (state == SessionState.Disconnected && _model.ConnectionResolved)
            return;

        if (_view.ModManager.Visible)
            return;

        _view.HideAllSections();

        switch (state)
        {
            case SessionState.Connecting:
                _view.SetStatus("Connecting to Steam...");
                break;

            case SessionState.WaitingForCredentials:
                ShowLoginStage("Enter your Steam credentials");
                break;

            case SessionState.Authenticating:
                _view.SetStatus("Authenticating...");
                break;

            case SessionState.VerifyingOwnership:
                _view.SetStatus("Verifying game ownership...");
                break;

            case SessionState.LoggedIn:
                _model.ConnectionResolved = true;
                _view.SetStatus($"Logged in as {_model.AccountName}");
                if (LauncherModel.GameFilesReady())
                {
                    var text = _model.InGameMode ? "PLAY" : "RESTART APP";
                    ShowLaunchStage(text, showCloudSync: true, showUpdate: true);
                }
                else
                {
                    _view.Download.Visible = true;
                    _view.Download.SetButtonDisabled(false);
                }
                break;

            case SessionState.Failed:
                _model.ConnectionResolved = true;
                ShowLoginStage($"Error: {_model.FailReason}");
                break;

            case SessionState.Disconnected:
                ShowLoginStage("Enter your Steam credentials");
                break;
        }
    }

    private async void OnLoginPressed(string username, string password)
    {
        _view.Login.SetDisabled(true);
        _view.Login.ClearPassword();
        await _model.LoginAsync(username, password);
    }

    private void OnCodeSubmitPressed(string code)
    {
        _view.SetStatus("Verifying code...");
        _model.SubmitCode(code);
    }

    private async void OnDownloadPressed()
    {
        _view.Download.ShowProgress("Loading branches...");

        System.Collections.Generic.List<SteamBranchInfo> branches;
        try
        {
            branches = await _model.ListBranchesAsync();
        }
        catch (Exception ex)
        {
            _view.AppendLog($"Branch list failed: {ex.Message}");
            _view.Download.Reset();
            return;
        }

        var current = LauncherModel.LoadSelectedBranch();
        string picked;
        if (branches.Count <= 1)
        {
            picked = branches.Count == 1 ? branches[0].Name : "public";
        }
        else
        {
            picked = await ShowBranchPickerAsync(branches, current);
            if (picked == null)
            {
                _view.Download.Reset();
                return;
            }
        }

        LauncherModel.SaveSelectedBranch(picked);
        _view.Download.ShowProgress(
            picked == "public" ? "Connecting to Steam..." : $"Connecting to Steam ({picked})..."
        );
        await _model.StartDownloadAsync(picked);
    }

    private async void OnCheckForUpdatesPressed()
    {
        _checkingForUpdates = true;
        _view.Actions.SetUpdateButtonDisabled(true);
        _view.Actions.SetUpdateButtonText("Loading branches...");

        System.Collections.Generic.List<SteamBranchInfo> branches;
        try
        {
            branches = await _model.ListBranchesAsync();
        }
        catch (Exception ex)
        {
            _view.AppendLog($"Branch list failed: {ex.Message}");
            ResetUpdateButton();
            _checkingForUpdates = false;
            return;
        }

        var current = LauncherModel.LoadSelectedBranch();
        string picked;
        if (branches.Count <= 1)
        {
            picked = branches.Count == 1 ? branches[0].Name : "public";
        }
        else
        {
            picked = await ShowBranchPickerAsync(branches, current);
            if (picked == null)
            {
                ResetUpdateButton();
                _checkingForUpdates = false;
                return;
            }
        }

        LauncherModel.SaveSelectedBranch(picked);

        // Branch switch + existing files = force a fresh download. The delta path
        // has produced broken installs (e.g. card art mismatches) when going from
        // public ↔ public-beta even though every file passes its manifest SHA-1,
        // so we sidestep it for branch transitions.
        if (picked != current && LauncherModel.GameFilesReady())
        {
            var confirmed = await ConfirmAsync(
                $"Switch to '{picked}'?\n\nGame files (~3GB) will be redownloaded. Login and saves are kept."
            );
            if (!confirmed)
            {
                ResetUpdateButton();
                _checkingForUpdates = false;
                return;
            }
            _model.WipeGameFiles();
            _runOnMainThread(() =>
            {
                _view.Actions.HideAll();
                _view.Download.Visible = true;
                _view.Download.Reset("DOWNLOAD GAME FILES");
                _view.SetStatus($"Switched to {picked}. Tap DOWNLOAD GAME FILES to redownload.");
            });
            _checkingForUpdates = false;
            return;
        }

        _view.Actions.SetUpdateButtonText(
            picked == "public" ? "Checking..." : $"Checking {picked}..."
        );

        // Check for launcher (APK) updates from GitHub in parallel with game file updates.
        var appUpdateTask = CheckAppUpdateAsync();
        await _model.CheckForUpdatesAsync(picked);
        await appUpdateTask;

        _checkingForUpdates = false;
    }

    private Task<bool> ConfirmAsync(string message)
    {
        var tcs = new TaskCompletionSource<bool>();
        _runOnMainThread(() =>
        {
            _view.ShowConfirmation(
                message,
                onConfirmed: () => tcs.TrySetResult(true),
                onCancelled: () => tcs.TrySetResult(false)
            );
        });
        return tcs.Task;
    }

    private void ResetUpdateButton()
    {
        _view.Actions.SetUpdateButtonText("CHECK FOR UPDATES");
        _view.Actions.SetUpdateButtonDisabled(false);
    }

    private Task<string> ShowBranchPickerAsync(
        System.Collections.Generic.IReadOnlyList<SteamBranchInfo> branches,
        string currentBranch
    )
    {
        var tcs = new TaskCompletionSource<string>();
        _runOnMainThread(() =>
        {
            _view.ShowBranchPicker(
                branches,
                currentBranch,
                onConfirmed: name => tcs.TrySetResult(name),
                onCancelled: () => tcs.TrySetResult(null)
            );
        });
        return tcs.Task;
    }

    private static readonly Color YellowLog = new(1f, 0.85f, 0.2f);

    private async Task CheckAppUpdateAsync()
    {
        try
        {
            var result = await AppUpdateChecker.CheckAsync();
            if (!result.HasUpdate)
            {
                _runOnMainThread(() => _view.AppendLog("Launcher is up to date"));
            }
            else
            {
                _runOnMainThread(() =>
                {
                    _view.AppendColoredLog(
                        $"Launcher update available: v{result.LatestVersion} — "
                            + "download at https://github.com/Ekyso/StS2-Launcher/releases/latest",
                        YellowLog
                    );
                    _view.SetStatus(
                        $"Launcher update available! Visit GitHub to download v{result.LatestVersion}"
                    );
                });
            }
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Launcher] App update check failed: {ex.Message}");
        }
    }

    private void OnLocalBackupToggled(bool pressed)
    {
        if (pressed && !AppPaths.HasStoragePermission())
            AppPaths.RequestStoragePermission();

        if (pressed)
            AppPaths.EnsureExternalDirectories();

        LauncherModel.SaveLocalBackupPref(pressed);
        CloudSyncCoordinator.LocalBackupEnabled = pressed;
    }

    private void OnCloudSyncToggled(bool pressed)
    {
        LauncherModel.SaveCloudSyncPref(pressed);
        LauncherPatches.CloudSyncEnabled = pressed;
    }

    private void OnCloudPushPressed()
    {
        ShowConfirmation(
            "Push local saves to cloud?\nThis will overwrite your cloud saves.",
            () =>
            {
                _view.Actions.SetPushPullDisabled(true);
                _view.AppendLog("Pushing local saves to cloud...");
                Task.Run(async () =>
                {
                    await CloudSyncCoordinator.ManualPushAllAsync(
                        LauncherPatches.SavedAccountName,
                        LauncherPatches.SavedRefreshToken
                    );
                    _runOnMainThread(() =>
                    {
                        _view.AppendLog("Push complete.");
                        _view.Actions.SetPushPullDisabled(false);
                    });
                });
            }
        );
    }

    private void OnCloudPullPressed()
    {
        ShowConfirmation(
            "Pull cloud saves to local?\nThis will overwrite your local saves.",
            () =>
            {
                _view.Actions.SetPushPullDisabled(true);
                _view.AppendLog("Pulling cloud saves to local...");
                Task.Run(async () =>
                {
                    await CloudSyncCoordinator.ManualPullAllAsync(
                        LauncherPatches.SavedAccountName,
                        LauncherPatches.SavedRefreshToken
                    );
                    _runOnMainThread(() =>
                    {
                        _view.AppendLog("Pull complete.");
                        _view.Actions.SetPushPullDisabled(false);
                    });
                });
            }
        );
    }

    private void ShowConfirmation(string message, Action onConfirmed)
    {
        _view.ShowConfirmation(message, onConfirmed);
    }

    private void OnRetryPressed()
    {
        var result = _model.Retry();
        HandleFastPath(result);
    }

    private void OnLaunchPressed() => _model.Launch();
}
