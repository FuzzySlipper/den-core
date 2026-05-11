namespace DenMcp.Core.Services;

/// <summary>
/// Shared helper for extracting content from Markdown fenced code blocks.
/// Used by both <see cref="MarkdownBlockSegmenter"/> (segment Text extraction)
/// and <see cref="CollaborationResponseCompiler"/> (compiled response snippets)
/// to avoid duplicating fence-detection and content-stripping logic.
/// </summary>
internal static class MarkdownFenceHelper
{
    /// <summary>
    /// Detect whether a line starts with a Markdown fence prefix (``` or ~~~).
    /// </summary>
    /// <param name="line">Line to inspect (may include leading whitespace).</param>
    /// <param name="fence">The matched fence string (``` or ~~~), or <c>string.Empty</c>.</param>
    /// <param name="language">
    /// The language identifier after the fence, or <c>null</c> if absent.
    /// </param>
    /// <returns><c>true</c> if the line opens a fenced code block.</returns>
    internal static bool TryFence(string line, out string fence, out string? language)
    {
        fence = string.Empty;
        language = null;
        var trimmed = line.TrimStart();

        if (trimmed.StartsWith("```", StringComparison.Ordinal))
            fence = "```";
        else if (trimmed.StartsWith("~~~", StringComparison.Ordinal))
            fence = "~~~";
        else
            return false;

        language = trimmed[fence.Length..].Trim();
        if (language.Length == 0)
            language = null;
        return true;
    }

    /// <summary>
    /// Extract the inner content from a fenced code block, stripping the opening
    /// fence line (with optional language tag) and closing fence line.
    /// </summary>
    /// <param name="rawMarkdown">
    /// Raw markdown containing a fenced code block (e.g. "```js\ncode\n```").
    /// </param>
    /// <returns>
    /// The content between fences, with a trailing newline trim. Falls back to
    /// <paramref name="rawMarkdown"/> trimmed if no fence is detected.
    /// </returns>
    internal static string ExtractFencedContent(string rawMarkdown)
    {
        var normalized = rawMarkdown.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        if (lines.Length == 0 || !TryFence(lines[0], out var fence, out _))
            return rawMarkdown.Trim();

        var end = lines.Length;
        if (end > 1 && lines[^1].TrimStart().StartsWith(fence, StringComparison.Ordinal))
            end--;

        return string.Join('\n', lines[1..end]).Trim('\n');
    }
}
