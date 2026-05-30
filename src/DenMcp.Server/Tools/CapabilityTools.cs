using System.ComponentModel;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Mcp;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using ModelContextProtocol.Server;

namespace DenMcp.Server.Tools;

/// <summary>
/// Core MCP tools for the capability service registry.
/// These expose capability definitions, invocation, and the analyze_image convenience wrapper.
/// </summary>
[McpServerToolType]
public sealed class CapabilityTools
{
    // ── List (planner read-only subset) ───────────────────────────────

    [McpToolProfile("admin-current", "planner", "runner")]
    [McpToolBundle("capability")]
    [McpServerTool(Name = "list_capabilities"), Description(
        "List registered capability definitions with optional status/side-effect-level/project filtering. " +
        "Capabilities represent external services or executors that can be invoked through Core.")]
    public static async Task<string> ListCapabilities(
        ICapabilityRepository repo,
        [Description("Optional status filter: enabled, disabled, deprecated.")] string? status = null,
        [Description("Optional side-effect level filter: none, auditable, destructive.")] string? side_effect_level = null,
        [Description("Optional owner project id filter.")] string? owner_project_id = null,
        [Description("Maximum items to return (max 200).")] int limit = 50)
    {
        var capabilities = await repo.ListDefinitionsAsync(new CapabilityListOptions
        {
            Status = status,
            SideEffectLevel = side_effect_level,
            OwnerProjectId = owner_project_id,
            Limit = Math.Clamp(limit, 1, 200),
        });

        var summaries = capabilities.Select(c => new
        {
            capability_id = c.CapabilityId,
            display_name = c.DisplayName,
            status = c.Status,
            executor_kind = c.ExecutorKind,
            side_effect_level = c.SideEffectLevel,
            owner_project_id = c.OwnerProjectId,
        }).ToList();

        return JsonSerializer.Serialize(new
        {
            summary = $"listed {capabilities.Count} capability definition(s)",
            count = capabilities.Count,
            capabilities = summaries,
        }, JsonOpts.Default);
    }

    [McpToolProfile("admin-current", "planner", "runner")]
    [McpToolBundle("capability")]
    [McpServerTool(Name = "get_capability"), Description(
        "Get a single capability definition by its capability_id. " +
        "Returns full record including endpoint configuration, schema, and metadata.")]
    public static async Task<string> GetCapability(
        ICapabilityRepository repo,
        [Description("Capability identifier (e.g. 'vision.analyze_image.v1').")] string capability_id)
    {
        var cap = await repo.GetDefinitionAsync(capability_id);
        if (cap is null)
            return JsonSerializer.Serialize(new
            {
                summary = $"capability '{capability_id}' not found",
                error = true,
            }, JsonOpts.Default);

        return JsonSerializer.Serialize(new
        {
            capability_id = cap.CapabilityId,
            display_name = cap.DisplayName,
            description = cap.Description,
            status = cap.Status,
            executor_kind = cap.ExecutorKind,
            side_effect_level = cap.SideEffectLevel,
            http_endpoint = cap.HttpEndpoint,
            owner_project_id = cap.OwnerProjectId,
            request_schema_json = cap.RequestSchemaJson,
            response_schema_json = cap.ResponseSchemaJson,
            metadata = cap.Metadata,
            created_at = cap.CreatedAt.ToString("o"),
            updated_at = cap.UpdatedAt.ToString("o"),
        }, JsonOpts.Default);
    }

    // ── Upsert (admin-current, runner) ─────────────────────────────────

    [McpToolProfile("admin-current", "runner")]
    [McpToolBundle("capability")]
    [McpServerTool(Name = "upsert_capability_definition"), Description(
        "Register or update a capability definition. Capabilities represent external " +
        "service executors that can be discovered and invoked through Core. " +
        "Core only proxies read-only (side_effect_level='none') HTTP endpoint capabilities.")]
    public static async Task<string> UpsertCapabilityDefinition(
        ICapabilityRepository repo,
        [Description("Unique capability identifier (e.g. 'vision.analyze_image.v1').")] string capability_id,
        [Description("Human-readable display name.")] string display_name,
        [Description("Optional description of what this capability does.")] string? description = null,
        [Description("Status: enabled, disabled, deprecated. Default: enabled.")] string status = "enabled",
        [Description("HTTP endpoint URL for http_endpoint executor kind.")] string? http_endpoint = null,
        [Description("Executor kind: 'http_endpoint' or 'external_service'. Default: external_service.")] string executor_kind = "external_service",
        [Description("Side-effect level: 'none', 'auditable', 'destructive'. Default: none.")] string side_effect_level = "none",
        [Description("Optional project that owns this capability.")] string? owner_project_id = null,
        [Description("Optional JSON schema string for request validation.")] string? request_schema_json = null,
        [Description("Optional JSON schema string for response validation.")] string? response_schema_json = null,
        [Description("Optional JSON metadata.")] string? metadata = null)
    {
        var result = await repo.UpsertDefinitionAsync(new CapabilityDefinition
        {
            CapabilityId = capability_id,
            DisplayName = display_name,
            Description = description ?? "",
            Status = status,
            HttpEndpoint = http_endpoint,
            ExecutorKind = executor_kind,
            SideEffectLevel = side_effect_level,
            OwnerProjectId = owner_project_id,
            RequestSchemaJson = request_schema_json,
            ResponseSchemaJson = response_schema_json,
            Metadata = metadata,
        });

        return JsonSerializer.Serialize(new
        {
            summary = $"upserted capability '{result.CapabilityId}' (status={result.Status})",
            capability_id = result.CapabilityId,
            status = result.Status,
            executor_kind = result.ExecutorKind,
            side_effect_level = result.SideEffectLevel,
        }, JsonOpts.Default);
    }

    // ── Invoke (runner, admin-current) ─────────────────────────────────

    [McpToolProfile("admin-current", "runner")]
    [McpToolBundle("capability")]
    [McpServerTool(Name = "invoke_capability"), Description(
        "Invoke a capability by its capability_id. Core records the invocation in the audit log " +
        "and proxies the call for read-only http_endpoint capabilities. " +
        "Non-read-only capabilities are rejected with a clear error. " +
        "Always produces a terminal audit record regardless of outcome.")]
    public static async Task<string> InvokeCapability(
        ICapabilityInvocationService service,
        [Description("Capability identifier to invoke.")] string capability_id,
        [Description("Caller project id (for audit).")] string caller_project_id,
        [Description("Caller identity (for audit).")] string caller_identity,
        [Description("Optional caller task id (for audit).")] string? caller_task_id = null,
        [Description("Optional JSON request payload.")] string? payload = null,
        [Description("Request timeout in milliseconds (1000-120000, default 30000).")] int timeout_ms = 30000)
    {
        var invocation = await service.InvokeAsync(
            capability_id,
            caller_project_id,
            caller_task_id,
            caller_identity,
            payload,
            timeout_ms);

        return JsonSerializer.Serialize(new
        {
            summary = $"invoked capability '{capability_id}': status={invocation.Status}",
            invocation_id = invocation.Id,
            capability_id = invocation.CapabilityId,
            status = invocation.Status,
            error_message = invocation.ErrorMessage,
            duration_ms = invocation.DurationMs,
            response_payload = invocation.ResponsePayload,
            created_at = invocation.CreatedAt.ToString("o"),
        }, JsonOpts.Default);
    }

    // ── Analyze Image convenience wrapper ──────────────────────────────

    [McpToolProfile("admin-current", "runner")]
    [McpToolBundle("capability")]
    [McpServerTool(Name = "analyze_image"), Description(
        "Convenience wrapper over the vision.analyze_image.v1 capability. " +
        "Accepts an image reference (file path, artifact URL, or Den artifact ref) " +
        "and returns a structured result. Rejects raw base64 and data: URL image input. " +
        "Returns stable structured errors when the vision executor is missing or disabled.")]
    public static async Task<string> AnalyzeImage(
        ICapabilityInvocationService service,
        [Description("Image reference (file path, artifact URL, or Den artifact ref). " +
                     "Data: URLs and raw base64 strings are rejected.")] string image_ref,
        [Description("Caller project id (for audit).")] string caller_project_id,
        [Description("Caller identity (for audit).")] string caller_identity,
        [Description("Optional caller task id (for audit).")] string? caller_task_id = null,
        [Description("Optional vision model prompt.")] string? prompt = null,
        [Description("Optional model preference.")] string? model = null,
        [Description("Optional max tokens for vision response.")] int? max_tokens = null,
        [Description("Request timeout in milliseconds (1000-120000, default 60000).")] int timeout_ms = 60000)
    {
        var result = await service.AnalyzeImageAsync(
            image_ref,
            caller_project_id,
            caller_task_id,
            caller_identity,
            prompt,
            model,
            max_tokens,
            timeout_ms);

        return JsonSerializer.Serialize(new
        {
            summary = result.Success
                ? "Image analysis completed"
                : $"Image analysis failed: {result.Error}",
            success = result.Success,
            status = result.Status,
            description = result.Description,
            error = result.Error,
            duration_ms = result.DurationMs,
            raw_output = result.RawOutput,
        }, JsonOpts.Default);
    }
}
