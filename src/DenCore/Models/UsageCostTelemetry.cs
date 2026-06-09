using System.Text.Json;

namespace DenCore.Models;

/// <summary>
/// A single worker model usage event — append-only-ish record keyed to
/// Den workflow identities. Core owns these records; producers in
/// den-host / den-hermes-bridge POST them via Core APIs.
///
/// COST PRECISION CONTRACT:
/// Costs are stored as micro-cents (1/100,000 of one cent) in
/// <see cref="ApproximateCostMicroCents"/> so that integer arithmetic
/// is deterministic. A value of 12345 means $0.00012345.
/// Unknown cost is represented as NULL, never 0.
///
/// PRIVACY CONTRACT:
/// This record intentionally stores NO prompts, completions, API keys,
/// secrets, or full request/response payloads. Only counters and
/// attribution metadata are stored.
/// </summary>
public sealed class ModelUsageEvent
{
    public int Id { get; set; }

    /// <summary>ISO-8601 timestamp when the usage occurred.</summary>
    public required string OccurredAt { get; set; }

    // ── Den workflow attribution ──────────────────────────────────────
    public required string ProjectId { get; set; }
    public int? TaskId { get; set; }

    /// <summary>Worker assignment ID when known.</summary>
    public int? AssignmentId { get; set; }

    /// <summary>Worker run ID (e.g. spawned-Hermes run_id).</summary>
    public string? RunId { get; set; }

    /// <summary>Hermes session ID when known.</summary>
    public string? SessionId { get; set; }

    // ── Agent / worker identity ───────────────────────────────────────
    public string? AgentIdentity { get; set; }
    public string? ProfileIdentity { get; set; }
    public string? WorkerRole { get; set; }
    public string? WorkerIdentity { get; set; }

    // ── Operation kind ────────────────────────────────────────────────
    /// <summary>
    /// Operation kind: planner_turn, runner_turn, worker_turn, review,
    /// validation, drift_check, packet_audit, cron_tick, tool_auxiliary, etc.
    /// </summary>
    public required string OperationKind { get; set; }

    // ── Provider / model attribution ──────────────────────────────────
    public required string Provider { get; set; }

    /// <summary>Model as reported by the provider (e.g. "deepseek-v4-flash").</summary>
    public required string Model { get; set; }

    /// <summary>
    /// The configured model alias at call time (e.g. "cheap-fast-bounded").
    /// Useful when alias routes may change. Null if no alias was used.
    /// </summary>
    public string? ModelAlias { get; set; }

    /// <summary>
    /// The resolved/effective model after alias routing (may equal Model
    /// when no alias is used). Must always be populated.
    /// </summary>
    public string? ResolvedModel { get; set; }

    /// <summary>
    /// Endpoint kind: api, local, free, proxy, unknown.
    /// </summary>
    public string? EndpointKind { get; set; }

    // ── Token counters ────────────────────────────────────────────────
    /// <summary>Input / prompt tokens.</summary>
    public int? InputTokens { get; set; }

    /// <summary>Output / completion tokens.</summary>
    public int? OutputTokens { get; set; }

    /// <summary>Cache-read tokens (prompt caching hits).</summary>
    public int? CacheReadTokens { get; set; }

    /// <summary>Cache-write tokens (new prompt cache entries).</summary>
    public int? CacheWriteTokens { get; set; }

    /// <summary>Reasoning / thinking tokens when broken out by provider.</summary>
    public int? ReasoningTokens { get; set; }

    /// <summary>
    /// Tool-result / context tokens consumed (when available).
    /// Distinct from input/output to avoid double-counting.
    /// </summary>
    public int? ToolResultTokens { get; set; }

    // ── Request / retry / streaming ───────────────────────────────────
    public int RequestCount { get; set; } = 1;
    public int RetryCount { get; set; } = 0;
    public bool Streaming { get; set; } = false;

    /// <summary>
    /// Error kind when the call failed: timeout, rate_limit, auth, server_error, etc.
    /// Null for successful calls.
    /// </summary>
    public string? ErrorKind { get; set; }

    // ── Pricing and approximate cost ──────────────────────────────────
    /// <summary>
    /// ID linking to the pricing snapshot used for cost computation.
    /// Null when no pricing was available (free/unknown-cost path).
    /// </summary>
    public int? PricingSnapshotId { get; set; }

    /// <summary>
    /// Approximate cost in micro-cents (1/100,000 of one cent).
    /// Integer arithmetic — no floating-point drift.
    /// NULL = cost unknown (honest representation of missing data).
    /// Example: 500_000 = $0.00500000, 12_345_678 = $0.12345678.
    /// </summary>
    public long? ApproximateCostMicroCents { get; set; }

    // ── Provenance ────────────────────────────────────────────────────
    /// <summary>
    /// Source of this record: "den-host", "hermes-bridge", "core_self", etc.
    /// </summary>
    public string? Provenance { get; set; }

    /// <summary>Adapter/library version that produced this record.</summary>
    public string? AdapterVersion { get; set; }

    /// <summary>Raw-usage source: "openai_usage_block", "anthropic_usage", etc.</summary>
    public string? RawUsageSource { get; set; }

    /// <summary>Redacted request ID for cross-referencing (no secrets).</summary>
    public string? RequestIdHint { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A versioned pricing snapshot containing per-model pricing entries.
/// Snapshot IDs are referenced by <see cref="ModelUsageEvent.PricingSnapshotId"/>
/// so old events do not change when the catalog changes.
/// </summary>
public sealed class PricingSnapshot
{
    public int Id { get; set; }

    /// <summary>Human-readable label: "hermes-v1-2026-06", "initial-seed".</summary>
    public required string SnapshotLabel { get; set; }

    /// <summary>Semantic version for the catalog.</summary>
    public required string SnapshotVersion { get; set; }

    /// <summary>ISO-8601 when this snapshot became effective.</summary>
    public string? EffectiveAt { get; set; }

    /// <summary>JSON array of <see cref="PricingEntry"/> objects.</summary>
    public required string EntriesJson { get; set; }

    /// <summary>Who or what created this snapshot.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>Optional notes about the snapshot.</summary>
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A single provider/model pricing entry within a <see cref="PricingSnapshot"/>.
/// Stored as JSON inside <see cref="PricingSnapshot.EntriesJson"/>.
/// All per-unit prices are in micro-cents (1/100,000 cent) to allow
/// integer arithmetic.
/// </summary>
public sealed class PricingEntry
{
    /// <summary>Provider name: "openai", "anthropic", "deepseek", "google", "local", etc.</summary>
    public required string Provider { get; set; }

    /// <summary>Model name: "deepseek-v4-flash", "claude-sonnet-4", etc.</summary>
    public required string Model { get; set; }

    /// <summary>Price per 1M input tokens in micro-cents.</summary>
    public long? InputPriceMicroCentsPerMillion { get; set; }

    /// <summary>Price per 1M output tokens in micro-cents.</summary>
    public long? OutputPriceMicroCentsPerMillion { get; set; }

    /// <summary>Price per 1M cache-read tokens in micro-cents.</summary>
    public long? CacheReadPriceMicroCentsPerMillion { get; set; }

    /// <summary>Price per 1M cache-write tokens in micro-cents.</summary>
    public long? CacheWritePriceMicroCentsPerMillion { get; set; }

    /// <summary>Price per 1M reasoning tokens in micro-cents.</summary>
    public long? ReasoningPriceMicroCentsPerMillion { get; set; }

    /// <summary>Flat price per request in micro-cents (e.g. tool_use markup).</summary>
    public long? PerRequestPriceMicroCents { get; set; }

    /// <summary>
    /// Pricing kind: "api" (standard provider pricing), "free" (free tier — zero cost),
    /// "local" (self-hosted — marginal cost estimate or zero),
    /// "unknown" (no pricing data — cost recorded as NULL).
    /// </summary>
    public string PricingKind { get; set; } = "api";

    /// <summary>Optional currency context (default: "USD").</summary>
    public string Currency { get; set; } = "USD";

    /// <summary>Optional notes about pricing source/assumptions.</summary>
    public string? Notes { get; set; }
}

/// <summary>
/// A usage/cost report query result. Built from aggregated
/// <see cref="ModelUsageEvent"/> records.
/// </summary>
public sealed class UsageCostReport
{
    /// <summary>List of aggregated rows.</summary>
    public List<UsageCostReportRow> Rows { get; set; } = [];

    /// <summary>Total approximate cost in micro-cents across all rows.</summary>
    public long? TotalCostMicroCents { get; set; }

    /// <summary>Total input tokens across all rows.</summary>
    public long? TotalInputTokens { get; set; }

    /// <summary>Total output tokens across all rows.</summary>
    public long? TotalOutputTokens { get; set; }

    /// <summary>Total events counted.</summary>
    public int TotalEvents { get; set; }

    /// <summary>How results were grouped.</summary>
    public string? GroupBy { get; set; }

    /// <summary>Query parameters echo'd back for reference.</summary>
    public UsageCostQueryOptions? Query { get; set; }
}

/// <summary>
/// A single aggregated row in a <see cref="UsageCostReport"/>.
/// </summary>
public sealed class UsageCostReportRow
{
    public string? ProjectId { get; set; }
    public int? TaskId { get; set; }
    public string? WorkerRole { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? OperationKind { get; set; }
    public int EventCount { get; set; }
    public long? TotalInputTokens { get; set; }
    public long? TotalOutputTokens { get; set; }
    public long? TotalCacheReadTokens { get; set; }
    public long? TotalReasoningTokens { get; set; }
    public long? ApproximateCostMicroCents { get; set; }
    public int? EventsWithKnownCost { get; set; }
    public int? EventsWithUnknownCost { get; set; }
}

/// <summary>
/// Query options for usage/cost reports.
/// </summary>
public sealed class UsageCostQueryOptions
{
    /// <summary>Required: scope to a specific project.</summary>
    public string? ProjectId { get; set; }

    /// <summary>Optional: scope to a specific task.</summary>
    public int? TaskId { get; set; }

    /// <summary>Optional: start of time window (ISO-8601).</summary>
    public string? FromOccurredAt { get; set; }

    /// <summary>Optional: end of time window (ISO-8601).</summary>
    public string? ToOccurredAt { get; set; }

    /// <summary>
    /// Group-by dimension: "task", "role", "model", "provider", "project".
    /// Default: "task".
    /// </summary>
    public string GroupBy { get; set; } = "task";

    /// <summary>Max rows to return. Default 100.</summary>
    public int Limit { get; set; } = 100;
}

/// <summary>
/// Input for recording a single usage event.
/// </summary>
public sealed class UsageEventIngestInput
{
    public required string ProjectId { get; set; }
    public int? TaskId { get; set; }
    public int? AssignmentId { get; set; }
    public string? RunId { get; set; }
    public string? SessionId { get; set; }
    public string? AgentIdentity { get; set; }
    public string? ProfileIdentity { get; set; }
    public string? WorkerRole { get; set; }
    public string? WorkerIdentity { get; set; }
    public required string OperationKind { get; set; }
    public required string Provider { get; set; }
    public required string Model { get; set; }
    public string? ModelAlias { get; set; }
    public string? ResolvedModel { get; set; }
    public string? EndpointKind { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? CacheReadTokens { get; set; }
    public int? CacheWriteTokens { get; set; }
    public int? ReasoningTokens { get; set; }
    public int? ToolResultTokens { get; set; }
    public int RequestCount { get; set; } = 1;
    public int RetryCount { get; set; } = 0;
    public bool Streaming { get; set; }
    public string? ErrorKind { get; set; }
    public string? Provenance { get; set; }
    public string? AdapterVersion { get; set; }
    public string? RawUsageSource { get; set; }
    public string? RequestIdHint { get; set; }
    public string? OccurredAt { get; set; }
}

/// <summary>
/// Constants for usage cost telemetry operations.
/// </summary>
public static class UsageCostConstants
{
    public const string OperationPlannerTurn = "planner_turn";
    public const string OperationRunnerTurn = "runner_turn";
    public const string OperationWorkerTurn = "worker_turn";
    public const string OperationReview = "review";
    public const string OperationValidation = "validation";
    public const string OperationDriftCheck = "drift_check";
    public const string OperationPacketAudit = "packet_audit";
    public const string OperationCronTick = "cron_tick";
    public const string OperationToolAuxiliary = "tool_auxiliary";

    public const string EndpointApi = "api";
    public const string EndpointLocal = "local";
    public const string EndpointFree = "free";
    public const string EndpointProxy = "proxy";
    public const string EndpointUnknown = "unknown";

    public const string PricingKindApi = "api";
    public const string PricingKindFree = "free";
    public const string PricingKindLocal = "local";
    public const string PricingKindUnknown = "unknown";

    public const string GroupByTask = "task";
    public const string GroupByRole = "role";
    public const string GroupByModel = "model";
    public const string GroupByProvider = "provider";
    public const string GroupByProject = "project";

    /// <summary>Micro-cents per 1 cent (10,000).</summary>
    public const long MicroCentsPerCent = 10_000;

    /// <summary>Micro-cents per dollar (1,000,000).</summary>
    public const long MicroCentsPerDollar = 1_000_000;

    /// <summary>
    /// Default pricing catalog: sufficient for current routed models,
    /// including zero-cost and unknown-cost paths.
    /// Prices are approximate and may not reflect discounts, batch pricing,
    /// or provider-specific quirks.
    /// </summary>
    public static readonly PricingEntry[] DefaultPricingCatalog =
    [
        // ── DeepSeek ─────────────────────────────────────────────────
        new()
        {
            Provider = "deepseek",
            Model = "deepseek-v4-flash",
            InputPriceMicroCentsPerMillion = 27_000,   // $0.27/M input
            OutputPriceMicroCentsPerMillion = 1_100_000,  // $1.10/M output
            PricingKind = PricingKindApi,
            Notes = "DeepSeek V4 Flash — cheap/fast bounded-role model"
        },
        new()
        {
            Provider = "deepseek",
            Model = "deepseek-v4-pro",
            InputPriceMicroCentsPerMillion = 55_000,   // $0.55/M input
            OutputPriceMicroCentsPerMillion = 2_190_000,  // $2.19/M output
            PricingKind = PricingKindApi,
            Notes = "DeepSeek V4 Pro"
        },
        new()
        {
            Provider = "deepseek",
            Model = "deepseek-r1",
            InputPriceMicroCentsPerMillion = 55_000,
            OutputPriceMicroCentsPerMillion = 2_190_000,
            PricingKind = PricingKindApi,
            Notes = "DeepSeek R1 reasoning model"
        },

        // ── Anthropic ────────────────────────────────────────────────
        new()
        {
            Provider = "anthropic",
            Model = "claude-sonnet-4",
            InputPriceMicroCentsPerMillion = 300_000,   // $3.00/M input
            OutputPriceMicroCentsPerMillion = 1_500_000,   // $15.00/M output
            CacheReadPriceMicroCentsPerMillion = 30_000,   // $0.30/M cache read
            CacheWritePriceMicroCentsPerMillion = 375_000, // $3.75/M cache write
            PricingKind = PricingKindApi,
            Notes = "Claude Sonnet 4"
        },
        new()
        {
            Provider = "anthropic",
            Model = "claude-opus-4",
            InputPriceMicroCentsPerMillion = 1_500_000,  // $15.00/M input
            OutputPriceMicroCentsPerMillion = 7_500_000,  // $75.00/M output
            CacheReadPriceMicroCentsPerMillion = 150_000,  // $1.50/M cache read
            CacheWritePriceMicroCentsPerMillion = 1_875_000, // $18.75/M cache write
            PricingKind = PricingKindApi,
            Notes = "Claude Opus 4"
        },
        new()
        {
            Provider = "anthropic",
            Model = "claude-haiku-4",
            InputPriceMicroCentsPerMillion = 80_000,    // $0.80/M input
            OutputPriceMicroCentsPerMillion = 400_000,   // $4.00/M output
            PricingKind = PricingKindApi,
            Notes = "Claude Haiku 4"
        },

        // ── OpenAI ──────────────────────────────────────────────────
        new()
        {
            Provider = "openai",
            Model = "gpt-4o",
            InputPriceMicroCentsPerMillion = 250_000,   // $2.50/M input
            OutputPriceMicroCentsPerMillion = 1_000_000,   // $10.00/M output
            PricingKind = PricingKindApi,
            Notes = "GPT-4o"
        },
        new()
        {
            Provider = "openai",
            Model = "gpt-4o-mini",
            InputPriceMicroCentsPerMillion = 15_000,    // $0.15/M input
            OutputPriceMicroCentsPerMillion = 60_000,    // $0.60/M output
            PricingKind = PricingKindApi,
            Notes = "GPT-4o mini"
        },
        new()
        {
            Provider = "openai",
            Model = "o3",
            InputPriceMicroCentsPerMillion = 1_000_000,   // $10.00/M input
            OutputPriceMicroCentsPerMillion = 4_000_000,   // $40.00/M output
            PricingKind = PricingKindApi,
            Notes = "OpenAI o3 reasoning model"
        },
        new()
        {
            Provider = "openai",
            Model = "o4-mini",
            InputPriceMicroCentsPerMillion = 110_000,   // $1.10/M input
            OutputPriceMicroCentsPerMillion = 440_000,   // $4.40/M output
            PricingKind = PricingKindApi,
            Notes = "OpenAI o4-mini"
        },

        // ── Google ──────────────────────────────────────────────────
        new()
        {
            Provider = "google",
            Model = "gemini-2.5-pro",
            InputPriceMicroCentsPerMillion = 125_000,   // $1.25/M input
            OutputPriceMicroCentsPerMillion = 1_000_000,   // $10.00/M output
            PricingKind = PricingKindApi,
            Notes = "Gemini 2.5 Pro"
        },
        new()
        {
            Provider = "google",
            Model = "gemini-2.5-flash",
            InputPriceMicroCentsPerMillion = 15_000,    // $0.15/M input
            OutputPriceMicroCentsPerMillion = 60_000,    // $0.60/M output
            PricingKind = PricingKindApi,
            Notes = "Gemini 2.5 Flash"
        },

        // ── xAI / Grok ──────────────────────────────────────────────
        new()
        {
            Provider = "xai",
            Model = "grok-3",
            InputPriceMicroCentsPerMillion = 300_000,   // $3.00/M input
            OutputPriceMicroCentsPerMillion = 1_500_000,   // $15.00/M output
            PricingKind = PricingKindApi,
            Notes = "Grok 3"
        },
        new()
        {
            Provider = "xai",
            Model = "grok-3-mini",
            InputPriceMicroCentsPerMillion = 30_000,    // $0.30/M input
            OutputPriceMicroCentsPerMillion = 50_000,    // $0.50/M output
            PricingKind = PricingKindApi,
            Notes = "Grok 3 Mini"
        },

        // ── Local / free models ─────────────────────────────────────
        new()
        {
            Provider = "local",
            Model = "*",
            PricingKind = PricingKindLocal,
            Notes = "Local self-hosted model — marginal cost treated as zero"
        },
        new()
        {
            Provider = "ollama",
            Model = "*",
            PricingKind = PricingKindFree,
            Notes = "Ollama local model — free tier"
        },

        // ── Unknown catch-all ───────────────────────────────────────
        new()
        {
            Provider = "*",
            Model = "*",
            PricingKind = PricingKindUnknown,
            Notes = "Unknown provider/model — cost recorded as NULL"
        },
    ];
}
