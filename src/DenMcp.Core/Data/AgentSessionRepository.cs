using DenMcp.Core.Models;
using Microsoft.Data.Sqlite;

namespace DenMcp.Core.Data;

public interface IAgentSessionRepository
{
    Task<AgentSession> CheckInAsync(string agent, string projectId, string? sessionId = null, string? metadata = null);
    Task<bool> HeartbeatAsync(string agent, string projectId);
    Task<bool> CheckOutAsync(string agent, string projectId);
    Task<bool> CheckOutBySessionAsync(string sessionId);
    Task<List<AgentSession>> ListActiveAsync(string? projectId = null, int timeoutMinutes = 5);
    Task<int> CleanupStaleAsync(int timeoutMinutes = 5);
}

public sealed class AgentSessionRepository : IAgentSessionRepository
{
    private readonly DbConnectionFactory _db;

    public AgentSessionRepository(DbConnectionFactory db) => _db = db;

    public async Task<AgentSession> CheckInAsync(string agent, string projectId, string? sessionId = null, string? metadata = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agent_sessions (agent, project_id, session_id, status, checked_in_at, last_heartbeat, metadata)
            VALUES (@agent, @projectId, @sessionId, 'active', datetime('now'), datetime('now'), @metadata)
            ON CONFLICT(agent, project_id) DO UPDATE SET
                session_id = COALESCE(@sessionId, agent_sessions.session_id),
                status = 'active',
                checked_in_at = datetime('now'),
                last_heartbeat = datetime('now'),
                metadata = COALESCE(@metadata, agent_sessions.metadata)
            RETURNING agent, project_id, session_id, status, checked_in_at, last_heartbeat, metadata
            """;
        cmd.Parameters.AddWithValue("@agent", agent);
        cmd.Parameters.AddWithValue("@projectId", projectId);
        cmd.Parameters.AddWithValue("@sessionId", (object?)sessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@metadata", (object?)metadata ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ReadSession(reader);
    }

    public async Task<bool> HeartbeatAsync(string agent, string projectId)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE agent_sessions
            SET last_heartbeat = datetime('now')
            WHERE agent = @agent AND project_id = @projectId AND status = 'active'
            """;
        cmd.Parameters.AddWithValue("@agent", agent);
        cmd.Parameters.AddWithValue("@projectId", projectId);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> CheckOutAsync(string agent, string projectId)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE agent_sessions
            SET status = 'inactive', last_heartbeat = datetime('now')
            WHERE agent = @agent AND project_id = @projectId AND status = 'active'
            """;
        cmd.Parameters.AddWithValue("@agent", agent);
        cmd.Parameters.AddWithValue("@projectId", projectId);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> CheckOutBySessionAsync(string sessionId)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE agent_sessions
            SET status = 'inactive', last_heartbeat = datetime('now')
            WHERE session_id = @sessionId AND status = 'active'
            """;
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<List<AgentSession>> ListActiveAsync(string? projectId = null, int timeoutMinutes = 5)
    {
        await CleanupStaleAsync(timeoutMinutes);

        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var where = "status = 'active'";
        if (projectId is not null)
        {
            where += " AND project_id = @projectId";
            cmd.Parameters.AddWithValue("@projectId", projectId);
        }

        cmd.CommandText = $"""
            SELECT agent, project_id, session_id, status, checked_in_at, last_heartbeat, metadata
            FROM agent_sessions
            WHERE {where}
            ORDER BY last_heartbeat DESC
            """;

        var sessions = new List<AgentSession>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            sessions.Add(ReadSession(reader));
        return sessions;
    }

    public async Task<int> CleanupStaleAsync(int timeoutMinutes = 5)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE agent_sessions
            SET status = 'inactive'
            WHERE status = 'active'
              AND last_heartbeat < datetime('now', @timeout)
            """;
        cmd.Parameters.AddWithValue("@timeout", $"-{timeoutMinutes} minutes");
        return await cmd.ExecuteNonQueryAsync();
    }

    private static AgentSession ReadSession(SqliteDataReader reader) => new()
    {
        Agent = reader.GetString(0),
        ProjectId = reader.GetString(1),
        SessionId = reader.IsDBNull(2) ? null : reader.GetString(2),
        Status = EnumExtensions.ParseAgentSessionStatus(reader.GetString(3)),
        CheckedInAt = DateTime.Parse(reader.GetString(4)),
        LastHeartbeat = DateTime.Parse(reader.GetString(5)),
        Metadata = reader.IsDBNull(6) ? null : reader.GetString(6)
    };
}
