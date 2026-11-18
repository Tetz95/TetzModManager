using System.Text.RegularExpressions;

namespace TetzModManager;

/// <summary>Compares mod versions as authors write them: "2.5.2", "0.6.0c", "1.1", "rr2026-v1.4".</summary>
public static partial class Versions
{
	[GeneratedRegex(@"\d+|[A-Za-z]+")]
	private static partial Regex Token();

	public static int Compare(string? a, string? b)
	{
		List<string> x = Token().Matches(a ?? "").Select(m => m.Value).ToList();
		List<string> y = Token().Matches(b ?? "").Select(m => m.Value).ToList();
		for (int i = 0; i < Math.Max(x.Count, y.Count); i++)
		{
			if (i >= x.Count)
			{
				return -1;
			}
			if (i >= y.Count)
			{
				return 1;
			}
			bool nx = char.IsDigit(x[i][0]), ny = char.IsDigit(y[i][0]);
			int c = nx && ny
				? decimal.Parse(x[i]).CompareTo(decimal.Parse(y[i]))
				: nx != ny ? (nx ? 1 : -1)
				: string.Compare(x[i], y[i], StringComparison.OrdinalIgnoreCase);
			if (c != 0)
			{
				return c;
			}
		}
		return 0;
	}

	public static bool Same(string? a, string? b) => Compare(a, b) == 0;
}
