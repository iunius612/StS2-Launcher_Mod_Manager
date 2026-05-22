using System;
using Godot;
using STS2Mobile.Launcher;

namespace STS2Mobile.Launcher.Components;

// Issue #36 Part A: result modal shown after a manual "Local Backup" run.
// The Local Backup toggle was replaced by a one-shot action button — pressing
// it kicks off LocalBackupService.BackupNow() on a background thread, and this
// dialog reports the outcome (file count + where it landed, or the failure
// reason). OK-only acknowledgment; no destructive choice to make here.
//
// Built from the same StyledButton/StyledLabel/PanelContainer idiom as
// StyledDialog so it visually matches the rest of the launcher's modals. Kept
// decoupled from the Steam-side result type: the controller unpacks the result
// and passes plain values, so this component has no dependency on
// STS2Mobile.Steam.
public class BackupResultDialog : ColorRect
{
    public event Action Closed;

    public BackupResultDialog(
        bool success,
        int fileCount,
        long totalBytes,
        string backupPath,
        string failureReason,
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
        vbox.AddThemeConstantOverride("separation", (int)(14 * scale));
        dialogBox.AddChild(vbox);

        var title = new StyledLabel(
            success ? "백업 완료" : "백업 실패",
            scale,
            fontSize: 20
        );
        vbox.AddChild(title);

        if (success)
        {
            AddRow(vbox, "백업된 파일", $"{fileCount}개", scale);
            if (totalBytes > 0)
                AddRow(vbox, "총 크기", LauncherModel.FormatSize(totalBytes), scale);
            if (!string.IsNullOrEmpty(backupPath))
            {
                // Full path is long; show it on its own wrapped line so the
                // user can find the folder via a file manager / adb pull.
                var pathLabel = new StyledLabel(
                    backupPath,
                    scale,
                    fontSize: 12,
                    align: HorizontalAlignment.Left
                );
                pathLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                pathLabel.CustomMinimumSize = new Vector2((int)(420 * scale), 0);
                pathLabel.Modulate = new Color(1, 1, 1, 0.7f);
                vbox.AddChild(MakeKeyLabel("저장 위치", scale));
                vbox.AddChild(pathLabel);
            }
        }
        else
        {
            var reason = new StyledLabel(
                string.IsNullOrEmpty(failureReason)
                    ? "백업 중 오류가 발생했습니다."
                    : failureReason,
                scale,
                fontSize: 14
            );
            reason.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            reason.CustomMinimumSize = new Vector2((int)(420 * scale), 0);
            vbox.AddChild(reason);
        }

        var buttonRow = new HBoxContainer();
        buttonRow.AddThemeConstantOverride("separation", (int)(12 * scale));
        buttonRow.Alignment = BoxContainer.AlignmentMode.Center;
        vbox.AddChild(buttonRow);

        var okButton = new StyledButton("확인", scale, fontSize: 14, height: 44);
        okButton.CustomMinimumSize = new Vector2((int)(140 * scale), okButton.CustomMinimumSize.Y);
        okButton.Pressed += () =>
        {
            QueueFree();
            Closed?.Invoke();
        };
        buttonRow.AddChild(okButton);

        center.AddChild(dialogBox);
        AddChild(center);
    }

    private static Label MakeKeyLabel(string key, float scale)
    {
        var k = new StyledLabel(key, scale, fontSize: 12, align: HorizontalAlignment.Left);
        k.Modulate = new Color(1, 1, 1, 0.6f);
        return k;
    }

    private static void AddRow(VBoxContainer parent, string key, string value, float scale)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", (int)(12 * scale));

        var k = new StyledLabel(key, scale, fontSize: 14, align: HorizontalAlignment.Left);
        k.Modulate = new Color(1, 1, 1, 0.6f);
        k.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(k);

        var v = new StyledLabel(value, scale, fontSize: 14, align: HorizontalAlignment.Right);
        row.AddChild(v);

        parent.AddChild(row);
    }
}
