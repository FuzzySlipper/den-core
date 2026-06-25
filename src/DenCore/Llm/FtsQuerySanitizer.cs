using System.Text.RegularExpressions;

namespace DenCore.Llm;

/// <summary>
/// Converts natural language text into safe SQLite FTS5 queries and prepares
/// natural language input for provider-specific full-text search paths.
/// </summary>
public static partial class FtsQuerySanitizer
{
    private static readonly HashSet<string> FtsKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "AND", "OR", "NOT", "NEAR"
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "is", "are", "was", "were", "be", "been", "being",
        "have", "has", "had", "do", "does", "did", "will", "would", "could",
        "should", "may", "might", "shall", "can", "to", "of", "in", "for",
        "on", "with", "at", "by", "from", "as", "into", "through", "during",
        "before", "after", "between", "out", "up", "down", "off", "over",
        "under", "then", "once", "when", "where", "why", "how", "all",
        "each", "more", "most", "other", "some", "such", "no", "only",
        "own", "same", "so", "than", "too", "very", "just", "but", "if",
        "while", "that", "this", "it", "its", "i", "me", "my", "we", "our",
        "ve", "re", "ll", "ve", "im", "don", "doesn", "didn", "won", "isn"
    };

    /// <summary>
    /// Converts natural language text into a safe FTS5 query using OR-joined terms.
    /// Returns null if no meaningful terms remain after sanitization.
    /// </summary>
    public static string? Sanitize(string? input)
    {
        var terms = ExtractTerms(input);
        if (terms.Count == 0)
            return null;

        return string.Join(" OR ", terms);
    }

    /// <summary>
    /// Extracts meaningful search terms from natural language text.
    /// Strips FTS5 operators, punctuation, and stop words.
    /// </summary>
    public static List<string> ExtractTerms(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return [];

        // Strip characters with special meaning in FTS5 syntax
        var cleaned = FtsPunctuation().Replace(input, " ");

        return cleaned
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length >= 2)
            .Where(t => !FtsKeywords.Contains(t))
            .Where(t => !StopWords.Contains(t))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Builds a combined FTS5 query from multiple text sources (query, title, tags).
    /// Deduplicates terms across sources. Returns null if no terms remain.
    /// </summary>
    public static string? BuildCombinedQuery(params string?[] sources)
    {
        var allTerms = sources
            .SelectMany(s => ExtractTerms(s))
            .Distinct()
            .ToList();

        if (allTerms.Count == 0)
            return null;

        return string.Join(" OR ", allTerms);
    }

    /// <summary>
    /// Prepares raw user text for Postgres websearch_to_tsquery.
    /// The database parser handles phrases, operators, punctuation, and
    /// stop words; this method only rejects empty input.
    /// </summary>
    public static string? ToPostgresWebSearchQuery(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        return input.Trim();
    }

    [GeneratedRegex(@"[""*(){}+\-^~:,.;!?/\\@#$%&=\[\]<>|'`_]")]
    private static partial Regex FtsPunctuation();
}
