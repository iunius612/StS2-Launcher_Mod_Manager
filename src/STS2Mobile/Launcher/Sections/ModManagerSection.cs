using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.Launcher.Components;
using STS2Mobile.Modding;

namespace STS2Mobile.Launcher.Sections;

// Full-width mod manager screen shown when the user taps "MOD MANAGER" on the
// login screen. Scans AppPaths.ExternalModsDir, renders one ModListRow per mod,
// and provides import/reorder/toggle/remove actions wired directly to ModConfig
// and ModImporter.
public class ModManagerSection : VBoxContainer
{
    public event Action BackPressed;
    public event Action<string, Action, Action> ConfirmationRequested;

    private readonly float _scale;
    private readonly VBoxContainer _listContainer;
    private readonly StyledLabel _statusLabel;
    private readonly StyledButton _importButton;
    private readonly StyledButton _refreshButton;
    private readonly StyledButton _backButton;
    private readonly StyledButton _permissionButton;

    private bool _importInFlight;

    public ModManagerSection(float scale)
    {
        _scale = scale;
        Visible = false;
        AddThemeConstantOverride("separation", (int)(8 * scale));

        var title = new StyledLabel("Mod Manager", scale, fontSize: 20);
        AddChild(title);

        _statusLabel = new StyledLabel("", scale, fontSize: 12);
        _statusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        AddChild(_statusLabel);

        _permissionButton = new StyledButton("Grant Storage Permission", scale, fontSize: 14);
        _permissionButton.Visible = false;
        _permissionButton.Pressed += OnGrantPermissionPressed;
        AddChild(_permissionButton);

        var actionRow = new HBoxContainer();
        actionRow.AddThemeConstantOverride("separation", (int)(6 * scale));
        AddChild(actionRow);

        _importButton = new StyledButton("Import Mod (.zip)...", scale, fontSize: 14);
        _importButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _importButton.Pressed += OnImportPressed;
        actionRow.AddChild(_importButton);

        _refreshButton = new StyledButton("Refresh", scale, fontSize: 14);
        _refreshButton.CustomMinimumSize = new Vector2((int)(100 * scale), 0);
        _refreshButton.Pressed += Refresh;
        actionRow.AddChild(_refreshButton);

        var scroll = new ScrollContainer();
        scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        scroll.CustomMinimumSize = new Vector2(0, (int)(220 * scale));
        AddChild(scroll);

        _listContainer = new VBoxContainer();
        _listContainer.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _listContainer.AddThemeConstantOverride("separation", (int)(6 * scale));
        scroll.AddChild(_listContainer);

        _backButton = new StyledButton("BACK", scale, fontSize: 14);
        _backButton.Pressed += () => BackPressed?.Invoke();
        AddChild(_backButton);
    }

    public void Refresh()
    {
        ClearList();

        if (!AppPaths.HasStoragePermission())
        {
            SetStatus(
                "Storage permission is required to manage mods.",
                new Color(0.95f, 0.75f, 0.3f)
            );
            _permissionButton.Visible = true;
            _importButton.Disabled = true;
            return;
        }

        _permissionButton.Visible = false;
        _importButton.Disabled = _importInFlight;
        AppPaths.EnsureExternalDirectories();

        var scanned = ModScanner.Scan();
        var cfg = ModConfig.Load();
        var reconciled = cfg.Reconcile(scanned.Select(m => m.Id));

        var byId = scanned.ToDictionary(m => m.Id, m => m);
        var orderedInfos = new List<ModEntryInfo>();
        foreach (var entry in reconciled)
        {
            if (byId.TryGetValue(entry.Id, out var info))
                orderedInfos.Add(info);
        }

        if (orderedInfos.Count == 0)
        {
            SetStatus(
                "No mods installed. Tap \"Import Mod\" and pick one or more .zip files.",
                new Color(0.75f, 0.75f, 0.8f)
            );
            return;
        }

        SetStatus($"{orderedInfos.Count} mod(s) installed.", new Color(0.75f, 0.75f, 0.8f));

        for (int i = 0; i < orderedInfos.Count; i++)
        {
            var info = orderedInfos[i];
            var entry = cfg.Get(info.Id);
            var canUp = i > 0;
            var canDown = i < orderedInfos.Count - 1;
            var row = new ModListRow(info, entry.Enabled, canUp, canDown, _scale);
            var capturedId = info.Id;
            row.Toggled += on => OnRowToggled(capturedId, on);
            row.MoveUpPressed += () => OnRowMoved(capturedId, -1);
            row.MoveDownPressed += () => OnRowMoved(capturedId, +1);
            row.RemovePressed += () => OnRowRemovePressed(info);
            _listContainer.AddChild(row);
        }
    }

    private void ClearList()
    {
        for (int i = _listContainer.GetChildCount() - 1; i >= 0; i--)
        {
            var child = _listContainer.GetChild(i);
            _listContainer.RemoveChild(child);
            child.QueueFree();
        }
    }

    private void OnRowToggled(string id, bool enabled)
    {
        var cfg = ModConfig.Load();
        cfg.Add(id, enabled);
        cfg.Save();
    }

    private void OnRowMoved(string id, int delta)
    {
        var cfg = ModConfig.Load();
        cfg.Move(id, delta);
        cfg.Save();
        Refresh();
    }

    private void OnRowRemovePressed(ModEntryInfo info)
    {
        var id = info.Id;
        ConfirmationRequested?.Invoke(
            $"Remove '{info.Manifest.DisplayName}'?\nThis deletes the mod folder from storage.",
            () =>
            {
                if (ModImporter.DeleteMod(id))
                    SetStatus($"Removed {id}.", new Color(0.8f, 0.8f, 0.85f));
                else
                    SetStatus($"Failed to remove {id}.", new Color(0.95f, 0.4f, 0.4f));
                Refresh();
            },
            null
        );
    }

    private void OnGrantPermissionPressed()
    {
        AppPaths.RequestStoragePermission();
        SetStatus(
            "After granting permission, return here and tap Refresh.",
            new Color(0.95f, 0.75f, 0.3f)
        );
    }

    private void OnImportPressed()
    {
        if (_importInFlight)
            return;
        PatchHelper.Log("[Mods] Import button tapped");
        _importInFlight = true;
        _importButton.Disabled = true;
        SetStatus("Opening file picker...", new Color(0.75f, 0.75f, 0.8f));

        // Run the whole import pipeline on the thread pool to avoid Godot's
        // SynchronizationContext being disrupted by the SAF picker's OnPause/OnResume.
        // Any UI touches inside the pipeline must go through SetStatus/FinishImport
        // (which CallDeferred onto the main thread).
        _ = Task.Run(RunImportPipelineAsync);
    }

    private async Task RunImportPipelineAsync()
    {
        try
        {
            PatchHelper.Log("[Mods] RunImportPipelineAsync started");
            string[] zipPaths;
            try
            {
                zipPaths = await SafBridge
                    .PickZipsToCacheAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"[Mods] SAF pick failed: {ex}");
                FinishImport("Import failed: " + ex.Message, error: true, refresh: false);
                return;
            }

            PatchHelper.Log(
                $"[Mods] SAF returned {(zipPaths == null ? "null" : zipPaths.Length.ToString())} path(s)"
            );

            if (zipPaths == null || zipPaths.Length == 0)
            {
                FinishImport("Import cancelled.", error: false, refresh: false);
                return;
            }

            PatchHelper.Log($"[Mods] Starting sequential import of {zipPaths.Length} file(s)");
            await ImportSequentially(zipPaths, 0, imported: 0, failed: 0).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Mods] RunImportPipelineAsync fatal: {ex}");
            FinishImport("Import failed: " + ex.Message, error: true, refresh: false);
        }
    }

    private async Task ImportSequentially(string[] zipPaths, int index, int imported, int failed)
    {
        PatchHelper.Log($"[Mods] ImportSequentially enter index={index}/{zipPaths.Length}");
        if (index >= zipPaths.Length)
        {
            var msg = zipPaths.Length == 1
                ? (imported == 1 ? $"Imported 1 mod." : "Import failed.")
                : $"Imported {imported} / {zipPaths.Length} mod(s)" +
                  (failed > 0 ? $" ({failed} failed)." : ".");
            FinishImport(msg, error: imported == 0, refresh: imported > 0);
            return;
        }

        var zipPath = zipPaths[index];
        SetStatus($"Importing {index + 1}/{zipPaths.Length}...", new Color(0.75f, 0.75f, 0.8f));

        try
        {
            PatchHelper.Log($"[Mods] ImportZipAsync start: {zipPath}");
            var result = await ModImporter.ImportZipAsync(zipPath, overwrite: false);
            PatchHelper.Log(
                $"[Mods] ImportZipAsync done: success={result.Success} exists={result.AlreadyExists} id={result.ModId} err={result.Error}"
            );
            if (result.AlreadyExists)
            {
                var idx = index;
                var imp = imported;
                var fail = failed;
                // ConfirmationRequested creates a Godot Dialog; the subscriber is on the
                // main thread, so dispatch the invocation there explicitly. The confirm
                // callbacks continue the import on the thread pool again.
                Callable
                    .From(() =>
                    {
                        ConfirmationRequested?.Invoke(
                            $"'{result.ModId}' is already installed. Overwrite?",
                            () => _ = Task.Run(async () =>
                            {
                                var overwritten = await ModImporter.ImportZipAsync(zipPath, overwrite: true);
                                if (overwritten.Success)
                                    imp++;
                                else
                                    fail++;
                                await ImportSequentially(zipPaths, idx + 1, imp, fail);
                            }),
                            () => _ = Task.Run(async () =>
                            {
                                ModImporter.CleanupImportZip(zipPath);
                                await ImportSequentially(zipPaths, idx + 1, imp, fail + 1);
                            })
                        );
                    })
                    .CallDeferred();
                return;
            }

            if (result.Success)
                imported++;
            else
                failed++;
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Mods] Import exception for {zipPath}: {ex}");
            failed++;
        }

        await ImportSequentially(zipPaths, index + 1, imported, failed);
    }

    private void FinishImport(string message, bool error, bool refresh)
    {
        SetStatus(
            message,
            error ? new Color(0.95f, 0.4f, 0.4f) : new Color(0.75f, 0.75f, 0.8f)
        );
        _importInFlight = false;
        Callable
            .From(() =>
            {
                _importButton.Disabled = false;
                if (refresh)
                    Refresh();
            })
            .CallDeferred();
    }

    // Marshals to the Godot main thread because import continuations may resume
    // on the thread pool after SAF picker round-trip.
    private void SetStatus(string text, Godot.Color color)
    {
        Callable
            .From(() =>
            {
                _statusLabel.Text = text;
                _statusLabel.AddThemeColorOverride("font_color", color);
            })
            .CallDeferred();
    }
}
