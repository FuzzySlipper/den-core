using System.ComponentModel;
using System.Text.Json;
using DenCore.Data;
using DenCore.Mcp;
using DenCore.Models;
using DenCore.Services;
using ModelContextProtocol.Server;

namespace DenCore.Service.Tools;

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
        [Description("Optional status filter: experimental, active, degraded, disabled.")] string? status = null,
        [Description("Optional side-effect level filter: read_only, notification_only, bounded_write, external_write.")] string? side_effect_level = null,
        [Description("Optional owner project id filter.")] string? owner_project_id = null,
        [Description("Maximum items to return (max 200).")] int limit = 50,
        [Description("Include full definition details.")] bool verbose = false)
    {
        var capabilities = await repo.ListDefinitionsAsync(new CapabilityListOptions
        {
            Status = status,
            SideEffectLevel = side_effect_level,
            OwnerProjectId = owner_project_id,
            Limit = Math.Clamp(limit, 1, 200),
            Verbose = verbose,
        });

        if (verbose)
        {
            return JsonSerializer.Serialize(new
            {
                summary = $"listed {capabilities.Count} capability definition(s)",
                count = capabilities.Count,
                capabilities,
            }, JsonOpts.Default);
        }

        var summaries = capabilities.Select(c => new
        {
            capability_id = c.CapabilityId,
            display_name = c.DisplayName,
            status = c.Status,
            implementation_kind = c.ImplementationKind,
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
        "Returns full record including endpoint configuration, schemas, model config, and metadata.")]
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
            owner_project_id = cap.OwnerProjectId,
            implementation_kind = cap.ImplementationKind,
            service_endpoint = cap.ServiceEndpoint,
            http_method = cap.HttpMethod,
            input_schema_ref = cap.InputSchemaRef,
            output_schema_ref = cap.OutputSchemaRef,
            input_schema_json = cap.InputSchemaJson,
            output_schema_json = cap.OutputSchemaJson,
            side_effect_level = cap.SideEffectLevel,
            status = cap.Status,
            default_model_json = cap.DefaultModelJson,
            fallback_models_json = cap.FallbackModelsJson,
            eval_refs_json = cap.EvalRefsJson,
            timeout_ms = cap.TimeoutMs,
            max_request_bytes = cap.MaxRequestBytes,
            metadata_json = cap.MetadataJson,
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
        "Core only proxies read-only (side_effect_level='read_only') HTTP endpoint capabilities.")]
    public static async Task<string> UpsertCapabilityDefinition(
        ICapabilityRepository repo,
        [Description("Unique capability identifier (e.g. 'vision.analyze_image.v1').")] string capability_id,
        [Description("Human-readable display name.")] string display_name,
        [Description("Optional description of what this capability does.")] string? description = null,
        [Description("Optional project that owns this capability.")] string? owner_project_id = null,
        [Description("Implementation kind: http_endpoint, core_builtin, registry_only. Default: registry_only.")] string implementation_kind = "registry_only",
        [Description("Service endpoint URL for http_endpoint implementation kind.")] string? service_endpoint = null,
        [Description("HTTP method for the endpoint (default: POST).")] string? http_method = null,
        [Description("Reference to an input schema definition.")] string? input_schema_ref = null,
        [Description("Reference to an output schema definition.")] string? output_schema_ref = null,
        [Description("Inline JSON schema for request validation.")] string? input_schema_json = null,
        [Description("Inline JSON schema for response validation.")] string? output_schema_json = null,
        [Description("Side-effect level: read_only, notification_only, bounded_write, external_write. Default: read_only.")] string side_effect_level = "read_only",
        [Description("Status: experimental, active, degraded, disabled. Default: experimental.")] string status = "experimental",
        [Description("Default model configuration as JSON.")] string? default_model_json = null,
        [Description("Fallback model configurations as JSON array.")] string? fallback_models_json = null,
        [Description("Evaluation references as JSON array.")] string? eval_refs_json = null,
        [Description("Timeout in milliseconds (1000-300000, default: 30000).")] int timeout_ms = 30000,
        [Description("Maximum request body size in bytes (default: 10485760).")] int max_request_bytes = 10485760,
        [Description("Arbitrary JSON metadata.")] string? metadata_json = null)
    {
        var result = await repo.UpsertDefinitionAsync(new CapabilityDefinition
        {
            CapabilityId = capability_id,
            DisplayName = display_name,
            Description = description ?? "",
            OwnerProjectId = owner_project_id,
            ImplementationKind = implementation_kind,
            ServiceEndpoint = service_endpoint,
            HttpMethod = http_method ?? DefaultMethods.HttpMethod,
            InputSchemaRef = input_schema_ref,
            OutputSchemaRef = output_schema_ref,
            InputSchemaJson = input_schema_json,
            OutputSchemaJson = output_schema_json,
            SideEffectLevel = side_effect_level,
            Status = status,
            DefaultModelJson = default_model_json,
            FallbackModelsJson = fallback_models_json,
            EvalRefsJson = eval_refs_json,
            TimeoutMs = timeout_ms,
            MaxRequestBytes = max_request_bytes,
            MetadataJson = metadata_json,
        });

        return JsonSerializer.Serialize(new
        {
            summary = $"upserted capability '{result.CapabilityId}' (status={result.Status})",
            capability_id = result.CapabilityId,
            status = result.Status,
            implementation_kind = result.ImplementationKind,
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
        [Description("Optional JSON request payload.")] string? request_json = null,
        [Description("Optional caller project id (for audit).")] string? caller_project_id = null,
        [Description("Optional caller task id (for audit).")] int? caller_task_id = null,
        [Description("Optional caller agent (for audit).")] string? caller_agent = null,
        [Description("Optional caller message id.")] string? caller_message_id = null,
        [Description("Optional caller surface (e.g. 'mcp', 'rest').")] string? caller_surface = null,
        [Description("Request timeout in milliseconds (1000-300000, default 30000).")] int timeout_ms = 30000,
        [Description("Include full invocation record in response.")] bool verbose = false)
    {
        var invocation = await service.InvokeAsync(
            capability_id,
            request_json,
            caller_project_id,
            caller_task_id,
            caller_agent,
            caller_message_id,
            caller_surface,
            timeout_ms);

        if (verbose)
        {
            return JsonSerializer.Serialize(new
            {
                summary = $"invoked capability '{capability_id}': status={invocation.Status}",
                invocation,
            }, JsonOpts.Default);
        }

        return JsonSerializer.Serialize(new
        {
            summary = $"invoked capability '{capability_id}': status={invocation.Status}",
            invocation_id = invocation.InvocationId,
            capability_id = invocation.CapabilityId,
            status = invocation.Status,
            error_type = invocation.ErrorType,
            error_message = invocation.ErrorMessage,
            duration_ms = invocation.DurationMs,
            output_summary = invocation.OutputSummary,
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
        [Description("Question or prompt for the vision model.")] string? question = null,
        [Description("Analysis mode: general, ui_screenshot, diagram, ocr, error_screen, diff. Default: general.")] string mode = "general",
        [Description("Optional caller project id (for audit).")] string? caller_project_id = null,
        [Description("Optional caller task id (for audit).")] int? caller_task_id = null,
        [Description("Optional caller agent (for audit).")] string? caller_agent = null,
        [Description("Whether to include OCR text extraction.")] bool include_ocr = true,
        [Description("Whether to include region detection.")] bool include_regions = false,
        [Description("Request timeout in milliseconds (1000-300000, default 60000).")] int timeout_ms = 60000,
        [Description("Include full invocation record in response.")] bool verbose = false)
    {
        var result = await service.AnalyzeImageAsync(
            image_ref,
            question,
            mode,
            caller_project_id,
            caller_task_id,
            caller_agent,
            include_ocr,
            include_regions,
            timeout_ms);

        return JsonSerializer.Serialize(new
        {
            summary = result.Success
                ? "Image analysis completed"
                : $"Image analysis failed: {result.Error}",
            success = result.Success,
            status = result.Status,
            invocation_id = result.InvocationId,
            description = result.Description,
            error = result.Error,
            error_type = result.ErrorType,
            duration_ms = result.DurationMs,
            raw_output = result.RawOutput,
        }, JsonOpts.Default);
    }
}
