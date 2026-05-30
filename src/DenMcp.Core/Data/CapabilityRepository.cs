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
        int invocationId, string status, string? responsePayload = null,
        string? errorMessage = null, int? durationMs = null);
    Task<CapabilityInvocation?> GetInvocationAsync(int invocationId);
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
                capability_id, display_name, description, status,
                http_endpoint, executor_kind, side_effect_level,
                owner_project_id, request_schema_json, response_schema_json, metadata
            ) VALUES (
                @capabilityId, @displayName, @description, @status,
                @httpEndpoint, @executorKind, @sideEffectLevel,
                @ownerProjectId, @requestSchemaJson, @responseSchemaJson, @metadata
            )
            ON CONFLICT(capability_id) DO UPDATE SET
                display_name = COALESCE(excluded.display_name, capability_definitions.display_name),
                description = COALESCE(excluded.description, capability_definitions.description),
                status = excluded.status,
                http_endpoint = COALESCE(excluded.http_endpoint, capability_definitions.http_endpoint),
                executor_kind = COALESCE(excluded.executor_kind, capability_definitions.executor_kind),
                side_effect_level = COALESCE(excluded.side_effect_level, capability_definitions.side_effect_level),
                owner_project_id = COALESCE(excluded.owner_project_id, capability_definitions.owner_project_id),
                request_schema_json = COALESCE(excluded.request_schema_json, capability_definitions.request_schema_json),
                response_schema_json = COALESCE(excluded.response_schema_json, capability_definitions.response_schema_json),
                metadata = COALESCE(excluded.metadata, capability_definitions.metadata),
                updated_at = datetime('now')
            RETURNING capability_id, display_name, description, status,
                      http_endpoint, executor_kind, side_effect_level,
                      owner_project_id, request_schema_json, response_schema_json, metadata,
                      created_at, updated_at
            """;
        cmd.Parameters.AddWithValue("@capabilityId", definition.CapabilityId);
        cmd.Parameters.AddWithValue("@displayName", definition.DisplayName);
        cmd.Parameters.AddWithValue("@description", definition.Description);
        cmd.Parameters.AddWithValue("@status", definition.Status);
        cmd.Parameters.AddWithValue("@httpEndpoint", (object?)definition.HttpEndpoint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@executorKind", definition.ExecutorKind);
        cmd.Parameters.AddWithValue("@sideEffectLevel", definition.SideEffectLevel);
        cmd.Parameters.AddWithValue("@ownerProjectId", (object?)definition.OwnerProjectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@requestSchemaJson", (object?)definition.RequestSchemaJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@responseSchemaJson", (object?)definition.ResponseSchemaJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@metadata", (object?)definition.Metadata ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ReadDefinition(reader);
    }

    public async Task<CapabilityDefinition?> GetDefinitionAsync(string capabilityId)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT capability_id, display_name, description, status,
                   http_endpoint, executor_kind, side_effect_level,
                   owner_project_id, request_schema_json, response_schema_json, metadata,
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
            SELECT capability_id, display_name, description, status,
                   http_endpoint, executor_kind, side_effect_level,
                   owner_project_id, request_schema_json, response_schema_json, metadata,
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
                capability_id, caller_project_id, caller_task_id, caller_identity,
                status, request_payload, response_payload, error_message, duration_ms, metadata
            ) VALUES (
                @capabilityId, @callerProjectId, @callerTaskId, @callerIdentity,
                @status, @requestPayload, @responsePayload, @errorMessage, @durationMs, @metadata
            )
            RETURNING id, capability_id, caller_project_id, caller_task_id, caller_identity,
                      status, request_payload, response_payload, error_message, duration_ms, metadata,
                      created_at, updated_at
            """;
        cmd.Parameters.AddWithValue("@capabilityId", invocation.CapabilityId);
        cmd.Parameters.AddWithValue("@callerProjectId", invocation.CallerProjectId);
        cmd.Parameters.AddWithValue("@callerTaskId", (object?)invocation.CallerTaskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@callerIdentity", invocation.CallerIdentity);
        cmd.Parameters.AddWithValue("@status", invocation.Status);
        cmd.Parameters.AddWithValue("@requestPayload", (object?)invocation.RequestPayload ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@responsePayload", (object?)invocation.ResponsePayload ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@errorMessage", (object?)invocation.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@durationMs", (object?)invocation.DurationMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@metadata", (object?)invocation.Metadata ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ReadInvocation(reader);
    }

    public async Task<CapabilityInvocation?> UpdateInvocationStatusAsync(
        int invocationId, string status, string? responsePayload = null,
        string? errorMessage = null, int? durationMs = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE capability_invocations
            SET status = @status,
                response_payload = COALESCE(@responsePayload, response_payload),
                error_message = COALESCE(@errorMessage, error_message),
                duration_ms = COALESCE(@durationMs, duration_ms),
                updated_at = datetime('now')
            WHERE id = @id
            RETURNING id, capability_id, caller_project_id, caller_task_id, caller_identity,
                      status, request_payload, response_payload, error_message, duration_ms, metadata,
                      created_at, updated_at
            """;
        cmd.Parameters.AddWithValue("@id", invocationId);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@responsePayload", (object?)responsePayload ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@errorMessage", (object?)errorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@durationMs", (object?)durationMs ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadInvocation(reader) : null;
    }

    public async Task<CapabilityInvocation?> GetInvocationAsync(int invocationId)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, capability_id, caller_project_id, caller_task_id, caller_identity,
                   status, request_payload, response_payload, error_message, duration_ms, metadata,
                   created_at, updated_at
            FROM capability_invocations
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", invocationId);

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
        if (!string.IsNullOrWhiteSpace(options.CallerTaskId))
        {
            where.Add("caller_task_id = @callerTaskId");
            cmd.Parameters.AddWithValue("@callerTaskId", options.CallerTaskId);
        }
        if (!string.IsNullOrWhiteSpace(options.Status))
        {
            where.Add("status = @status");
            cmd.Parameters.AddWithValue("@status", options.Status);
        }

        var whereClause = where.Count > 0 ? $"WHERE {string.Join(" AND ", where)}" : string.Empty;
        cmd.CommandText = $"""
            SELECT id, capability_id, caller_project_id, caller_task_id, caller_identity,
                   status, request_payload, response_payload, error_message, duration_ms, metadata,
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
            Status = reader.GetString(3),
            HttpEndpoint = reader.IsDBNull(4) ? null : reader.GetString(4),
            ExecutorKind = reader.GetString(5),
            SideEffectLevel = reader.GetString(6),
            OwnerProjectId = reader.IsDBNull(7) ? null : reader.GetString(7),
            RequestSchemaJson = reader.IsDBNull(8) ? null : reader.GetString(8),
            ResponseSchemaJson = reader.IsDBNull(9) ? null : reader.GetString(9),
            Metadata = reader.IsDBNull(10) ? null : reader.GetString(10),
            CreatedAt = DateTime.Parse(reader.GetString(11)),
            UpdatedAt = DateTime.Parse(reader.GetString(12)),
        };
    }

    private static CapabilityInvocation ReadInvocation(SqliteDataReader reader)
    {
        return new CapabilityInvocation
        {
            Id = reader.GetInt32(0),
            CapabilityId = reader.GetString(1),
            CallerProjectId = reader.GetString(2),
            CallerTaskId = reader.IsDBNull(3) ? null : reader.GetString(3),
            CallerIdentity = reader.GetString(4),
            Status = reader.GetString(5),
            RequestPayload = reader.IsDBNull(6) ? null : reader.GetString(6),
            ResponsePayload = reader.IsDBNull(7) ? null : reader.GetString(7),
            ErrorMessage = reader.IsDBNull(8) ? null : reader.GetString(8),
            DurationMs = reader.IsDBNull(9) ? null : reader.GetInt32(9),
            Metadata = reader.IsDBNull(10) ? null : reader.GetString(10),
            CreatedAt = DateTime.Parse(reader.GetString(11)),
            UpdatedAt = DateTime.Parse(reader.GetString(12)),
        };
    }
}
