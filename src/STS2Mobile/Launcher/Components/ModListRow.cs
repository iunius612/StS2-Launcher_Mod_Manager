using System;
using Godot;
using STS2Mobile.Modding;

namespace STS2Mobile.Launcher.Components;

// One row in the mod manager list. Shows enable toggle, title, reorder buttons,
// and an expandable detail panel with description/readme/remove.
public class ModListRow : PanelContainer
{
    public event Action<bool> Toggled;
    public event Action MoveUpPressed;
    public event Action MoveDownPressed;
    public event Action RemovePressed;

    public string ModId { get; }

    private readonly Button _toggleButton;
    private readonly Button _moveUpButton;
    private readonly Button _moveDownButton;
    private readonly Button _infoButton;
    private readonly VBoxContainer _detail;

    public ModListRow(ModEntryInfo info, bool enabled, bool canMoveUp, bool canMoveDown, float scale)
    {
        ModId = info.Id;

        var bg = new StyleBoxFlat();
        bg.BgColor = new Color(0.18f, 0.18f, 0.22f);
        bg.SetCornerRadiusAll((int)(4 * scale));
        bg.SetContentMarginAll((int)(8 * scale));
        AddThemeStyleboxOverride("panel", bg);

        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", (int)(6 * scale));
        AddChild(outer);

        var topRow = new HBoxContainer();
        topRow.AddThemeConstantOverride("separation", (int)(6 * scale));
        outer.AddChild(topRow);

        _toggleButton = new StyledButton(enabled ? "ON" : "OFF", scale, fontSize: 12, height: 36);
        _toggleButton.ToggleMode = true;
        _toggleButton.ButtonPressed = enabled;
        _toggleButton.CustomMinimumSize = new Vector2((int)(52 * scale), (int)(36 * scale));
        ApplyToggleStyle(_toggleButton, enabled, scale);
        _toggleButton.Toggled += pressed =>
        {
            _toggleButton.Text = pressed ? "ON" : "OFF";
            ApplyToggleStyle(_toggleButton, pressed, scale);
            Toggled?.Invoke(pressed);
        };
        topRow.AddChild(_toggleButton);

        var titleLabel = new StyledLabel(BuildTitle(info), scale, fontSize: 14);
        titleLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        titleLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        topRow.AddChild(titleLabel);

        _moveUpButton = new StyledButton("▲", scale, fontSize: 12, height: 36);
        _moveUpButton.CustomMinimumSize = new Vector2((int)(36 * scale), (int)(36 * scale));
        _moveUpButton.Disabled = !canMoveUp;
        _moveUpButton.Pressed += () => MoveUpPressed?.Invoke();
        topRow.AddChild(_moveUpButton);

        _moveDownButton = new StyledButton("▼", scale, fontSize: 12, height: 36);
        _moveDownButton.CustomMinimumSize = new Vector2((int)(36 * scale), (int)(36 * scale));
        _moveDownButton.Disabled = !canMoveDown;
        _moveDownButton.Pressed += () => MoveDownPressed?.Invoke();
        topRow.AddChild(_moveDownButton);

        _infoButton = new StyledButton("ⓘ", scale, fontSize: 14, height: 36);
        _infoButton.CustomMinimumSize = new Vector2((int)(36 * scale), (int)(36 * scale));
        _infoButton.ToggleMode = true;
        _infoButton.Toggled += pressed => _detail.Visible = pressed;
        topRow.AddChild(_infoButton);

        _detail = new VBoxContainer();
        _detail.Visible = false;
        _detail.AddThemeConstantOverride("separation", (int)(4 * scale));
        outer.AddChild(_detail);

        if (!string.IsNullOrWhiteSpace(info.Manifest.Description))
        {
            var descLabel = new StyledLabel(info.Manifest.Description, scale, fontSize: 12);
            descLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            descLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.85f));
            _detail.AddChild(descLabel);
        }

        if (!string.IsNullOrWhiteSpace(info.ReadmeSnippet))
        {
            var readmeLabel = new StyledLabel("README: " + info.ReadmeSnippet, scale, fontSize: 11);
            readmeLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            readmeLabel.AddThemeColorOverride("font_color", new Color(0.65f, 0.65f, 0.7f));
            _detail.AddChild(readmeLabel);
        }

        var pathLabel = new StyledLabel("Path: " + info.Path, scale, fontSize: 10);
        pathLabel.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
        pathLabel.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.55f));
        _detail.AddChild(pathLabel);

        var removeButton = new StyledButton("Remove Mod", scale, fontSize: 12, height: 36);
        var r = (int)(4 * scale);
        var bw = Math.Max(1, (int)(2 * scale));
        var dangerStyle = StyledButton.MakeOutline(new Color(0.85f, 0.3f, 0.3f), r, bw);
        removeButton.AddThemeStyleboxOverride("normal", dangerStyle);
        removeButton.AddThemeStyleboxOverride("hover", dangerStyle);
        removeButton.AddThemeStyleboxOverride("pressed", dangerStyle);
        removeButton.Pressed += () => RemovePressed?.Invoke();
        _detail.AddChild(removeButton);
    }

    private static string BuildTitle(ModEntryInfo info)
    {
        var name = info.Manifest.DisplayName;
        var version = string.IsNullOrWhiteSpace(info.Manifest.Version)
            ? ""
            : " v" + info.Manifest.Version;
        var author = string.IsNullOrWhiteSpace(info.Manifest.Author)
            ? ""
            : " — " + info.Manifest.Author;
        return name + version + author;
    }

    private static void ApplyToggleStyle(Button button, bool on, float scale)
    {
        var r = (int)(4 * scale);
        var bw = Math.Max(1, (int)(2 * scale));
        var style = on
            ? StyledButton.MakeOutline(new Color(0.25f, 0.65f, 0.3f), r, bw)
            : StyledButton.MakeOutline(new Color(0.7f, 0.25f, 0.25f), r, bw);
        button.AddThemeStyleboxOverride("normal", style);
        button.AddThemeStyleboxOverride("hover", style);
        button.AddThemeStyleboxOverride("pressed", style);
        button.AddThemeStyleboxOverride("disabled", style);
    }
}
