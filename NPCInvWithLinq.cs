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
using System.Collections;
using System.Reflection;

namespace NPCInvWithLinq;

public class ServerAndStashWindow
{
	public IList<WindowSet> Tabs { get; set; } = new List<WindowSet>();

	public class WindowSet
	{
		public int Index { get; set; }
		public string Title { get; set; } = string.Empty;
		public bool IsVisible { get; set; }
		public Element? TabNameElement { get; set; }
		public List<CustomItemData> ServerItems { get; set; } = new();
		public List<CustomItemData> TradeWindowItems { get; set; } = new();
		public ServerInventory? Inventory { get; set; }

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
	private List<RuleBinding>? _ruleBindings;
	private PurchaseWindow? _purchaseWindowHideout;
	private PurchaseWindow? _purchaseWindow;
	private readonly Stopwatch _sinceLastPurchase = Stopwatch.StartNew();
	private readonly Stopwatch _sinceLastToggle = Stopwatch.StartNew();
	private bool _toggleKeyHeld;
	private readonly Dictionary<string, int> _purchasesPerTab = new();
	private string _lastTabTitle = "";
	private int _lastSelectedTabIndex = -1;
	private string _lastTabSelectionReason = "";
	private readonly Stopwatch _selectionDebugTimer = Stopwatch.StartNew();
	private long _lastSelectionDebugMs = 0;
	private int _selectedIndexHint = -1;
	private long _selectedIndexHintMs = 0;
	private long _frameCounter = 0;

	private sealed class RuleBinding
	{
		public NPCInvRule Rule { get; }
		public ItemFilter<CustomItemData>? Filter { get; }
		public int? MinOpenPrefixes { get; }
		public int? MinOpenSuffixes { get; }

		public RuleBinding(NPCInvRule rule, ItemFilter<CustomItemData>? filter, int? minOpenPrefixes, int? minOpenSuffixes)
		{
			Rule = rule ?? throw new ArgumentNullException(nameof(rule));
			Filter = filter; // may be null when rule is disabled; callers must guard
			MinOpenPrefixes = minOpenPrefixes;
			MinOpenSuffixes = minOpenSuffixes;
		}
	}

	private sealed class StampedItem
	{
		public required CustomItemData Data { get; init; }
		public int SelectedIndex { get; init; }
		public long Frame { get; init; }
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
		Settings.AutoPurchaseToggleKey.OnValueChanged += () => Input.RegisterKey(Settings.AutoPurchaseToggleKey.Value.Key);
		Input.RegisterKey(Settings.AutoPurchaseToggleKey.Value.Key);
		LoadRuleFiles();
		return true;
	}

	public override void Tick()
	{
		_purchaseWindowHideout = GameController.Game.IngameState.IngameUi.PurchaseWindowHideout;
		_purchaseWindow = GameController.Game.IngameState.IngameUi.PurchaseWindow;
		
		// Handle auto-purchase toggle with 1s debounce and edge detection
		var isDown = Input.GetKeyState(Settings.AutoPurchaseToggleKey.Value.Key);
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
		_frameCounter++;
		var hoveredItem = GetHoveredItem();
		PerformItemFilterTest(hoveredItem);
		ProcessPurchaseWindow(hoveredItem);
		ProcessRewardsWindow(hoveredItem);
		ProcessRitualWindow(hoveredItem);
		ProcessHaggleWindow(hoveredItem); // Expedition vendor (Tujen) support
	}

	private void ProcessRewardsWindow(Element? hoveredItem)
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
	private void ProcessRitualWindow(Element? hoveredItem)
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
	private void ProcessPurchaseWindow(Element? hoveredItem)
	{
		if (!IsPurchaseWindowVisible())
			return;

		List<string> unSeenItems = [];
		var purchaseWindowItems = GetVisiblePurchaseWindow();
		var selectedIndexHint = GetSelectedTabIndexFromContainer(purchaseWindowItems);
		_selectedIndexHint = selectedIndexHint;
		_selectedIndexHintMs = _selectionDebugTimer.ElapsedMilliseconds;
		ProcessStoredTabs(unSeenItems, hoveredItem, purchaseWindowItems, selectedIndexHint);

		if (unSeenItems.Count == 0 || purchaseWindowItems?.TabContainer == null)
			return;
		var serverItemsBox = CalculateServerItemsBox(unSeenItems, purchaseWindowItems);

		DrawServerItems(serverItemsBox, unSeenItems, hoveredItem);
	}

	private void DrawServerItems(RectangleF serverItemsBox, List<string> unSeenItems, Element? hoveredItem)
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

	private Element? GetHoveredItem()
	{
		return GameController.IngameState.UIHover is { Address: not 0, Entity.IsValid: true } hover ? hover : null;
	}

	private bool IsPurchaseWindowVisible()
	{
		return (_purchaseWindowHideout?.IsVisible ?? false) || (_purchaseWindow?.IsVisible ?? false);
	}

	private void ProcessStoredTabs(List<string> unSeenItems, Element? hoveredItem, PurchaseWindow? purchase, int selectedIndex)
	{
		if (purchase == null) return;
		if (_lastSelectedTabIndex != selectedIndex)
		{
			// DebugWindow.LogMsg($"[NPCInv] Selected tab changed: {_lastSelectedTabIndex} -> {selectedIndex} (reason={_lastTabSelectionReason})", 2);
			_lastSelectedTabIndex = selectedIndex;
			_lastSelectionDebugMs = _selectionDebugTimer.ElapsedMilliseconds;
		}
		// Detect Buyback state via common reflective property names; if active, treat as no selected shop tab
		try
		{
			var buybackRoot = GetPropertyValue(purchase, "BuybackInventory")
						?? GetPropertyValue(purchase, "BuyBackInventory")
						?? GetPropertyValue(purchase, "BuybackPanel")
						?? GetPropertyValue(purchase, "BuyBackPanel");
			if (TryGetBoolProperty(buybackRoot, out var bbVis, "IsVisible") && bbVis)
			{
				selectedIndex = -1; // none of the shop tabs should be treated as selected
			}
		}
		catch { }
		bool hasSelected = selectedIndex >= 0;
		RectangleF selectedGridRect = default;
		// If buyback is present/visible, suppress shop rendering entirely
		var (hasBB, _) = DetectBuyback(purchase);
		if (hasBB)
		{
			return;
		}
		if (hasSelected)
		{
			try
			{
				var selUiInv = TryGetRef(() => purchase?.TabContainer?.Inventories?[selectedIndex]?.Inventory);
				if (selUiInv != null)
				{
					selectedGridRect = selUiInv.GetClientRectCache;
				}
			}
			catch { }
		}
		foreach (var storedTab in _storedStashAndWindows.Value)
		{
			if (storedTab == null) continue;
			bool isCurrentlySelected = hasSelected && storedTab.Index == selectedIndex;
			if (isCurrentlySelected)
				ProcessVisibleTabItems(storedTab.TradeWindowItems, hoveredItem, storedTab.Title, selectedGridRect, storedTab.ServerItems);
			else
				ProcessHiddenTabItems(storedTab, unSeenItems, hoveredItem);
		}
	}

	private void ProcessVisibleTabItems(IEnumerable<CustomItemData> items, Element? hoveredItem, string tabTitle, RectangleF selectedGridRect, IEnumerable<CustomItemData>? serverItemsForTab)
	{
		var nowMs = _selectionDebugTimer.ElapsedMilliseconds;
		if (nowMs - _lastSelectionDebugMs < 120)
			return;
		if (_lastTabTitle != tabTitle)
		{
			_lastTabTitle = tabTitle;
			if (_purchasesPerTab.ContainsKey(tabTitle))
				_purchasesPerTab[tabTitle] = 0;
			// Clear any potential cached state when switching tabs
		}
		// First, get all non-null items
		var itemsList = items.Where(item => item != null).ToList();
		// Filter by server addresses if available
		HashSet<long>? serverAddrs = null;
		try
		{
			serverAddrs = serverItemsForTab?.Where(s => s?.Entity != null).Select(s => s.Entity.Address).ToHashSet() ?? new HashSet<long>();
		}
		catch { serverAddrs = new HashSet<long>(); }
		if (serverAddrs.Count > 0)
		{
			itemsList = itemsList.Where(it =>
			{
				try { return it?.Entity != null && serverAddrs.Contains(it.Entity.Address); } catch { return false; }
			}).ToList();
		}
		
		// Filter by grid rectangle if valid
		if (selectedGridRect.Width > 1 && selectedGridRect.Height > 1)
		{
			itemsList = itemsList.Where(it =>
			{
				try { return selectedGridRect.Intersects(it.ClientRectangle); } catch { return false; }
			}).ToList();
		}
		
		// Frame-stamp items for the current selected index and frame
		var stamped = itemsList.Select(d => new StampedItem { Data = d, SelectedIndex = _lastSelectedTabIndex, Frame = _frameCounter }).ToList();
		var currentFrameItems = stamped.Where(s => s.SelectedIndex == _lastSelectedTabIndex && s.Frame == _frameCounter).Select(s => s.Data).ToList();
		
		// Now apply the item filter only to current frame items with valid entities
		var renderables = currentFrameItems.Where(item => 
		{
			try 
			{ 
				// Extra safety: only process items from the currently selected tab
				if (item?.Entity == null || !item.Entity.IsValid)
					return false;
					
				// Verify this item is actually from the selected tab
				if (item.TabIndex >= 0 && item.TabIndex != _lastSelectedTabIndex)
					return false;
				
				return ItemInFilter(item); 
			} 
			catch 
			{ 
				return false; 
			}
		}).ToList();
		CustomItemData? nextToPurchase = null;
		if (Settings.AutoPurchase)
		{
			if (!_purchasesPerTab.TryGetValue(tabTitle, out var purchaseCount))
				purchaseCount = 0;
			if (Settings.MaxPurchasesPerTab == 0 || purchaseCount < Settings.MaxPurchasesPerTab)
			{
				nextToPurchase = renderables.FirstOrDefault();
			}
		}
		foreach (var visibleItem in renderables)
		{
			DrawItemFrame(visibleItem, hoveredItem, visibleItem == nextToPurchase);
		}
		if (Settings.AutoPurchase && _sinceLastPurchase.ElapsedMilliseconds >= Settings.PurchaseDelay && nextToPurchase != null)
		{
			if (!_purchasesPerTab.TryGetValue(tabTitle, out var purchaseCount))
				purchaseCount = 0;
			AutoPurchaseItem(nextToPurchase);
			_purchasesPerTab[tabTitle] = purchaseCount + 1;
		}
	}

	private void ProcessHiddenTabItems(WindowSet storedTab, List<string> unSeenItems, Element? hoveredItem)
	{
		if (storedTab.ServerItems == null || storedTab.ServerItems.Count == 0)
			return;
		var tabHadWantedItem = false;
		foreach (var hiddenItem in storedTab.ServerItems)
		{
			if (hiddenItem == null) continue;
			// For hidden tabs, we need to check filters without accessing potentially stale Entity components
			if (ItemInFilterSafe(hiddenItem, _lastSelectedTabIndex))
			{
				ProcessUnseenItems(unSeenItems, storedTab, hoveredItem);
				unSeenItems.Add($"\t{hiddenItem.Name}");
				tabHadWantedItem = true;
			}
		}

		if (tabHadWantedItem)
			unSeenItems.Add("");
	}

	private void ProcessUnseenItems(List<string> unSeenItems, WindowSet storedTab, Element? hoveredItem)
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

	private void DrawItemFrame(CustomItemData item, Element? hoveredItem, bool isNextToPurchase = false)
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

	private void DrawTabNameElementFrame(Element? tabNameElement, Element? hoveredItem, ColorNode? frameColorOverride = null)
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

	private PurchaseWindow? GetVisiblePurchaseWindow()
	{
		return (_purchaseWindowHideout?.IsVisible ?? false) ? _purchaseWindowHideout : ((_purchaseWindow?.IsVisible ?? false) ? _purchaseWindow : null);
	}

	private (bool HasBuyback, int BuybackIndex) DetectBuyback(PurchaseWindow? purchase)
	{
		if (purchase == null) return (false, -1);
		try
		{
			var bbRoot = GetPropertyValue(purchase, "BuybackInventory")
				?? GetPropertyValue(purchase, "BuyBackInventory")
				?? GetPropertyValue(purchase, "BuybackPanel")
				?? GetPropertyValue(purchase, "BuyBackPanel");
			if (bbRoot == null) return (false, -1);
			if (TryGetBoolProperty(bbRoot, out var bbVis, "IsVisible") && bbVis)
			{
				// Many UIs put buyback after shop tabs; try to find its index if exposed
				if (TryGetIntProperty(bbRoot, out var idx, "TabIndex", "Index"))
					return (true, idx);
				return (true, -1);
			}
		}
		catch { }
		return (false, -1);
	}

	private int GetSelectedTabIndexFromContainer(PurchaseWindow? purchase)
	{
		var container = TryGetRef(() => purchase?.TabContainer);
		var inventories = TryGetRef(() => container?.Inventories);
		if (inventories == null || inventories.Count == 0)
		{
			_lastTabSelectionReason = "None";
			return -1;
		}
		var (hasBuyback, buybackIdx) = DetectBuyback(purchase);
		// If buyback is visible, do not select any shop tab; treat as no selection
		if (hasBuyback)
		{
			_lastTabSelectionReason = "BuybackVisible";
			return -1;
		}
		// Prefer explicit properties that represent the currently visible stash
		try
		{
			if (TryGetIntProperty(container, out var vIdx, "VisibleStashIndex", "IndexVisibleStash"))
			{
				if (vIdx >= 0 && vIdx < inventories.Count)
				{
					if (hasBuyback && buybackIdx >= 0 && vIdx == buybackIdx)
					{
						_lastTabSelectionReason = "VisibleStashIndex=BuybackIgnored";
						// ignore and fall through
					}
					else
					{
						_lastTabSelectionReason = "Container.VisibleStashIndex";
						return vIdx;
					}
				}
			}
		}
		catch { }
		// Match VisibleStash reference if exposed
		try
		{
			var visInv = TryGetRef(() => container?.VisibleStash);
			if (visInv != null)
			{
				for (int i = 0; i < inventories.Count; i++)
				{
					try
					{
						if (ReferenceEquals(inventories[i].Inventory, visInv))
						{
							if (!(hasBuyback && i == buybackIdx))
							{
								_lastTabSelectionReason = "VisibleStashRef";
								return i;
							}
						}
					}
					catch { }
				}
			}
		}
		catch { }
		// If exactly one TabButton reports selected, trust it; otherwise ambiguous
		int uniqueBtnIdx = -1; int btnTrueCount = 0;
		for (int i = 0; i < inventories.Count; i++)
		{
			if (hasBuyback && i == buybackIdx) continue;
			var btn = TryGetRef(() => inventories[i].TabButton);
			if (TryGetBoolProperty(btn, out var sel, "IsSelected", "Selected", "IsActive", "IsCurrent") && sel)
			{
				btnTrueCount++;
				uniqueBtnIdx = i;
			}
		}
		if (btnTrueCount == 1)
		{
			_lastTabSelectionReason = "TabButtonUnique";
			return uniqueBtnIdx;
		}
		// If exactly one UI inventory reports IsVisible, use it (exclude buyback)
		int uniqueVisIdx = -1; int visTrueCount = 0;
		for (int i = 0; i < inventories.Count; i++)
		{
			if (hasBuyback && i == buybackIdx) continue;
			var uiInv = TryGetRef(() => inventories[i].Inventory);
			if (uiInv != null)
			{
				bool vis = false;
				try { vis = uiInv.IsVisible; } catch { }
				if (vis)
				{
					visTrueCount++;
					uniqueVisIdx = i;
				}
			}
		}
		if (visTrueCount == 1)
		{
			_lastTabSelectionReason = "UI.IsVisibleUnique";
			return uniqueVisIdx;
		}
		// Fall back to SelectedIndex-like integers (exclude buyback index if present)
		if (TryGetIntProperty(container, out var idx, "SelectedIndex", "SelectedTabIndex", "CurrentIndex", "CurrentTabIndex", "ActiveIndex", "ActiveTabIndex"))
		{
			if (idx >= 0 && idx < inventories.Count && !(hasBuyback && idx == buybackIdx)) { _lastTabSelectionReason = "Container.SelectedIndex"; return idx; }
		}
		if (TryGetIntProperty(purchase, out var idx2, "SelectedIndex", "SelectedTabIndex", "CurrentIndex", "CurrentTabIndex", "ActiveIndex", "ActiveTabIndex"))
		{
			if (idx2 >= 0 && idx2 < inventories.Count && !(hasBuyback && idx2 == buybackIdx)) { _lastTabSelectionReason = "Purchase.SelectedIndex"; return idx2; }
		}
		_lastTabSelectionReason = btnTrueCount > 1 ? "TabButtonAmbiguous" : (visTrueCount > 1 ? "UI.IsVisibleAmbiguous" : "None");
		return -1;
	}
 
	private void PerformItemFilterTest(Element? hoveredItem)
	{
		if (Settings.FilterTest.Value is { Length: > 0 } && hoveredItem != null)
		{
			var expr = Settings.FilterTest;
			TryExtractOpenCounts(NormalizeExpression(StripComments(expr)), out var cleaned, out var minPref, out var minSuff);
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
		var pickitConfigFileDirectory = ConfigDirectory ?? AppDomain.CurrentDomain.BaseDirectory;
		var existingRules = Settings.NPCInvRules;
		
		if (!string.IsNullOrEmpty(Settings.CustomConfigDir))
		{
			var baseDir = Path.GetDirectoryName(ConfigDirectory) ?? AppDomain.CurrentDomain.BaseDirectory;
			var customConfigFileDirectory = Path.Combine(baseDir, Settings.CustomConfigDir);
			
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
				ItemFilter<CustomItemData>? filter = null;
				int? minP = null, minS = null;
				if (rule.Enabled)
				{
					var fullPath = Path.Combine(pickitConfigFileDirectory, rule.Location);
					var text = File.ReadAllText(fullPath);
					TryExtractOpenCounts(NormalizeExpression(StripComments(text)), out var cleaned, out minP, out minS);
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

	private List<WindowSet> UpdateCurrentTradeWindow(List<WindowSet>? previousValue)
	{
		// Create dictionary with tab index as part of the key to ensure proper isolation
		var previousDict = previousValue?.ToDictionary(x => (x.Index, x.Title, x.Inventory?.Address ?? 0, x.Inventory?.ServerRequestCounter ?? -1));
		var purchaseWindowItems = GetVisiblePurchaseWindow();
		
		if (purchaseWindowItems == null || purchaseWindowItems.TabContainer?.Inventories == null)
		{
			return [];
		}
		
		int selectedIndex;
		var nowMs2 = _selectionDebugTimer.ElapsedMilliseconds;
		if (nowMs2 - _selectedIndexHintMs <= 200 && _selectedIndexHint >= 0)
			selectedIndex = _selectedIndexHint;
		else
			selectedIndex = GetSelectedTabIndexFromContainer(purchaseWindowItems);
		var tabContainer = purchaseWindowItems.TabContainer;
		return tabContainer.Inventories.Select((inventory, i) =>
		{
			try
			{
				if (inventory == null) return null;
				
				var uiInventory = TryGetRef(() => inventory.Inventory);
				if (uiInventory == null) return null;
				
				var serverInventory = TryGetRef(() => uiInventory.ServerInventory);
				if (serverInventory == null)
				{
					return null;
				}
				
				var visibleValidUiItems = TryGetRef(() => uiInventory.VisibleInventoryItems)?.Where(x => x?.Item?.Path != null).ToList() ?? [];
				// Filter out UI items that are not visible to avoid stale/stuck children
				visibleValidUiItems = visibleValidUiItems.Where(x => { try { return x != null && x.IsVisible; } catch { return false; } }).ToList();
				bool isSelected = i == selectedIndex;
				if (isSelected)
				{
					List<NormalInventoryItem> childUiItems;
					try { childUiItems = uiInventory.GetChildrenAs<NormalInventoryItem>()?.ToList() ?? new List<NormalInventoryItem>(); }
					catch { childUiItems = new List<NormalInventoryItem>(); }
					var childValid = childUiItems.Where(x => x?.Item?.Path != null).ToList();
					if (childValid.Count > 0)
					{
						visibleValidUiItems = childValid;
					}
				}
				var isVisible = isSelected; // only the active tab is considered visible
				var title = $"-{i + 1}-";
				if (previousDict?.TryGetValue((i, title, serverInventory?.Address ?? 0, serverInventory?.ServerRequestCounter ?? -1),
						out var previousSet) == true)
				{
					// Create a new WindowSet instance to avoid sharing references between tabs
					var newSet = new WindowSet
					{
						Inventory = serverInventory,
						Index = i,
						Title = title,
						IsVisible = isVisible,
						TabNameElement = TryGetRef(() => inventory.TabButton),
						// Only create CustomItemData for the selected tab to avoid stale Entity references
						TradeWindowItems = (isSelected && visibleValidUiItems.Count > 0 ? visibleValidUiItems
							.Where(x => x != null && x.Item != null && x.Item.Path != null)
							.Select(x => new CustomItemData(x!.Item!, GameController, EKind.Shop, x!.GetClientRectCache, i))
							.ToList() : new List<CustomItemData>()),
						ServerItems = (serverInventory?.Items is { } items
							? items.Where(x => x is { Path: { } })
								.Select(x => new CustomItemData(x, GameController, EKind.Shop, default, i))
								.ToList()
							: new List<CustomItemData>())
					};
					return newSet;
				}
				var tabButton = TryGetRef(() => inventory.TabButton);
				var newTab = new WindowSet
				{
					Inventory = serverInventory,
					Index = i,
					ServerItems = (serverInventory?.Items is { } items2 ? items2.Where(x => x is { Path: { } }).Select(x => new CustomItemData(x, GameController, EKind.Shop, default, i)).ToList() : new List<CustomItemData>()),
					TradeWindowItems = (isSelected && visibleValidUiItems.Count > 0 ? visibleValidUiItems
						.Where(x => x != null && x.Item != null && x.Item.Path != null)
						.Select(x => new CustomItemData(x!.Item!, GameController, EKind.Shop, x!.GetClientRectCache, i))
						.ToList() : new List<CustomItemData>()),
					Title = title,
					IsVisible = isVisible,
					TabNameElement = tabButton
				};
				return newTab;
			}
			catch
			{
				return null;
			}
		}).Where(x => x != null).Select(x => x!).ToList();
	}

	private bool ItemInFilter(CustomItemData item)
	{
		return ItemInFilterSafe(item, _lastSelectedTabIndex);
	}
	
	private bool ItemInFilterSafe(CustomItemData item, int currentSelectedTabIndex)
	{
		if (_ruleBindings == null || item?.Entity == null || !item.Entity.IsValid) 
			return false;
		
		// Special handling for haggle window items with negative TabIndex
		// -1 = main haggle inventory, -2 = buyback inventory
		// These must match exactly to prevent cross-contamination
		if (item.TabIndex < 0 && currentSelectedTabIndex < 0)
		{
			// Only process if the negative indices match exactly
			if (item.TabIndex != currentSelectedTabIndex)
				return false;
			
			// Additional validation for haggle items to prevent ghosting
			// Check if the item's rectangle is actually visible on screen
			try
			{
				var rect = item.ClientRectangle;
				if (rect.Width <= 0 || rect.Height <= 0 || rect.X < 0 || rect.Y < 0)
					return false;
			}
			catch
			{
				return false;
			}
		}
		else if (item.TabIndex < 0 || currentSelectedTabIndex < 0)
		{
			// One is negative and one isn't - don't match
			return false;
		}
		
		// Determine if this item is from a hidden tab
		// For positive indices, hidden means different tab
		// For negative indices, we already handled the matching above
		bool isHiddenTab = item.TabIndex >= 0 && item.TabIndex != currentSelectedTabIndex;
			
		foreach (var b in _ruleBindings)
		{
			if (!b.Rule.Enabled || b.Filter == null)
				continue;
			try
			{
				// Skip open affix constraints for hidden tabs to avoid accessing stale Entity components
				if (!isHiddenTab)
				{
					if (!ExtraOpenAffixConstraintsPass(item, b))
						continue;
				}
				else if (b.MinOpenPrefixes != null || b.MinOpenSuffixes != null)
				{
					// Skip this rule entirely for hidden tabs if it has open affix constraints
					continue;
				}
				
				if (b.Filter.Matches(item))
					return true;
			}
			catch
			{
				// Skip rules that throw exceptions
			}
		}
		return false;
	}

	private ColorNode GetFilterColor(CustomItemData item)
	{
		if (_ruleBindings == null || _ruleBindings.Count == 0 || item?.Entity == null || !item.Entity.IsValid)
			return Settings.DefaultFrameColor;
		
		// Top-to-bottom precedence: the first enabled rule that matches decides the color
		foreach (var binding in _ruleBindings)
		{
			if (!binding.Rule.Enabled || binding.Filter == null)
				continue;
			try
			{
				if (!ExtraOpenAffixConstraintsPass(item, binding))
					continue;
				if (binding.Filter.Matches(item))
				{
					return binding.Rule.Color;
				}
			}
			catch
			{
				// Skip rules that throw exceptions
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
		
		// For tab coloring, we use the safe filter check with current selected tab
		foreach (var item in itemsList)
		{
			if (ItemInFilterSafe(item, _lastSelectedTabIndex))
			{
				// Get the color from the first matching rule
				foreach (var binding in _ruleBindings)
				{
					if (!binding.Rule.Enabled || binding.Filter == null)
						continue;
					
					// Determine if this item is from a hidden tab
					bool isHiddenTab = item.TabIndex >= 0 && item.TabIndex != _lastSelectedTabIndex;
					
					try
					{
						// Skip rules with open affix constraints for hidden tabs
						if (isHiddenTab && (binding.MinOpenPrefixes != null || binding.MinOpenSuffixes != null))
							continue;
							
						if (binding.Filter.Matches(item))
							return binding.Rule.Color;
					}
					catch
					{
						// Skip rules that throw exceptions
					}
				}
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

	private static T? TryGetRef<T>(Func<T?> getter) where T : class
	{
		try { return getter(); } catch { return null; }
	}

	private static T TryGetValue<T>(Func<T> getter) where T : struct
	{
		try { return getter(); } catch { return default; }
	}

	// ===== Expedition Haggle (vendor) support =====
	private void ProcessHaggleWindow(Element? hoveredItem)
	{
		var haggle = GameController?.Game?.IngameState?.IngameUi?.HaggleWindow;
		if (haggle == null || !haggle.IsVisible) return;
		
		var (visibleItems, label, isBuyback) = GetHaggleVisibleItemsWithLabel(haggle);
		if (visibleItems.Count == 0) return;
		
		// Get the bounds of the current inventory view to filter out ghost items
		RectangleF inventoryBounds = default;
		try
		{
			// Try to get the bounds of the visible inventory grid
			if (isBuyback)
			{
				var buybackRoot = GetPropertyValue(haggle, "BuybackInventory") as Element
							?? GetPropertyValue(haggle, "BuyBackInventory") as Element;
				if (buybackRoot != null)
					inventoryBounds = buybackRoot.GetClientRectCache;
			}
			else
			{
				// Main inventory bounds - try to find the main grid
				inventoryBounds = haggle.GetClientRectCache;
			}
		}
		catch { }
		
		// Filter out items that are outside the current inventory bounds
		if (inventoryBounds.Width > 0 && inventoryBounds.Height > 0)
		{
			visibleItems = visibleItems.Where(item => 
			{
				try
				{
					var rect = item.ClientRectangle;
					return rect.X >= inventoryBounds.X && 
						   rect.Y >= inventoryBounds.Y &&
						   rect.Right <= inventoryBounds.Right &&
						   rect.Bottom <= inventoryBounds.Bottom;
				}
				catch { return false; }
			}).ToList();
		}
		
		// Use different virtual tab indices for main vs buyback to ensure proper filtering
		// Main inventory uses -1, buyback uses -2
		int virtualTabIndex = isBuyback ? -2 : -1;
		
		// Filter items using the appropriate virtual tab index
		var filtered = visibleItems.Where(item => ItemInFilterSafe(item, virtualTabIndex)).ToList();
		
		// Select next-to-purchase similar to purchase window (single pseudo-tab)
		string tabKey = isBuyback ? "__haggle_buyback__" : "__haggle__";
		
		CustomItemData? nextToPurchase = null;
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

	private (List<CustomItemData> Items, Element? Label, bool IsBuyback) GetHaggleVisibleItemsWithLabel(Element haggleRoot)
	{
		var items = new List<CustomItemData>();
		Element? label = null;
		bool isBuyback = false;
		Element? buybackRoot = null;
		
		try { label = haggleRoot.GetChildFromIndices(6, 2, 0); } catch { }
		
		// Check if we're in buyback mode
		try
		{
			buybackRoot = GetPropertyValue(haggleRoot, "BuybackInventory") as Element
						?? GetPropertyValue(haggleRoot, "BuyBackInventory") as Element
						?? GetPropertyValue(haggleRoot, "BuybackPanel") as Element
						?? GetPropertyValue(haggleRoot, "BuyBackPanel") as Element;
			if (buybackRoot != null && TryGetBoolProperty(buybackRoot, out var bbVis, "IsVisible") && bbVis)
			{
				isBuyback = true;
			}
		}
		catch { }
		
		// Use different TabIndex for main inventory vs buyback to prevent entity reuse issues
		int tabIndex = isBuyback ? -2 : -1;
		
		// If buyback is active, we need to get items from the buyback inventory specifically
		Element inventorySource = isBuyback && buybackRoot != null ? buybackRoot : haggleRoot;
		
		// Preferred path: use typed ExpeditionVendorElement.InventoryItems via reflection if available
		try
		{
			var invProp = inventorySource?.GetType().GetProperty("InventoryItems", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
			if (invProp != null)
			{
				if (invProp.GetValue(inventorySource) is IEnumerable invEnum)
				{
					foreach (var obj in invEnum)
					{
						try
						{
							NormalInventoryItem? nii = obj as NormalInventoryItem;
							if (nii == null && obj is NormalInventoryItem cast)
							{
								nii = cast;
							}
							// Check visibility to prevent ghost items
							bool isVisible = nii?.IsVisible ?? false;
							if (!isVisible && obj is Element elem)
								isVisible = elem.IsVisible;
								
							var entity = nii?.Item;
							if (entity == null)
							{
								entity = GetPropertyValue(obj, "Item") as Entity;
							}
							if (entity is { Address: not 0, IsValid: true } && isVisible)
							{
								var rect = (obj as Element)?.GetClientRectCache ?? nii?.GetClientRectCache ?? default;
								items.Add(new CustomItemData(entity, GameController, EKind.Shop, rect, tabIndex));
							}
						}
						catch { }
					}
					if (items.Count > 0)
						return (items, label, isBuyback);
				}
			}
		}
		catch { }
		
		// Fallback: locate the grid and enumerate children
		var grid = isBuyback && buybackRoot != null 
			? TryFindHaggleInventoryGrid(buybackRoot) 
			: TryFindHaggleInventoryGrid(haggleRoot);
			
		if (grid == null) return (items, label, isBuyback);
		
		List<NormalInventoryItem> uiItems;
		try { uiItems = grid.GetChildrenAs<NormalInventoryItem>()?.Skip(1).ToList() ?? new List<NormalInventoryItem>(); }
		catch { uiItems = new List<NormalInventoryItem>(); }
		
		foreach (var x in uiItems)
		{
			try
			{
				// Only add items that are visible to prevent ghost items from other inventories
				if (x?.Item is { Address: not 0, IsValid: true } && x.IsVisible)
				{
					items.Add(new CustomItemData(x.Item, GameController, EKind.Shop, x.GetClientRectCache, tabIndex));
				}
			}
			catch { }
		}
		
		return (items, label, isBuyback);
	}
	
	private static Element? TryFindHaggleInventoryGrid(Element root)
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
			
		// Never check open affixes for items without proper tab assignment
		// This includes haggle/expedition items and prevents entity memory issues
		if (item.TabIndex < 0)
			return false;
			
		if (rule.MinOpenPrefixes is int pReq && OpenPrefixCount(item) < pReq) return false;
		if (rule.MinOpenSuffixes is int sReq && OpenSuffixCount(item) < sReq) return false;
		return true;
	}
	
	private static readonly Regex OpenPrefixRegex = new Regex(@"OpenPrefixCount\s*\(\)\s*(==|>=|<=|>|<)\s*(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
	private static readonly Regex OpenSuffixRegex = new Regex(@"OpenSuffixCount\s*\(\)\s*(==|>=|<=|>|<)\s*(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static string StripComments(string expr)
	{
		if (string.IsNullOrEmpty(expr)) return string.Empty;
		var sb = new System.Text.StringBuilder(expr.Length);
		bool inString = false;
		bool inBlock = false;
		for (int i = 0; i < expr.Length; i++)
		{
			char c = expr[i];
			char next = i + 1 < expr.Length ? expr[i + 1] : '\0';
			if (!inString && !inBlock && c == '/' && next == '/')
			{
				while (i < expr.Length && expr[i] != '\n') i++;
				continue;
			}
			if (!inString && !inBlock && c == '/' && next == '*')
			{
				inBlock = true; i++;
				continue;
			}
			if (inBlock)
			{
				if (c == '*' && next == '/') { inBlock = false; i++; }
				continue;
			}
			if (c == '"')
			{
				bool escaped = i > 0 && expr[i - 1] == '\\';
				if (!escaped) inString = !inString;
				sb.Append(c);
				continue;
			}
			sb.Append(c);
		}
		return sb.ToString();
	}

	private static string NormalizeExpression(string expr)
	{
		if (string.IsNullOrWhiteSpace(expr)) return string.Empty;
		var nl = expr.Replace("\r\n", "\n").Replace('\r', '\n');
		var lines = nl.Split('\n');
		var sb = new System.Text.StringBuilder(nl.Length + 32);
		bool firstWritten = false;
		for (int i = 0; i < lines.Length; i++)
		{
			var raw = lines[i];
			var line = raw.Trim();
			if (line.Length == 0) continue;
			if (firstWritten)
			{
				bool startsWithOp = line.StartsWith("&&") || line.StartsWith("||") || line.StartsWith(")") || line.StartsWith("]") || line.StartsWith(",");
				char last = sb.Length > 0 ? sb[sb.Length - 1] : '\0';
				bool prevOpener = last == '(' || last == '{' || last == '[' || last == ',' || last == '&' || last == '|';
				if (!startsWithOp && !prevOpener) sb.Append(" || "); else sb.Append(' ');
			}
			sb.Append(line);
			firstWritten = true;
		}
		return sb.ToString();
	}
 
	private static void TryExtractOpenCounts(string expr, out string cleanedExpr, out int? minPrefixes, out int? minSuffixes)
	{
		int? localMinPrefixes = null;
		int? localMinSuffixes = null;
		var cleaned = NormalizeExpression(StripComments(expr ?? string.Empty));

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
	
	// Minimal port of open affix counting from ItemfilterExtension with AffixType/Tags-based max fallbacks
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
	
	private static Mods? TryGetMods(ItemData item)
	{
		try 
		{ 
			if (item?.Entity == null || !item.Entity.IsValid)
				return null;
			return item.Entity.GetComponent<Mods>(); 
		} 
		catch 
		{ 
			return null; 
		}
	}
	
	private static int GetPrefixCount(ItemData item)
	{
		// Safety check for items without proper tab assignment
		if (item is CustomItemData customItem && customItem.TabIndex < 0)
			return 0;
			
		var mods = TryGetMods(item);
		if (mods == null) return 0;
		if (TryGetIntProperty(mods, out var v, "PrefixesCount", "PrefixCount", "NumPrefixes")) return v;
		return CountAffixesByKind(mods, wantPrefix: true);
	}
	
	private static int GetSuffixCount(ItemData item)
	{
		// Safety check for items without proper tab assignment
		if (item is CustomItemData customItem && customItem.TabIndex < 0)
			return 0;
			
		var mods = TryGetMods(item);
		if (mods == null) return 0;
		if (TryGetIntProperty(mods, out var v, "SuffixesCount", "SuffixCount", "NumSuffixes")) return v;
		return CountAffixesByKind(mods, wantPrefix: false);
	}
	
	private static int GetMaxPrefixes(ItemData item)
	{
		var mods = TryGetMods(item);
		if (mods == null) return 0;
		return ComputeMaxByTagsAndRarity(item);
	}
	
	private static int GetMaxSuffixes(ItemData item)
	{
		var mods = TryGetMods(item);
		if (mods == null) return 0;
		return ComputeMaxByTagsAndRarity(item);
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
				var modRecord = GetPropertyValue(m, "ModRecord");
				if (modRecord != null)
				{
					var affixTypeObj = GetPropertyValue(modRecord, "AffixType");
					if (affixTypeObj != null)
					{
						var text = affixTypeObj.ToString()?.ToLowerInvariant() ?? string.Empty;
						if (wantPrefix && text.Contains("prefix")) { count++; continue; }
						if (!wantPrefix && text.Contains("suffix")) { count++; continue; }
					}
				}
				if (TryGetBoolProperty(m, out var isPrefix, "IsPrefix") && wantPrefix && isPrefix) { count++; continue; }
				if (TryGetBoolProperty(m, out var isSuffix, "IsSuffix") && !wantPrefix && isSuffix) { count++; continue; }
				if (modRecord != null)
				{
					var genType = GetPropertyValue(modRecord, "GenerationType");
					if (genType != null)
					{
						var t = genType.ToString()?.ToLowerInvariant() ?? string.Empty;
						if (wantPrefix && t.Contains("prefix")) { count++; continue; }
						if (!wantPrefix && t.Contains("suffix")) { count++; continue; }
					}
					if (TryGetIntProperty(modRecord, out var genId, "GenerationTypeId", "GenerationId", "GenType"))
					{
						if (wantPrefix && genId == 1) count++;
						else if (!wantPrefix && genId == 2) count++;
					}
				}
			}
			return count;
		}
		catch { return 0; }
	}
	
	private static int ComputeMaxByTagsAndRarity(ItemData item)
	{
		// Extra safety: never compute max affixes for items without proper tab assignment
		// This prevents accessing potentially stale entity data from haggle/expedition windows
		if (item is CustomItemData customItem && customItem.TabIndex < 0)
			return 0;
			
		var tags = GetItemTags(item);
		int baseMax;
		if (tags.Contains("flask")) baseMax = 1;
		else if (tags.Contains("jewel") || tags.Contains("abyssjewel") || tags.Contains("clusterjewel")) baseMax = 2;
		else baseMax = 3;
		var mods = TryGetMods(item);
		var rarity = GetRarityCode(mods!);
		switch (rarity)
		{
			case 0: return 0; // Normal
			case 1: return Math.Min(baseMax, 1); // Magic
			case 2: return baseMax; // Rare
			case 3: return 0; // Unique
			default: return baseMax;
		}
	}
	
	private static int GetRarityCode(Mods mods)
	{
		if (mods == null) return -1;
		if (TryGetIntProperty(mods, out var r, "ItemRarity", "Rarity")) return r;
		var ro = GetPropertyValue(mods, "ItemRarity") ?? GetPropertyValue(mods, "Rarity");
		var t = ro?.ToString()?.ToLowerInvariant();
		return t switch { "normal" => 0, "magic" => 1, "rare" => 2, "unique" => 3, _ => -1 };
	}
	
	private static HashSet<string> GetItemTags(ItemData item)
	{
		var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		
		if (item?.Entity == null || !item.Entity.IsValid)
			return tags;
			
		var path = item.Entity.Path ?? string.Empty;
		if (string.IsNullOrEmpty(path)) return tags;
		
		try
		{
			var baseComp = item.Entity.GetComponent<Base>();
			if (baseComp != null)
			{
				var baseType = GetPropertyValue(baseComp, "ItemBase") ?? GetPropertyValue(baseComp, "BaseItemType") ?? (object)baseComp;
				var t1 = GetPropertyValue(baseType, "Tags") as IEnumerable;
				var t2 = GetPropertyValue(baseType, "MoreTagsFromPath") as IEnumerable;
				AddStrings(tags, t1);
				AddStrings(tags, t2);
			}
		}
		catch { }
		
		var lower = path.ToLowerInvariant();
		if (lower.Contains("flask")) tags.Add("flask");
		if (lower.Contains("jewel")) tags.Add("jewel");
		if (lower.Contains("abyss")) tags.Add("abyssjewel");
		if (lower.Contains("cluster")) tags.Add("clusterjewel");
		
		return tags;
	}
	
	private static void AddStrings(HashSet<string> into, IEnumerable? list)
	{
		if (list == null) return;
		foreach (var o in list)
		{
			if (o is string s && !string.IsNullOrWhiteSpace(s)) into.Add(s);
		}
	}
	
	private static bool TryGetIntProperty(object? source, out int value, params string[] names)
	{
		value = 0;
		if (source == null) return false;
		foreach (var name in names)
		{
			var prop = source.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
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
	
	private static bool TryGetBoolProperty(object? source, out bool value, params string[] names)
	{
		value = false;
		if (source == null) return false;
		foreach (var name in names)
		{
			var prop = source.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
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
	
	private static object? GetPropertyValue(object? source, string name)
	{
		try
		{
			var prop = source?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
			return prop?.GetValue(source);
		}
		catch { return null; }
	}
}
