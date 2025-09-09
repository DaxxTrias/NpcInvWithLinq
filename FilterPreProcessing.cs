using System;
using System.Text;
using System.Text.RegularExpressions;

namespace NPCInvWithLinq;

internal static class FilterPreProcessing
{
	private static readonly Regex OpenPrefixRegex = new Regex(@"OpenPrefixCount\s*\(\)\s*(==|>=|<=|>|<)\s*(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
	private static readonly Regex OpenSuffixRegex = new Regex(@"OpenSuffixCount\s*\(\)\s*(==|>=|<=|>|<)\s*(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	public static string StripComments(string expr)
	{
		if (string.IsNullOrEmpty(expr)) return string.Empty;
		var sb = new StringBuilder(expr.Length);
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

	public static string NormalizeExpression(string expr)
	{
		if (string.IsNullOrWhiteSpace(expr)) return string.Empty;
		var nl = expr.Replace("\r\n", "\n").Replace('\r', '\n');
		var lines = nl.Split('\n');
		var sb = new StringBuilder(nl.Length + 32);
		bool firstWritten = false;
		for (int i = 0; i < lines.Length; i++)
		{
			var raw = lines[i];
			var line = raw.Trim();
			if (line.Length == 0) continue;
			if (firstWritten)
			{
				bool startsWithOp = line.StartsWith("&&") || line.StartsWith("||") || line.StartsWith(")") || line.StartsWith("]") || line.StartsWith(",") || line.StartsWith("}") || line.StartsWith(".");
				char last = sb.Length > 0 ? sb[sb.Length - 1] : '\0';
				bool prevOpener = last == '(' || last == '{' || last == '[' || last == ',' || last == '&' || last == '|';
				if (!startsWithOp && !prevOpener) sb.Append(" || "); else sb.Append(' ');
			}
			sb.Append(line);
			firstWritten = true;
		}
		return sb.ToString();
	}

	public static void TryExtractOpenCounts(string expr, out string cleanedExpr, out int? minPrefixes, out int? minSuffixes)
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
}


