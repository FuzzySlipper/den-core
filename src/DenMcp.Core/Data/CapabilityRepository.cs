using DenMcp.Core.Models;
using Microsoft.Data.Sqlite;

namespace DenMcp.Core.Data;

/// <summary>
/// Core repository for capability definitions and invocation audit records.
/// Capabilities are registered by external service agents and invoked through
/// Core's read-only synchronous HTTP executor proxy.
/// </summary>
public interface ICapabilityRepository
{
    // ── Definitions ───────────────────────────────────────────────────
    Task<CapabilityDefinition> UpsertDefinitionAsync(CapabilityDefinition definition);
    Task<CapabilityDefinition?> GetDefinitionAsync(string capabilityId);
    Task<List<CapabilityDefinition>> ListDefinitionsAsync(CapabilityListOptions options);

    // ── Invocations (audit) ───────────────────────────────────────────
    Task<CapabilityInvocation> CreateInvocationAsync(CapabilityInvocation invocation);
    Task<CapabilityInvocation?> UpdateInvocationStatusAsync(
        int id, string status, string? outputSummary = null,
        string? errorType = null, string? errorMessage = null, int? durationMs = null,
        DateTime? completedAt = null, string? outputJson = null,
        string? modelProvider = null, string? modelName = null, string? modelVersion = null,
        string? timingsMsJson = null, string? costJson = null,
        string? outputArtifactRefsJson = null, string? metadataJson = null);
    Task<CapabilityInvocation?> GetInvocationByInvocationIdAsync(string invocationId);
    Task<CapabilityInvocation?> GetInvocationByIdAsync(int id);
    Task<List<CapabilityInvocation>> ListInvocationsAsync(InvocationListOptions options);
}

public sealed class CapabilityRepository : ICapabilityRepository
{
    private readonly DbConnectionFactory _db;

    public CapabilityRepository(DbConnectionFactory db)
    {
        _db = db;
    }

    // ── Definitions ──────────────────────────────────────────────────────

    public async Task<CapabilityDefinition> UpsertDefinitionAsync(CapabilityDefinition definition)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO capability_definitions (
                capability_id, display_name, description, owner_project_id,
                implementation_kind, service_endpoint, http_method,
                input_schema_ref, output_schema_ref, input_schema_json, output_schema_json,
                side_effect_level, status,
                default_model_json, fallback_models_json, eval_refs_json,
                timeout_ms, max_request_bytes, metadata_json
            ) VALUES (
                @capabilityId, @displayName, @description, @ownerProjectId,
                @implementationKind, @serviceEndpoint, @httpMethod,
                @inputSchemaRef, @outputSchemaRef, @inputSchemaJson, @outputSchemaJson,
                @sideEffectLevel, @status,
                @defaultModelJson, @fallbackModelsJson, @evalRefsJson,
                @timeoutMs, @maxRequestBytes, @metadataJson
            )
            ON CONFLICT(capability_id) DO UPDATE SET
                display_name = COALESCE(excluded.display_name, capability_definitions.display_name),
                description = COALESCE(excluded.description, capability_definitions.description),
                owner_project_id = COALESCE(excluded.owner_project_id, capability_definitions.owner_project_id),
                implementation_kind = COALESCE(excluded.implementation_kind, capability_definitions.implementation_kind),
                service_endpoint = COALESCE(excluded.service_endpoint, capability_definitions.service_endpoint),
                http_method = COALESCE(excluded.http_method, capability_definitions.http_method),
                input_schema_ref = COALESCE(excluded.input_schema_ref, capability_definitions.input_schema_ref),
                output_schema_ref = COALESCE(excluded.output_schema_ref, capability_definitions.output_schema_ref),
                input_schema_json = COALESCE(excluded.input_schema_json, capability_definitions.input_schema_json),
                output_schema_json = COALESCE(excluded.output_schema_json, capability_definitions.output_schema_json),
                side_effect_level = excluded.side_effect_level,
                status = excluded.status,
                default_model_json = COALESCE(excluded.default_model_json, capability_definitions.default_model_json),
                fallback_models_json = COALESCE(excluded.fallback_models_json, capability_definitions.fallback_models_json),
                eval_refs_json = COALESCE(excluded.eval_refs_json, capability_definitions.eval_refs_json),
                timeout_ms = COALESCE(excluded.timeout_ms, capability_definitions.timeout_ms),
                max_request_bytes = COALESCE(excluded.max_request_bytes, capability_definitions.max_request_bytes),
                metadata_json = COALESCE(excluded.metadata_json, capability_definitions.metadata_json),
                updated_at = datetime('now')
            RETURNING capability_id, display_name, description, owner_project_id,
                      implementation_kind, service_endpoint, http_method,
                      input_schema_ref, output_schema_ref, input_schema_json, output_schema_json,
                      side_effect_level, status,
                      default_model_json, fallback_models_json, eval_refs_json,
                      timeout_ms, max_request_bytes, metadata_json,
                      created_at, updated_at
            """;
        cmd.Parameters.AddWithValue("@capabilityId", definition.CapabilityId);
        cmd.Parameters.AddWithValue("@displayName", definition.DisplayName);
        cmd.Parameters.AddWithValue("@description", definition.Description);
        cmd.Parameters.AddWithValue("@ownerProjectId", (object?)definition.OwnerProjectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@implementationKind", definition.ImplementationKind);
        cmd.Parameters.AddWithValue("@serviceEndpoint", (object?)definition.ServiceEndpoint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@httpMethod", definition.HttpMethod);
        cmd.Parameters.AddWithValue("@inputSchemaRef", (object?)definition.InputSchemaRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@outputSchemaRef", (object?)definition.OutputSchemaRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@inputSchemaJson", (object?)definition.InputSchemaJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@outputSchemaJson", (object?)definition.OutputSchemaJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sideEffectLevel", definition.SideEffectLevel);
        cmd.Parameters.AddWithValue("@status", definition.Status);
        cmd.Parameters.AddWithValue("@defaultModelJson", (object?)definition.DefaultModelJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fallbackModelsJson", (object?)definition.FallbackModelsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@evalRefsJson", (object?)definition.EvalRefsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@timeoutMs", definition.TimeoutMs);
        cmd.Parameters.AddWithValue("@maxRequestBytes", definition.MaxRequestBytes);
        cmd.Parameters.AddWithValue("@metadataJson", (object?)definition.MetadataJson ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ReadDefinition(reader);
    }

    public async Task<CapabilityDefinition?> GetDefinitionAsync(string capabilityId)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT capability_id, display_name, description, owner_project_id,
                   implementation_kind, service_endpoint, http_method,
                   input_schema_ref, output_schema_ref, input_schema_json, output_schema_json,
                   side_effect_level, status,
                   default_model_json, fallback_models_json, eval_refs_json,
                   timeout_ms, max_request_bytes, metadata_json,
                   created_at, updated_at
            FROM capability_definitions
            WHERE capability_id = @capabilityId
            """;
        cmd.Parameters.AddWithValue("@capabilityId", capabilityId);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadDefinition(reader) : null;
    }

    public async Task<List<CapabilityDefinition>> ListDefinitionsAsync(CapabilityListOptions options)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.Status))
        {
            where.Add("status = @status");
            cmd.Parameters.AddWithValue("@status", options.Status);
        }
        if (!string.IsNullOrWhiteSpace(options.SideEffectLevel))
        {
            where.Add("side_effect_level = @sideEffectLevel");
            cmd.Parameters.AddWithValue("@sideEffectLevel", options.SideEffectLevel);
        }
        if (!string.IsNullOrWhiteSpace(options.OwnerProjectId))
        {
            where.Add("owner_project_id = @ownerProjectId");
            cmd.Parameters.AddWithValue("@ownerProjectId", options.OwnerProjectId);
        }

        var whereClause = where.Count > 0 ? $"WHERE {string.Join(" AND ", where)}" : string.Empty;
        cmd.CommandText = $"""
            SELECT capability_id, display_name, description, owner_project_id,
                   implementation_kind, service_endpoint, http_method,
                   input_schema_ref, output_schema_ref, input_schema_json, output_schema_json,
                   side_effect_level, status,
                   default_model_json, fallback_models_json, eval_refs_json,
                   timeout_ms, max_request_bytes, metadata_json,
                   created_at, updated_at
            FROM capability_definitions
            {whereClause}
            ORDER BY updated_at DESC, capability_id
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(options.Limit, 1, 200));

        var results = new List<CapabilityDefinition>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(ReadDefinition(reader));
        return results;
    }

    // ── Invocations ──────────────────────────────────────────────────────

    public async Task<CapabilityInvocation> CreateInvocationAsync(CapabilityInvocation invocation)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO capability_invocations (
                invocation_id, capability_id, capability_version,
                caller_agent, caller_project_id, caller_task_id,
                caller_message_id, caller_surface,
                input_artifact_refs_json, request_json, request_hash,
                status, started_at, completed_at,
                duration_ms, error_type, error_message, metadata_json
            ) VALUES (
                @invocationId, @capabilityId, @capabilityVersion,
                @callerAgent, @callerProjectId, @callerTaskId,
                @callerMessageId, @callerSurface,
                @inputArtifactRefsJson, @requestJson, @requestHash,
                @status, @startedAt, @completedAt,
                @durationMs, @errorType, @errorMessage, @metadataJson
            )
            RETURNING id, invocation_id, capability_id, capability_version,
                      caller_agent, caller_project_id, caller_task_id,
                      caller_message_id, caller_surface,
                      input_artifact_refs_json, request_json, request_hash,
                      status, started_at, completed_at, duration_ms,
                      model_provider, model_name, model_version,
                      timings_ms_json, cost_json,
                      output_summary, output_json, output_artifact_refs_json,
                      error_type, error_message, metadata_json,
                      created_at, updated_at
            """;
        cmd.Parameters.AddWithValue("@invocationId", invocation.InvocationId ?? GenerateInvocationId());
        cmd.Parameters.AddWithValue("@capabilityId", invocation.CapabilityId);
        cmd.Parameters.AddWithValue("@capabilityVersion", (object?)invocation.CapabilityVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@callerAgent", (object?)invocation.CallerAgent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@callerProjectId", invocation.CallerProjectId);
        cmd.Parameters.AddWithValue("@callerTaskId", (object?)invocation.CallerTaskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@callerMessageId", (object?)invocation.CallerMessageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@callerSurface", (object?)invocation.CallerSurface ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@inputArtifactRefsJson", (object?)invocation.InputArtifactRefsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@requestJson", (object?)invocation.RequestJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@requestHash", (object?)invocation.RequestHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", invocation.Status);
        cmd.Parameters.AddWithValue("@startedAt", (object?)(invocation.StartedAt?.ToString("o")) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@completedAt", (object?)(invocation.CompletedAt?.ToString("o")) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@durationMs", (object?)invocation.DurationMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@errorType", (object?)invocation.ErrorType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@errorMessage", (object?)invocation.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@metadataJson", (object?)invocation.MetadataJson ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ReadInvocation(reader);
    }

    public async Task<CapabilityInvocation?> UpdateInvocationStatusAsync(
        int id, string status, string? outputSummary = null,
        string? errorType = null, string? errorMessage = null, int? durationMs = null,
        DateTime? completedAt = null, string? outputJson = null,
        string? modelProvider = null, string? modelName = null, string? modelVersion = null,
        string? timingsMsJson = null, string? costJson = null,
        string? outputArtifactRefsJson = null, string? metadataJson = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE capability_invocations
            SET status = @status,
                output_summary = COALESCE(@outputSummary, output_summary),
                error_type = COALESCE(@errorType, error_type),
                error_message = COALESCE(@errorMessage, error_message),
                duration_ms = COALESCE(@durationMs, duration_ms),
                completed_at = COALESCE(@completedAt, completed_at),
                output_json = COALESCE(@outputJson, output_json),
                model_provider = COALESCE(@modelProvider, model_provider),
                model_name = COALESCE(@modelName, model_name),
                model_version = COALESCE(@modelVersion, model_version),
                timings_ms_json = COALESCE(@timingsMsJson, timings_ms_json),
                cost_json = COALESCE(@costJson, cost_json),
                output_artifact_refs_json = COALESCE(@outputArtifactRefsJson, output_artifact_refs_json),
                metadata_json = COALESCE(@metadataJson, metadata_json),
                updated_at = datetime('now')
            WHERE id = @id
            RETURNING id, invocation_id, capability_id, capability_version,
                      caller_agent, caller_project_id, caller_task_id,
                      caller_message_id, caller_surface,
                      input_artifact_refs_json, request_json, request_hash,
                      status, started_at, completed_at, duration_ms,
                      model_provider, model_name, model_version,
                      timings_ms_json, cost_json,
                      output_summary, output_json, output_artifact_refs_json,
                      error_type, error_message, metadata_json,
                      created_at, updated_at
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@outputSummary", (object?)outputSummary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@errorType", (object?)errorType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@errorMessage", (object?)errorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@durationMs", (object?)durationMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@completedAt", (object?)(completedAt?.ToString("o")) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@outputJson", (object?)outputJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@modelProvider", (object?)modelProvider ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@modelName", (object?)modelName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@modelVersion", (object?)modelVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@timingsMsJson", (object?)timingsMsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@costJson", (object?)costJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@outputArtifactRefsJson", (object?)outputArtifactRefsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@metadataJson", (object?)metadataJson ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadInvocation(reader) : null;
    }

    public async Task<CapabilityInvocation?> GetInvocationByInvocationIdAsync(string invocationId)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, invocation_id, capability_id, capability_version,
                   caller_agent, caller_project_id, caller_task_id,
                   caller_message_id, caller_surface,
                   input_artifact_refs_json, request_json, request_hash,
                   status, started_at, completed_at, duration_ms,
                   model_provider, model_name, model_version,
                   timings_ms_json, cost_json,
                   output_summary, output_json, output_artifact_refs_json,
                   error_type, error_message, metadata_json,
                   created_at, updated_at
            FROM capability_invocations
            WHERE invocation_id = @invocationId
            """;
        cmd.Parameters.AddWithValue("@invocationId", invocationId);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadInvocation(reader) : null;
    }

    public async Task<CapabilityInvocation?> GetInvocationByIdAsync(int id)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, invocation_id, capability_id, capability_version,
                   caller_agent, caller_project_id, caller_task_id,
                   caller_message_id, caller_surface,
                   input_artifact_refs_json, request_json, request_hash,
                   status, started_at, completed_at, duration_ms,
                   model_provider, model_name, model_version,
                   timings_ms_json, cost_json,
                   output_summary, output_json, output_artifact_refs_json,
                   error_type, error_message, metadata_json,
                   created_at, updated_at
            FROM capability_invocations
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadInvocation(reader) : null;
    }

    public async Task<List<CapabilityInvocation>> ListInvocationsAsync(InvocationListOptions options)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.CapabilityId))
        {
            where.Add("capability_id = @capabilityId");
            cmd.Parameters.AddWithValue("@capabilityId", options.CapabilityId);
        }
        if (!string.IsNullOrWhiteSpace(options.CallerProjectId))
        {
            where.Add("caller_project_id = @callerProjectId");
            cmd.Parameters.AddWithValue("@callerProjectId", options.CallerProjectId);
        }
        if (options.CallerTaskId.HasValue)
        {
            where.Add("caller_task_id = @callerTaskId");
            cmd.Parameters.AddWithValue("@callerTaskId", options.CallerTaskId.Value);
        }
        if (!string.IsNullOrWhiteSpace(options.Status))
        {
            where.Add("status = @status");
            cmd.Parameters.AddWithValue("@status", options.Status);
        }

        var whereClause = where.Count > 0 ? $"WHERE {string.Join(" AND ", where)}" : string.Empty;
        cmd.CommandText = $"""
            SELECT id, invocation_id, capability_id, capability_version,
                   caller_agent, caller_project_id, caller_task_id,
                   caller_message_id, caller_surface,
                   input_artifact_refs_json, request_json, request_hash,
                   status, started_at, completed_at, duration_ms,
                   model_provider, model_name, model_version,
                   timings_ms_json, cost_json,
                   output_summary, output_json, output_artifact_refs_json,
                   error_type, error_message, metadata_json,
                   created_at, updated_at
            FROM capability_invocations
            {whereClause}
            ORDER BY created_at DESC, id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(options.Limit, 1, 200));

        var results = new List<CapabilityInvocation>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(ReadInvocation(reader));
        return results;
    }

    // ── Reader helpers ───────────────────────────────────────────────────

    private static CapabilityDefinition ReadDefinition(SqliteDataReader reader)
    {
        return new CapabilityDefinition
        {
            CapabilityId = reader.GetString(0),
            DisplayName = reader.GetString(1),
            Description = reader.GetString(2),
            OwnerProjectId = reader.IsDBNull(3) ? null : reader.GetString(3),
            ImplementationKind = reader.GetString(4),
            ServiceEndpoint = reader.IsDBNull(5) ? null : reader.GetString(5),
            HttpMethod = reader.GetString(6),
            InputSchemaRef = reader.IsDBNull(7) ? null : reader.GetString(7),
            OutputSchemaRef = reader.IsDBNull(8) ? null : reader.GetString(8),
            InputSchemaJson = reader.IsDBNull(9) ? null : reader.GetString(9),
            OutputSchemaJson = reader.IsDBNull(10) ? null : reader.GetString(10),
            SideEffectLevel = reader.GetString(11),
            Status = reader.GetString(12),
            DefaultModelJson = reader.IsDBNull(13) ? null : reader.GetString(13),
            FallbackModelsJson = reader.IsDBNull(14) ? null : reader.GetString(14),
            EvalRefsJson = reader.IsDBNull(15) ? null : reader.GetString(15),
            TimeoutMs = reader.GetInt32(16),
            MaxRequestBytes = reader.GetInt32(17),
            MetadataJson = reader.IsDBNull(18) ? null : reader.GetString(18),
            CreatedAt = DateTime.Parse(reader.GetString(19)),
            UpdatedAt = DateTime.Parse(reader.GetString(20)),
        };
    }

    private static CapabilityInvocation ReadInvocation(SqliteDataReader reader)
    {
        return new CapabilityInvocation
        {
            Id = reader.GetInt32(0),
            InvocationId = reader.IsDBNull(1) ? null : reader.GetString(1),
            CapabilityId = reader.GetString(2),
            CapabilityVersion = reader.IsDBNull(3) ? null : reader.GetString(3),
            CallerAgent = reader.IsDBNull(4) ? null : reader.GetString(4),
            CallerProjectId = reader.GetString(5),
            CallerTaskId = reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6),
            CallerMessageId = reader.IsDBNull(7) ? null : reader.GetString(7),
            CallerSurface = reader.IsDBNull(8) ? null : reader.GetString(8),
            InputArtifactRefsJson = reader.IsDBNull(9) ? null : reader.GetString(9),
            RequestJson = reader.IsDBNull(10) ? null : reader.GetString(10),
            RequestHash = reader.IsDBNull(11) ? null : reader.GetString(11),
            Status = reader.GetString(12),
            StartedAt = reader.IsDBNull(13) ? (DateTime?)null : DateTime.Parse(reader.GetString(13)),
            CompletedAt = reader.IsDBNull(14) ? (DateTime?)null : DateTime.Parse(reader.GetString(14)),
            DurationMs = reader.IsDBNull(15) ? (int?)null : reader.GetInt32(15),
            ModelProvider = reader.IsDBNull(16) ? null : reader.GetString(16),
            ModelName = reader.IsDBNull(17) ? null : reader.GetString(17),
            ModelVersion = reader.IsDBNull(18) ? null : reader.GetString(18),
            TimingsMsJson = reader.IsDBNull(19) ? null : reader.GetString(19),
            CostJson = reader.IsDBNull(20) ? null : reader.GetString(20),
            OutputSummary = reader.IsDBNull(21) ? null : reader.GetString(21),
            OutputJson = reader.IsDBNull(22) ? null : reader.GetString(22),
            OutputArtifactRefsJson = reader.IsDBNull(23) ? null : reader.GetString(23),
            ErrorType = reader.IsDBNull(24) ? null : reader.GetString(24),
            ErrorMessage = reader.IsDBNull(25) ? null : reader.GetString(25),
            MetadataJson = reader.IsDBNull(26) ? null : reader.GetString(26),
            CreatedAt = DateTime.Parse(reader.GetString(27)),
            UpdatedAt = DateTime.Parse(reader.GetString(28)),
        };
    }

    /// <summary>
    /// Generate a unique invocation ID (capinv_&lt;timestamp&gt;_&lt;random&gt;).
    /// </summary>
    internal static string GenerateInvocationId()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var random = Random.Shared.NextInt64(100000, 999999);
        return $"capinv_{timestamp}_{random}";
    }
}
