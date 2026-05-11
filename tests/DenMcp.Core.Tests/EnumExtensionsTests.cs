using DenMcp.Core.Models;

namespace DenMcp.Core.Tests;

public class EnumExtensionsTests
{
    [Theory]
    [InlineData(AgentRecipientResolutionStatus.Resolved, "resolved")]
    [InlineData(AgentRecipientResolutionStatus.MissingBinding, "missing_binding")]
    [InlineData(AgentRecipientResolutionStatus.MissingRecipient, "missing_recipient")]
    [InlineData(AgentRecipientResolutionStatus.Ambiguous, "ambiguous")]
    public void ToApiValue_MapsAgentRecipientResolutionStatus(AgentRecipientResolutionStatus status, string expected)
    {
        Assert.Equal(expected, status.ToApiValue());
    }

    [Fact]
    public void ToApiValue_ThrowsForUnknownAgentRecipientResolutionStatus()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((AgentRecipientResolutionStatus)999).ToApiValue());
    }
}
