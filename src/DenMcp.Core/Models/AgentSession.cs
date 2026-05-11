namespace DenMcp.Core.Models;

public sealed class AgentSession
{
    public required string Agent { get; set; }
    public required string ProjectId { get; set; }
    public string? SessionId { get; set; }
    public AgentSessionStatus Status { get; set; } = AgentSessionStatus.Active;
    public DateTime CheckedInAt { get; set; }
    public DateTime LastHeartbeat { get; set; }
    public string? Metadata { get; set; }
}
