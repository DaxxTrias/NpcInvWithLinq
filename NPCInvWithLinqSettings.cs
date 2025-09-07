using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json.Serialization;
using ImGuiNET;
using System.Numerics;
using System.Windows.Forms;

namespace NPCInvWithLinq;

public class NPCInvWithLinqSettings : ISettings
{
    private bool _reloadRequired = false;

    public NPCInvWithLinqSettings()
    {
        RuleConfig = new RuleRenderer(this);
    }

    public ToggleNode Enable { get; set; } = new ToggleNode(false);
    [Menu("Draw On Tab Labels", "Draw outline on vendor tabs if filter match is detected")]
    public ToggleNode DrawOnTabLabels { get; set; } = new ToggleNode(true);
    [Menu("Auto Purchase", "Automatically ctrl+click matched items to purchase them")]
    public ToggleNode AutoPurchase { get; set; } = new ToggleNode(false);
    [Menu("Purchase Delay (ms)", "Delay in milliseconds between auto-purchases")]
    public RangeNode<int> PurchaseDelay { get; set; } = new RangeNode<int>(100, 50, 1000);
    [Menu("Max Purchases Per Tab", "Maximum number of items to auto-purchase from each tab (0 = unlimited)")]
    public RangeNode<int> MaxPurchasesPerTab { get; set; } = new RangeNode<int>(5, 0, 50);
    [Menu("Auto Purchase Toggle Key", "Hotkey to toggle auto-purchase on/off")]
    public HotkeyNode AutoPurchaseToggleKey { get; set; } = new HotkeyNode(Keys.F5);
    public ColorNode DefaultFrameColor { get; set; } = new ColorNode(Color.Red);
    [Menu("Auto Purchase Color", "Color for the next item to be auto-purchased")]
    public ColorNode AutoPurchaseColor { get; set; } = new ColorNode(Color.Lime);
    public RangeNode<int> FrameThickness { get; set; } = new RangeNode<int>(1, 1, 20);

    [JsonIgnore]
    public TextNode FilterTest { get; set; } = new TextNode();

    [JsonIgnore]
    public ButtonNode ReloadFilters { get; set; } = new ButtonNode();

    [Menu("Use a Custom \"\\config\\custom_folder\" folder ")]
    public TextNode CustomConfigDir { get; set; } = new TextNode();

    [IgnoreMenu]
    public List<NPCInvRule> NPCInvRules { get; set; } = new List<NPCInvRule>();

    [JsonIgnore]
    public RuleRenderer RuleConfig { get; set; }

    [Submenu(RenderMethod = nameof(Render))]
    public class RuleRenderer
    {
        private readonly NPCInvWithLinqSettings _parent;

        public RuleRenderer(NPCInvWithLinqSettings parent)
        {
            _parent = parent;
        }

        public void Render(NPCInvWithLinq plugin)
        {
            if (ImGui.Button("Open rule folder"))
            {
                var configDir = plugin.ConfigDirectory;
                var customConfigFileDirectory = !string.IsNullOrEmpty(_parent.CustomConfigDir)
                    ? Path.Combine(Path.GetDirectoryName(plugin.ConfigDirectory), _parent.CustomConfigDir)
                    : null;

                var directoryToOpen = Directory.Exists(customConfigFileDirectory)
                    ? customConfigFileDirectory
                    : configDir;

                Process.Start("explorer.exe", directoryToOpen);
            }

            ImGui.Separator();
            ImGui.BulletText("Select Rules To Load");
            ImGui.BulletText("Rules are evaluated top-to-bottom; higher entries have higher precedence (first match wins).");
            ImGui.BulletText("Place specific rules higher and general/catch-all rules lower for correctness and speed.");

            var tempNpcInvRules = new List<NPCInvRule>(_parent.NPCInvRules); // Create a copy

            for (int i = 0; i < tempNpcInvRules.Count; i++)
            {
                if (ImGui.ArrowButton($"##upButton{i}", ImGuiDir.Up) && i > 0)
                {
                    (tempNpcInvRules[i - 1], tempNpcInvRules[i]) = (tempNpcInvRules[i], tempNpcInvRules[i - 1]);
                    _parent._reloadRequired = true;
                }

                ImGui.SameLine();
                ImGui.Text(" ");
                ImGui.SameLine();

                if (ImGui.ArrowButton($"##downButton{i}", ImGuiDir.Down) && i < tempNpcInvRules.Count - 1)
                {
                    (tempNpcInvRules[i + 1], tempNpcInvRules[i]) = (tempNpcInvRules[i], tempNpcInvRules[i + 1]);
                    _parent._reloadRequired = true;
                }

                ImGui.SameLine();
                ImGui.Text(" - ");
                ImGui.SameLine();

                var refToggle = tempNpcInvRules[i].Enabled;
                if (ImGui.Checkbox($"{tempNpcInvRules[i].Name}##checkbox{i}", ref refToggle))
                {
                    tempNpcInvRules[i].Enabled = refToggle;
                    plugin.ReloadRules();
                }

                ImGui.SameLine();
                var color = new Vector4(
                    tempNpcInvRules[i].Color.Value.R / 255.0f,
                    tempNpcInvRules[i].Color.Value.G / 255.0f,
                    tempNpcInvRules[i].Color.Value.B / 255.0f,
                    tempNpcInvRules[i].Color.Value.A / 255.0f);

                var previewFlags = ImGuiColorEditFlags.AlphaPreview | ImGuiColorEditFlags.AlphaPreviewHalf | ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.DisplayHex;
                if (ImGui.ColorButton($"##colorBtn{i}", color, previewFlags, new Vector2(18f, 18f)))
                {
                    ImGui.OpenPopup($"##colorPopup{i}");
                }
                if (ImGui.BeginPopup($"##colorPopup{i}"))
                {
                    var pickerColor = color;
                    if (ImGui.ColorPicker4($"##picker{i}", ref pickerColor, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.DisplayRGB | ImGuiColorEditFlags.DisplayHex | ImGuiColorEditFlags.InputRGB | ImGuiColorEditFlags.AlphaPreview))
                    {
                        tempNpcInvRules[i].Color.Value = Color.FromArgb((int)(pickerColor.W * 255), (int)(pickerColor.X * 255), (int)(pickerColor.Y * 255), (int)(pickerColor.Z * 255));
                    }
                    ImGui.EndPopup();
                }

                // Inline HEX input + Copy/Paste helpers for easy transfer between windows
                ImGui.SameLine();
                var ruleKey = $"{tempNpcInvRules[i].Name}|{tempNpcInvRules[i].Location}";
                if (!_hexByRuleKey.TryGetValue(ruleKey, out var hexStr))
                {
                    hexStr = ToHex(tempNpcInvRules[i].Color.Value);
                    _hexByRuleKey[ruleKey] = hexStr;
                }

                ImGui.SetNextItemWidth(110f);
                var editedHex = hexStr;
                if (ImGui.InputText($"##hex{i}", ref editedHex, 10u, ImGuiInputTextFlags.CharsHexadecimal | ImGuiInputTextFlags.AutoSelectAll))
                {
                    _hexByRuleKey[ruleKey] = editedHex;
                    if (TryParseHexColor(editedHex, out var parsed))
                    {
                        tempNpcInvRules[i].Color.Value = parsed;
                    }
                }
            }

            _parent.NPCInvRules = tempNpcInvRules;

            if (_parent._reloadRequired)
            {
                plugin.ReloadRules();
                _parent._reloadRequired = false;
            }
        }

        private static readonly System.Collections.Generic.Dictionary<string, string> _hexByRuleKey = new System.Collections.Generic.Dictionary<string, string>();

        private static string ToHex(Color color)
        {
            // RGBA hex with alpha last for readability/copying
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}{color.A:X2}";
        }

        private static bool TryParseHexColor(string text, out Color result)
        {
            result = Color.Empty;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var s = text.Trim();
            if (s.StartsWith("#")) s = s.Substring(1);
            if (s.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);

            if (s.Length != 6 && s.Length != 8) return false;

            byte r = 0, g = 0, b = 0;
            var style = System.Globalization.NumberStyles.HexNumber;
            bool ok = byte.TryParse(s.Substring(0, 2), style, null, out r)
                   && byte.TryParse(s.Substring(2, 2), style, null, out g)
                   && byte.TryParse(s.Substring(4, 2), style, null, out b);

            if (!ok) return false;

            byte a = 255;
            if (s.Length == 8)
            {
                if (!byte.TryParse(s.Substring(6, 2), style, null, out a))
                    return false;
            }

            result = Color.FromArgb(a, r, g, b);
            return true;
        }
    }
}

public class NPCInvRule
{
    public string Name { get; set; } = "";
    public string Location { get; set; } = "";
    public bool Enabled { get; set; } = false;
    public ColorNode Color { get; set; } = new ColorNode(System.Drawing.Color.Red);

    public NPCInvRule(string name, string location, bool enabled)
    {
        Name = name;
        Location = location;
        Enabled = enabled;
    }
}