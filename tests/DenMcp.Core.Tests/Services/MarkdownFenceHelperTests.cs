using DenMcp.Core.Services;

namespace DenMcp.Core.Tests.Services;

public sealed class MarkdownFenceHelperTests
{
    // --- TryFence ---

    [Theory]
    [InlineData("```csharp")]
    [InlineData("```js")]
    [InlineData("```")]
    public void TryFence_BacktickFence_Detected(string line)
    {
        Assert.True(MarkdownFenceHelper.TryFence(line, out var fence, out _));
        Assert.Equal("```", fence);
    }

    [Theory]
    [InlineData("~~~")]
    [InlineData("~~~python")]
    public void TryFence_TildeFence_Detected(string line)
    {
        Assert.True(MarkdownFenceHelper.TryFence(line, out var fence, out _));
        Assert.Equal("~~~", fence);
    }

    [Theory]
    [InlineData("not a fence")]
    [InlineData("` inline `")]
    [InlineData("~~ strikethrough ~~")]
    [InlineData("")]
    [InlineData("   ")]
    public void TryFence_NonFence_ReturnsFalse(string line)
    {
        Assert.False(MarkdownFenceHelper.TryFence(line, out var fence, out var lang));
        Assert.Equal(string.Empty, fence);
        Assert.Null(lang);
    }

    [Fact]
    public void TryFence_LeadingWhitespace_Tolerated()
    {
        Assert.True(MarkdownFenceHelper.TryFence("   ```sh", out var fence, out var lang));
        Assert.Equal("```", fence);
        Assert.Equal("sh", lang);
    }

    [Fact]
    public void TryFence_LanguageExtracted()
    {
        Assert.True(MarkdownFenceHelper.TryFence("```csharp", out _, out var lang));
        Assert.Equal("csharp", lang);
    }

    [Fact]
    public void TryFence_NoLanguage_ReturnsNull()
    {
        Assert.True(MarkdownFenceHelper.TryFence("```", out _, out var lang));
        Assert.Null(lang);
    }

    [Fact]
    public void TryFence_WhitespaceAfterLanguage_Trimmed()
    {
        Assert.True(MarkdownFenceHelper.TryFence("```js  ", out _, out var lang));
        Assert.Equal("js", lang);
    }

    // --- ExtractFencedContent ---

    [Fact]
    public void ExtractFencedContent_BacktickFence_StripsFences()
    {
        var raw = "```csharp\nvar x = 42;\nConsole.WriteLine(x);\n```";
        var result = MarkdownFenceHelper.ExtractFencedContent(raw);
        Assert.Equal("var x = 42;\nConsole.WriteLine(x);", result);
        Assert.DoesNotContain("```", result);
    }

    [Fact]
    public void ExtractFencedContent_TildeFence_StripsFences()
    {
        var raw = "~~~\nplain code\n~~~";
        var result = MarkdownFenceHelper.ExtractFencedContent(raw);
        Assert.Equal("plain code", result);
    }

    [Fact]
    public void ExtractFencedContent_NoFence_ReturnsTrimmed()
    {
        var raw = "just code, no fences";
        var result = MarkdownFenceHelper.ExtractFencedContent(raw);
        Assert.Equal("just code, no fences", result);
    }

    [Fact]
    public void ExtractFencedContent_CRLF_Normalized()
    {
        var raw = "```js\r\nconsole.log('hi');\r\n```";
        var result = MarkdownFenceHelper.ExtractFencedContent(raw);
        Assert.Equal("console.log('hi');", result);
    }

    [Fact]
    public void ExtractFencedContent_NoClosingFence_ReturnsAllAfterOpening()
    {
        var raw = "```js\nvar x = 1;";
        var result = MarkdownFenceHelper.ExtractFencedContent(raw);
        Assert.Equal("var x = 1;", result);
    }

    [Fact]
    public void ExtractFencedContent_EmptyContent()
    {
        var raw = "```\n```";
        var result = MarkdownFenceHelper.ExtractFencedContent(raw);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void ExtractFencedContent_MultilinePreserved()
    {
        var raw = "```python\ndef foo():\n    pass\n\nbar()\n```";
        var result = MarkdownFenceHelper.ExtractFencedContent(raw);
        Assert.Equal("def foo():\n    pass\n\nbar()", result);
    }
}
