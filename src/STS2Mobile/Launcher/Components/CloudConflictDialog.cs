using System;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.Steam;
// SyncDecision is in STS2Mobile.Steam — already imported above.

namespace STS2Mobile.Launcher.Components;

public enum CloudConflictChoice
{
    KeepLocal,
    KeepCloud,
    Cancel,
}

// Modal shown on first PLAY when local and cloud progress.save snapshots differ.
// Two side-by-side summary cards; the more recent one is highlighted with a
// colored border and a "최근" badge so the user can tell at a glance which
// copy reflects their latest play. Choice resolves a TaskCompletionSource so
// the launcher can await the user's decision before continuing into the game.
public class CloudConflictDialog : ColorRect
{
    private readonly TaskCompletionSource<CloudConflictChoice> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<CloudConflictChoice> Result => _result.Task;

    public CloudConflictDialog(
        SaveProgressSummary local,
        SaveProgressSummary cloud,
        bool localIsMoreRecent,
        float scale,
        SyncDecision decision = SyncDecision.Conflict
    )
    {
        local ??= new SaveProgressSummary();
        cloud ??= new SaveProgressSummary();

        SetAnchorsPreset(LayoutPreset.FullRect);
        Color = new Color(0, 0, 0, 0.7f);
        // Higher than LauncherUI (ZIndex=100) so this stays on top during the
        // brief overlap with launcher teardown. Also future-proofs against any
        // residual game UI that might draw between launcher.QueueFree() and
        // the next frame.
        ZIndex = 200;

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);

        var dialogBox = new PanelContainer();
        var boxStyle = new StyleBoxFlat();
        boxStyle.BgColor = new Color(0.13f, 0.13f, 0.16f);
        boxStyle.SetCornerRadiusAll((int)(10 * scale));
        boxStyle.SetContentMarginAll((int)(28 * scale));
        dialogBox.AddThemeStyleboxOverride("panel", boxStyle);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", (int)(18 * scale));
        dialogBox.AddChild(vbox);

        bool isInSync = decision == SyncDecision.Identical || decision == SyncDecision.NoData;
        var (titleText, subtitleText) = decision switch
        {
            SyncDecision.Identical => (
                "세이브 동기화 상태",
                "로컬과 Steam Cloud의 진행도가 일치합니다.\n별도 작업이 필요하지 않습니다."
            ),
            SyncDecision.NoData => (
                "세이브 동기화 상태",
                "로컬과 Steam Cloud 모두 진행도 데이터가 없습니다."
            ),
            SyncDecision.MobileOnly => (
                "세이브 데이터 동기화",
                "Steam Cloud에 진행도가 없습니다.\n이 디바이스 진행도를 클라우드로 업로드할까요?"
            ),
            SyncDecision.CloudOnly => (
                "세이브 데이터 동기화",
                "이 디바이스에 진행도가 없습니다.\nSteam Cloud의 진행도를 가져올까요?"
            ),
            _ => (
                "세이브 데이터 충돌",
                "이 디바이스와 Steam Cloud의 진행도가 다릅니다.\n어느 쪽을 유지할지 선택하세요."
            ),
        };
        var title = new StyledLabel(titleText, scale, fontSize: 22);
        vbox.AddChild(title);

        var subtitle = new StyledLabel(subtitleText, scale, fontSize: 13);
        subtitle.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        subtitle.CustomMinimumSize = new Vector2((int)(620 * scale), 0);
        vbox.AddChild(subtitle);

        var cardsRow = new HBoxContainer();
        cardsRow.AddThemeConstantOverride("separation", (int)(16 * scale));
        cardsRow.Alignment = BoxContainer.AlignmentMode.Center;
        vbox.AddChild(cardsRow);

        cardsRow.AddChild(BuildSummaryCard("📱  이 디바이스 (로컬)", local, localIsMoreRecent, scale));
        cardsRow.AddChild(BuildSummaryCard("☁  Steam Cloud", cloud, !localIsMoreRecent, scale));

        // No "remember my choice" checkbox by design: which side is correct
        // depends on the situation (which device was last played, whether the
        // user just restored from a backup, etc.), so blanket auto-apply would
        // silently destroy data on the next conflict that should have gone the
        // other way. Force a fresh decision every time.

        var buttonRow = new HBoxContainer();
        buttonRow.AddThemeConstantOverride("separation", (int)(12 * scale));
        buttonRow.Alignment = BoxContainer.AlignmentMode.Center;
        vbox.AddChild(buttonRow);

        // When sides are in sync (Identical / NoData), the local/cloud buttons
        // would just trigger a redundant push or pull of identical data. Hide
        // them and offer only a close action so the dialog is purely
        // informational.
        var cancelBtn = new StyledButton(
            isInSync ? "닫기" : "취소",
            scale,
            fontSize: 14,
            height: 48
        );
        cancelBtn.CustomMinimumSize = new Vector2((int)(140 * scale), cancelBtn.CustomMinimumSize.Y);
        cancelBtn.Pressed += () => Resolve(CloudConflictChoice.Cancel);
        buttonRow.AddChild(cancelBtn);

        if (!isInSync)
        {
            var localBtn = new StyledButton("로컬 유지", scale, fontSize: 15, height: 48);
            localBtn.CustomMinimumSize = new Vector2(
                (int)(160 * scale),
                localBtn.CustomMinimumSize.Y
            );
            if (localIsMoreRecent)
                EmphasizeButton(localBtn, scale);
            localBtn.Pressed += () => Resolve(CloudConflictChoice.KeepLocal);
            buttonRow.AddChild(localBtn);

            var cloudBtn = new StyledButton("클라우드 유지", scale, fontSize: 15, height: 48);
            cloudBtn.CustomMinimumSize = new Vector2(
                (int)(160 * scale),
                cloudBtn.CustomMinimumSize.Y
            );
            if (!localIsMoreRecent)
                EmphasizeButton(cloudBtn, scale);
            cloudBtn.Pressed += () => Resolve(CloudConflictChoice.KeepCloud);
            buttonRow.AddChild(cloudBtn);
        }

        center.AddChild(dialogBox);
        AddChild(center);
    }

    private void Resolve(CloudConflictChoice choice)
    {
        QueueFree();
        _result.TrySetResult(choice);
    }

    // Highlights the recommended button. Slight green tint mirrors the "최근"
    // badge color so the user can pattern-match the highlight to the side
    // marked as "more recent" without re-reading the cards.
    private static void EmphasizeButton(StyledButton btn, float scale)
    {
        var r = (int)(4 * scale);
        var accent = new Color(0.18f, 0.45f, 0.30f);
        var accentHover = new Color(0.22f, 0.52f, 0.35f);
        btn.AddThemeStyleboxOverride("normal", StyledButton.MakeFilled(accent, r));
        btn.AddThemeStyleboxOverride("hover", StyledButton.MakeFilled(accentHover, r));
        btn.AddThemeStyleboxOverride(
            "pressed",
            StyledButton.MakeFilled(new Color(0.14f, 0.38f, 0.25f), r)
        );
    }

    private static Control BuildSummaryCard(
        string title,
        SaveProgressSummary s,
        bool isRecent,
        float scale
    )
    {
        var card = new PanelContainer();
        var style = new StyleBoxFlat();
        style.BgColor = new Color(0.18f, 0.18f, 0.22f);
        style.SetCornerRadiusAll((int)(6 * scale));
        style.SetContentMarginAll((int)(16 * scale));
        if (isRecent)
        {
            style.BorderColor = new Color(0.3f, 0.75f, 0.5f);
            style.SetBorderWidthAll((int)(2 * scale));
        }
        card.AddThemeStyleboxOverride("panel", style);
        card.CustomMinimumSize = new Vector2((int)(300 * scale), 0);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", (int)(8 * scale));
        card.AddChild(col);

        var headerRow = new HBoxContainer();
        headerRow.AddThemeConstantOverride("separation", (int)(8 * scale));
        col.AddChild(headerRow);

        var titleLabel = new StyledLabel(title, scale, fontSize: 16, align: HorizontalAlignment.Left);
        titleLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        headerRow.AddChild(titleLabel);

        if (isRecent)
        {
            var badge = new PanelContainer();
            var badgeStyle = new StyleBoxFlat();
            badgeStyle.BgColor = new Color(0.3f, 0.75f, 0.5f);
            badgeStyle.SetCornerRadiusAll((int)(4 * scale));
            badgeStyle.SetContentMarginAll((int)(6 * scale));
            badge.AddThemeStyleboxOverride("panel", badgeStyle);
            var badgeLabel = new StyledLabel("최근", scale, fontSize: 11);
            badge.AddChild(badgeLabel);
            headerRow.AddChild(badge);
        }

        // Separator
        var sep = new ColorRect();
        sep.Color = new Color(1, 1, 1, 0.08f);
        sep.CustomMinimumSize = new Vector2(0, 1);
        col.AddChild(sep);

        if (s.IsEmpty)
        {
            var emptyMsg = new StyledLabel(
                "진행도 데이터 없음",
                scale,
                fontSize: 14,
                align: HorizontalAlignment.Center
            );
            emptyMsg.Modulate = new Color(1, 1, 1, 0.5f);
            emptyMsg.CustomMinimumSize = new Vector2(0, (int)(80 * scale));
            emptyMsg.VerticalAlignment = VerticalAlignment.Center;
            col.AddChild(emptyMsg);
            return card;
        }

        AddRow(col, "파일 생성 시간", s.FormatLastModified(), scale);
        AddRow(col, "파일 크기", s.FormatSize(), scale);

        if (s.ParseSucceeded)
        {
            AddRow(col, "총 플레이타임", s.FormatPlaytime(), scale);
            AddRow(col, "캐릭터", $"{s.CharactersTracked}명", scale);
            AddRow(col, "전적", $"{s.TotalWins}승 / {s.TotalLosses}패", scale);
            if (s.MaxAscension > 0)
                AddRow(col, "최고 승천", $"{s.MaxAscension}", scale);
            if (s.FloorsClimbed > 0)
                AddRow(col, "올라간 층", $"{s.FloorsClimbed:N0}", scale);
            if (s.RelicsDiscovered > 0)
                AddRow(col, "발견 유물", $"{s.RelicsDiscovered}", scale);
        }
        else if (!s.IsEmpty)
        {
            // Schema parse failed but file has content — still useful to show.
            var note = new StyledLabel(
                "(상세 통계를 읽지 못함 — 파일은 존재함)",
                scale,
                fontSize: 11,
                align: HorizontalAlignment.Left
            );
            note.Modulate = new Color(1, 1, 1, 0.6f);
            col.AddChild(note);
        }

        return card;
    }

    private static void AddRow(VBoxContainer parent, string key, string value, float scale)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", (int)(12 * scale));

        var k = new StyledLabel(key, scale, fontSize: 12, align: HorizontalAlignment.Left);
        k.Modulate = new Color(1, 1, 1, 0.6f);
        k.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(k);

        var v = new StyledLabel(value, scale, fontSize: 12, align: HorizontalAlignment.Right);
        row.AddChild(v);

        parent.AddChild(row);
    }
}
