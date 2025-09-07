using ExileCore2;
using ExileCore2.PoEMemory;
using ExileCore2.PoEMemory.Elements;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Cache;
using ExileCore2.Shared.Helpers;
using ItemFilterLibrary;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using ExileCore2.Shared.Nodes;
using static NPCInvWithLinq.ServerAndStashWindow;
using RectangleF = ExileCore2.Shared.RectangleF;
using System.Diagnostics;
using System.Windows.Forms;

namespace NPCInvWithLinq;

public class ServerAndStashWindow
{
    public IList<WindowSet> Tabs { get; set; }

    public class WindowSet
    {
        public int Index { get; set; }
        public string Title { get; set; }
        public bool IsVisible { get; set; }
        public Element TabNameElement { get; set; }
        public List<CustomItemData> ServerItems { get; set; }
        public List<CustomItemData> TradeWindowItems { get; set; }
        public ServerInventory Inventory { get; set; }

        public override string ToString()
        {
            return $"Tab({Title}) is Index({Index}) IsVisible({IsVisible}) [ServerItems({ServerItems.Count}), TradeWindowItems({TradeWindowItems.Count})]";
        }
    }
}

public class NPCInvWithLinq : BaseSettingsPlugin<NPCInvWithLinqSettings>
{
    private readonly CachedValue<List<WindowSet>> _storedStashAndWindows;
    private readonly CachedValue<List<CustomItemData>> _rewardItems;
    private readonly CachedValue<List<CustomItemData>> _ritualItems;
    private List<RuleBinding> _ruleBindings;
    private PurchaseWindow _purchaseWindowHideout;
    private PurchaseWindow _purchaseWindow;
    private readonly Stopwatch _sinceLastPurchase = Stopwatch.StartNew();
    private readonly Stopwatch _sinceLastToggle = Stopwatch.StartNew();
    private bool _toggleKeyHeld;
    private readonly Dictionary<string, int> _purchasesPerTab = new();
    private string _lastTabTitle = "";

    private sealed class RuleBinding
    {
        public NPCInvRule Rule { get; }
        public ItemFilter<CustomItemData> Filter { get; }

        public RuleBinding(NPCInvRule rule, ItemFilter<CustomItemData> filter)
        {
            Rule = rule ?? throw new ArgumentNullException(nameof(rule));
            Filter = filter; // may be null when rule is disabled; callers must guard
        }
    }

    public NPCInvWithLinq()
    {
        Name = "NPC Inv With Linq";
        _storedStashAndWindows = new TimeCache<List<WindowSet>>(CacheUtils.RememberLastValue<List<WindowSet>>(UpdateCurrentTradeWindow), 50);
        _rewardItems = new TimeCache<List<CustomItemData>>(GetRewardItems, 1000);
        _ritualItems = new TimeCache<List<CustomItemData>>(GetRitualItems, 1000);
    }
    public override bool Initialise()
    {
        Settings.ReloadFilters.OnPressed = LoadRuleFiles;
        Settings.AutoPurchaseToggleKey.OnValueChanged += () => Input.RegisterKey(Settings.AutoPurchaseToggleKey.Value);
        Input.RegisterKey(Settings.AutoPurchaseToggleKey.Value);
        LoadRuleFiles();
        return true;
    }

    public override void Tick()
    {
        _purchaseWindowHideout = GameController.Game.IngameState.IngameUi.PurchaseWindowHideout;
        _purchaseWindow = GameController.Game.IngameState.IngameUi.PurchaseWindow;
        
        // Handle auto-purchase toggle with 1s debounce and edge detection
        var isDown = Input.GetKeyState(Settings.AutoPurchaseToggleKey.Value);
        if (isDown)
        {
            if (!_toggleKeyHeld && _sinceLastToggle.ElapsedMilliseconds >= 1000)
            {
                Settings.AutoPurchase.Value = !Settings.AutoPurchase.Value;
                var status = Settings.AutoPurchase.Value ? "enabled" : "disabled";
                LogMessage($"Auto-purchase {status}", 3);
                _sinceLastToggle.Restart();
            }
            _toggleKeyHeld = true;
        }
        else
        {
            _toggleKeyHeld = false;
        }
    }

    public override void Render()
    {
        var hoveredItem = GetHoveredItem();
        PerformItemFilterTest(hoveredItem);
        ProcessPurchaseWindow(hoveredItem);
        ProcessRewardsWindow(hoveredItem);
        ProcessRitualWindow(hoveredItem);
    }

    private void ProcessRewardsWindow(Element hoveredItem)
    {
        if (!GameController.IngameState.IngameUi.QuestRewardWindow.IsVisible) return;

        foreach (var reward in _rewardItems?.Value.Where(x => _ruleBindings?.Any(b => b.Rule.Enabled && b.Filter?.Matches(x) == true) == true) ?? Enumerable.Empty<CustomItemData>())
        {
            var frameColor = GetFilterColor(reward);
            if (hoveredItem != null && hoveredItem.Tooltip.GetClientRectCache.Intersects(reward.ClientRectangle) && hoveredItem.Entity.Address != reward.Entity.Address)
            {
                frameColor = frameColor.Value.ToImguiVec4(45).ToColor();
            }

            Graphics.DrawFrame(reward.ClientRectangle, frameColor, Settings.FrameThickness);
        }
    }
    private void ProcessRitualWindow(Element hoveredItem)
    {
        if (!GameController.IngameState.IngameUi.RitualWindow.IsVisible) return;

        foreach (var reward in _ritualItems?.Value.Where(x => _ruleBindings?.Any(b => b.Rule.Enabled && b.Filter?.Matches(x) == true) == true) ?? Enumerable.Empty<CustomItemData>())
        {
            var frameColor = GetFilterColor(reward);
            if (hoveredItem != null && hoveredItem.Tooltip.GetClientRectCache.Intersects(reward.ClientRectangle) && hoveredItem.Entity.Address != reward.Entity.Address)
            {
                frameColor = frameColor.Value.ToImguiVec4(45).ToColor();
            }

            Graphics.DrawFrame(reward.ClientRectangle, frameColor, Settings.FrameThickness);
        }
    }

    private List<CustomItemData> GetRewardItems() =>
        GameController.IngameState.IngameUi.QuestRewardWindow.GetPossibleRewards()
            .Where(item => item.Item2 is { Address: not 0, IsValid: true })
            .Select(item => new CustomItemData(item.Item1, GameController, EKind.QuestReward, item.Item2.GetClientRectCache))
            .ToList();

    private List<CustomItemData> GetRitualItems() =>
    GameController.IngameState.IngameUi.RitualWindow.InventoryElement.VisibleInventoryItems
        .Where(item => item.Item is { Address: not 0, IsValid: true })
        .Select(item => new CustomItemData(item.Item, GameController, EKind.RitualReward, item.GetClientRectCache))
        .ToList();
    private void ProcessPurchaseWindow(Element hoveredItem)
    {
        if (!IsPurchaseWindowVisible())
            return;

        List<string> unSeenItems = [];
        ProcessStoredTabs(unSeenItems, hoveredItem);

        PurchaseWindow purchaseWindowItems = GetVisiblePurchaseWindow();
        if (unSeenItems.Count == 0 || purchaseWindowItems?.TabContainer == null)
            return;
        var serverItemsBox = CalculateServerItemsBox(unSeenItems, purchaseWindowItems);

        DrawServerItems(serverItemsBox, unSeenItems, hoveredItem);
    }

    private void DrawServerItems(RectangleF serverItemsBox, List<string> unSeenItems, Element hoveredItem)
    {
        if (unSeenItems == null || unSeenItems.Count == 0) return;

        // Skip drawing if the box is degenerate; prevents odd tiny boxes appearing at origin
        if (serverItemsBox.Width <= 2 || serverItemsBox.Height <= 2)
            return;

        if (hoveredItem == null || !hoveredItem.Tooltip.GetClientRectCache.Intersects(serverItemsBox))
        {
            var boxColor = Color.FromArgb(150, 0, 0, 0);
            var textColor = Color.FromArgb(230, 255, 255, 255);

            Graphics.DrawBox(serverItemsBox, boxColor);

            for (int i = 0; i < unSeenItems.Count; i++)
            {
                string stringItem = unSeenItems[i];
                var textHeight = Graphics.MeasureText(stringItem);
                var textPadding = 10;

                Graphics.DrawText(stringItem, new Vector2(serverItemsBox.X + textPadding, serverItemsBox.Y + (textHeight.Y * i)), textColor);
            }
        }
    }

    private Element GetHoveredItem()
    {
        return GameController.IngameState.UIHover is { Address: not 0, Entity.IsValid: true } hover ? hover : null;
    }

    private bool IsPurchaseWindowVisible()
    {
        return (_purchaseWindowHideout?.IsVisible ?? false) || (_purchaseWindow?.IsVisible ?? false);
    }

    private void ProcessStoredTabs(List<string> unSeenItems, Element hoveredItem)
    {
        foreach (var storedTab in _storedStashAndWindows.Value)
        {
            if (storedTab.IsVisible)
                ProcessVisibleTabItems(storedTab.TradeWindowItems, hoveredItem, storedTab.Title);
            else
                ProcessHiddenTabItems(storedTab, unSeenItems, hoveredItem);
        }
    }

    private void ProcessVisibleTabItems(IEnumerable<CustomItemData> items, Element hoveredItem, string tabTitle)
    {
        // Track tab changes
        if (_lastTabTitle != tabTitle)
        {
            _lastTabTitle = tabTitle;
            // Reset purchase count for this tab if it's been re-opened
            if (_purchasesPerTab.ContainsKey(tabTitle))
                _purchasesPerTab[tabTitle] = 0;
        }
        
        var itemsList = items.Where(item => item != null && ItemInFilter(item)).ToList();
        
        // Determine which item will be auto-purchased next
        CustomItemData nextToPurchase = null;
        if (Settings.AutoPurchase)
        {
            if (!_purchasesPerTab.TryGetValue(tabTitle, out var purchaseCount))
                purchaseCount = 0;
                
            if (Settings.MaxPurchasesPerTab == 0 || purchaseCount < Settings.MaxPurchasesPerTab)
            {
                nextToPurchase = itemsList.FirstOrDefault();
            }
        }
        
        foreach (var visibleItem in itemsList)
        {
            DrawItemFrame(visibleItem, hoveredItem, visibleItem == nextToPurchase);
        }
        
        // Auto-purchase logic
        if (Settings.AutoPurchase && _sinceLastPurchase.ElapsedMilliseconds >= Settings.PurchaseDelay && nextToPurchase != null)
        {
            if (!_purchasesPerTab.TryGetValue(tabTitle, out var purchaseCount))
                purchaseCount = 0;
                
            AutoPurchaseItem(nextToPurchase);
            _purchasesPerTab[tabTitle] = purchaseCount + 1;
        }
    }

    private void ProcessHiddenTabItems(WindowSet storedTab, List<string> unSeenItems, Element hoveredItem)
    {
        var tabHadWantedItem = false;
        foreach (var hiddenItem in storedTab.ServerItems)
        {
            if (hiddenItem == null) continue;
            if (ItemInFilter(hiddenItem))
            {
                ProcessUnseenItems(unSeenItems, storedTab, hoveredItem);
                unSeenItems.Add($"\t{hiddenItem.Name}");
                tabHadWantedItem = true;
            }
        }

        if (tabHadWantedItem)
            unSeenItems.Add("");
    }

    private void ProcessUnseenItems(List<string> unSeenItems, WindowSet storedTab, Element hoveredItem)
    {
        if (unSeenItems == null) throw new ArgumentNullException(nameof(unSeenItems));
        if (storedTab == null) throw new ArgumentNullException(nameof(storedTab));

        if (!unSeenItems.Contains($"Tab [{storedTab.Title}]"))
        {
            unSeenItems.Add($"Tab [{storedTab.Title}]");
            if (Settings.DrawOnTabLabels)
            {
                var tabColor = GetFilterColorForTab(storedTab.ServerItems);
                DrawTabNameElementFrame(storedTab.TabNameElement, hoveredItem, tabColor);
            }
        }
    }

    private void DrawItemFrame(CustomItemData item, Element hoveredItem, bool isNextToPurchase = false)
    {
        var rect = item.ClientRectangle;
        if (rect.Width <= 1 || rect.Height <= 1)
            return;

        var frameColor = isNextToPurchase ? Settings.AutoPurchaseColor : GetFilterColor(item);
        if (hoveredItem != null)
        {
            bool intersectsHovered;
            try
            {
                intersectsHovered = hoveredItem.Tooltip.GetClientRectCache.Intersects(rect) && hoveredItem.Entity.Address != item.Entity.Address;
            }
            catch
            {
                intersectsHovered = false;
            }

            if (intersectsHovered)
            {
                frameColor = frameColor.Value.ToImguiVec4(45).ToColor();
            }
        }

        Graphics.DrawFrame(rect, frameColor, Settings.FrameThickness);
    }

    private void DrawTabNameElementFrame(Element tabNameElement, Element hoveredItem, ColorNode? frameColorOverride = null)
    {
        // Validate element and its rectangle before drawing to prevent label-jumping and invalid draws
        if (tabNameElement == null)
            return;

        RectangleF rect;
        try
        {
            rect = tabNameElement.GetClientRectCache;
        }
        catch
        {
            return;
        }

        if (rect.Width <= 1 || rect.Height <= 1)
            return;

        var frameColor = frameColorOverride ?? Settings.DefaultFrameColor;

        bool intersectsHovered = false;
        if (hoveredItem != null)
        {
            try
            {
                intersectsHovered = hoveredItem.Tooltip.GetClientRectCache.Intersects(rect);
            }
            catch
            {
                intersectsHovered = false;
            }
        }

        if (!intersectsHovered)
        {
            Graphics.DrawFrame(rect, frameColor, Settings.FrameThickness);
        }
        else
        {
            Graphics.DrawFrame(rect, frameColor.Value.ToImguiVec4(45).ToColor(), Settings.FrameThickness);
        }
    }

    private RectangleF CalculateServerItemsBox(List<string> unSeenItems, PurchaseWindow purchaseWindowItems)
    {
        var startingPoint = purchaseWindowItems.TabContainer.GetClientRectCache.TopRight;
        startingPoint.X += 15;

        var longestText = unSeenItems.MaxBy(s => s.Length);
        var textHeight = Graphics.MeasureText(longestText);
        var textPadding = 10;

        return new RectangleF
        {
            Height = textHeight.Y * unSeenItems.Count,
            Width = textHeight.X + (textPadding * 2),
            X = startingPoint.X,
            Y = startingPoint.Y
        };
    }

    private PurchaseWindow GetVisiblePurchaseWindow()
    {
        return (_purchaseWindowHideout?.IsVisible ?? false) ? _purchaseWindowHideout : ((_purchaseWindow?.IsVisible ?? false) ? _purchaseWindow : null);
    }

    private void PerformItemFilterTest(Element hoveredItem)
    {
        if (Settings.FilterTest.Value is { Length: > 0 } && hoveredItem != null)
        {
            var filter = ItemFilter.LoadFromString(Settings.FilterTest);
            var matched = filter.Matches(new ItemData(hoveredItem.Entity, GameController));
            DebugWindow.LogMsg($"Debug item match on hover: {matched}");
        }
    }

    private void LoadRuleFiles()
    {
        var pickitConfigFileDirectory = ConfigDirectory;
        var existingRules = Settings.NPCInvRules;

        if (!string.IsNullOrEmpty(Settings.CustomConfigDir))
        {
            var customConfigFileDirectory = Path.Combine(Path.GetDirectoryName(ConfigDirectory), Settings.CustomConfigDir);

            if (Directory.Exists(customConfigFileDirectory))
            {
                pickitConfigFileDirectory = customConfigFileDirectory;
            }
            else
            {
                DebugWindow.LogError("[NPC Inventory] custom config folder does not exist.", 15);
            }
        }

        try
        {
            // Discover all .ifl files on disk
            var discovered = new DirectoryInfo(pickitConfigFileDirectory).GetFiles("*.ifl")
                .Select(x => new NPCInvRule(x.Name, Path.GetRelativePath(pickitConfigFileDirectory, x.FullName), false))
                .ToDictionary(r => r.Location, r => r);

            // Preserve current user-defined order for existing rules
            var newRules = new List<NPCInvRule>();
            foreach (var rule in existingRules)
            {
                var fullPath = Path.Combine(pickitConfigFileDirectory, rule.Location);
                if (File.Exists(fullPath))
                {
                    newRules.Add(rule);
                }
                else
                {
                    LogError($"File '{rule.Name}' not found.");
                }
            }

            // Append any newly discovered files that weren't already present
            foreach (var kv in discovered)
            {
                if (!newRules.Any(r => string.Equals(r.Location, kv.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    newRules.Add(kv.Value);
                }
            }

            // Build bindings in the same order as rules; disabled rules have null filters
            _ruleBindings = newRules
                .Select(rule => new RuleBinding(
                    rule,
                    rule.Enabled ? ItemFilter.LoadFromPath<CustomItemData>(Path.Combine(pickitConfigFileDirectory, rule.Location)) : null))
                .ToList();

            Settings.NPCInvRules = newRules;
        }
        catch (Exception e)
        {
            LogError($"An error occurred while loading rule files: {e.Message}");
        }
    }

    internal void ReloadRules()
    {
        LoadRuleFiles();
    }

    private List<WindowSet> UpdateCurrentTradeWindow(List<WindowSet> previousValue)
    {
        var previousDict = previousValue?.ToDictionary(x => (x.Title, x.Inventory?.Address ?? 0, x.Inventory?.ServerRequestCounter ?? -1, x.IsVisible, x.TradeWindowItems?.Count ?? 0));
        var purchaseWindowItems = GetVisiblePurchaseWindow();

        if (purchaseWindowItems == null || purchaseWindowItems.TabContainer?.Inventories == null)
            return [];

        return purchaseWindowItems.TabContainer.Inventories.Select((inventory, i) =>
        {
            try
            {
                if (inventory == null) return null;

                var uiInventory = TryGetRef(() => inventory.Inventory);
                if (uiInventory == null) return null;

                var serverInventory = TryGetRef(() => uiInventory.ServerInventory);
                if (serverInventory == null)
                {
                    // Server inventory may briefly be missing while the UI updates; skip this tab safely.
                    return null;
                }

                var isVisible = TryGetValue(() => uiInventory.IsVisible);
                var visibleValidUiItems = TryGetRef(() => uiInventory.VisibleInventoryItems)?.Where(x => x?.Item?.Path != null).ToList() ?? [];
                var title = $"-{i + 1}-";
                if (previousDict?.TryGetValue((title, serverInventory?.Address ?? 0, serverInventory?.ServerRequestCounter ?? -1, isVisible, visibleValidUiItems.Count),
                        out var previousSet) == true)
                {
                    // Refresh the tab header Element each frame to avoid stale rectangles (label-jumping)
                    var refreshedTabButton = TryGetRef(() => inventory.TabButton);
                    if (refreshedTabButton != null)
                    {
                        previousSet.TabNameElement = refreshedTabButton;
                    }

                    // Keep visibility in sync in case it changed but the cache key still matched
                    previousSet.IsVisible = isVisible;

                    // IMPORTANT: Rebuild visible item rectangles each frame to avoid stale draws when
                    // items change without a server request/count change.
                    if (visibleValidUiItems.Count > 0)
                    {
                        previousSet.TradeWindowItems = visibleValidUiItems
                            .Select(x => new CustomItemData(x.Item, GameController, EKind.Shop, x.GetClientRectCache))
                            .ToList();
                    }
                    else
                    {
                        previousSet.TradeWindowItems = [];
                    }

                    // Keep server items fresh as well; cheap and avoids stale data when counts match
                    // but contents differ.
                    previousSet.ServerItems = serverInventory.Items
                        .Where(x => x?.Path != null)
                        .Select(x => new CustomItemData(x, GameController, EKind.Shop))
                        .ToList();

                    return previousSet;
                }

                var tabButton = TryGetRef(() => inventory.TabButton);
                var newTab = new WindowSet
                {
                    Inventory = serverInventory,
                    Index = i,
                    ServerItems = serverInventory.Items.Where(x => x?.Path != null).Select(x => new CustomItemData(x, GameController, EKind.Shop)).ToList(),
                    TradeWindowItems = visibleValidUiItems
                        .Select(x => new CustomItemData(x.Item, GameController, EKind.Shop, x.GetClientRectCache))
                        .ToList(),
                    Title = title,
                    IsVisible = isVisible,
                    TabNameElement = tabButton
                };

                return newTab;
            }
            catch
            {
                // Any transient failure reading volatile UI/memory should not crash the render loop; skip this tab.
                return null;
            }
        }).Where(x => x != null).ToList();
    }

    private bool ItemInFilter(CustomItemData item)
    {
        return _ruleBindings?.Any(b => b.Rule.Enabled && b.Filter?.Matches(item) == true) ?? false;
    }

    private ColorNode GetFilterColor(CustomItemData item)
    {
        if (_ruleBindings == null || _ruleBindings.Count == 0)
            return Settings.DefaultFrameColor;

        // Top-to-bottom precedence: the first enabled rule that matches decides the color
        foreach (var binding in _ruleBindings)
        {
            if (binding.Rule.Enabled && binding.Filter?.Matches(item) == true)
            {
                return binding.Rule.Color;
            }
        }
        return Settings.DefaultFrameColor;
    }

    private ColorNode GetFilterColorForTab(IEnumerable<CustomItemData> items)
    {
        if (_ruleBindings == null || _ruleBindings.Count == 0)
            return Settings.DefaultFrameColor;

        var itemsList = items?.Where(i => i != null).ToList();
        if (itemsList == null || itemsList.Count == 0)
            return Settings.DefaultFrameColor;

        // Follow rule precedence: first enabled rule that matches any item determines tab color
        foreach (var binding in _ruleBindings)
        {
            if (!binding.Rule.Enabled || binding.Filter == null)
                continue;

            foreach (var item in itemsList)
            {
                if (binding.Filter.Matches(item))
                    return binding.Rule.Color;
            }
        }

        return Settings.DefaultFrameColor;
    }

    private void AutoPurchaseItem(CustomItemData item)
    {
        if (item?.ClientRectangle == null || item.ClientRectangle.Width <= 1 || item.ClientRectangle.Height <= 1)
            return;

        try
        {
            // Get the center of the item rectangle
            var rect = item.ClientRectangle;
            var center = rect.Center;

            // Make sure the click position is within the game window
            var gameWindowRect = GameController.Window.GetWindowRectangleTimeCache with { Location = Vector2.Zero };
            gameWindowRect.Inflate(-36, -36);

            if (!gameWindowRect.Contains(center.X, center.Y))
                return;

            // Convert to screen coordinates
            var screenPos = center + GameController.Window.GetWindowRectangleTimeCache.TopLeft;

            // Move cursor to item and allow hover to update
            Input.SetCursorPos(screenPos);
            System.Threading.Thread.Sleep(35);

            // Press and hold Ctrl for the entire click sequence
            Input.KeyDown(Keys.LControlKey);
            System.Threading.Thread.Sleep(25);

            // Perform the click while Ctrl is held
            Input.Click(MouseButtons.Left);

            // Small tail to ensure modified click is processed before releasing
            System.Threading.Thread.Sleep(15);

            // Reset the purchase timer
            _sinceLastPurchase.Restart();

            LogMessage($"Auto-purchased: {item.Name}", 2);
        }
        catch (Exception ex)
        {
            LogError($"Error during auto-purchase: {ex.Message}");
        }
        finally
        {
            // Always release modifiers to prevent 'stuck key' behavior even on errors
            Input.KeyUp(Keys.LControlKey);
            Input.KeyUp(Keys.RControlKey);
        }
    }

    private static T? TryGetRef<T>(Func<T> getter) where T : class
    {
        try { return getter(); } catch { return null; }
    }

    private static T TryGetValue<T>(Func<T> getter) where T : struct
    {
        try { return getter(); } catch { return default; }
    }
}
