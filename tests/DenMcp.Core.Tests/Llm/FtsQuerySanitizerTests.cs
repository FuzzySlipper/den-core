using DenMcp.Core.Llm;

namespace DenMcp.Core.Tests.Llm;

public class FtsQuerySanitizerTests
{
    [Fact]
    public void Sanitize_PlainTerms_ReturnsOrJoined()
    {
        var result = FtsQuerySanitizer.Sanitize("implement search feature");
        Assert.NotNull(result);
        Assert.Contains("implement", result);
        Assert.Contains("search", result);
        Assert.Contains("feature", result);
        Assert.Contains(" OR ", result);
    }

    [Fact]
    public void Sanitize_Null_ReturnsNull()
    {
        Assert.Null(FtsQuerySanitizer.Sanitize(null));
    }

    [Fact]
    public void Sanitize_Empty_ReturnsNull()
    {
        Assert.Null(FtsQuerySanitizer.Sanitize(""));
        Assert.Null(FtsQuerySanitizer.Sanitize("   "));
    }

    [Fact]
    public void Sanitize_OnlyStopWords_ReturnsNull()
    {
        Assert.Null(FtsQuerySanitizer.Sanitize("the is a to of in"));
    }

    [Fact]
    public void Sanitize_OnlyPunctuation_ReturnsNull()
    {
        Assert.Null(FtsQuerySanitizer.Sanitize("... !!! ???"));
    }

    [Fact]
    public void Sanitize_StripsFtsKeywords()
    {
        var result = FtsQuerySanitizer.Sanitize("search AND documents NOT deleted");
        Assert.NotNull(result);
        Assert.Contains("search", result);
        Assert.Contains("documents", result);
        Assert.Contains("deleted", result);
        // AND and NOT should not appear as terms
        Assert.DoesNotContain(" AND ", result);
        Assert.DoesNotContain(" NOT ", result);
    }

    [Fact]
    public void Sanitize_StripsPunctuation()
    {
        var result = FtsQuerySanitizer.Sanitize("user's \"quoted\" input (with parens) + special*chars");
        Assert.NotNull(result);
        // Should not contain any FTS-meaningful punctuation
        Assert.DoesNotContain("\"", result);
        Assert.DoesNotContain("(", result);
        Assert.DoesNotContain(")", result);
        Assert.DoesNotContain("*", result);
        Assert.DoesNotContain("+", result);
    }

    [Fact]
    public void Sanitize_DeduplicatesTerms()
    {
        var result = FtsQuerySanitizer.Sanitize("search search search");
        Assert.Equal("search", result);
    }

    [Fact]
    public void Sanitize_FiltersShortTerms()
    {
        var result = FtsQuerySanitizer.Sanitize("I x do ab long");
        Assert.NotNull(result);
        // "I" and "x" are < 2 chars, "do" is stop word, "ab" is 2 chars and kept
        Assert.Contains("ab", result);
        Assert.Contains("long", result);
    }

    [Fact]
    public void ExtractTerms_CombinesMultipleSources()
    {
        var result = FtsQuerySanitizer.BuildCombinedQuery(
            "implement FTS search",
            "Document Storage Feature",
            "core server");
        Assert.NotNull(result);
        Assert.Contains("implement", result);
        Assert.Contains("fts", result);
        Assert.Contains("document", result);
        Assert.Contains("storage", result);
        Assert.Contains("core", result);
        Assert.Contains("server", result);
    }

    [Fact]
    public void BuildCombinedQuery_AllNullSources_ReturnsNull()
    {
        Assert.Null(FtsQuerySanitizer.BuildCombinedQuery(null, null));
    }

    [Fact]
    public void Sanitize_NearKeyword_IsStripped()
    {
        var result = FtsQuerySanitizer.Sanitize("NEAR the database");
        Assert.NotNull(result);
        Assert.DoesNotContain("near", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("database", result);
    }
}
