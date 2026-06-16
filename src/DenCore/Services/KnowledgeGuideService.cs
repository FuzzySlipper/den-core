using System.Text.RegularExpressions;
using DenCore.Data;
using DenCore.Llm;
using DenCore.Models;

namespace DenCore.Services;

/// <summary>
/// Pluggable provider for answer-card generation.
/// Retrieval, status filtering, tag gating, and citation source selection
/// are handled by KnowledgeGuideService before calling the provider.
/// </summary>
public interface IKnowledgeGuideProvider
{
    /// <summary>
    /// Given ranked search results and the original question, produce a bounded
    /// answer card with citations. Must preserve citation correctness.
    /// </summary>
    KnowledgeGuideResponse Build(KnowledgeGuideQuery query, List<KnowledgeSearchResult> candidates);
}

/// <summary>
/// Default extractive provider: assembles an answer card from cited excerpts only.
/// No LLM calls — each claim is backed by a citation excerpt.
/// </summary>
public sealed class ExtractiveKnowledgeGuideProvider : IKnowledgeGuideProvider
{
    private const int DefaultContextBudget = 1600;
    private const int MaxTopEntries = 5;
    private const int MaxExcerptCharsPerEntry = 400;

    public KnowledgeGuideResponse Build(KnowledgeGuideQuery query, List<KnowledgeSearchResult> candidates)
    {
        var budget = query.ContextBudget ?? DefaultContextBudget;
        var top = candidates.Take(MaxTopEntries).ToList();
        var citations = new List<KnowledgeGuideCitation>();
        var uncertainty = new List<string>();
        var usedChars = 0;

        if (top.Count == 0)
        {
            uncertainty.Add("No reviewed knowledge entries matched your question.");
            uncertainty.Add("Try include_unreviewed=true or broader search terms.");
            return new KnowledgeGuideResponse
            {
                Answer = "I could not find any relevant curated knowledge entries.",
                Citations = [],
                WhatToReadNext = [],
                Uncertainty = uncertainty,
                BudgetUsed = 0
            };
        }

        foreach (var entry in top)
        {
            if (usedChars >= budget)
                break;

            // Extract best excerpt: use snippet first, then summary, then first paragraph
            var excerpt = ExtractBestExcerpt(entry, query.Question);
            if (string.IsNullOrWhiteSpace(excerpt))
                continue;

            // Trim to budget
            if (usedChars + excerpt.Length > budget)
                excerpt = excerpt[..Math.Min(excerpt.Length, budget - usedChars)];

            citations.Add(new KnowledgeGuideCitation
            {
                Slug = entry.Slug,
                Title = entry.Title,
                Excerpt = excerpt,
                SourceRefs = entry.SourceRefs
            });

            usedChars += excerpt.Length;
        }

        // Build answer from bullet-pointed excerpts
        var answerParts = citations
            .Select(c => $"- **{c.Title}**: {c.Excerpt}")
            .ToList();

        var answer = answerParts.Count > 0
            ? string.Join("\n\n", answerParts)
            : "I found relevant entries but could not extract specific excerpts. Try a more specific question.";

        // Build "what to read next" from non-cited candidates
        var citedSlugs = citations.Select(c => c.Slug).ToHashSet();
        var nextReads = top
            .Where(e => !citedSlugs.Contains(e.Slug))
            .Select(e => new KnowledgeNextRead
            {
                Slug = e.Slug,
                Reason = $"Related: {e.Summary ?? e.Title}"
            })
            .ToList();

        return new KnowledgeGuideResponse
        {
            Answer = answer,
            Citations = citations,
            WhatToReadNext = nextReads,
            Uncertainty = uncertainty,
            BudgetUsed = usedChars
        };
    }

    private static string ExtractBestExcerpt(KnowledgeSearchResult entry, string question)
    {
        // Prefer the FTS snippet if it's substantive
        if (entry.Snippet is { Length: > 20 } snippet
            && !snippet.StartsWith("...<b>...")  // Not a useless stub
            && !snippet.All(c => c is '.' or '<' or '>' or '/' or 'b'))
        {
            return snippet;
        }

        // Fall back to summary
        if (entry.Summary is { Length: > 10 })
            return entry.Summary;

        return entry.Title;
    }
}

/// <summary>
/// Orchestrates guided knowledge retrieval: builds a search query from the
/// natural-language question, retrieves candidates via KnowledgeRepository,
/// applies status/tag gates, then delegates answer-card assembly to the
/// configured IKnowledgeGuideProvider.
/// </summary>
public sealed class KnowledgeGuideService
{
    private readonly IKnowledgeRepository _repo;
    private readonly IKnowledgeGuideProvider _provider;

    public KnowledgeGuideService(IKnowledgeRepository repo, IKnowledgeGuideProvider provider)
    {
        _repo = repo;
        _provider = provider;
    }

    public async Task<KnowledgeGuideResponse> GuideAsync(KnowledgeGuideQuery query)
    {
        // Build FTS query from natural language question
        var terms = FtsQuerySanitizer.ExtractTerms(query.Question);
        if (terms.Count == 0)
        {
            return new KnowledgeGuideResponse
            {
                Answer = "I could not extract searchable terms from your question.",
                Citations = [],
                WhatToReadNext = [],
                Uncertainty = ["No extractable terms in question."],
                BudgetUsed = 0
            };
        }

        var searchQuery = new KnowledgeSearchQuery
        {
            Query = string.Join(" OR ", terms),
            RequiredTags = query.RequiredTags,
            AnyTags = query.AnyTags,
            Audience = query.Audience,
            IncludeDeprecated = query.IncludeDeprecated,
            IncludeUnreviewed = query.IncludeUnreviewed,
            Limit = 10
        };

        var results = await _repo.SearchAsync(searchQuery);

        // Delegate answer-card assembly to the configured provider
        return _provider.Build(query, results);
    }
}
