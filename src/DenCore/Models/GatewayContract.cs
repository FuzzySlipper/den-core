using System.Text.Json;

namespace DenCore.Models;

public sealed class GatewayReadinessResponse
{
    public required string Status { get; set; }
    public required string Service { get; set; }
    public DateTime CheckedAt { get; set; }
    public required Dictionary<string, GatewayReadinessCheck> Checks { get; set; }
}

public sealed class GatewayReadinessCheck
{
    public required string Status { get; set; }
    public string? Message { get; set; }
    public Dictionary<string, object?> Metadata { get; set; } = new();
}

public sealed class GatewayBindingSnapshotPage
{
    public DateTime GeneratedAt { get; set; }
    public required List<GatewayBindingSnapshot> Items { get; set; }
}

public sealed class GatewayBindingSnapshot
{
    public required string InstanceId { get; set; }
    public required string ProjectId { get; set; }
    public required string AgentIdentity { get; set; }
    public required string AgentFamily { get; set; }
    public string? Role { get; set; }
    public required string TransportKind { get; set; }
    public string? SessionId { get; set; }
    public required string Status { get; set; }
    public DateTime CheckedInAt { get; set; }
    public DateTime LastHeartbeat { get; set; }
    public JsonElement? Metadata { get; set; }
}

public sealed class GatewaySentinelEventRequest
{
    public string? SentinelId { get; set; }
    public string? EventType { get; set; }
    public string? State { get; set; }
    public string? ProjectId { get; set; }
    public string? OutageId { get; set; }
    public string? Reason { get; set; }
    public DateTime? ObservedAt { get; set; }
    public string? Cursor { get; set; }
    public Dictionary<string, JsonElement>? Metadata { get; set; }
    public string? DedupeKey { get; set; }
}

public sealed class GatewaySentinelEventResponse
{
    public required string Status { get; set; }
    public int AgentStreamEntryId { get; set; }
    public required string EventType { get; set; }
    public required string DedupeKey { get; set; }
    public required string OutboxCursor { get; set; }
}
