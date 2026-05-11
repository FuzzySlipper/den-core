using DenMcp.Core.Models;
using Microsoft.Data.Sqlite;

namespace DenMcp.Core.Data;

public interface IAgentInstanceBindingRepository
{
    Task<AgentInstanceBinding> UpsertAsync(AgentInstanceBinding binding);
    Task<bool> HeartbeatAsync(string instanceId);
    Task<bool> CheckOutAsync(string instanceId);
    Task<int> CheckOutBySessionAsync(string sessionId);
    Task<AgentInstanceBinding?> GetActiveByInstanceIdAsync(string instanceId, int timeoutMinutes = 5);
    Task<List<AgentInstanceBinding>> ListAsync(AgentInstanceBindingListOptions? options = null);
    Task<int> CleanupStaleAsync(int timeoutMinutes = 5);
}

public sealed class AgentInstanceBindingRepository : IAgentInstanceBindingRepository
{
    private readonly DbConnectionFactory _db;

    public AgentInstanceBindingRepository(DbConnectionFactory db) => _db = db;

    public async Task<AgentInstanceBinding> UpsertAsync(AgentInstanceBinding binding)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agent_instance_bindings (
                instance_id,
                project_id,
                agent_identity,
                agent_family,
                role,
                transport_kind,
                session_id,
                status,
                metadata,
                checked_in_at,
                last_heartbeat
            )
            VALUES (
                @instanceId,
                @projectId,
                @agentIdentity,
                @agentFamily,
                @role,
                @transportKind,
                @sessionId,
                @status,
                @metadata,
                datetime('now'),
                datetime('now')
            )
            ON CONFLICT(instance_id) DO UPDATE SET
                project_id = excluded.project_id,
                agent_identity = excluded.agent_identity,
                agent_family = excluded.agent_family,
                role = COALESCE(excluded.role, agent_instance_bindings.role),
                transport_kind = excluded.transport_kind,
                session_id = COALESCE(excluded.session_id, agent_instance_bindings.session_id),
                status = excluded.status,
                metadata = COALESCE(excluded.metadata, agent_instance_bindings.metadata),
                checked_in_at = datetime('now'),
                last_heartbeat = datetime('now')
            RETURNING
                instance_id,
                project_id,
                agent_identity,
                agent_family,
                role,
                transport_kind,
                session_id,
                status,
                metadata,
                checked_in_at,
                last_heartbeat
            """;
        AddParameters(cmd, binding);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ReadBinding(reader);
    }

    public async Task<bool> HeartbeatAsync(string instanceId)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE agent_instance_bindings
            SET last_heartbeat = datetime('now')
            WHERE instance_id = @instanceId AND status IN ('active', 'degraded')
            """;
        cmd.Parameters.AddWithValue("@instanceId", instanceId);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> CheckOutAsync(string instanceId)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE agent_instance_bindings
            SET status = 'inactive', last_heartbeat = datetime('now')
            WHERE instance_id = @instanceId AND status IN ('active', 'degraded')
            """;
        cmd.Parameters.AddWithValue("@instanceId", instanceId);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<int> CheckOutBySessionAsync(string sessionId)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE agent_instance_bindings
            SET status = 'inactive', last_heartbeat = datetime('now')
            WHERE session_id = @sessionId AND status IN ('active', 'degraded')
            """;
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<AgentInstanceBinding?> GetActiveByInstanceIdAsync(string instanceId, int timeoutMinutes = 5)
    {
        await CleanupStaleAsync(timeoutMinutes);

        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                instance_id,
                project_id,
                agent_identity,
                agent_family,
                role,
                transport_kind,
                session_id,
                status,
                metadata,
                checked_in_at,
                last_heartbeat
            FROM agent_instance_bindings
            WHERE instance_id = @instanceId AND status IN ('active', 'degraded')
            """;
        cmd.Parameters.AddWithValue("@instanceId", instanceId);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadBinding(reader) : null;
    }

    public async Task<List<AgentInstanceBinding>> ListAsync(AgentInstanceBindingListOptions? options = null)
    {
        options ??= new AgentInstanceBindingListOptions();
        await CleanupStaleAsync(options.TimeoutMinutes);

        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var where = new List<string>();

        if (!string.IsNullOrWhiteSpace(options.ProjectId))
        {
            where.Add("project_id = @projectId");
            cmd.Parameters.AddWithValue("@projectId", options.ProjectId);
        }

        if (!string.IsNullOrWhiteSpace(options.AgentIdentity))
        {
            where.Add("agent_identity = @agentIdentity");
            cmd.Parameters.AddWithValue("@agentIdentity", options.AgentIdentity);
        }

        if (!string.IsNullOrWhiteSpace(options.Role))
        {
            where.Add("role = @role");
            cmd.Parameters.AddWithValue("@role", options.Role);
        }

        if (!string.IsNullOrWhiteSpace(options.TransportKind))
        {
            where.Add("transport_kind = @transportKind");
            cmd.Parameters.AddWithValue("@transportKind", options.TransportKind);
        }

        if (options.Statuses is { Length: > 0 })
        {
            var placeholders = new List<string>();
            for (var i = 0; i < options.Statuses.Length; i++)
            {
                var name = $"@status{i}";
                placeholders.Add(name);
                cmd.Parameters.AddWithValue(name, options.Statuses[i].ToDbValue());
            }

            where.Add($"status IN ({string.Join(", ", placeholders)})");
        }

        var whereClause = where.Count > 0 ? $"WHERE {string.Join(" AND ", where)}" : string.Empty;
        cmd.CommandText = $"""
            SELECT
                instance_id,
                project_id,
                agent_identity,
                agent_family,
                role,
                transport_kind,
                session_id,
                status,
                metadata,
                checked_in_at,
                last_heartbeat
            FROM agent_instance_bindings
            {whereClause}
            ORDER BY last_heartbeat DESC, instance_id ASC
            """;

        var bindings = new List<AgentInstanceBinding>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            bindings.Add(ReadBinding(reader));
        return bindings;
    }

    public async Task<int> CleanupStaleAsync(int timeoutMinutes = 5)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE agent_instance_bindings
            SET status = 'inactive'
            WHERE status IN ('active', 'degraded')
              AND last_heartbeat < datetime('now', @timeout)
            """;
        cmd.Parameters.AddWithValue("@timeout", $"-{timeoutMinutes} minutes");
        return await cmd.ExecuteNonQueryAsync();
    }

    private static void AddParameters(SqliteCommand cmd, AgentInstanceBinding binding)
    {
        cmd.Parameters.AddWithValue("@instanceId", binding.InstanceId);
        cmd.Parameters.AddWithValue("@projectId", binding.ProjectId);
        cmd.Parameters.AddWithValue("@agentIdentity", binding.AgentIdentity);
        cmd.Parameters.AddWithValue("@agentFamily", binding.AgentFamily);
        cmd.Parameters.AddWithValue("@role", (object?)binding.Role ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@transportKind", binding.TransportKind);
        cmd.Parameters.AddWithValue("@sessionId", (object?)binding.SessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", binding.Status.ToDbValue());
        cmd.Parameters.AddWithValue("@metadata", (object?)binding.Metadata ?? DBNull.Value);
    }

    private static AgentInstanceBinding ReadBinding(SqliteDataReader reader) => new()
    {
        InstanceId = reader.GetString(0),
        ProjectId = reader.GetString(1),
        AgentIdentity = reader.GetString(2),
        AgentFamily = reader.GetString(3),
        Role = reader.IsDBNull(4) ? null : reader.GetString(4),
        TransportKind = reader.GetString(5),
        SessionId = reader.IsDBNull(6) ? null : reader.GetString(6),
        Status = EnumExtensions.ParseAgentInstanceBindingStatus(reader.GetString(7)),
        Metadata = reader.IsDBNull(8) ? null : reader.GetString(8),
        CheckedInAt = DateTime.Parse(reader.GetString(9)),
        LastHeartbeat = DateTime.Parse(reader.GetString(10))
    };
}
