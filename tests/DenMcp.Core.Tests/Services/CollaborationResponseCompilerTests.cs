using DenMcp.Core.Models;
using DenMcp.Core.Services;

namespace DenMcp.Core.Tests.Services;

public sealed class CollaborationResponseCompilerTests
{
    private static CollaborationSegment MakeSegment(long id, int seq, CollaborationSegmentType type, string raw, string? text = null)
    {
        return new CollaborationSegment
        {
            Id = id,
            TurnId = 1,
            SequenceNumber = seq,
            SegmentType = type,
            SegmentHash = "hash-" + id,
            RawMarkdown = raw,
            Text = text ?? raw.Trim(),
            CreatedAt = DateTime.UtcNow
        };
    }

    private static CollaborationAnnotation MakeAnnotation(long segId, CollaborationAnnotationType type, string? body = null)
    {
        return new CollaborationAnnotation
        {
            Id = segId * 100,
            SessionId = 1,
            TurnId = 1,
            SegmentId = segId,
            SegmentHash = "hash-" + segId,
            AnnotationType = type,
            Body = body,
            CreatedBy = "operator",
            Revision = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private static readonly IReadOnlyList<CollaborationSegment> SampleSegments = new List<CollaborationSegment>
    {
        MakeSegment(1, 1, CollaborationSegmentType.Heading, "# Plan", "Plan"),
        MakeSegment(2, 2, CollaborationSegmentType.Paragraph, "First paragraph of the plan with enough text to exercise snippet truncation if needed."),
        MakeSegment(3, 3, CollaborationSegmentType.List, "- item A\n- item B"),
        MakeSegment(4, 4, CollaborationSegmentType.BlockQuote, "> quoted insight"),
        MakeSegment(5, 5, CollaborationSegmentType.CodeBlock, "```js\nconsole.log('hello');\n```", "console.log('hello');"),
    };

    // --- Footer behavior ---

    [Fact]
    public void Compile_NoAnnotations_ReturnsFullAcknowledgementFooter()
    {
        var result = CollaborationResponseCompiler.Compile(SampleSegments, []);

        Assert.Contains("[no annotations — acknowledged in full, proceed]", result);
    }

    [Fact]
    public void Compile_PartialAnnotations_IncludesUnannotatedCountFooter()
    {
        var annotations = new List<CollaborationAnnotation>
        {
            MakeAnnotation(1, CollaborationAnnotationType.Note, "clarify")
        };

        var result = CollaborationResponseCompiler.Compile(SampleSegments, annotations);

        Assert.Contains("---", result);
        Assert.Contains("4 section(s) not annotated — treat as acknowledged, proceed with flagged items", result);
    }

    [Fact]
    public void Compile_AllSegmentsAnnotated_OmitsFooter()
    {
        var annotations = SampleSegments.Select(s => MakeAnnotation(s.Id, CollaborationAnnotationType.Note, "ok")).ToList();
        var result = CollaborationResponseCompiler.Compile(SampleSegments, annotations);

        Assert.DoesNotContain("not annotated", result);
        Assert.DoesNotContain("acknowledged in full", result);
        // But should still contain the annotations themselves
        Assert.Contains("[note]", result);
    }

    // --- Annotation types ---

    [Fact]
    public void Compile_SkipAnnotation_EmitsSkipFormat()
    {
        var annotations = new List<CollaborationAnnotation>
        {
            MakeAnnotation(2, CollaborationAnnotationType.Skip)
        };

        var result = CollaborationResponseCompiler.Compile(SampleSegments, annotations);

        Assert.Contains("[skip — no response needed]", result);
        Assert.DoesNotContain("note", result);
    }

    [Fact]
    public void Compile_DoneAnnotation_EmitsDoneFormat()
    {
        var annotations = new List<CollaborationAnnotation>
        {
            MakeAnnotation(3, CollaborationAnnotationType.Done, "already implemented")
        };

        var result = CollaborationResponseCompiler.Compile(SampleSegments, annotations);

        Assert.Contains("[done — already handled]: already implemented", result);
    }

    [Fact]
    public void Compile_DoneWithoutBody_EmitsDoneOnly()
    {
        var annotations = new List<CollaborationAnnotation>
        {
            MakeAnnotation(2, CollaborationAnnotationType.Done)
        };

        var result = CollaborationResponseCompiler.Compile(SampleSegments, annotations);

        Assert.Contains("[done — already handled]", result);
    }

    [Fact]
    public void Compile_NoteAnnotation_EmitsNoteFormat()
    {
        var annotations = new List<CollaborationAnnotation>
        {
            MakeAnnotation(2, CollaborationAnnotationType.Note, "please add error handling")
        };

        var result = CollaborationResponseCompiler.Compile(SampleSegments, annotations);

        Assert.Contains("[note]: please add error handling", result);
    }

    [Fact]
    public void Compile_NoteWithoutBody_EmitsNoteAcknowledgement()
    {
        var annotations = new List<CollaborationAnnotation>
        {
            MakeAnnotation(2, CollaborationAnnotationType.Note)
        };

        var result = CollaborationResponseCompiler.Compile(SampleSegments, annotations);

        Assert.Contains("[note]: acknowledged", result);
    }

    [Fact]
    public void Compile_FlagAnnotation_EmitsFlagFormat()
    {
        var annotations = new List<CollaborationAnnotation>
        {
            MakeAnnotation(4, CollaborationAnnotationType.Flag, "need to discuss approach")
        };

        var result = CollaborationResponseCompiler.Compile(SampleSegments, annotations);

        Assert.Contains("[FLAG]: need to discuss approach", result);
    }

    [Fact]
    public void Compile_FlagWithoutBody_UsesDefaultText()
    {
        var annotations = new List<CollaborationAnnotation>
        {
            MakeAnnotation(4, CollaborationAnnotationType.Flag)
        };

        var result = CollaborationResponseCompiler.Compile(SampleSegments, annotations);

        Assert.Contains("[FLAG]: needs discussion", result);
    }

    // --- Snippet format ---

    [Fact]
    public void Compile_CodeBlockSnippet_UsesCodeBlockPrefix()
    {
        var annotations = new List<CollaborationAnnotation>
        {
            MakeAnnotation(5, CollaborationAnnotationType.Note, "optimize this")
        };

        var result = CollaborationResponseCompiler.Compile(SampleSegments, annotations);

        // Should prefix with [code block: ...]
        Assert.Contains("[code block: console.log('hello');]", result);
    }

    [Fact]
    public void Compile_CodeBlockSnippet_StripsStoredFenceTextForUsefulPreview()
    {
        var raw = "```js\nconsole.log('legacy text included fences');\n```";
        var segments = new List<CollaborationSegment>
        {
            MakeSegment(1, 1, CollaborationSegmentType.CodeBlock, raw)
        };
        var annotations = new List<CollaborationAnnotation>
        {
            MakeAnnotation(1, CollaborationAnnotationType.Note, "optimize this")
        };

        var result = CollaborationResponseCompiler.Compile(segments, annotations);

        Assert.Contains("[code block: console.log('legacy text included fences');]", result);
        Assert.DoesNotContain("[code block: ```js]", result);
    }

    [Fact]
    public void Compile_HeadingSnippet_ShowsText()
    {
        var annotations = new List<CollaborationAnnotation>
        {
            MakeAnnotation(1, CollaborationAnnotationType.Note, "good plan")
        };

        var result = CollaborationResponseCompiler.Compile(SampleSegments, annotations);

        Assert.Contains("Plan", result); // heading text is "Plan"
    }

    // --- Multiple annotations on same segment ---

    [Fact]
    public void Compile_MultipleAnnotationsOnSameSegment_ListsAll()
    {
        var annotations = new List<CollaborationAnnotation>
        {
            MakeAnnotation(2, CollaborationAnnotationType.Note, "first note"),
            MakeAnnotation(2, CollaborationAnnotationType.Flag, "also flagging")
        };

        var result = CollaborationResponseCompiler.Compile(SampleSegments, annotations);

        var lines = result.Split('\n');
        var firstSnippet = lines[0]; // > snippet
        Assert.StartsWith("> ", firstSnippet);
        Assert.Contains("  [note]: first note", lines);
        Assert.Contains("  [FLAG]: also flagging", lines);
    }

    // --- Segment reference in snippet ---

    [Fact]
    public void Compile_SnippetIncludesSegmentContent()
    {
        var md = "Short segment.";
        var segments = new List<CollaborationSegment>
        {
            MakeSegment(1, 1, CollaborationSegmentType.Paragraph, md)
        };
        var annotations = new List<CollaborationAnnotation>
        {
            MakeAnnotation(1, CollaborationAnnotationType.Note, "noted")
        };

        var result = CollaborationResponseCompiler.Compile(segments, annotations);

        Assert.Contains("Short segment.", result);
    }

    [Fact]
    public void Compile_SnippetTruncatesLongText()
    {
        var longText = new string('x', 200);
        var segments = new List<CollaborationSegment>
        {
            MakeSegment(1, 1, CollaborationSegmentType.Paragraph, longText)
        };
        var annotations = new List<CollaborationAnnotation>
        {
            MakeAnnotation(1, CollaborationAnnotationType.Note, "too long")
        };

        var result = CollaborationResponseCompiler.Compile(segments, annotations);

        // Snippet should be truncated to 80 chars + "..." after the stored segment reference.
        Assert.Contains(new string('x', 80) + "...", result);
    }

    [Fact]
    public void Compile_SnippetIncludesStoredSegmentReference()
    {
        var segments = new List<CollaborationSegment>
        {
            MakeSegment(42, 7, CollaborationSegmentType.Paragraph, "Referenced segment")
        };
        segments[0].SegmentHash = "0123456789abcdef";
        var annotations = new List<CollaborationAnnotation>
        {
            MakeAnnotation(42, CollaborationAnnotationType.Note, "track this exact block")
        };

        var result = CollaborationResponseCompiler.Compile(segments, annotations);

        Assert.Contains("> [segment 7 · 01234567] Referenced segment", result);
    }

    // --- Error handling ---

    [Fact]
    public void Compile_NullSegments_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CollaborationResponseCompiler.Compile(null!, []));
    }

    [Fact]
    public void Compile_NullAnnotations_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CollaborationResponseCompiler.Compile([], null!));
    }

    // --- Full integration example ---

    [Fact]
    public void Compile_IntegrationExample_ProducesExpectedFormat()
    {
        var segments = new List<CollaborationSegment>
        {
            MakeSegment(1, 1, CollaborationSegmentType.Heading, "# Status Update", "Status Update"),
            MakeSegment(2, 2, CollaborationSegmentType.Paragraph, "The migration is complete."),
            MakeSegment(3, 3, CollaborationSegmentType.Paragraph, "Tests pass and the deployment is green."),
        };

        var annotations = new List<CollaborationAnnotation>
        {
            MakeAnnotation(1, CollaborationAnnotationType.Skip),
            MakeAnnotation(2, CollaborationAnnotationType.Done, "verified in CI"),
        };

        var result = CollaborationResponseCompiler.Compile(segments, annotations);

        var expectedLines = new[]
        {
            "> [segment 1 · hash-1] Status Update",
            "  [skip — no response needed]",
            "",
            "> [segment 2 · hash-2] The migration is complete.",
            "  [done — already handled]: verified in CI",
            "",
            "---",
            "[1 section(s) not annotated — treat as acknowledged, proceed with flagged items]"
        };

        Assert.Equal(string.Join('\n', expectedLines), result);
    }

    [Fact]
    public void Compile_NoAnnotations_IntegrationFooter()
    {
        var segments = new List<CollaborationSegment>
        {
            MakeSegment(1, 1, CollaborationSegmentType.Paragraph, "All done."),
        };

        var result = CollaborationResponseCompiler.Compile(segments, []);

        Assert.Equal("[no annotations — acknowledged in full, proceed]", result);
    }
}
