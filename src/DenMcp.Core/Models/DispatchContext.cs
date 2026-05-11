using Thread = DenMcp.Core.Models.Thread;

namespace DenMcp.Core.Models;

public sealed class DispatchContextEnvelope
{
    public required DispatchEntry Dispatch { get; set; }
    public required DispatchContextSnapshot Context { get; set; }
}

public sealed class DispatchContextSnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public required string ContextKind { get; set; }
    public required string ProjectId { get; set; }
    public required string TargetAgent { get; set; }
    public string? TargetRole { get; set; }
    public string? AddressedVia { get; set; }
    public string? ActivityHint { get; set; }
    public int? TaskId { get; set; }
    public string? Sender { get; set; }
    public string? Recipient { get; set; }
    public string? MessageTargetRole { get; set; }
    public MessageIntent? MessageIntent { get; set; }
    public string? MessageType { get; set; }
    public string? PacketKind { get; set; }
    public string? HandoffKind { get; set; }
    public string? Branch { get; set; }
    public string? FromStatus { get; set; }
    public string? ToStatus { get; set; }
    public Message? TriggeringMessage { get; set; }
    public Thread? TriggerThread { get; set; }
    public TaskDetail? TaskDetail { get; set; }
    public required List<string> WorkflowGuardrails { get; set; }
    public required List<string> NextActions { get; set; }
}
