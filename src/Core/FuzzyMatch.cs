namespace SmartActiveTools.Core;

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

    // --- chunk matching ---------------------------------------------------

    /// <summary>Chunks shorter than this are dropped as noise.</summary>
    private const int MinChunkLength = 2;

    /// <summary>
    /// How many chunks must be found for an overall match. One is enough: the
    /// value is only ever compared against a field we just pasted into, so a
    /// single recognisable group is sufficient evidence the paste landed.
    /// </summary>
    private const int MinChunksRequired = 1;

    /// <summary>Outcome of a chunked comparison; <see cref="Matched"/> of <see cref="Total"/>.</summary>
    public readonly record struct ChunkMatch(
        int Matched, int Total, bool IsMatch, IReadOnlyList<(string Chunk, bool Found)> Chunks)
    {
        public override string ToString() => $"{Matched}/{Total} chunks";

        /// <summary>
        /// Per-chunk verdict for the log, e.g. <c>WFU5 ✗  XR8S ✗  5A43 ✓</c>.
        /// Without this a failed probe is indistinguishable from a wrong click.
        /// </summary>
        public string Detail =>
            Chunks is null or [] ? "" : string.Join("  ", Chunks.Select(c => $"{c.Chunk} {(c.Found ? "✓" : "✗")}"));
    }

    /// <summary>
    /// Compares <paramref name="needle"/> to <paramref name="haystack"/> in groups
    /// rather than as one string. Matching a whole activation key
    /// ("AESK-XNVN-XUM3-5BKC-N3EG") fails in practice: separators render
    /// inconsistently, a long needle accumulates more OCR errors than its
    /// length-scaled tolerance permits, and the value may be split across lines.
    /// Splitting on non-alphanumerics gives each group its own small error budget,
    /// so one badly-read group no longer sinks the comparison.
    /// </summary>
    public static ChunkMatch MatchChunks(string haystack, string needle)
    {
        var chunks = SplitChunks(needle);
        if (chunks.Length == 0 || string.IsNullOrWhiteSpace(haystack))
            return new ChunkMatch(0, chunks.Length, false,
                [.. chunks.Select(c => (c, false))]);

        var h = Normalize(haystack);
        var verdicts = chunks.Select(c => (Chunk: c, Found: ContainsChunk(h, Normalize(c)))).ToArray();
        var matched = verdicts.Count(v => v.Found);

        var required = Math.Min(chunks.Length, MinChunksRequired);
        return new ChunkMatch(matched, chunks.Length, matched >= required, verdicts);
    }

    private static bool ContainsChunk(string normalizedHaystack, string normalizedChunk) =>
        normalizedHaystack.Contains(normalizedChunk)
        || BestSubstringDistance(normalizedHaystack, normalizedChunk) <= ChunkTolerance(normalizedChunk.Length);

    /// <summary>
    /// Because a single chunk is enough to declare a match, a short chunk must hit
    /// exactly — a 1-edit budget on four characters collides with ordinary words
    /// ("AESK" is one edit from "desk", so "Desktop" on screen would read as a
    /// successful paste). Longer chunks are distinctive enough to stay fuzzy.
    /// </summary>
    private static int ChunkTolerance(int chunkLength) =>
        chunkLength <= ExactChunkLength ? 0 : Math.Clamp(chunkLength / 6, 1, 2);

    /// <summary>Chunks this short or shorter must match exactly.</summary>
    private const int ExactChunkLength = 5;

    /// <summary>Splits on every non-alphanumeric run, dropping groups that are too short.</summary>
    private static string[] SplitChunks(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        var parts = new List<string>();
        var buf = new System.Text.StringBuilder();

        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
                buf.Append(ch);
            else
                Flush();
        }
        Flush();
        return [.. parts];

        void Flush()
        {
            if (buf.Length >= MinChunkLength)
                parts.Add(buf.ToString());
            buf.Clear();
        }
    }

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
