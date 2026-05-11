using System.Text.Json;

namespace DenMcp.Core.Models;

public sealed class AgentWorkspace
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public required int TaskId { get; set; }
    public required string Branch { get; set; }
    public required string WorktreePath { get; set; }
    public required string BaseBranch { get; set; }
    public string? BaseCommit { get; set; }
    public string? HeadCommit { get; set; }
    public AgentWorkspaceState State { get; set; } = AgentWorkspaceState.Active;
    public string? CreatedByRunId { get; set; }
    public string? DevServerUrl { get; set; }
    public string? PreviewUrl { get; set; }
    public AgentWorkspaceCleanupPolicy CleanupPolicy { get; set; } = AgentWorkspaceCleanupPolicy.Keep;
    public JsonElement? ChangedFileSummary { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class AgentWorkspaceListOptions
{
    public string? ProjectId { get; set; }
    public int? TaskId { get; set; }
    public AgentWorkspaceState? State { get; set; }
    public int Limit { get; set; } = 50;
}
