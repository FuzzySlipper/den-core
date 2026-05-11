using System.Text.Json;
using System.Text.Json.Serialization;

namespace DenMcp.Core.Models;

/// <summary>
/// Per-project routing configuration. Stored as a den document
/// (doc_type: convention, slug: dispatch-routing). Defines legacy dispatch
/// bridge behavior for projects that still opt in to dispatch creation.
/// </summary>
public sealed class RoutingConfig
{
    /// <summary>
    /// Maps role names to agent identities. E.g. "implementer" → "pi", "reviewer" → "pi".
    /// </summary>
    [JsonPropertyName("roles")]
    public Dictionary<string, string> Roles { get; set; } = new();

    [JsonPropertyName("triggers")]
    public List<RoutingTrigger> Triggers { get; set; } = [];

    [JsonPropertyName("defaults")]
    public RoutingDefaults Defaults { get; set; } = new();
}

/// <summary>
/// A single routing trigger rule. Matches against normalized dispatch events.
/// </summary>
public sealed class RoutingTrigger
{
    /// <summary>
    /// The event kind to match: "task_status_changed" or "message_received".
    /// </summary>
    [JsonPropertyName("event")]
    public required string Event { get; set; }

    /// <summary>Task status the event transitions TO. Null means any.</summary>
    [JsonPropertyName("to_status")]
    public string? ToStatus { get; set; }

    /// <summary>Task status the event transitions FROM. Null means any.</summary>
    [JsonPropertyName("from_status")]
    public string? FromStatus { get; set; }

    /// <summary>
    /// Canonical message intent to match. Null means any.
    /// Serialized as snake_case, e.g. "review_feedback" or "handoff".
    /// </summary>
    [JsonPropertyName("message_intent")]
    public string? MessageIntent { get; set; }

    /// <summary>
    /// Legacy message metadata "type" value to match. Null means any.
    /// Kept as a temporary compatibility alias for older routing documents.
    /// </summary>
    [JsonPropertyName("message_type")]
    public string? MessageType { get; set; }

    /// <summary>
    /// Exact review-packet subtype to match (e.g. "review_request" or "rereview_request").
    /// Null means any packet subtype.
    /// </summary>
    [JsonPropertyName("packet_kind")]
    public string? PacketKind { get; set; }

    /// <summary>
    /// Exact handoff subtype to match (e.g. "planning_summary" or "merge_request").
    /// Null means any handoff subtype.
    /// </summary>
    [JsonPropertyName("handoff_kind")]
    public string? HandoffKind { get; set; }

    /// <summary>If true, only matches messages with an explicit recipient in metadata.</summary>
    [JsonPropertyName("has_recipient")]
    public bool? HasRecipient { get; set; }

    /// <summary>If true, only matches messages with an explicit target_role in metadata.</summary>
    [JsonPropertyName("has_target_role")]
    public bool? HasTargetRole { get; set; }

    /// <summary>
    /// Dispatch target: a role name (looked up in Roles) or a literal agent identity.
    /// Supports "{recipient}" to use the message metadata's recipient value and
    /// "{target_role}" to resolve the message metadata's target_role through Roles.
    /// </summary>
    [JsonPropertyName("dispatch_to")]
    public required string DispatchTo { get; set; }

    /// <summary>
    /// Prompt template with interpolation placeholders.
    /// Supported: {project_id}, {task_id}, {task_title}, {branch}, {sender},
    /// {target_role},
    /// {message_intent}, {message_type}, {packet_kind}, {handoff_kind},
    /// {to_status}, {from_status}
    /// </summary>
    [JsonPropertyName("prompt_template")]
    public string? PromptTemplate { get; set; }
}

public sealed class RoutingDefaults
{
    /// <summary>
    /// Dispatch creation is retired from the default conductor workflow.
    /// Projects must explicitly enable this for legacy bridges that still consume dispatch rows.
    /// </summary>
    [JsonPropertyName("legacy_dispatch_enabled")]
    public bool LegacyDispatchEnabled { get; set; } = false;

    [JsonPropertyName("auto_approve")]
    public bool AutoApprove { get; set; } = false;

    [JsonPropertyName("expiry_minutes")]
    public int ExpiryMinutes { get; set; } = 1440; // 24 hours
}
