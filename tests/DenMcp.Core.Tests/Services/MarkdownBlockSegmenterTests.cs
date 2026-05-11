using DenMcp.Core.Models;
using DenMcp.Core.Services;

namespace DenMcp.Core.Tests.Services;

public sealed class MarkdownBlockSegmenterTests
{
    private const string V1 = "den-block-v1";

    [Fact]
    public void Segment_NullMarkdown_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => MarkdownBlockSegmenter.Segment(null!, V1));
    }

    [Fact]
    public void Segment_EmptyVersion_Throws()
    {
        Assert.Throws<ArgumentException>(() => MarkdownBlockSegmenter.Segment("hello", string.Empty));
    }

    [Fact]
    public void Segment_EmptyMarkdown_ReturnsEmpty()
    {
        var result = MarkdownBlockSegmenter.Segment(string.Empty, V1);
        Assert.Empty(result);
    }

    [Fact]
    public void Segment_WhitespaceOnly_ReturnsEmpty()
    {
        var result = MarkdownBlockSegmenter.Segment("   \n\n  \n ", V1);
        Assert.Empty(result);
    }

    [Fact]
    public void Segment_SimpleParagraph_YieldsOneSegments()
    {
        var text = "This is a simple paragraph.";
        var segments = MarkdownBlockSegmenter.Segment(text, V1);

        var seg = Assert.Single(segments);
        Assert.Equal(CollaborationSegmentType.Paragraph, seg.SegmentType);
        Assert.Equal(1, seg.SequenceNumber);
        Assert.Equal(text, seg.RawMarkdown);
    }

    [Fact]
    public void Segment_Headings_CorrectlyParsed()
    {
        var md = "# H1\n\n## H2\n\n### H3";
        var segments = MarkdownBlockSegmenter.Segment(md, V1);

        Assert.Equal(3, segments.Count);
        Assert.Equal(CollaborationSegmentType.Heading, segments[0].SegmentType);
        Assert.Equal(1, segments[0].HeadingLevel);
        Assert.Equal("H1", segments[0].Text);

        Assert.Equal(CollaborationSegmentType.Heading, segments[1].SegmentType);
        Assert.Equal(2, segments[1].HeadingLevel);
        Assert.Equal("H2", segments[1].Text);

        Assert.Equal(CollaborationSegmentType.Heading, segments[2].SegmentType);
        Assert.Equal(3, segments[2].HeadingLevel);
        Assert.Equal("H3", segments[2].Text);
    }

    [Fact]
    public void Segment_CodeBlock_ParsesLanguageAndContent()
    {
        var md = """
            ```csharp
            var x = 42;
            Console.WriteLine(x);
            ```
            """;
        var segments = MarkdownBlockSegmenter.Segment(md, V1);

        var seg = Assert.Single(segments);
        Assert.Equal(CollaborationSegmentType.CodeBlock, seg.SegmentType);
        Assert.Equal("csharp", seg.CodeLanguage);
        Assert.Contains("var x = 42", seg.RawMarkdown);
        Assert.Equal("var x = 42;\nConsole.WriteLine(x);", seg.Text);
        Assert.DoesNotContain("```", seg.Text);
    }

    [Fact]
    public void Segment_TildeFencedCodeBlock_Parses()
    {
        var md = """
            ~~~
            plain code
            ~~~
            """;
        var segments = MarkdownBlockSegmenter.Segment(md, V1);

        var seg = Assert.Single(segments);
        Assert.Equal(CollaborationSegmentType.CodeBlock, seg.SegmentType);
        Assert.Null(seg.CodeLanguage);
        Assert.Contains("plain code", seg.RawMarkdown);
    }

    [Theory]
    [InlineData("-")]
    [InlineData("*")]
    [InlineData("+")]
    public void Segment_List_UnorderedPrefixes(string prefix)
    {
        var md = $"{prefix} item one\n{prefix} item two\n{prefix} item three";
        var segments = MarkdownBlockSegmenter.Segment(md, V1);

        var seg = Assert.Single(segments);
        Assert.Equal(CollaborationSegmentType.List, seg.SegmentType);
        Assert.Contains("item one", seg.RawMarkdown);
        Assert.Contains("item three", seg.RawMarkdown);
    }

    [Fact]
    public void Segment_List_Ordered()
    {
        var md = "1. first\n2. second\n3. third";
        var segments = MarkdownBlockSegmenter.Segment(md, V1);

        var seg = Assert.Single(segments);
        Assert.Equal(CollaborationSegmentType.List, seg.SegmentType);
    }

    [Fact]
    public void Segment_BlockQuote_YieldsBlockQuoteType()
    {
        var md = "> quoted line\n> another quote";
        var segments = MarkdownBlockSegmenter.Segment(md, V1);

        var seg = Assert.Single(segments);
        Assert.Equal(CollaborationSegmentType.BlockQuote, seg.SegmentType);
    }

    [Fact]
    public void Segment_MixedContent_YieldsExpectedTypes()
    {
        var md = """
            # Plan

            This is a paragraph.

            - first
            - second

            > quoted
            > reply

            ```csharp
            Console.WriteLine("hi");
            ```
            """;

        var segments = MarkdownBlockSegmenter.Segment(md, V1);
        Assert.Collection(segments,
            h => Assert.Equal(CollaborationSegmentType.Heading, h.SegmentType),
            p => Assert.Equal(CollaborationSegmentType.Paragraph, p.SegmentType),
            l => Assert.Equal(CollaborationSegmentType.List, l.SegmentType),
            q => Assert.Equal(CollaborationSegmentType.BlockQuote, q.SegmentType),
            c => Assert.Equal(CollaborationSegmentType.CodeBlock, c.SegmentType));
    }

    [Fact]
    public void Segment_DeterministicHashes_IdenticalInputYieldsSameHashes()
    {
        var md = "# Title\n\nSome body text.\n\n- a\n- b";
        var first = MarkdownBlockSegmenter.Segment(md, V1);
        var second = MarkdownBlockSegmenter.Segment(md, V1);

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].SegmentHash, second[i].SegmentHash);
            Assert.Equal(first[i].SequenceNumber, second[i].SequenceNumber);
            Assert.Equal(first[i].SegmentType, second[i].SegmentType);
            Assert.Equal(first[i].RawMarkdown, second[i].RawMarkdown);
        }
    }

    [Fact]
    public void Segment_DifferentVersion_YieldsDifferentHashes()
    {
        var md = "# Title\n\nContent.";
        var v1Segments = MarkdownBlockSegmenter.Segment(md, "den-block-v1");
        var v2Segments = MarkdownBlockSegmenter.Segment(md, "den-block-v2");

        Assert.Equal(v1Segments.Count, v2Segments.Count);
        for (var i = 0; i < v1Segments.Count; i++)
        {
            Assert.NotEqual(v1Segments[i].SegmentHash, v2Segments[i].SegmentHash);
        }
    }

    [Fact]
    public void Segment_AllHashesAre64HexChars()
    {
        var md = """
            # Title
            ## Subtitle
            Body text.

            - item
            - item

            > quote

            ```js
            console.log("test");
            ```
            """;
        var segments = MarkdownBlockSegmenter.Segment(md, V1);
        Assert.All(segments, s => Assert.Equal(64, s.SegmentHash.Length));
        Assert.All(segments, s => Assert.Matches("^[0-9a-f]{64}$", s.SegmentHash));
    }

    [Fact]
    public void Segment_AllHashesUnique()
    {
        var md = """
            # Title

            Paragraph one.

            Paragraph two.

            ```txt
            code block
            ```

            > quote
            """;
        var segments = MarkdownBlockSegmenter.Segment(md, V1);
        Assert.Equal(segments.Count, segments.Select(s => s.SegmentHash).Distinct().Count());
    }

    [Fact]
    public void ComputeSegmentHash_MatchesInlineHash()
    {
        var md = "# Title\n\nContent.";
        var segments = MarkdownBlockSegmenter.Segment(md, V1);

        foreach (var seg in segments)
        {
            var computed = MarkdownBlockSegmenter.ComputeSegmentHash(V1, seg.SequenceNumber, seg.SegmentType, seg.RawMarkdown);
            Assert.Equal(seg.SegmentHash, computed);
        }
    }

    [Fact]
    public void Segment_RawMarkdownPreservesExactContent()
    {
        var md = """
            # Heading

            Paragraph with **bold** and *italic*.

            ```json
            {"key": "value"}
            ```
            """;
        var segments = MarkdownBlockSegmenter.Segment(md, V1);

        // Heading
        Assert.Equal("# Heading", segments[0].RawMarkdown);
        // Paragraph
        Assert.Equal("Paragraph with **bold** and *italic*.", segments[1].RawMarkdown);
        // Code block - exact markdown preserved for hash identity, while Text is useful code content.
        Assert.StartsWith("```json", segments[2].RawMarkdown);
        Assert.EndsWith("```", segments[2].RawMarkdown.TrimEnd());
        Assert.Equal("{\"key\": \"value\"}", segments[2].Text);
    }

    [Fact]
    public void Segment_ListItemWithContinuation_StaysInOneSegment()
    {
        var md = "- item one\n  continuation\n- item two";
        var segments = MarkdownBlockSegmenter.Segment(md, V1);

        Assert.Single(segments);
        Assert.Equal(CollaborationSegmentType.List, segments[0].SegmentType);
        Assert.Contains("continuation", segments[0].RawMarkdown);
    }

    [Fact]
    public void Segment_NormalizesLineEndings()
    {
        var crlf = "# Heading\r\n\r\nParagraph.\r\n";
        var lf = "# Heading\n\nParagraph.\n";

        var fromCrlf = MarkdownBlockSegmenter.Segment(crlf, V1);
        var fromLf = MarkdownBlockSegmenter.Segment(lf, V1);

        // Same number of segments and identical hashes despite different line endings
        Assert.Equal(fromCrlf.Count, fromLf.Count);
        for (var i = 0; i < fromCrlf.Count; i++)
        {
            Assert.Equal(fromCrlf[i].SegmentHash, fromLf[i].SegmentHash);
            Assert.Equal(fromCrlf[i].RawMarkdown, fromLf[i].RawMarkdown);
        }
    }

    [Fact]
    public void Segment_EmptyLinesBetweenBlocks_AreSkipped()
    {
        var md = "# A\n\n\n\n# B";
        var segments = MarkdownBlockSegmenter.Segment(md, V1);

        Assert.Equal(2, segments.Count);
        Assert.Equal("A", segments[0].Text);
        Assert.Equal("B", segments[1].Text);
    }

    [Fact]
    public void Segment_BlockQuoteText_StripsPrefix()
    {
        var md = "> deep\n> > nested";
        var segments = MarkdownBlockSegmenter.Segment(md, V1);

        var seg = Assert.Single(segments);
        Assert.Equal(CollaborationSegmentType.BlockQuote, seg.SegmentType);
    }

    [Fact]
    public void ComputeSHA256_ReturnsLowercase64CharHex()
    {
        var hash = MarkdownBlockSegmenter.ComputeSHA256("hello");
        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void ComputeSHA256_Deterministic()
    {
        var a = MarkdownBlockSegmenter.ComputeSHA256("same text");
        var b = MarkdownBlockSegmenter.ComputeSHA256("same text");
        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputeSHA256_DifferentInputs_DifferentHashes()
    {
        var a = MarkdownBlockSegmenter.ComputeSHA256("text a");
        var b = MarkdownBlockSegmenter.ComputeSHA256("text b");
        Assert.NotEqual(a, b);
    }
}
