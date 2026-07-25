namespace InputAutomationTool.Core;

/// <summary>
/// Approximate text matching for OCR output. OCR frequently changes case and
/// misreads a character or two ("0"↔"o", "l"↔"I", "rn"↔"m"), so exact matching
/// is too brittle. This treats a needle as "found" if it appears in the haystack
/// allowing case differences and a small number of edits.
/// </summary>
public static class FuzzyMatch
{
    /// <summary>
    /// True if <paramref name="needle"/> occurs within <paramref name="haystack"/>
    /// ignoring case and allowing up to <paramref name="maxEdits"/> character errors
    /// (defaults to a length-scaled tolerance).
    /// </summary>
    public static bool Contains(string haystack, string needle, int? maxEdits = null)
    {
        if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(needle))
            return false;

        var h = Normalize(haystack);
        var n = Normalize(needle);

        if (h.Contains(n))
            return true;

        var tol = maxEdits ?? DefaultTolerance(n.Length);
        return BestSubstringDistance(h, n) <= tol;
    }

    /// <summary>Allow ~1 error per 8 characters, between 1 and 5.</summary>
    public static int DefaultTolerance(int needleLength) =>
        Math.Clamp(needleLength / 8, 1, 5);

    /// <summary>Lowercase and collapse all whitespace runs to a single space.</summary>
    private static string Normalize(string s) =>
        string.Join(' ', s.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Full Levenshtein distance between two whole strings (case-insensitive).
    /// Used to rank competing matches: the line that most closely *equals* the
    /// search text wins over a line that merely *contains* it.
    /// </summary>
    public static int FullDistance(string a, string b)
    {
        a = Normalize(a);
        b = Normalize(b);
        int m = a.Length, n = b.Length;
        if (m == 0) return n;
        if (n == 0) return m;

        var prev = new int[n + 1];
        var cur = new int[n + 1];
        for (int j = 0; j <= n; j++) prev[j] = j;

        for (int i = 1; i <= m; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= n; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[n];
    }

    /// <summary>
    /// Minimum Levenshtein distance between <paramref name="pattern"/> and any
    /// substring of <paramref name="text"/> (classic approximate substring search:
    /// the top DP row is zeroed so a match may begin at any offset for free).
    /// </summary>
    public static int BestSubstringDistance(string text, string pattern)
    {
        int m = pattern.Length, n = text.Length;
        if (m == 0) return 0;
        if (n == 0) return m;

        var prev = new int[n + 1]; // all zeros: empty pattern matches anywhere
        var cur = new int[n + 1];

        for (int i = 1; i <= m; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= n; j++)
            {
                int cost = pattern[i - 1] == text[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }

        int best = int.MaxValue;
        for (int j = 0; j <= n; j++)
            best = Math.Min(best, prev[j]);
        return best;
    }
}
