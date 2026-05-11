using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DenMcp.Core.Models;

namespace DenMcp.Core.Services;

/// <summary>
/// Segments raw markdown into block-level annotatable units: headings, paragraphs,
/// code blocks, lists, and block quotes.
///
/// Each segment carries a deterministic <see cref="CollaborationSegment.SegmentHash"/>
/// computed from <c>segmenterVersion \n sequenceNumber \n type \n rawMarkdown</c> so
/// that identical source markdown and segmenter version always produce the same hash,
/// and version bumps produce different hashes. This follows the immutable source
/// snapshot semantics introduced in task #916.
/// </summary>
public static class MarkdownBlockSegmenter
{
    /// <summary>
    /// Current segmenter version identifier. Bump this when segmentation logic changes
    /// so that new turns use a different hash domain while existing stored segments
    /// and their annotations remain valid.
    /// </summary>
    public const string DefaultSegmenterVersion = "den-block-v1";

    /// <summary>
    /// Segment the given markdown into annotatable block-level units.
    /// </summary>
    /// <param name="markdown">Raw markdown text to segment.</param>
    /// <param name="segmenterVersion">
    /// Version identifier used in hash computation. Pass <see cref="DefaultSegmenterVersion"/>
    /// for current segmentation, or a custom value for compatibility scenarios.
    /// </param>
    /// <returns>List of segments in document order.</returns>
    public static List<CollaborationSegment> Segment(string markdown, string segmenterVersion)
    {
        if (markdown is null)
            throw new ArgumentNullException(nameof(markdown));
        if (string.IsNullOrWhiteSpace(segmenterVersion))
            throw new ArgumentException("Segmenter version is required.", nameof(segmenterVersion));

        var normalized = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var segments = new List<CollaborationSegment>();
        var i = 0;
        while (i < lines.Length)
        {
            while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i]))
                i++;
            if (i >= lines.Length)
                break;

            var start = i;
            var first = lines[i];
            var trimmedStart = first.TrimStart();
            CollaborationSegmentType type;
            int? headingLevel = null;
            string? codeLanguage = null;

            if (MarkdownFenceHelper.TryFence(trimmedStart, out var fence, out codeLanguage))
            {
                type = CollaborationSegmentType.CodeBlock;
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith(fence, StringComparison.Ordinal))
                    i++;
                if (i < lines.Length)
                    i++;
            }
            else if (TryHeading(trimmedStart, out var level))
            {
                type = CollaborationSegmentType.Heading;
                headingLevel = level;
                i++;
            }
            else if (IsBlockQuote(trimmedStart))
            {
                type = CollaborationSegmentType.BlockQuote;
                i++;
                while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && IsBlockQuote(lines[i].TrimStart()))
                    i++;
            }
            else if (IsListItem(trimmedStart))
            {
                type = CollaborationSegmentType.List;
                i++;
                while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) &&
                       (IsListItem(lines[i].TrimStart()) || char.IsWhiteSpace(lines[i][0])))
                    i++;
            }
            else
            {
                type = CollaborationSegmentType.Paragraph;
                i++;
                while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && !StartsBlock(lines[i].TrimStart()))
                    i++;
            }

            var raw = string.Join('\n', lines[start..i]).Trim('\n');
            var sequence = segments.Count + 1;

            // IMPORTANT: Hash format must remain stable for deterministic segment
            // identity. Changing it would break annotations anchored to existing
            // segment hashes from earlier turns.
            var hashInput = $"{segmenterVersion}\n{sequence}\n{type.ToDbValue()}\n{raw}";

            segments.Add(new CollaborationSegment
            {
                SequenceNumber = sequence,
                SegmentType = type,
                SegmentHash = ComputeSHA256(hashInput),
                RawMarkdown = raw,
                Text = ExtractText(raw, type),
                HeadingLevel = headingLevel,
                CodeLanguage = codeLanguage
            });
        }

        return segments;
    }

    /// <summary>
    /// Compute the deterministic segment hash for the given inputs, following the
    /// same algorithm used during segmentation. Useful for verifying segment identity
    /// without re-segmenting the entire document.
    /// </summary>
    public static string ComputeSegmentHash(string segmenterVersion, int sequenceNumber, CollaborationSegmentType type, string rawMarkdown)
    {
        var hashInput = $"{segmenterVersion}\n{sequenceNumber}\n{type.ToDbValue()}\n{rawMarkdown}";
        return ComputeSHA256(hashInput);
    }

    /// <summary>
    /// Compute the SHA-256 hex digest (lowercase) for a text string. Shared utility
    /// used for both segment hashes and source content hashes.
    /// </summary>
    public static string ComputeSHA256(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool StartsBlock(string line) =>
        MarkdownFenceHelper.TryFence(line, out _, out _) || TryHeading(line, out _) || IsBlockQuote(line) || IsListItem(line);

    private static bool TryHeading(string line, out int level)
    {
        level = 0;
        var match = Regex.Match(line, "^(#{1,6})\\s+.+$");
        if (!match.Success)
            return false;
        level = match.Groups[1].Value.Length;
        return true;
    }

    private static bool IsBlockQuote(string line) => line.StartsWith('>');

    private static bool IsListItem(string line) =>
        line.StartsWith("- ", StringComparison.Ordinal) ||
        line.StartsWith("* ", StringComparison.Ordinal) ||
        line.StartsWith("+ ", StringComparison.Ordinal) ||
        Regex.IsMatch(line, "^\\d+[.)]\\s+");

    private static string ExtractText(string raw, CollaborationSegmentType type)
    {
        if (type == CollaborationSegmentType.Heading)
            return Regex.Replace(raw.Trim(), "^#{1,6}\\s+", string.Empty).Trim();
        if (type == CollaborationSegmentType.BlockQuote)
            return string.Join('\n', raw.Split('\n').Select(line => line.TrimStart().TrimStart('>').TrimStart()));
        if (type == CollaborationSegmentType.CodeBlock)
            return ExtractCodeBlockText(raw);
        return raw.Trim();
    }

    private static string ExtractCodeBlockText(string raw) => MarkdownFenceHelper.ExtractFencedContent(raw);
}
