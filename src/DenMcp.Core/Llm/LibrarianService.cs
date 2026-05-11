using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using Microsoft.Extensions.Logging;

namespace DenMcp.Core.Llm;

public sealed partial class LibrarianService
{
    private readonly LibrarianGatherer _gatherer;
    private readonly ITaskRepository _tasks;
    private readonly ILlmClient _llm;
    private readonly LlmConfig _config;
    private readonly ILogger<LibrarianService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        PropertyNameCaseInsensitive = true
    };

    public LibrarianService(
        LibrarianGatherer gatherer,
        ITaskRepository tasks,
        ILlmClient llm,
        LlmConfig config,
        ILogger<LibrarianService> logger)
    {
        _gatherer = gatherer;
        _tasks = tasks;
        _llm = llm;
        _config = config;
        _logger = logger;
    }

    public async Task<LibrarianResponse> QueryAsync(
        string projectId,
        string query,
        int? taskId = null,
        bool includeGlobal = true,
        CancellationToken ct = default)
    {
        if (taskId is { } requestedTaskId)
        {
            var task = await _tasks.GetByIdAsync(requestedTaskId);
            if (task is null)
                throw new KeyNotFoundException($"Task {requestedTaskId} not found");

            if (!string.Equals(task.ProjectId, projectId, StringComparison.Ordinal))
                throw new InvalidOperationException($"Task {requestedTaskId} does not belong to project {projectId}");
        }

        var context = await _gatherer.GatherAsync(
            projectId,
            query,
            taskId,
            includeGlobal,
            _config.ContextTokenBudget);

        if (string.IsNullOrWhiteSpace(context.FormattedText))
        {
            _logger.LogDebug("Librarian: no context gathered for project={Project}, skipping LLM call", projectId);
            return LibrarianResponse.Empty;
        }

        _logger.LogDebug("Librarian: gathered {Tokens} estimated tokens for project={Project}", context.EstimatedTokens, projectId);

        var systemPrompt = BuildSystemPrompt(context.FormattedText);
        var rawResponse = await _llm.CompleteAsync(systemPrompt, query, ct);

        return ParseResponse(rawResponse);
    }

    // --- Prompt ---

    internal static string BuildSystemPrompt(string gatheredContext) => $$"""
        You are a librarian assistant for a software project management system. Your job is to review gathered context and identify information relevant to an agent's current work.

        ## Gathered Context

        {{gatheredContext}}

        ## Instructions

        Given the agent's query, identify the most relevant items from the context above. For each item:
        1. Identify its type: "task", "document", or "message"
        2. Include its source_id exactly as shown in the context (e.g., "#47", "den-mcp/fts-design", "msg#123")
        3. Include the project_id for documents to distinguish project-local from _global
        4. Summarize what the item contains
        5. Explain why it's relevant to the agent's query
        6. Include a supporting snippet — the specific passage that makes it relevant

        Also provide actionable recommendations based on the relevant items.

        Rate your overall confidence:
        - "high": the context clearly addresses the agent's needs
        - "medium": some relevant information found but may be incomplete
        - "low": little relevant context available

        ## Response Format

        Respond with ONLY a JSON object — no markdown fences, no explanation outside the JSON:
        {
          "relevant_items": [
            {
              "type": "task|document|message",
              "source_id": "exact ID from context",
              "project_id": "project ID if applicable",
              "summary": "what this item contains",
              "why_relevant": "why it matters for the query",
              "snippet": "specific supporting passage"
            }
          ],
          "recommendations": ["actionable suggestion 1", "..."],
          "confidence": "high|medium|low"
        }

        If nothing is relevant, return empty relevant_items with "low" confidence.
        Do NOT invent information not present in the context.
        """;

    // --- Parsing with multi-stage fallback ---

    internal static LibrarianResponse ParseResponse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return LibrarianResponse.Empty;

        // Stage 1: direct parse
        var result = TryDeserialize(raw);
        if (result is not null) return result;

        // Stage 2: strip markdown code fences
        var stripped = StripCodeFences(raw);
        if (stripped != raw)
        {
            result = TryDeserialize(stripped);
            if (result is not null) return result;
        }

        // Stage 3: extract first balanced braces
        var extracted = ExtractJsonObject(raw);
        if (extracted is not null)
        {
            result = TryDeserialize(extracted);
            if (result is not null) return result;
        }

        // Stage 4: fallback — treat raw text as a single recommendation with low confidence
        return new LibrarianResponse
        {
            RelevantItems = [],
            Recommendations = [raw.Trim()],
            Confidence = LibrarianConfidence.Low
        };
    }

    private static LibrarianResponse? TryDeserialize(string json)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<LibrarianResponse>(json, JsonOpts);
            if (dto is null) return null;

            // Ensure lists are never null
            dto.RelevantItems ??= [];
            dto.Recommendations ??= [];

            return dto;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string StripCodeFences(string text)
    {
        var match = CodeFencePattern().Match(text);
        return match.Success ? match.Groups[1].Value.Trim() : text;
    }

    internal static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return text[start..(i + 1)];
                    break;
            }
        }

        return null;
    }

    [GeneratedRegex(@"```(?:json)?\s*\n?([\s\S]*?)```", RegexOptions.Singleline)]
    private static partial Regex CodeFencePattern();
}
