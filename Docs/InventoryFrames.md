## Inventory Frames Deep Dive: Sourcing, Processing, Rendering (PurchaseWindow & HaggleWindow)

### Overview

This document explains how `NPCInvWithLinq` sources items from UI inventories, applies filtering/constraints, and renders frames/overlays. Emphasis is on the vendor `PurchaseWindow` and Expedition `HaggleWindow` flows, including auto-purchase behavior and safety/performance notes.

---

## Sourcing

### PurchaseWindow

- The main orchestration is done via `ProcessPurchaseWindow(Element hoveredItem)` which early-returns if no purchase window is visible, determined by `IsPurchaseWindowVisible()` using `GameController.Game.IngameState.IngameUi.PurchaseWindowHideout` and `PurchaseWindow`.
- The data source is `_storedStashAndWindows` cache, a `TimeCache<List<WindowSet>>` built by `UpdateCurrentTradeWindow` every ~50ms.
- `UpdateCurrentTradeWindow` walks `purchaseWindowItems.TabContainer.Inventories` and produces a list of `WindowSet` entries:
  - `ServerItems`: derived from `ServerInventory.Items` (persistent server-side list). Each item is tagged with its TabIndex for proper tracking.
  - `TradeWindowItems`: derived from visible UI items with client rectangles captured via `GetClientRectCache`. For selected tabs, uses `GetChildrenAs<NormalInventoryItem>()` when available for more accurate results.
  - `IsVisible`: whether the tab's `Inventory` UI is visible (only true for the currently selected tab).
  - `TabNameElement`: the tab header/button `Element` used for drawing outlines.
  - `Title`: uses label pattern `"-<index>-"`.
  - `Index`: 0-based tab index for proper tab identification.
- **Tab Selection Detection**: Uses multiple strategies to determine the selected tab:
  - Preferred: `Container.VisibleStashIndex` property
  - Fallback: `VisibleStash` reference matching
  - Fallback: TabButton.IsSelected checking
  - Fallback: UI Inventory.IsVisible checking
  - Fallback: SelectedIndex-like properties
- **Buyback Window Handling**: Detects buyback state via reflection on common property names (`BuybackInventory`, `BuyBackInventory`, etc.). When buyback is active, treats no shop tab as selected to prevent incorrect rendering.
- A stability map (`previousDict`) keyed by `(Index, Title, ServerInventory.Address, ServerRequestCounter)` is used to decide whether to reuse a previous `WindowSet` or create a new one.
- **Key Improvements**:
  - All `WindowSet` instances are now created fresh each update to prevent object reference sharing between tabs
  - `TabIndex` is explicitly set on all `CustomItemData` objects to track which tab they belong to
  - Items from `GetChildrenAs<NormalInventoryItem>()` that lack proper tab assignment get TabIndex -1
- Defensive helpers `TryGetRef` and `TryGetValue` catch transient memory/UI exceptions and return null/default to keep the render loop resilient.

### HaggleWindow (Expedition vendor)

- Entry point is `ProcessHaggleWindow(Element hoveredItem)`; returns immediately if `HaggleWindow` is null or not visible.
- Multi-layer ghosting prevention:
  1. **Inventory Detection**: Detects whether buyback is active via reflection on common property names
  2. **Bounds Filtering**: Gets the bounds of the current inventory and filters out items outside these bounds
  3. **Source Isolation**: When buyback is active, gets items from buyback root element specifically
- Items are sourced via `GetHaggleVisibleItemsWithLabel`:
  - Uses different inventory sources based on mode (main vs buyback)
  - Preferred path: Uses typed `ExpeditionVendorElement.InventoryItems` via reflection if available
  - Fallback: Locates the inventory grid via `TryFindHaggleInventoryGrid` from the appropriate root
  - **Visibility Checks**: Only includes items where `IsVisible` is true
  - Converts each `NormalInventoryItem` into `CustomItemData` with different TabIndex based on mode:
    - **Main inventory**: TabIndex = -1
    - **Buyback inventory**: TabIndex = -2
- **ItemInFilterSafe enhancements**:
  - Special logic for negative TabIndex values requires exact matching
  - Additional rectangle validation for haggle items
  - Prevents any cross-contamination between different haggle inventories
- Tab keys are `"__haggle__"` for main and `"__haggle_buyback__"` for buyback

### Rewards/Ritual (for completeness)

- `GetRewardItems` and `GetRitualItems` are 1s `TimeCache` sources for `QuestRewardWindow` and `RitualWindow` respectively.
- They project valid items into `CustomItemData` including client rectangles for draw, with TabIndex -1.

---

## Processing (Filtering, Constraints, Decisions)

### Rule loading and precedence

- `LoadRuleFiles()` discovers `*.ifl` files from `ConfigDirectory` or an optional custom folder.
- Existing user-ordered rules are preserved if files still exist; new `.ifl` files are appended.
- Each `NPCInvRule` yields a `RuleBinding` with:
  - `Filter`: `ItemFilter<CustomItemData>` compiled from file content (null when rule disabled).
  - `MinOpenPrefixes` / `MinOpenSuffixes`: extracted constraints.
- Precedence is top-to-bottom; first enabled rule that matches determines a positive match and color.

### Open prefix/suffix constraints

- `TryExtractOpenCounts(text, out cleaned, out minP, out minS)` parses and removes patterns like:
  - `OpenPrefixCount() >= N`, `OpenSuffixCount() >= N`, etc.
- The expressions are replaced with `true` so the remaining text can be compiled by `ItemFilterLibrary`.
- The minimum requirements are merged by `MergeConstraint` to a single effective threshold per side.

### Affix counting implementation

- `OpenPrefixCount(item)` and `OpenSuffixCount(item)` are computed as `max - used` (clamped at zero).
- `GetMaxPrefixes/GetMaxSuffixes`: Computed via `ComputeMaxByTagsAndRarity`:
  - Base max determined by tags (flask=1, jewel=2, default=3)
  - Modified by rarity (normal=0, magic=min(baseMax,1), rare=baseMax, unique=0)
- `GetPrefixCount/GetSuffixCount`: Obtained via reflection on `Mods` component:
  - Direct properties: `PrefixesCount`/`SuffixesCount`
  - Fallback: Count explicit mods by checking `IsPrefix`/`IsSuffix` or `ModRecord.AffixType`
- **Tag Detection**: `GetItemTags` now computes tags directly without caching to prevent cross-tab contamination
- All reflection is guarded; failures return conservative zeros to avoid crashes.

### Matching flow used across windows

- `ItemInFilter(item)` calls `ItemInFilterSafe(item, _lastSelectedTabIndex)`
- `ItemInFilterSafe(item, currentSelectedTabIndex)`:
  - **Critical Change**: Items with TabIndex -1 or from different tabs are treated as "hidden"
  - **Negative TabIndex Handling**: For haggle windows, negative indices must match exactly (-1 ≠ -2)
  - For hidden tab items, rules with open affix constraints are skipped entirely
  - This prevents accessing potentially stale Entity components from non-current tabs
  - **ItemStats Protection**: The exact matching prevents ItemStats filters from reading stale data across haggle inventories
  - Returns true on the first match (topmost rule wins)
- `GetFilterColor(item)` mirrors the same precedence for coloring with entity validation.
- `GetFilterColorForTab(items)` returns the color of the first rule that matches any item in the tab (uses safe filtering for hidden tabs).
- Debug helper: `PerformItemFilterTest` can test a user-provided filter expression against the currently hovered item.

### Auto-purchase decisioning

- Auto-purchase is toggled via `Settings.AutoPurchaseToggleKey` with 1s debounce and edge detection in `Tick()`.
- Per-window processing (`ProcessVisibleTabItems` / `ProcessHaggleWindow`) selects `nextToPurchase` = first filtered item if:
  - `Settings.AutoPurchase` is on,
  - Per-tab count `_purchasesPerTab[tabKey]` is below `Settings.MaxPurchasesPerTab` (or unlimited when 0).
- The actual purchase triggers if `_sinceLastPurchase.ElapsedMilliseconds >= Settings.PurchaseDelay`.

---

## Rendering

### Item frames

- `DrawItemFrame(item, hoveredItem, isNextToPurchase)`:
  - Validates rectangle and bails on degenerate sizes.
  - Chooses color: `AutoPurchaseColor` if it's the next planned purchase, otherwise `GetFilterColor(item)`.
  - When the hovered tooltip intersects the item's rectangle (and it's not the same entity), applies a translucent highlight variant.
  - Draws frame via `Graphics.DrawFrame(rect, color, FrameThickness)`.

### Tab label outlines

- `DrawTabNameElementFrame(tabNameElement, hoveredItem, frameColorOverride)`:
  - Validates `Element` and rectangle, defensive `try/catch` around `GetClientRectCache`.
  - Uses a provided color or `DefaultFrameColor`.
  - Avoids drawing when intersecting with hovered tooltip; otherwise draws full-intensity frame.
- The color used for a tab is computed by `GetFilterColorForTab(ServerItems)` when a hidden tab contains at least one matching item.
- **Note**: Tab highlighting is disabled for filters using open affix constraints due to the cross-tab contamination fix.

### "Unseen items" text box (hidden tabs)

- In `ProcessHiddenTabItems`, when a hidden tab contains matching server items (without open affix constraints), the tab title and item names are collected into `unSeenItems`.
- `CalculateServerItemsBox` sizes a text box to the right of the tab container based on the longest line.
- `DrawServerItems` renders a semi-transparent black box with white lines of text, unless the hovered tooltip overlaps the box. Degenerate boxes are skipped.
- **Note**: This feature is limited when using open affix filters due to the safety constraints.

---

## Auto-Purchase Execution

- `AutoPurchaseItem(item)` performs a safe, deterministic click sequence:
  - Validates the on-screen rectangle and bounds relative to the game window (inflated inward to avoid borders), early-returning if out of bounds.
  - Converts item center to screen coordinates and moves cursor.
  - Holds `Ctrl` for the entire click, performs a left click, then releases modifiers in a `finally` block to prevent stuck keys.
  - Restarts `_sinceLastPurchase` and logs the action.
- Per-tab counts are updated to respect `MaxPurchasesPerTab`.

---

## Safety and Performance Considerations

- **Cross-Tab Entity Safety**:
  - **Key Innovation**: All `CustomItemData` objects now track their source TabIndex
  - Items with TabIndex -1 or from non-current tabs are treated as "hidden" for open affix calculations
  - This prevents accessing Entity components that may have been reused by the game for different items
  - **Haggle Window**: Multi-layer protection against ghosting:
    - Different negative TabIndex values (main: -1, buyback: -2)
    - Source isolation based on buyback state
    - Visibility filtering at item collection
    - Bounds checking to exclude out-of-view items
    - Rectangle validation in `ItemInFilterSafe`
  - **ItemInFilterSafe**: Special logic for negative TabIndex values ensures exact matching
  - Additional safety checks in `GetPrefixCount`, `GetSuffixCount`, and `ComputeMaxByTagsAndRarity` prevent any entity access for TabIndex < 0 items
  - `ExtraOpenAffixConstraintsPass` immediately returns false for items with TabIndex < 0
  - Trade-off: Tab highlighting doesn't work for filters using `OpenPrefixCount()` or `OpenSuffixCount()`
- Resilience:
  - Almost all UI/memory access is wrapped in `try/catch` to avoid tearing the render loop.
  - `TryGetRef`/`TryGetValue` helpers return null/default on transient faults.
  - Element and rectangle validation prevents odd draws and label-jumping (stale pointers).
- Freshness:
  - `TabNameElement` is refreshed every frame; `TradeWindowItems` are rebuilt every frame to avoid stale rectangles; `ServerItems` are refreshed cheaply.
  - Caches: `_storedStashAndWindows` (50ms), `_rewardItems` and `_ritualItems` (1000ms).
- Allocation/GC:
  - Direct tag computation without caching reduces memory usage but increases CPU slightly
  - WindowSet instances are created fresh to prevent reference sharing between tabs
  - Projections build new `List<CustomItemData>` per update; acceptable at current scale
- Haggle:
  - Preferred typed property access via reflection is more efficient than BFS fallback
  - BFS fallback searches the UI tree to find the grid; robust against layout changes at minor CPU cost
- Input Safety:
  - Modifier keys are always released in `finally`.
  - Bounds checking avoids clicking outside the game client.
- **Debug Logging**:
  - Optional `EnableDebugLogging` setting available for troubleshooting
  - All debug logging code is cleanly separated and doesn't impact normal performance

---

## Edge Cases and Limitations

- Server inventory may briefly be null while UI updates; code skips the tab safely.
- Relying on reflection to read `Mods` and `Stats` may break across game updates; functions handle failures by returning zeros.
- Using first-match semantics for `nextToPurchase` is simple but not optimal if multiple high-priority items exist.
- Tab highlighting is disabled for open affix filters to prevent cross-tab entity access issues.
- Items from `GetChildrenAs<NormalInventoryItem>()` may not have proper tab assignment, defaulting to TabIndex -1.
- **Haggle Window Specific**:
  - Relies on negative TabIndex values to distinguish main vs buyback inventory
  - Buyback detection depends on reflection finding specific property names
  - If ghosting persists, it may indicate the UI structure differs from expectations
  - Some complex filters may still experience issues if they access deep entity properties

---

## Recent Improvements Summary

1. **Fixed Cross-Tab Ghosting**:
   - Root cause: Entity memory reuse causing items from tab 0 to appear in other tabs
   - Solution: Track TabIndex on all items and treat non-current tab items as "hidden"
   - Open affix constraints are never checked on hidden tab items

2. **Fixed Haggle Window Ghosting** (Multi-layer approach):
   - Discovered that ItemStats filters also cause ghosting due to entity memory reuse
   - Layer 1: Assign different negative TabIndex values for main (-1) vs buyback (-2)
   - Layer 2: Source items from the correct inventory root (main vs buyback)
   - Layer 3: Add visibility checks to exclude non-visible UI elements
   - Layer 4: Add bounds filtering to exclude items outside current inventory view
   - Layer 5: Enhanced `ItemInFilterSafe` with rectangle validation for haggle items
   - This comprehensive approach addresses various sources of cross-inventory contamination

3. **Enhanced Tab Selection Detection**:
   - Multiple fallback strategies to reliably detect the selected tab
   - Proper buyback window detection and handling
   - Frame-stamping to ensure only current-frame items are processed

4. **Improved Data Freshness**:
   - WindowSet instances created fresh each update
   - No more shared object references between tabs
   - Direct tag computation without static caching

5. **Better Error Handling**:
   - All entity access properly validated
   - Comprehensive try-catch wrapping
   - Clean separation of debug logging

---

## Improvement Suggestions

- Purchase selection:
  - Support prioritization strategies (e.g., by rule order then item value/rarity) instead of naïve first-match.
  - Require hover-stability (optional) before click to reduce risk of clicking stale positions.
- Alternative open affix handling:
  - Consider caching affix counts at the WindowSet level when tabs are selected
  - This would allow tab highlighting to work with open affix filters
- Rendering efficiency:
  - Pool `List<CustomItemData>` instances for hot paths to reduce GC pressure.
  - Cache text measurements for `unSeenItems` while the list is unchanged.
- Diagnostics:
  - Add counters/timers for matches per rule and per frame time budget, optionally visualized in an overlay panel.
- Safety knobs:
  - Add a global hotkey kill-switch that disables auto-purchase and drawing immediately.
  - Add a setting to limit auto-purchases per window open (currently per tab) and a session cap to avoid runaway behavior.

---

## Key API Touchpoints (by function)

- Visibility and windows: `IsPurchaseWindowVisible`, `GetVisiblePurchaseWindow`.
- Tab selection: `GetSelectedTabIndexFromContainer`, `DetectBuyback`.
- Data sourcing: `UpdateCurrentTradeWindow`, `GetHaggleVisibleItemsWithLabel`, `TryFindHaggleInventoryGrid`, `GetRewardItems`, `GetRitualItems`.
- Filtering/constraints: `LoadRuleFiles`, `ItemInFilter`, `ItemInFilterSafe`, `GetFilterColor`, `GetFilterColorForTab`, `TryExtractOpenCounts`, `OpenPrefixCount`, `OpenSuffixCount`.
- Tag detection: `GetItemTags`, `ComputeMaxByTagsAndRarity`.
- Rendering: `DrawItemFrame`, `DrawTabNameElementFrame`, `DrawServerItems`, `CalculateServerItemsBox`.
- Automation: `AutoPurchaseItem`, `_sinceLastPurchase`, `_purchasesPerTab`, `Tick` for hotkey toggling.

---

## Summary

- Purchase/Haggle flows share a consistent pipeline:
  1) Source up-to-date item rectangles and server contents, resilient to transient UI/memory failures.
  2) Apply rule-based filtering with optional open affix constraints, using top-to-bottom precedence for both matching and coloring.
  3) Render non-intrusive outlines on items and tabs, display unseen-item hints, and (optionally) perform rate-limited, bounds-checked auto-purchases with safe modifier handling.
- The implementation emphasizes safety and freshness, with particular attention to preventing cross-tab entity contamination.
- Recent improvements have eliminated the "ghosting" effect where items from one tab would incorrectly appear highlighted in other tabs.
- Trade-offs include limited tab highlighting functionality when using open affix filters, but this ensures stability and correct behavior.
