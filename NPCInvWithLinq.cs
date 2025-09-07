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
using System.Text.RegularExpressions;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.Elements.InventoryElements; // Haggle window inventory parsing

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
        public int? MinOpenPrefixes { get; }
        public int? MinOpenSuffixes { get; }

        public RuleBinding(NPCInvRule rule, ItemFilter<CustomItemData> filter, int? minOpenPrefixes, int? minOpenSuffixes)
        {
            Rule = rule ?? throw new ArgumentNullException(nameof(rule));
            Filter = filter; // may be null when rule is disabled; callers must guard
            MinOpenPrefixes = minOpenPrefixes;
            MinOpenSuffixes = minOpenSuffixes;
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
        ProcessHaggleWindow(hoveredItem); // Expedition vendor (Tujen) support
    }

    private void ProcessRewardsWindow(Element hoveredItem)
    {
        if (!GameController.IngameState.IngameUi.QuestRewardWindow.IsVisible) return;

        foreach (var reward in _rewardItems?.Value.Where(x => ItemInFilter(x)) ?? Enumerable.Empty<CustomItemData>())
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

        foreach (var reward in _ritualItems?.Value.Where(x => ItemInFilter(x)) ?? Enumerable.Empty<CustomItemData>())
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
            var expr = Settings.FilterTest;
            TryExtractOpenCounts(expr, out var cleaned, out var minPref, out var minSuff);
            var filter = ItemFilter.LoadFromString<CustomItemData>(cleaned);
            var item = new CustomItemData(hoveredItem.Entity, GameController, EKind.Shop);
            var openOk = (minPref is null || OpenPrefixCount(item) >= minPref)
                         && (minSuff is null || OpenSuffixCount(item) >= minSuff);
            var matched = openOk && filter.Matches(item);
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
            _ruleBindings = new List<RuleBinding>(newRules.Count);
            foreach (var rule in newRules)
            {
                ItemFilter<CustomItemData> filter = null;
                int? minP = null, minS = null;
                if (rule.Enabled)
                {
                    var fullPath = Path.Combine(pickitConfigFileDirectory, rule.Location);
                    var text = File.ReadAllText(fullPath);
                    TryExtractOpenCounts(text, out var cleaned, out minP, out minS);
                    filter = ItemFilter.LoadFromString<CustomItemData>(cleaned);
                }
                _ruleBindings.Add(new RuleBinding(rule, filter, minP, minS));
            }

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
        {
            return [];
        }

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
        if (_ruleBindings == null) return false;
        foreach (var b in _ruleBindings)
        {
            if (!b.Rule.Enabled || b.Filter == null)
                continue;
            if (!ExtraOpenAffixConstraintsPass(item, b))
                continue;
            if (b.Filter.Matches(item))
                return true;
        }
        return false;
    }

    private ColorNode GetFilterColor(CustomItemData item)
    {
        if (_ruleBindings == null || _ruleBindings.Count == 0)
            return Settings.DefaultFrameColor;

        // Top-to-bottom precedence: the first enabled rule that matches decides the color
        foreach (var binding in _ruleBindings)
        {
            if (!binding.Rule.Enabled || binding.Filter == null)
                continue;
            if (!ExtraOpenAffixConstraintsPass(item, binding))
                continue;
            if (binding.Filter.Matches(item))
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
                if (!ExtraOpenAffixConstraintsPass(item, binding))
                    continue;
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

    // ===== Expedition Haggle (vendor) support =====
    private void ProcessHaggleWindow(Element hoveredItem)
    {
        var haggle = GameController?.Game?.IngameState?.IngameUi?.HaggleWindow;
        if (haggle == null || !haggle.IsVisible) return;

        var (visibleItems, label) = GetHaggleVisibleItemsWithLabel(haggle);
        if (visibleItems.Count == 0) return;

        // Select next-to-purchase similar to purchase window (single pseudo-tab)
        const string tabKey = "__haggle__";
        var filtered = visibleItems.Where(ItemInFilter).ToList();
        CustomItemData nextToPurchase = null;
        if (Settings.AutoPurchase)
        {
            if (!_purchasesPerTab.TryGetValue(tabKey, out var purchaseCount))
                purchaseCount = 0;
            if (Settings.MaxPurchasesPerTab == 0 || purchaseCount < Settings.MaxPurchasesPerTab)
                nextToPurchase = filtered.FirstOrDefault();
        }

        foreach (var item in filtered)
        {
            DrawItemFrame(item, hoveredItem, item == nextToPurchase);
        }

        if (Settings.AutoPurchase && _sinceLastPurchase.ElapsedMilliseconds >= Settings.PurchaseDelay && nextToPurchase != null)
        {
            if (!_purchasesPerTab.TryGetValue(tabKey, out var purchaseCount)) purchaseCount = 0;
            AutoPurchaseItem(nextToPurchase);
            _purchasesPerTab[tabKey] = purchaseCount + 1;
        }
    }

    private (List<CustomItemData> Items, Element Label) GetHaggleVisibleItemsWithLabel(Element haggleRoot)
    {
        var items = new List<CustomItemData>();
        Element label = null;
        try { label = haggleRoot.GetChildFromIndices(6, 2, 0); } catch { }

        var grid = TryFindHaggleInventoryGrid(haggleRoot);
        if (grid == null) return (items, label);

        List<NormalInventoryItem> uiItems;
        try { uiItems = grid.GetChildrenAs<NormalInventoryItem>()?.Skip(1).ToList() ?? new List<NormalInventoryItem>(); }
        catch { uiItems = new List<NormalInventoryItem>(); }

        foreach (var x in uiItems)
        {
            try
            {
                if (x?.Item is { Address: not 0, IsValid: true })
                {
                    items.Add(new CustomItemData(x.Item, GameController, EKind.Shop, x.GetClientRectCache));
                }
            }
            catch { }
        }

        return (items, label);
    }

    private static Element TryFindHaggleInventoryGrid(Element root)
    {
        // Fast path: known child indices used in other plugins
        try
        {
            var direct = root.GetChildFromIndices(8, 1, 0, 0);
            if (direct != null && direct.Address != 0) return direct;
        }
        catch { }

        // Fallback: BFS to find the first element that yields NormalInventoryItem children
        var q = new Queue<Element>();
        q.Enqueue(root);
        while (q.Count > 0)
        {
            var e = q.Dequeue();
            try
            {
                var uiItems = e.GetChildrenAs<NormalInventoryItem>();
                if (uiItems != null && uiItems.Count > 0) return e;
            }
            catch { }

            try
            {
                var children = e.Children;
                if (children != null)
                {
                    foreach (var c in children)
                        if (c != null) q.Enqueue(c);
                }
            }
            catch { }
        }
        return null;
    }

    // ===== Open affix support =====
    private static bool ExtraOpenAffixConstraintsPass(CustomItemData item, RuleBinding rule)
    {
        if (rule.MinOpenPrefixes is null && rule.MinOpenSuffixes is null)
            return true;
        if (rule.MinOpenPrefixes is int pReq && OpenPrefixCount(item) < pReq) return false;
        if (rule.MinOpenSuffixes is int sReq && OpenSuffixCount(item) < sReq) return false;
        return true;
    }

    private static readonly Regex OpenPrefixRegex = new Regex(@"OpenPrefixCount\s*\(\)\s*(==|>=|<=|>|<)\s*(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OpenSuffixRegex = new Regex(@"OpenSuffixCount\s*\(\)\s*(==|>=|<=|>|<)\s*(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static void TryExtractOpenCounts(string expr, out string cleanedExpr, out int? minPrefixes, out int? minSuffixes)
    {
        int? localMinPrefixes = null;
        int? localMinSuffixes = null;
        var cleaned = expr ?? string.Empty;

        cleaned = OpenPrefixRegex.Replace(cleaned, m =>
        {
            var op = m.Groups[1].Value;
            var num = int.Parse(m.Groups[2].Value);
            localMinPrefixes = MergeConstraint(localMinPrefixes, op, num);
            return "true";
        });
        cleaned = OpenSuffixRegex.Replace(cleaned, m =>
        {
            var op = m.Groups[1].Value;
            var num = int.Parse(m.Groups[2].Value);
            localMinSuffixes = MergeConstraint(localMinSuffixes, op, num);
            return "true";
        });

        cleanedExpr = cleaned;
        minPrefixes = localMinPrefixes;
        minSuffixes = localMinSuffixes;
    }

    private static int? MergeConstraint(int? existing, string op, int value)
    {
        int threshold = op switch
        {
            ">" => value + 1,
            ">=" => value,
            "==" => value,
            "<" => int.MinValue,
            "<=" => int.MinValue,
            _ => value,
        };
        if (op == "==") return value;
        if (existing is null) return threshold;
        return Math.Max(existing.Value, threshold);
    }

    // Minimal port of open affix counting from ItemfilterExtension
    private static int OpenPrefixCount(ItemData item)
    {
        var max = GetMaxPrefixes(item);
        var used = GetPrefixCount(item);
        var open = max - used;
        return open > 0 ? open : 0;
    }

    private static int OpenSuffixCount(ItemData item)
    {
        var max = GetMaxSuffixes(item);
        var used = GetSuffixCount(item);
        var open = max - used;
        return open > 0 ? open : 0;
    }

    private static Mods TryGetMods(ItemData item)
    {
        try { return item?.Entity?.GetComponent<Mods>(); } catch { return null; }
    }

    private static int GetPrefixCount(ItemData item)
    {
        var mods = TryGetMods(item);
        if (mods == null) return 0;
        if (TryGetIntProperty(mods, out var v, "PrefixesCount", "PrefixCount", "NumPrefixes")) return v;
        return CountAffixesByKind(mods, wantPrefix: true);
    }

    private static int GetSuffixCount(ItemData item)
    {
        var mods = TryGetMods(item);
        if (mods == null) return 0;
        if (TryGetIntProperty(mods, out var v, "SuffixesCount", "SuffixCount", "NumSuffixes")) return v;
        return CountAffixesByKind(mods, wantPrefix: false);
    }

    private static int GetMaxPrefixes(ItemData item)
    {
        var mods = TryGetMods(item);
        if (mods == null) return 0;
        if (TryGetIntProperty(mods, out var v, "PrefixesMax", "MaxPrefixes", "TotalAllowedPrefixes", "MaximumPrefixes")) return v;
        return 3;
    }

    private static int GetMaxSuffixes(ItemData item)
    {
        var mods = TryGetMods(item);
        if (mods == null) return 0;
        if (TryGetIntProperty(mods, out var v, "SuffixesMax", "MaxSuffixes", "TotalAllowedSuffixes", "MaximumSuffixes")) return v;
        return 3;
    }

    private static int CountAffixesByKind(Mods mods, bool wantPrefix)
    {
        try
        {
            var explicitMods = GetPropertyValue(mods, "ExplicitMods") as System.Collections.IEnumerable;
            if (explicitMods == null) return 0;
            int count = 0;
            foreach (var m in explicitMods)
            {
                if (m == null) continue;
                if (TryGetBoolProperty(m, out var isPrefix, "IsPrefix") && wantPrefix && isPrefix) { count++; continue; }
                if (TryGetBoolProperty(m, out var isSuffix, "IsSuffix") && !wantPrefix && isSuffix) { count++; continue; }

                var modRecord = GetPropertyValue(m, "ModRecord");
                if (modRecord == null) continue;
                var genType = GetPropertyValue(modRecord, "GenerationType");
                if (genType != null)
                {
                    var text = genType.ToString()?.ToLowerInvariant() ?? string.Empty;
                    if (wantPrefix && text.Contains("prefix")) { count++; continue; }
                    if (!wantPrefix && text.Contains("suffix")) { count++; continue; }
                }
                if (TryGetIntProperty(modRecord, out var genId, "GenerationTypeId", "GenerationId", "GenType"))
                {
                    if (wantPrefix && genId == 1) count++;
                    else if (!wantPrefix && genId == 2) count++;
                }
            }
            return count;
        }
        catch { return 0; }
    }

    private static bool TryGetIntProperty(object source, out int value, params string[] names)
    {
        value = 0;
        if (source == null) return false;
        foreach (var name in names)
        {
            var prop = source.GetType().GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase);
            if (prop == null) continue;
            try
            {
                var raw = prop.GetValue(source);
                if (raw is int i) { value = i; return true; }
                if (raw is long l) { value = unchecked((int)l); return true; }
                if (raw is short s) { value = s; return true; }
                if (raw is byte b) { value = b; return true; }
                if (raw is Enum e) { value = Convert.ToInt32(e); return true; }
            }
            catch { }
        }
        return false;
    }

    private static bool TryGetBoolProperty(object source, out bool value, params string[] names)
    {
        value = false;
        if (source == null) return false;
        foreach (var name in names)
        {
            var prop = source.GetType().GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase);
            if (prop == null) continue;
            try
            {
                var raw = prop.GetValue(source);
                if (raw is bool b) { value = b; return true; }
            }
            catch { }
        }
        return false;
    }

    private static object GetPropertyValue(object source, string name)
    {
        try
        {
            var prop = source.GetType().GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase);
            return prop?.GetValue(source);
        }
        catch { return null; }
    }
}
