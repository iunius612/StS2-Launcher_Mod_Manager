using System;
using System.Collections.Generic;
using Godot;
using STS2Mobile.Steam;

namespace STS2Mobile.Launcher.Components;

// Modal that lists every public Steam branch the user can switch to.
// Mirrors the Steam client's "Game Versions and Beta" panel: one radio row per
// branch with name + description + last-updated date, plus OK/Cancel.
// Password-gated branches are rendered disabled (v0.2.3 will wire up the
// pwdrequired flow).
public class BranchPickerDialog : ColorRect
{
    public event Action<string> BranchConfirmed;
    public event Action Cancelled;

    public BranchPickerDialog(
        IReadOnlyList<SteamBranchInfo> branches,
        string currentBranch,
        float scale
    )
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        Color = new Color(0, 0, 0, 0.6f);

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);

        var dialogBox = new PanelContainer();
        var boxStyle = new StyleBoxFlat();
        boxStyle.BgColor = new Color(0.15f, 0.15f, 0.18f);
        boxStyle.SetCornerRadiusAll((int)(8 * scale));
        boxStyle.SetContentMarginAll((int)(24 * scale));
        dialogBox.AddThemeStyleboxOverride("panel", boxStyle);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", (int)(16 * scale));
        dialogBox.AddChild(vbox);

        var title = new StyledLabel("Select game version", scale, fontSize: 18);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(title);

        var hint = new StyledLabel(
            "Pick a Steam branch to download. Beta branches may be unstable.",
            scale,
            fontSize: 12
        );
        hint.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.75f));
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        hint.CustomMinimumSize = new Vector2((int)(420 * scale), 0);
        vbox.AddChild(hint);

        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", (int)(8 * scale));
        rows.CustomMinimumSize = new Vector2((int)(420 * scale), 0);
        vbox.AddChild(rows);

        var group = new ButtonGroup();
        CheckBox firstSelectable = null;
        CheckBox preselected = null;
        foreach (var branch in branches)
        {
            var row = BuildRow(branch, currentBranch, scale, group, out var checkBox);
            rows.AddChild(row);

            if (checkBox != null && !checkBox.Disabled)
            {
                firstSelectable ??= checkBox;
                if (branch.Name == currentBranch)
                    preselected = checkBox;
            }
        }

        if (preselected != null)
            preselected.ButtonPressed = true;
        else if (firstSelectable != null)
            firstSelectable.ButtonPressed = true;

        var buttonRow = new HBoxContainer();
        buttonRow.AddThemeConstantOverride("separation", (int)(12 * scale));
        buttonRow.Alignment = BoxContainer.AlignmentMode.Center;
        vbox.AddChild(buttonRow);

        var cancelButton = new StyledButton("Cancel", scale, fontSize: 14, height: 44);
        cancelButton.CustomMinimumSize = new Vector2(
            (int)(120 * scale),
            cancelButton.CustomMinimumSize.Y
        );
        cancelButton.Pressed += () =>
        {
            QueueFree();
            Cancelled?.Invoke();
        };
        buttonRow.AddChild(cancelButton);

        var okButton = new StyledButton("OK", scale, fontSize: 14, height: 44);
        okButton.CustomMinimumSize = new Vector2((int)(120 * scale), okButton.CustomMinimumSize.Y);
        okButton.Pressed += () =>
        {
            var pressed = group.GetPressedButton();
            var picked = pressed?.GetMeta("branch").AsString();
            QueueFree();
            if (!string.IsNullOrEmpty(picked))
                BranchConfirmed?.Invoke(picked);
            else
                Cancelled?.Invoke();
        };
        buttonRow.AddChild(okButton);

        center.AddChild(dialogBox);
        AddChild(center);
    }

    private static Control BuildRow(
        SteamBranchInfo branch,
        string currentBranch,
        float scale,
        ButtonGroup group,
        out CheckBox checkBox
    )
    {
        // Wrap the row in a Button so that tapping anywhere along the row toggles
        // the radio. Without this the user has to land precisely on the small
        // CheckBox circle, which is a fingertip-hit-target nightmare on phones.
        var row = new Button();
        row.Flat = true;
        row.ToggleMode = false;
        row.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
        row.CustomMinimumSize = new Vector2(0, (int)(56 * scale));

        var hbox = new HBoxContainer();
        hbox.SetAnchorsPreset(LayoutPreset.FullRect);
        hbox.AddThemeConstantOverride("separation", (int)(12 * scale));
        hbox.MouseFilter = MouseFilterEnum.Ignore;
        row.AddChild(hbox);

        checkBox = new CheckBox();
        checkBox.ButtonGroup = group;
        checkBox.SetMeta("branch", branch.Name);
        checkBox.Disabled = branch.IsPasswordProtected;
        checkBox.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        // Enlarge the toggle's icon and hit-area so it's tappable on its own too.
        checkBox.CustomMinimumSize = new Vector2((int)(36 * scale), (int)(36 * scale));
        checkBox.AddThemeConstantOverride("icon_max_width", (int)(28 * scale));
        checkBox.MouseFilter = MouseFilterEnum.Ignore;
        hbox.AddChild(checkBox);

        // Tapping the row body toggles the radio (skipping disabled rows).
        var capturedCheckBox = checkBox;
        if (!branch.IsPasswordProtected)
        {
            row.Pressed += () =>
            {
                if (!capturedCheckBox.ButtonPressed)
                    capturedCheckBox.ButtonPressed = true;
            };
        }
        else
        {
            row.Disabled = true;
        }

        var textCol = new VBoxContainer();
        textCol.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        textCol.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        textCol.AddThemeConstantOverride("separation", (int)(2 * scale));
        textCol.MouseFilter = MouseFilterEnum.Ignore;
        hbox.AddChild(textCol);

        var titleText = branch.IsPasswordProtected
            ? $"{branch.Name} (private)"
            : branch.Name + (branch.Name == currentBranch ? "  ·  current" : "");
        var titleLabel = new StyledLabel(titleText, scale, fontSize: 14);
        titleLabel.AddThemeColorOverride(
            "font_color",
            branch.IsPasswordProtected ? new Color(0.55f, 0.55f, 0.6f) : new Color(0.95f, 0.95f, 1f)
        );
        textCol.AddChild(titleLabel);

        if (!string.IsNullOrEmpty(branch.Description))
        {
            var descLabel = new StyledLabel(branch.Description, scale, fontSize: 11);
            descLabel.AddThemeColorOverride("font_color", new Color(0.65f, 0.65f, 0.7f));
            descLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            textCol.AddChild(descLabel);
        }

        if (branch.TimeUpdatedUtc != default)
        {
            var dateText = branch.TimeUpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd");
            var dateLabel = new StyledLabel(dateText, scale, fontSize: 11);
            dateLabel.AddThemeColorOverride("font_color", new Color(0.55f, 0.55f, 0.6f));
            dateLabel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            dateLabel.MouseFilter = MouseFilterEnum.Ignore;
            hbox.AddChild(dateLabel);
        }

        if (branch.IsPasswordProtected)
            row.Modulate = new Color(0.65f, 0.65f, 0.7f);

        return row;
    }
}
