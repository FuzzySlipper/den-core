using System.Text.Json.Serialization;

namespace DenMcp.Core.Models;

// ── Constants ──────────────────────────────────────────────────────────

/// <summary>
/// Well-known capability IDs defined in the capability service registry.
/// Core defines canonical capability IDs for built-in services.
/// </summary>
public static class CapabilityIds
{
    /// <summary>
    /// Vision analyzer capability — delegates to an external vision model executor.
    /// Core owns the registry and audit; the real executor is external.
    /// </summary>
    public const string VisionAnalyzeImageV1 = "vision.analyze_image.v1";
}

/// <summary>
/// Well-known side-effect levels for capability definitions.
/// Determines what invocation guardrails apply.
/// </summary>
public static class SideEffectLevels
{
    /// <summary>Read-only, no side effects. Synchronous HTTP proxy allowed.</summary>
    public const string None = "none";
    /// <summary>Auditable side effects. Requires additional checks.</summary>
    public const string Auditable = "auditable";
    /// <summary>Potentially destructive. Requires explicit confirmation.</summary>
    public const string Destructive = "destructive";
}

/// <summary>
/// Status values for capability definitions.
/// </summary>
public static class CapabilityStatuses
{
    public const string Enabled = "enabled";
    public const string Disabled = "disabled";
    public const string Deprecated = "deprecated";
}

/// <summary>
/// Status values for capability invocations (audit records).
/// Terminal: success, disabled, invalid_request, timeout, executor_failure,
/// invalid_output, non_read_only_rejected.
/// </summary>
public static class InvocationStatuses
{
    public const string Success = "success";
    public const string Disabled = "disabled";
    public const string InvalidRequest = "invalid_request";
    public const string Timeout = "timeout";
    public const string ExecutorFailure = "executor_failure";
    public const string InvalidOutput = "invalid_output";
    public const string NonReadOnlyRejected = "non_read_only_rejected";

    public static readonly string[] TerminalStatuses =
    [
        Success, Disabled, InvalidRequest, Timeout,
        ExecutorFailure, InvalidOutput, NonReadOnlyRejected
    ];

    public static bool IsTerminal(string status) =>
        Array.IndexOf(TerminalStatuses, status) >= 0;
}

// ── Core Models ────────────────────────────────────────────────────────

/// <summary>
/// A registered capability definition. Core owns the registry; external
/// service agents register themselves here for discovery and invocation routing.
/// </summary>
public sealed class CapabilityDefinition
{
    public required string CapabilityId { get; set; }
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = CapabilityStatuses.Enabled;

    /// <summary>
    /// HTTP endpoint URL. Only used when ExecutorKind is "http_endpoint"
    /// and the capability is marked as read-only (side_effect_level = "none").
    /// The endpoint must be a registered, audited service endpoint.
    /// </summary>
    public string? HttpEndpoint { get; set; }

    /// <summary>
    /// Executor kind: "http_endpoint" or "external_service" (not executed by Core).
    /// Core only proxies synchronous HTTP calls for read-only http_endpoint capabilities.
    /// </summary>
    public string ExecutorKind { get; set; } = "external_service";

    /// <summary>
    /// Side-effect level: none, auditable, destructive.
    /// Core only proxies calls for "none" (read-only) level.
    /// </summary>
    public string SideEffectLevel { get; set; } = SideEffectLevels.None;

    /// <summary>
    /// Project that owns this capability definition.
    /// </summary>
    public string? OwnerProjectId { get; set; }

    /// <summary>
    /// JSON schema for the request payload (optional).
    /// </summary>
    public string? RequestSchemaJson { get; set; }

    /// <summary>
    /// JSON schema for the response payload (optional).
    /// </summary>
    public string? ResponseSchemaJson { get; set; }

    /// <summary>
    /// Arbitrary JSON metadata.
    /// </summary>
    public string? Metadata { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// An audit record for a capability invocation.
/// Core records every invocation attempt, regardless of outcome.
/// </summary>
public sealed class CapabilityInvocation
{
    public int Id { get; set; }
    public required string CapabilityId { get; set; }
    public required string CallerProjectId { get; set; }
    public string? CallerTaskId { get; set; }
    public required string CallerIdentity { get; set; }

    /// <summary>
    /// Invocation status. Must be a terminal status when the record is closed.
    /// </summary>
    public required string Status { get; set; }

    /// <summary>
    /// JSON request payload (size-limited; large payloads use artifact references instead).
    /// </summary>
    public string? RequestPayload { get; set; }

    /// <summary>
    /// JSON response payload (size-limited).
    /// </summary>
    public string? ResponsePayload { get; set; }

    /// <summary>
    /// Human-readable error or status detail.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Duration of execution in milliseconds (null if not executed).
    /// </summary>
    public int? DurationMs { get; set; }

    /// <summary>
    /// Arbitrary JSON metadata for the invocation.
    /// </summary>
    public string? Metadata { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// ── Filter / List Options ──────────────────────────────────────────────

/// <summary>
/// Options for listing capability definitions.
/// </summary>
public sealed class CapabilityListOptions
{
    public string? Status { get; set; }
    public string? SideEffectLevel { get; set; }
    public string? OwnerProjectId { get; set; }
    public int Limit { get; set; } = 50;
}

/// <summary>
/// Options for listing capability invocations.
/// </summary>
public sealed class InvocationListOptions
{
    public string? CapabilityId { get; set; }
    public string? CallerProjectId { get; set; }
    public string? CallerTaskId { get; set; }
    public string? Status { get; set; }
    public int Limit { get; set; } = 50;
}

// ── Vision Analyzer Wrapper ────────────────────────────────────────────

/// <summary>
/// Request wrapper for vision.analyze_image.v1 capability invocation.
/// Core validates the request shape; the real executor is external.
/// </summary>
public sealed class AnalyzeImageRequest
{
    /// <summary>
    /// Image reference — must be an artifact/ref string (not raw base64 or data: URL).
    /// Should reference a file path, artifact URL, or Den artifact reference.
    /// </summary>
    public required string ImageRef { get; set; }

    /// <summary>
    /// Optional prompt/instruction for the vision model.
    /// </summary>
    public string? Prompt { get; set; }

    /// <summary>
    /// Optional model preference.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Maximum tokens for the vision response.
    /// </summary>
    public int? MaxTokens { get; set; }
}

/// <summary>
/// Structured response from the analyze_image wrapper.
/// </summary>
public sealed class AnalyzeImageResult
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public string? Description { get; set; }
    public string? RawOutput { get; set; }
    public int? DurationMs { get; set; }
}

// ── Request / Response DTOs ────────────────────────────────────────────

/// <summary>
/// Request body for creating or updating a capability definition.
/// </summary>
public sealed class UpsertCapabilityRequest
{
    public string? CapabilityId { get; set; }
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = CapabilityStatuses.Enabled;
    public string? HttpEndpoint { get; set; }
    public string ExecutorKind { get; set; } = "external_service";
    public string SideEffectLevel { get; set; } = SideEffectLevels.None;
    public string? OwnerProjectId { get; set; }
    public string? RequestSchemaJson { get; set; }
    public string? ResponseSchemaJson { get; set; }
    public string? Metadata { get; set; }
}

/// <summary>
/// Request body for invoking a capability.
/// </summary>
public sealed class InvokeCapabilityRequest
{
    /// <summary>
    /// Required caller project id for audit.
    /// </summary>
    public required string CallerProjectId { get; set; }

    /// <summary>
    /// Optional caller task id for audit.
    /// </summary>
    public string? CallerTaskId { get; set; }

    /// <summary>
    /// Required caller identity for audit.
    /// </summary>
    public required string CallerIdentity { get; set; }

    /// <summary>
    /// JSON request payload to send to the capability executor.
    /// Size-limited; large payloads should use artifact references.
    /// </summary>
    public string? Payload { get; set; }

    /// <summary>
    /// Request timeout in milliseconds. Default: 30000.
    /// </summary>
    public int TimeoutMs { get; set; } = 30000;
}
