using ExileCore2;
using ExileCore2.PoEMemory;
using ExileCore2.PoEMemory.Elements;
using ExileCore2.PoEMemory.Elements.InventoryElements;
using ExileCore2.Shared.Helpers;
using ExileCore2.PoEMemory.MemoryObjects;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace NPCInvWithLinq;

public partial class NPCInvWithLinq
{
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
			var invProp = inventorySource?.GetType().GetProperty("InventoryItems", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase);
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

		System.Collections.Generic.List<NormalInventoryItem> uiItems;
		try { uiItems = grid.GetChildrenAs<NormalInventoryItem>()?.Skip(1).ToList() ?? new System.Collections.Generic.List<NormalInventoryItem>(); }
		catch { uiItems = new System.Collections.Generic.List<NormalInventoryItem>(); }

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
		var q = new System.Collections.Generic.Queue<Element>();
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
}


