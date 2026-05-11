using System.Text.Json;

namespace DenMcp.Core.Models;

public sealed class AgentInstanceBinding
{
    public required string InstanceId { get; set; }
    public required string ProjectId { get; set; }
    public required string AgentIdentity { get; set; }
    public required string AgentFamily { get; set; }
    public string? Role { get; set; }
    public required string TransportKind { get; set; }
    public string? SessionId { get; set; }
    public AgentInstanceBindingStatus Status { get; set; } = AgentInstanceBindingStatus.Active;
    public string? Metadata { get; set; }
    public DateTime CheckedInAt { get; set; }
    public DateTime LastHeartbeat { get; set; }
}

public sealed class AgentInstanceBindingListOptions
{
    public string? ProjectId { get; set; }
    public string? AgentIdentity { get; set; }
    public string? Role { get; set; }
    public string? TransportKind { get; set; }
    public AgentInstanceBindingStatus[]? Statuses { get; set; }
    public int TimeoutMinutes { get; set; } = 5;
}

public enum AgentRecipientResolutionStatus
{
    Resolved,
    MissingRecipient,
    MissingBinding,
    Ambiguous
}

public sealed class AgentRecipientResolution
{
    public AgentRecipientResolutionStatus Status { get; set; }
    public AgentInstanceBinding? Binding { get; set; }
    public string? Reason { get; set; }
    public List<string>? CandidateInstanceIds { get; set; }
    public int? RecordedAgentStreamEntryId { get; set; }
}
