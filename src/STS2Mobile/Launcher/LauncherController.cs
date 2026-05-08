using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.Debug;
using STS2Mobile.Launcher.Components;
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
    private volatile bool _checkingForGameUpdate;
    private volatile bool _checkingForLauncherUpdate;
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
                    _view.Actions.SetGameUpdateButtonText("UP TO DATE");
                }
            });
        _model.UpdateCheckFailed += msg =>
            _runOnMainThread(() =>
            {
                _view.Actions.SetGameUpdateButtonText("CHECK FAILED");
                _view.Actions.SetGameUpdateButtonDisabled(false);
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
        _view.Actions.CheckGameUpdatePressed += OnCheckGameUpdatePressed;
        _view.Actions.CheckLauncherUpdatePressed += OnCheckLauncherUpdatePressed;
        _view.ModManagerButton.Pressed += OnModManagerPressed;
        _view.ModManager.BackPressed += OnModManagerBackPressed;
        _view.DebugButton.Pressed += OnDebugTogglePressed;
        UpdateDebugButtonLabel();

        var localBackupPref = LauncherModel.LoadLocalBackupPref();
        _view.Actions.SetLocalBackupChecked(localBackupPref);
        CloudSyncCoordinator.LocalBackupEnabled = localBackupPref;
        // Always ensure the external StS2LauncherMM/{Mods,Saves} tree exists when
        // the user has granted storage permission — the Mods directory in
        // particular is needed for ModLoaderPatches to find user-installed mods,
        // independently of the Local Backup toggle. Internally a no-op when
        // permission isn't granted yet.
        AppPaths.EnsureExternalDirectories();
        _view.Actions.SetCloudSyncChecked(LauncherModel.LoadCloudSyncPref());

        var result = _model.StartSession();
        HandleFastPath(result);
        MaybePromptStoragePermission();
    }

    // Re-prompt every launch until storage permission is actually granted.
    // Mods, save backup, and debug logs all live under
    // /storage/emulated/0/StS2LauncherMM/, so a stuck-on-no state silently
    // breaks half the launcher. The previous one-time marker meant a single
    // misclick on Cancel left the user permanently locked out with no way
    // back from inside the launcher.
    private void MaybePromptStoragePermission()
    {
        if (AppPaths.HasStoragePermission())
            return;

        _view.ShowConfirmation(
            "Allow 'All Files Access'?\n\nNeeded for installing mods, saving local game backups, and writing debug logs under /storage/emulated/0/StS2LauncherMM/.\n\nIf you cancel, this prompt will appear again on the next launch.",
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
        var firstShow = !_launchStageShown;
        _launchStageShown = true;
        _lastLaunchText = text;
        _lastShowCloudSync = showCloudSync;
        _lastShowUpdate = showUpdate;
        _view.Actions.ShowLaunch(text, showCloudSync, showUpdate);
        _view.ModManagerButton.Visible = true;

        // Kick off the launcher self-update check the first time we land on the
        // launch stage. Only once per session, silent if already on latest.
        if (firstShow && showUpdate && !_autoUpdateChecked)
        {
            _autoUpdateChecked = true;
            _ = AutoCheckLauncherUpdateOnStartup();
        }

        if (firstShow)
            DispatchDebugIntents();
    }

    // Debug-only: GodotApp.java drops marker files when started with
    // `adb shell am start --es debug_force_<dialog> 1` (only on -debug builds).
    // Convert them into real dialog calls so we can verify UI / Korean copy /
    // marker extraction without round-tripping through GitHub or Steam.
    private void DispatchDebugIntents()
    {
        try
        {
            var dataDir = OS.GetDataDir();
            var updateMarker = Path.Combine(dataDir, ".debug_force_update_dialog");
            if (File.Exists(updateMarker))
            {
                var lines = File.ReadAllLines(updateMarker);
                var fakeVersion = lines.Length > 0 ? lines[0] : "0.0.0";
                var fakeBody = lines.Length > 1
                    ? string.Join("\n", lines, 1, lines.Length - 1)
                    : "";
                var fakeNotes = ReleaseNotes.ExtractDialogBody(fakeBody);
                var fakeResult = new AppUpdateResult(
                    fakeVersion,
                    "https://example.invalid/fake.apk",
                    fakeNotes
                );
                File.Delete(updateMarker);
                PatchHelper.Log("[Debug] Forcing PromptLauncherUpdate via debug intent");
                PromptLauncherUpdate(fakeResult);
            }

        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Debug] DispatchDebugIntents failed: {ex.Message}");
        }
    }

    private bool _autoUpdateChecked;

    // Repurposed in 0.3.0 to open the Save Sync dialog instead of the WIP mod
    // manager screen. The original mod-manager navigation is preserved (commented
    // below) for when that flow is finished.
    private async void OnModManagerPressed()
    {
        PatchHelper.Log("[Mods] Save Manager button tapped");
        _view.Actions.SetSyncBusy(true);
        _view.SetStatus("Save Manager");
        try
        {
            await LauncherPatches.OpenSaveSyncDialogAsync(_view.RootControl);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Cloud] Save Manager error: {ex.Message}");
        }
        finally
        {
            _view.Actions.SetSyncBusy(false);
        }
        // Original navigation:
        // _view.ShowModManager();
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

        if (_checkingForGameUpdate)
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

    private async void OnCheckGameUpdatePressed()
    {
        _checkingForGameUpdate = true;
        _view.Actions.SetGameUpdateButtonDisabled(true);
        _view.Actions.SetGameUpdateButtonText("Loading branches...");

        System.Collections.Generic.List<SteamBranchInfo> branches;
        try
        {
            branches = await _model.ListBranchesAsync();
        }
        catch (Exception ex)
        {
            _view.AppendLog($"Branch list failed: {ex.Message}");
            ResetGameUpdateButton();
            _checkingForGameUpdate = false;
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
                ResetGameUpdateButton();
                _checkingForGameUpdate = false;
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
                ResetGameUpdateButton();
                _checkingForGameUpdate = false;
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
            _checkingForGameUpdate = false;
            return;
        }

        _view.Actions.SetGameUpdateButtonText(
            picked == "public" ? "Checking..." : $"Checking {picked}..."
        );

        await _model.CheckForUpdatesAsync(picked);

        _checkingForGameUpdate = false;
    }

    private const string ReleasesPageUrl =
        "https://github.com/iunius612/StS2-Launcher_Mod_Manager/releases/latest";

    private async void OnCheckLauncherUpdatePressed() =>
        await RunLauncherUpdateCheck(showLatestDialog: true);

    // Runs at startup once the launch stage is shown so the user is informed
    // about a new launcher version without having to remember to tap the button.
    // Silent on "already on latest" to avoid an unsolicited dialog every boot.
    private async Task AutoCheckLauncherUpdateOnStartup()
    {
        await Task.Delay(1500);
        await RunLauncherUpdateCheck(showLatestDialog: false);
    }

    private async Task RunLauncherUpdateCheck(bool showLatestDialog)
    {
        if (_checkingForLauncherUpdate)
            return;
        _checkingForLauncherUpdate = true;
        _view.Actions.SetLauncherUpdateButtonDisabled(true);
        _view.Actions.SetLauncherUpdateButtonText("Checking...");
        PatchHelper.Log("[Launcher] Checking for launcher update...");

        AppUpdateResult result;
        try
        {
            result = await AppUpdateChecker.CheckAsync();
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Launcher] Update check failed: {ex.Message}");
            _runOnMainThread(() =>
            {
                _view.AppendLog($"Launcher update check failed: {ex.Message}");
                _view.Actions.SetLauncherUpdateButtonText("CHECK LAUNCHER UPDATE");
                _view.Actions.SetLauncherUpdateButtonDisabled(false);
                if (showLatestDialog)
                    _view.ShowConfirmation(
                        $"Failed to check for launcher updates.\n\n{ex.Message}",
                        onConfirmed: () => { },
                        onCancelled: null
                    );
            });
            _checkingForLauncherUpdate = false;
            return;
        }

        PatchHelper.Log($"[Launcher] Update check result: HasUpdate={result.HasUpdate}, latest={result.LatestVersion}");

        if (!result.HasUpdate)
        {
            _runOnMainThread(() =>
            {
                _view.Actions.SetLauncherUpdateButtonText("CHECK LAUNCHER UPDATE");
                _view.Actions.SetLauncherUpdateButtonDisabled(false);
                if (showLatestDialog)
                    _view.ShowConfirmation(
                        "You're already on the latest launcher version.\n\nOpen the GitHub releases page anyway?",
                        onConfirmed: () => OS.ShellOpen(ReleasesPageUrl),
                        onCancelled: null
                    );
            });
            _checkingForLauncherUpdate = false;
            return;
        }

        _runOnMainThread(() =>
        {
            _view.Actions.SetLauncherUpdateButtonText($"v{result.LatestVersion} available");
            _view.Actions.SetLauncherUpdateButtonDisabled(false);
            PromptLauncherUpdate(result);
        });
        _checkingForLauncherUpdate = false;
    }

    private void PromptLauncherUpdate(AppUpdateResult result)
    {
        // No APK asset attached to the release — fall back to opening the GitHub page.
        if (string.IsNullOrEmpty(result.DownloadUrl))
        {
            _view.ShowConfirmation(
                $"Launcher v{result.LatestVersion} is available, but no APK asset was attached.\n\nOpen the GitHub releases page in a browser?",
                onConfirmed: () => OS.ShellOpen(ReleasesPageUrl),
                onCancelled: null
            );
            return;
        }

        // System "install unknown apps" toggle is per-source on Android 8+. Without it
        // the install Intent silently no-ops, so route the user to settings first.
        if (!AppUpdateInstaller.CanRequestInstallPackages())
        {
            _view.ShowConfirmation(
                $"Launcher v{result.LatestVersion} is available.\n\nTo install it, allow this app to install other apps. Open system settings?",
                onConfirmed: AppUpdateInstaller.RequestInstallPackagesPermission,
                onCancelled: null
            );
            return;
        }

        // Release notes excerpt (between <!-- launcher-dialog --> markers) is
        // shown verbatim if present. Authors keep these short — the full
        // changelog lives on the GitHub release page.
        var msg = string.IsNullOrEmpty(result.ReleaseNotes)
            ? $"Launcher v{result.LatestVersion} is available.\n\nDownload and install now?"
            : $"Launcher v{result.LatestVersion} is available.\n\n{result.ReleaseNotes}\n\nDownload and install now?";
        _view.ShowConfirmation(
            msg,
            onConfirmed: () => StartLauncherDownload(result),
            onCancelled: null
        );
    }

    private void StartLauncherDownload(AppUpdateResult result)
    {
        var dialog = _view.ShowLauncherUpdateDialog(result.LatestVersion);
        var cts = new CancellationTokenSource();
        dialog.Cancelled += () => cts.Cancel();

        var progress = new Progress<ApkDownloadProgress>(p =>
            _runOnMainThread(() => dialog.SetProgress(p.DownloadedBytes, p.TotalBytes, p.Percentage))
        );

        Task.Run(async () =>
        {
            try
            {
                var apkPath = await AppUpdateInstaller.DownloadApkAsync(
                    result.DownloadUrl,
                    progress,
                    cts.Token
                );
                _runOnMainThread(() =>
                {
                    dialog.Close();
                    _view.AppendLog($"Launcher update v{result.LatestVersion} downloaded; opening installer...");
                    AppUpdateInstaller.LaunchInstall(apkPath);
                });
            }
            catch (OperationCanceledException)
            {
                _runOnMainThread(() =>
                {
                    dialog.Close();
                    _view.AppendLog("Launcher update download cancelled.");
                });
            }
            catch (Exception ex)
            {
                _runOnMainThread(() =>
                {
                    dialog.Close();
                    _view.AppendLog($"Launcher update download failed: {ex.Message}");
                });
            }
        });
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

    private void ResetGameUpdateButton()
    {
        _view.Actions.SetGameUpdateButtonText("CHECK GAME UPDATE");
        _view.Actions.SetGameUpdateButtonDisabled(false);
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
                onCancelled: () => tcs.TrySetResult(null),
                // Issue #23 — manual atlas-cache wipe entrypoint. The branch
                // picker closes itself before raising the event; here we
                // resolve the picker's task as a cancel and chain the
                // confirm-and-restart flow.
                onAtlasWipeRequested: () =>
                {
                    tcs.TrySetResult(null);
                    ShowAtlasWipeConfirm();
                }
            );
        });
        return tcs.Task;
    }

    private void ShowAtlasWipeConfirm()
    {
        _view.ShowConfirmation(
            "이미지 인덱스 캐시 정리\n\n"
                + "포션 / 카드 / 유물 등 이미지가 잘못 표시될 때 사용하세요.\n"
                + "게임 텍스처 캐시(약 660개) 를 삭제하고 앱을 재시작합니다.\n\n"
                + "* 다음 실행이 30~60초 더 걸립니다 (재import)\n"
                + "* 게임을 다시 다운로드하지 않습니다\n"
                + "* 세이브 / 진행도 / 로그인 정보는 보존됩니다",
            onConfirmed: () =>
            {
                try
                {
                    var marker = Path.Combine(OS.GetDataDir(), ".atlas_wipe_pending");
                    File.Create(marker).Dispose();
                    PatchHelper.Log("[AtlasWipe] manual marker written, restarting");
                }
                catch (Exception ex)
                {
                    PatchHelper.Log($"[AtlasWipe] failed to write marker: {ex.Message}");
                }
                LauncherModel.GetGodotApp()?.Call("restartApp");
            },
            onCancelled: null
        );
    }

    private void OnDebugTogglePressed()
    {
        if (DebugLogger.IsEnabled())
        {
            var path = DebugLogger.GetCurrentFilePath() ?? DebugLogger.GetLogsDirPath();
            _view.ShowConfirmation(
                $"Debug logging is ON.\n\nCurrent log file:\n{path}\n\nTurn off?",
                onConfirmed: () =>
                {
                    DebugLogger.Disable();
                    UpdateDebugButtonLabel();
                    _view.AppendLog("Debug logging disabled.");
                },
                onCancelled: null
            );
        }
        else
        {
            var dir = DebugLogger.GetLogsDirPath();
            _view.ShowConfirmation(
                $"Turn debug logging on?\n\nLogs will be written under:\n{dir}\n\nFor full launch-to-gameplay logs, restart the app after enabling.",
                onConfirmed: () =>
                {
                    var path = DebugLogger.Enable();
                    UpdateDebugButtonLabel();
                    _view.AppendLog($"Debug logging enabled → {path ?? "(failed to start)"}");
                },
                onCancelled: null
            );
        }
    }

    private void UpdateDebugButtonLabel() =>
        _view.DebugButton.Text = DebugLogger.IsEnabled() ? "Debug: ON" : "Debug: OFF";

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
                _view.Actions.SetSyncBusy(true);
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
                        _view.Actions.SetSyncBusy(false);
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
                _view.Actions.SetSyncBusy(true);
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
                        _view.Actions.SetSyncBusy(false);
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
