using DenMcp.Core.Llm;
using DenMcp.Core.Models;

namespace DenMcp.Core.Tests.Llm;

public class LibrarianParsingTests
{
    [Fact]
    public void ParseResponse_CleanJson()
    {
        var json = """
            {
              "relevant_items": [
                {
                  "type": "document",
                  "source_id": "den-mcp/fts-design",
                  "project_id": "den-mcp",
                  "summary": "FTS5 design spec",
                  "why_relevant": "Covers the search feature",
                  "snippet": "Uses porter stemmer"
                }
              ],
              "recommendations": ["Read the spec first"],
              "confidence": "high"
            }
            """;

        var result = LibrarianService.ParseResponse(json);

        Assert.Equal(LibrarianConfidence.High, result.Confidence);
        Assert.Single(result.RelevantItems);
        Assert.Equal("document", result.RelevantItems[0].Type);
        Assert.Equal("den-mcp/fts-design", result.RelevantItems[0].SourceId);
        Assert.Equal("den-mcp", result.RelevantItems[0].ProjectId);
        Assert.Equal("Uses porter stemmer", result.RelevantItems[0].Snippet);
        Assert.Single(result.Recommendations);
        Assert.Equal("Read the spec first", result.Recommendations[0]);
    }

    [Fact]
    public void ParseResponse_MarkdownCodeFences()
    {
        var json = """
            Here's what I found:
            ```json
            {
              "relevant_items": [
                {
                  "type": "task",
                  "source_id": "#47",
                  "project_id": "den-mcp",
                  "summary": "FTS implementation",
                  "why_relevant": "Direct dependency"
                }
              ],
              "recommendations": [],
              "confidence": "medium"
            }
            ```
            """;

        var result = LibrarianService.ParseResponse(json);

        Assert.Equal(LibrarianConfidence.Medium, result.Confidence);
        Assert.Single(result.RelevantItems);
        Assert.Equal("#47", result.RelevantItems[0].SourceId);
    }

    [Fact]
    public void ParseResponse_JsonEmbeddedInProse()
    {
        var json = """
            Based on my analysis, here are the results:

            {"relevant_items": [{"type": "message", "source_id": "msg#123", "project_id": "den-mcp", "summary": "Review feedback", "why_relevant": "Contains fix instructions"}], "recommendations": ["Check the review"], "confidence": "high"}

            I hope this helps!
            """;

        var result = LibrarianService.ParseResponse(json);

        Assert.Equal(LibrarianConfidence.High, result.Confidence);
        Assert.Single(result.RelevantItems);
        Assert.Equal("msg#123", result.RelevantItems[0].SourceId);
    }

    [Fact]
    public void ParseResponse_MalformedJson_FallsBackToRawText()
    {
        var raw = "I couldn't parse the context properly, but here's what I found: there's a relevant spec about FTS.";

        var result = LibrarianService.ParseResponse(raw);

        Assert.Equal(LibrarianConfidence.Low, result.Confidence);
        Assert.Empty(result.RelevantItems);
        Assert.Single(result.Recommendations);
        Assert.Equal(raw, result.Recommendations[0]);
    }

    [Fact]
    public void ParseResponse_EmptyString_ReturnsEmpty()
    {
        var result = LibrarianService.ParseResponse("");

        Assert.Equal(LibrarianConfidence.Low, result.Confidence);
        Assert.Empty(result.RelevantItems);
        Assert.Empty(result.Recommendations);
    }

    [Fact]
    public void ParseResponse_NullFields_DefaultToEmptyLists()
    {
        var json = """{"confidence": "low"}""";

        var result = LibrarianService.ParseResponse(json);

        Assert.Equal(LibrarianConfidence.Low, result.Confidence);
        Assert.NotNull(result.RelevantItems);
        Assert.NotNull(result.Recommendations);
    }

    [Fact]
    public void ParseResponse_MissingConfidence_DefaultsToLow()
    {
        var json = """
            {
              "relevant_items": [],
              "recommendations": ["follow up on the spec"]
            }
            """;

        var result = LibrarianService.ParseResponse(json);

        Assert.Equal(LibrarianConfidence.Low, result.Confidence);
        Assert.Single(result.Recommendations);
    }

    [Fact]
    public void ParseResponse_MissingSnippet_IsNull()
    {
        var json = """
            {
              "relevant_items": [
                {
                  "type": "task",
                  "source_id": "#10",
                  "summary": "Some task",
                  "why_relevant": "It's related"
                }
              ],
              "recommendations": [],
              "confidence": "medium"
            }
            """;

        var result = LibrarianService.ParseResponse(json);

        Assert.Single(result.RelevantItems);
        Assert.Null(result.RelevantItems[0].Snippet);
        Assert.Null(result.RelevantItems[0].ProjectId);
    }

    [Fact]
    public void StripCodeFences_RemovesFences()
    {
        var input = "```json\n{\"key\": \"value\"}\n```";
        Assert.Equal("{\"key\": \"value\"}", LibrarianService.StripCodeFences(input));
    }

    [Fact]
    public void StripCodeFences_NoFences_ReturnsOriginal()
    {
        var input = "{\"key\": \"value\"}";
        Assert.Equal(input, LibrarianService.StripCodeFences(input));
    }

    [Fact]
    public void ExtractJsonObject_FindsBalancedBraces()
    {
        var input = "Some text before {\"nested\": {\"deep\": true}} and after";
        var result = LibrarianService.ExtractJsonObject(input);
        Assert.Equal("{\"nested\": {\"deep\": true}}", result);
    }

    [Fact]
    public void ExtractJsonObject_NoBraces_ReturnsNull()
    {
        Assert.Null(LibrarianService.ExtractJsonObject("no json here"));
    }

    [Fact]
    public void ExtractJsonObject_UnbalancedBraces_ReturnsNull()
    {
        Assert.Null(LibrarianService.ExtractJsonObject("{unbalanced"));
    }

    [Fact]
    public void BuildSystemPrompt_IncludesGatheredContext()
    {
        var prompt = LibrarianService.BuildSystemPrompt("## Task Context\n### Task #1: Test");
        Assert.Contains("## Task Context", prompt);
        Assert.Contains("### Task #1: Test", prompt);
        Assert.Contains("relevant_items", prompt);
        Assert.Contains("Do NOT invent", prompt);
    }
}
