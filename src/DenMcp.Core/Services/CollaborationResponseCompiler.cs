using DenMcp.Core.Models;

namespace DenMcp.Core.Services;

/// <summary>
/// Compiles stored <see cref="CollaborationSegment"/> instances and their associated
/// <see cref="CollaborationAnnotation"/> records into a structured text response
/// suitable for sending back to an agent.
///
/// The compiled output follows the prototype format defined in the collaboration
/// annotations design (SPEC.md): each annotated segment is quoted by a snippet
/// prefix, its annotations are listed below, and a footer declares that
/// unannotated sections are acknowledged and need not be re-addressed.
///
/// This logic is usable by both Den Desktop (via the .NET bridge) and Pi tooling
/// (via direct service calls).
/// </summary>
public static class CollaborationResponseCompiler
{
    /// <summary>
    /// Compile segments and annotations into a structured response text.
    /// </summary>
    /// <param name="segments">
    /// The segments of the turn being annotated. Typically loaded from the
    /// collaboration repository.
    /// </param>
    /// <param name="annotations">
    /// The annotations for this session. Only annotations whose <c>SegmentId</c>
    /// matches a segment's <c>Id</c> are included. Callers should pre-filter if
    /// they have annotations scoped to a specific turn.
    /// </param>
    /// <returns>Compiled response text in the prototype format.</returns>
    public static string Compile(IReadOnlyList<CollaborationSegment> segments, IReadOnlyList<CollaborationAnnotation> annotations)
    {
        if (segments is null)
            throw new ArgumentNullException(nameof(segments));
        if (annotations is null)
            throw new ArgumentNullException(nameof(annotations));

        // Build a lookup from segment ID to annotations targeting that segment.
        var annotationsBySegment = annotations
            .GroupBy(a => a.SegmentId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var lines = new List<string>();
        var anyAnnotated = false;

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];

            if (!annotationsBySegment.TryGetValue(segment.Id, out var segAnnotations))
                continue;

            anyAnnotated = true;

            // Snippet: include stored sequence/hash identity plus a compact text preview.
            var snippet = BuildSnippet(segment);
            var reference = BuildSegmentReference(segment);

            lines.Add($"> {reference} {snippet}");

            foreach (var annotation in segAnnotations)
            {
                lines.Add(FormatAnnotationLine(annotation));
            }

            lines.Add(string.Empty);
        }

        // Footer: declare unannotated sections as acknowledged.
        var annotatedSegmentIds = new HashSet<long>(annotations.Select(a => a.SegmentId));
        var unannotatedCount = segments.Count(s => !annotatedSegmentIds.Contains(s.Id));

        if (!anyAnnotated)
        {
            lines.Add("[no annotations — acknowledged in full, proceed]");
        }
        else if (unannotatedCount > 0)
        {
            lines.Add("---");
            lines.Add(
                $"[{unannotatedCount} section(s) not annotated — treat as acknowledged, proceed with flagged items]");
        }
        // If all segments are annotated, no additional footer is needed beyond the
        // last annotation block.

        return string.Join('\n', lines);
    }

    private static string BuildSnippet(CollaborationSegment segment)
    {
        if (segment.SegmentType == CollaborationSegmentType.CodeBlock)
            return BuildCodeBlockSnippet(segment);

        var rawText = segment.Text ?? segment.RawMarkdown;
        var snippet = rawText.Length > 80 ? rawText[..80] + "..." : rawText;
        return snippet;
    }

    private static string BuildCodeBlockSnippet(CollaborationSegment segment)
    {
        var text = segment.Text;
        if (string.IsNullOrWhiteSpace(text) || StartsWithFence(text))
            text = ExtractFencedCodeContent(segment.RawMarkdown);

        var firstLine = text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))
            ?.Trim() ?? "(empty)";

        var truncated = firstLine.Length > 50 ? firstLine[..50] + "..." : firstLine;
        return $"[code block: {truncated}]";
    }

    private static string ExtractFencedCodeContent(string rawMarkdown) =>
        MarkdownFenceHelper.ExtractFencedContent(rawMarkdown);

    private static bool StartsWithFence(string text) =>
        MarkdownFenceHelper.TryFence(text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')[0], out _, out _);

    private static string BuildSegmentReference(CollaborationSegment segment)
    {
        var hashPrefix = segment.SegmentHash.Length >= 8 ? segment.SegmentHash[..8] : segment.SegmentHash;
        return $"[segment {segment.SequenceNumber} · {hashPrefix}]";
    }

    private static string FormatAnnotationLine(CollaborationAnnotation annotation)
    {
        var prefix = annotation.AnnotationType switch
        {
            CollaborationAnnotationType.Skip => "[skip — no response needed]",
            CollaborationAnnotationType.Done => "[done — already handled]",
            CollaborationAnnotationType.Flag => "[FLAG]",
            CollaborationAnnotationType.Note => "[note]",
            _ => $"[{annotation.AnnotationType.ToString().ToLowerInvariant()}]"
        };

        if (annotation.AnnotationType == CollaborationAnnotationType.Skip)
            return $"  {prefix}";

        if (annotation.AnnotationType == CollaborationAnnotationType.Flag)
        {
            var body = !string.IsNullOrWhiteSpace(annotation.Body) ? $": {annotation.Body.Trim()}" : ": needs discussion";
            return $"  {prefix}{body}";
        }

        // Note or Done
        if (!string.IsNullOrWhiteSpace(annotation.Body))
            return $"  {prefix}: {annotation.Body.Trim()}";

        return annotation.AnnotationType switch
        {
            CollaborationAnnotationType.Note => "  [note]: acknowledged",
            CollaborationAnnotationType.Done => "  [done — already handled]",
            _ => $"  {prefix}"
        };
    }
}
