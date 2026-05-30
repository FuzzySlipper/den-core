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
/// Well-known implementation kinds for capability definitions.
/// </summary>
public static class ImplementationKinds
{
    /// <summary>Read-only synchronous HTTP proxy. Core executes these.</summary>
    public const string HttpEndpoint = "http_endpoint";
    /// <summary>Built into Core (reserved for future use).</summary>
    public const string CoreBuiltin = "core_builtin";
    /// <summary>Registered but not executable by Core. External executors handle these.</summary>
    public const string RegistryOnly = "registry_only";
}

/// <summary>
/// Well-known side-effect levels for capability definitions.
/// Determines what invocation guardrails apply.
/// </summary>
public static class SideEffectLevels
{
    /// <summary>Read-only, no side effects. Synchronous HTTP proxy allowed.</summary>
    public const string ReadOnly = "read_only";
    /// <summary>Auditable notification/side effects.</summary>
    public const string NotificationOnly = "notification_only";
    /// <summary>Bounded writes with deterministic executor validation, budgets, and dedup.</summary>
    public const string BoundedWrite = "bounded_write";
    /// <summary>External writes requiring additional confirmation.</summary>
    public const string ExternalWrite = "external_write";
}

/// <summary>
/// Status values for capability definitions.
/// </summary>
public static class CapabilityStatuses
{
    /// <summary>Experimental, not yet stable.</summary>
    public const string Experimental = "experimental";
    /// <summary>Active and available.</summary>
    public const string Active = "active";
    /// <summary>Degraded, may have issues.</summary>
    public const string Degraded = "degraded";
    /// <summary>Disabled, not available.</summary>
    public const string Disabled = "disabled";
}

/// <summary>
/// Status values for capability invocations (audit records).
/// Terminal: completed, failed, disabled, invalid_request, invalid_output, timed_out.
/// </summary>
public static class InvocationStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string InvalidRequest = "invalid_request";
    public const string InvalidOutput = "invalid_output";
    public const string TimedOut = "timed_out";
    public const string Disabled = "disabled";

    public static readonly string[] TerminalStatuses =
    [
        Completed, Failed, Disabled, InvalidRequest, InvalidOutput, TimedOut
    ];

    public static bool IsTerminal(string status) =>
        Array.IndexOf(TerminalStatuses, status) >= 0;
}

/// <summary>
/// Default HTTP method for capability endpoints.
/// </summary>
public static class DefaultMethods
{
    public const string HttpMethod = "POST";
}

/// <summary>
/// Default timeout for capability invocations (30 seconds).
/// </summary>
public static class DefaultTimeouts
{
    public const int InvocationTimeoutMs = 30000;
    public const int AnalyzeImageTimeoutMs = 60000;
    public const int MaxRequestBytes = 10 * 1024 * 1024; // 10MB
    public const int MaxInlinePayloadSize = 100 * 1024; // 100KB
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
    public string? OwnerProjectId { get; set; }

    /// <summary>Implementation kind: http_endpoint, core_builtin, or registry_only.</summary>
    public string ImplementationKind { get; set; } = ImplementationKinds.RegistryOnly;

    /// <summary>Service endpoint URL. Only used when ImplementationKind is http_endpoint.</summary>
    public string? ServiceEndpoint { get; set; }

    /// <summary>HTTP method for the service endpoint (default: POST).</summary>
    public string HttpMethod { get; set; } = DefaultMethods.HttpMethod;

    /// <summary>Reference to an input schema definition.</summary>
    public string? InputSchemaRef { get; set; }

    /// <summary>Reference to an output schema definition.</summary>
    public string? OutputSchemaRef { get; set; }

    /// <summary>Inline JSON schema for request validation.</summary>
    public string? InputSchemaJson { get; set; }

    /// <summary>Inline JSON schema for response validation.</summary>
    public string? OutputSchemaJson { get; set; }

    /// <summary>Side-effect level: read_only, notification_only, bounded_write, external_write.</summary>
    public string SideEffectLevel { get; set; } = SideEffectLevels.ReadOnly;

    /// <summary>Status: experimental, active, degraded, disabled.</summary>
    public string Status { get; set; } = CapabilityStatuses.Experimental;

    /// <summary>Default model configuration as JSON.</summary>
    public string? DefaultModelJson { get; set; }

    /// <summary>Fallback model configurations as JSON array.</summary>
    public string? FallbackModelsJson { get; set; }

    /// <summary>Evaluation references as JSON array.</summary>
    public string? EvalRefsJson { get; set; }

    /// <summary>Timeout in milliseconds.</summary>
    public int TimeoutMs { get; set; } = DefaultTimeouts.InvocationTimeoutMs;

    /// <summary>Maximum request body size in bytes.</summary>
    public int MaxRequestBytes { get; set; } = DefaultTimeouts.MaxRequestBytes;

    /// <summary>Arbitrary JSON metadata.</summary>
    public string? MetadataJson { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// An audit record for a capability invocation.
/// Core records every invocation attempt, regardless of outcome.
/// </summary>
public sealed class CapabilityInvocation
{
    /// <summary>Internal auto-increment integer ID.</summary>
    public int Id { get; set; }

    /// <summary>Public invocation identifier (e.g. capinv_&lt;timestamp/random&gt;).</summary>
    public string? InvocationId { get; set; }

    public required string CapabilityId { get; set; }

    /// <summary>Optional capability version string.</summary>
    public string? CapabilityVersion { get; set; }

    /// <summary>Caller agent identifier.</summary>
    public string? CallerAgent { get; set; }

    public required string CallerProjectId { get; set; }

    /// <summary>Caller task id (nullable integer).</summary>
    public int? CallerTaskId { get; set; }

    /// <summary>Caller message id for thread context.</summary>
    public string? CallerMessageId { get; set; }

    /// <summary>Caller surface (e.g. 'mcp', 'rest', 'channel').</summary>
    public string? CallerSurface { get; set; }

    /// <summary>JSON array of input artifact references.</summary>
    public string? InputArtifactRefsJson { get; set; }

    /// <summary>Bounded JSON request payload.</summary>
    public string? RequestJson { get; set; }

    /// <summary>Hash of the request for deduplication.</summary>
    public string? RequestHash { get; set; }

    /// <summary>Invocation status.</summary>
    public required string Status { get; set; }

    /// <summary>When execution started.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>When execution completed.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Duration of execution in milliseconds.</summary>
    public int? DurationMs { get; set; }

    /// <summary>Model provider used.</summary>
    public string? ModelProvider { get; set; }

    /// <summary>Model name used.</summary>
    public string? ModelName { get; set; }

    /// <summary>Model version used.</summary>
    public string? ModelVersion { get; set; }

    /// <summary>JSON object with timing breakdown.</summary>
    public string? TimingsMsJson { get; set; }

    /// <summary>JSON object with cost breakdown.</summary>
    public string? CostJson { get; set; }

    /// <summary>Human-readable output summary.</summary>
    public string? OutputSummary { get; set; }

    /// <summary>JSON output payload.</summary>
    public string? OutputJson { get; set; }

    /// <summary>JSON array of output artifact references.</summary>
    public string? OutputArtifactRefsJson { get; set; }

    /// <summary>Error type string (e.g. 'timeout', 'executor_error', 'invalid_request').</summary>
    public string? ErrorType { get; set; }

    /// <summary>Human-readable error message.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Arbitrary JSON metadata.</summary>
    public string? MetadataJson { get; set; }

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
    public bool Verbose { get; set; }
}

/// <summary>
/// Options for listing capability invocations.
/// </summary>
public sealed class InvocationListOptions
{
    public string? CapabilityId { get; set; }
    public string? CallerProjectId { get; set; }
    public int? CallerTaskId { get; set; }
    public string? Status { get; set; }
    public int Limit { get; set; } = 50;
}

// ── Vision Analyzer V1 ────────────────────────────────────────────────

/// <summary>
/// Request shape for Vision Analyzer V1 capability.
/// Core validates the request shape; the real executor is external.
/// </summary>
public sealed class AnalyzeImageRequest
{
    /// <summary>Image reference — must be an artifact/ref string (not raw base64 or data: URL).</summary>
    public required string ImageRef { get; set; }

    /// <summary>Analysis mode: general, ui_screenshot, diagram, ocr, error_screen, diff.</summary>
    public string Mode { get; set; } = "general";

    /// <summary>Optional question/prompt for the vision model.</summary>
    public string? Question { get; set; }

    /// <summary>Whether to include OCR text extraction. Default: true.</summary>
    public bool IncludeOcr { get; set; } = true;

    /// <summary>Whether to include region detection. Default: false.</summary>
    public bool IncludeRegions { get; set; }

    /// <summary>UI context (for ui_screenshot mode).</summary>
    public string? UiContext { get; set; }
}

/// <summary>
/// Structured result from analyze_image wrapper.
/// </summary>
public sealed class AnalyzeImageResult
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string? InvocationId { get; set; }
    public string? Error { get; set; }
    public string? ErrorType { get; set; }
    public string? Description { get; set; }
    public string? RawOutput { get; set; }
    public int? DurationMs { get; set; }
}

// ── Executor Envelope Types ────────────────────────────────────────────

/// <summary>
/// Envelope sent to the HTTP executor for capability invocation.
/// </summary>
public sealed class ExecutorRequestEnvelope
{
    public required string InvocationId { get; set; }
    public required string CapabilityId { get; set; }
    public string? CapabilityVersion { get; set; }
    public required ExecutorCaller Caller { get; set; }
    public required string SideEffectLevel { get; set; }
    public required string DeadlineUtc { get; set; }
    public object? Request { get; set; }
    public object? Safety { get; set; }
}

/// <summary>
/// Caller info embedded in the executor envelope.
/// </summary>
public sealed class ExecutorCaller
{
    public string? Agent { get; set; }
    public string? ProjectId { get; set; }
    public int? TaskId { get; set; }
}

/// <summary>
/// Envelope received from the HTTP executor.
/// </summary>
public sealed class ExecutorResponseEnvelope
{
    /// <summary>Executor-level status (e.g. 'completed', 'failed').</summary>
    public string? Status { get; set; }

    /// <summary>Human-readable output summary.</summary>
    public string? OutputSummary { get; set; }

    /// <summary>Raw output payload (JSON string).</summary>
    public string? Output { get; set; }

    /// <summary>Array of output artifact references.</summary>
    public string[]? OutputArtifactRefs { get; set; }

    /// <summary>Model info.</summary>
    public ExecutorModelInfo? Model { get; set; }

    /// <summary>Timing breakdown in milliseconds.</summary>
    public Dictionary<string, int>? TimingsMs { get; set; }

    /// <summary>Cost breakdown.</summary>
    public Dictionary<string, decimal>? Cost { get; set; }

    /// <summary>Arbitrary metadata.</summary>
    public Dictionary<string, object>? Metadata { get; set; }
}

/// <summary>
/// Model info from the executor response envelope.
/// </summary>
public sealed class ExecutorModelInfo
{
    public string? Provider { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
}

// ── Request / Response DTOs ────────────────────────────────────────────

/// <summary>
/// Request body for creating or updating a capability definition.
/// </summary>
public sealed class UpsertCapabilityRequest
{
    public string? CapabilityId { get; set; }
    public string DisplayName { get; set; } = "";
    public string? Description { get; set; }
    public string? OwnerProjectId { get; set; }
    public string ImplementationKind { get; set; } = ImplementationKinds.RegistryOnly;
    public string? ServiceEndpoint { get; set; }
    public string? HttpMethod { get; set; }
    public string? InputSchemaRef { get; set; }
    public string? OutputSchemaRef { get; set; }
    public string? InputSchemaJson { get; set; }
    public string? OutputSchemaJson { get; set; }
    public string SideEffectLevel { get; set; } = SideEffectLevels.ReadOnly;
    public string Status { get; set; } = CapabilityStatuses.Experimental;
    public string? DefaultModelJson { get; set; }
    public string? FallbackModelsJson { get; set; }
    public string? EvalRefsJson { get; set; }
    public int? TimeoutMs { get; set; }
    public int? MaxRequestBytes { get; set; }
    public string? MetadataJson { get; set; }
}

/// <summary>
/// Request body for invoking a capability.
/// </summary>
public sealed class InvokeCapabilityRequest
{
    /// <summary>JSON request payload.</summary>
    public string? RequestJson { get; set; }

    /// <summary>Optional caller project id for audit.</summary>
    public string? CallerProjectId { get; set; }

    /// <summary>Optional caller task id for audit.</summary>
    public int? CallerTaskId { get; set; }

    /// <summary>Optional caller agent for audit.</summary>
    public string? CallerAgent { get; set; }

    /// <summary>Optional caller message id.</summary>
    public string? CallerMessageId { get; set; }

    /// <summary>Optional caller surface.</summary>
    public string? CallerSurface { get; set; }

    /// <summary>Request timeout in milliseconds.</summary>
    public int TimeoutMs { get; set; } = DefaultTimeouts.InvocationTimeoutMs;
}
