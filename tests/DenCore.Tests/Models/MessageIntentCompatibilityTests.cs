using System.Text.Json;
using DenCore.Models;

namespace DenCore.Tests;

public class MessageIntentCompatibilityTests
{
    [Fact]
    public void TryParseMessageIntent_CanonicalValue_ReturnsTrue()
    {
        Assert.True(EnumExtensions.TryParseMessageIntent("status_update", out var intent));
        Assert.Equal(MessageIntent.StatusUpdate, intent);
    }

    [Fact]
    public void TryParseMessageIntent_UnknownValue_ReturnsFalse()
    {
        Assert.False(EnumExtensions.TryParseMessageIntent("planning_update", out _));
    }

    [Fact]
    public void TryParseMessageIntent_NullOrEmpty_ReturnsFalse()
    {
        Assert.False(EnumExtensions.TryParseMessageIntent(null!, out _));
        Assert.False(EnumExtensions.TryParseMessageIntent("", out _));
        Assert.False(EnumExtensions.TryParseMessageIntent("   ", out _));
    }

    [Fact]
    public void ResolveWriteIntent_NoExplicitIntent_NoMetadata_ReturnsGeneral()
    {
        var result = MessageIntentCompatibility.ResolveWriteIntent(null, null);
        Assert.Equal(MessageIntent.General, result);
    }

    [Fact]
    public void ResolveWriteIntent_NoExplicitIntent_WithLegacyType_DerivesFromLegacyType()
    {
        var metadata = JsonSerializer.Deserialize<JsonElement>(
            """{"type":"review_request"}""");

        var result = MessageIntentCompatibility.ResolveWriteIntent(null, metadata);
        Assert.Equal(MessageIntent.ReviewRequest, result);
    }

    [Fact]
    public void ResolveWriteIntent_CanonicalIntent_NoMetadata_ReturnsIntent()
    {
        var result = MessageIntentCompatibility.ResolveWriteIntent(MessageIntent.StatusUpdate, null);
        Assert.Equal(MessageIntent.StatusUpdate, result);
    }

    [Fact]
    public void ResolveWriteIntent_CanonicalIntent_ConflictingLegacyType_Throws()
    {
        var metadata = JsonSerializer.Deserialize<JsonElement>(
            """{"type":"review_feedback"}""");

        var ex = Assert.Throws<InvalidOperationException>(
            () => MessageIntentCompatibility.ResolveWriteIntent(MessageIntent.ReviewRequest, metadata));
        Assert.Contains("conflicts", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveWriteIntent_RequestedIntentInMetadata_WithLegacyType_DerivesFromLegacyType()
    {
        var metadata = JsonSerializer.Deserialize<JsonElement>(
            """{"type":"review_request","requested_intent":"planning_update"}""");

        var result = MessageIntentCompatibility.ResolveWriteIntent(null, metadata);
        Assert.Equal(MessageIntent.ReviewRequest, result);
    }

    [Fact]
    public void ResolveWriteIntent_RequestedIntentInMetadata_WithoutLegacyType_ReturnsGeneral()
    {
        var metadata = JsonSerializer.Deserialize<JsonElement>(
            """{"requested_intent":"bored_agent"}""");

        var result = MessageIntentCompatibility.ResolveWriteIntent(null, metadata);
        Assert.Equal(MessageIntent.General, result);
    }

    [Fact]
    public void ResolveWriteIntent_RequestedIntentInMetadata_WithUnknownLegacyType_ReturnsGeneral()
    {
        var metadata = JsonSerializer.Deserialize<JsonElement>(
            """{"type":"bogus_type","requested_intent":"planning_update"}""");

        var result = MessageIntentCompatibility.ResolveWriteIntent(null, metadata);
        Assert.Equal(MessageIntent.General, result);
    }
}
