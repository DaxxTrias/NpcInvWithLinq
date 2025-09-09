using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.Elements;
using ExileCore2.PoEMemory.MemoryObjects;
using System;
using System.Collections;
using System.Collections.Generic;
using ItemFilterLibrary;

namespace NPCInvWithLinq;

internal static class ItemFilterExtensions
{
	// Minimal port of open affix counting from ItemfilterExtension with AffixType/Tags-based max fallbacks
	public static int OpenPrefixCount(ItemData item)
	{
		var max = GetMaxPrefixes(item);
		var used = GetPrefixCount(item);
		var open = max - used;
		return open > 0 ? open : 0;
	}

	public static int OpenSuffixCount(ItemData item)
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
			var explicitMods = GetPropertyValue(mods, "ExplicitMods") as IEnumerable;
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

	// Local reflection helpers (duplicated minimal copies to avoid exposing internals)
	private static bool TryGetIntProperty(object? source, out int value, params string[] names)
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

	private static bool TryGetBoolProperty(object? source, out bool value, params string[] names)
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

	private static object? GetPropertyValue(object? source, string name)
	{
		try
		{
			var prop = source?.GetType().GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase);
			return prop?.GetValue(source);
		}
		catch { return null; }
	}
}


