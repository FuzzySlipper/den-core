using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using DenMcp.Core;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;

namespace DenMcp.Server.Routes;

public static class GatewayContractRoutes
{
    public static void MapGatewayContractRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/gateway");

        group.MapGet("/readiness", async (HttpContext http, DenMcpOptions options, DbConnectionFactory db) =>
        {
            if (!IsAuthorized(http, options))
                return Results.Unauthorized();

            var checkedAt = DateTime.UtcNow;
            var checks = new Dictionary<string, GatewayReadinessCheck>
            {
                ["process"] = new()
                {
                    Status = "ready",
                    Message = "Den Core process is reachable.",
                    Metadata = new Dictionary<string, object?>
                    {
                        ["version"] = BuildInfo.Version,
                        ["informational_version"] = BuildInfo.InformationalVersion,
                        ["commit"] = BuildInfo.Commit
                    }
                },
                ["app"] = new()
                {
                    Status = "ready",
                    Message = "Den Core application pipeline is initialized."
                },
                ["service_auth"] = new()
                {
                    Status = HasServiceToken(options) ? "ready" : "degraded",
                    Message = HasServiceToken(options)
                        ? "Gateway service token enforcement is configured."
                        : "Gateway service token enforcement is not configured; local/stub callers are allowed.",
                    Metadata = new Dictionary<string, object?>
                    {
                        ["required"] = HasServiceToken(options)
                    }
                }
            };

            await AddDatabaseReadinessChecksAsync(db, checks);

            var status = checks.Values.Any(c => c.Status == "blocked")
                ? "blocked"
                : checks.Values.Any(c => c.Status == "degraded") ? "degraded" : "ready";

            return Results.Ok(new GatewayReadinessResponse
            {
                Status = status,
                Service = "den-core-gateway-contract",
                CheckedAt = checkedAt,
                Checks = checks
            });
        });

        group.MapGet("/bindings", async (
            HttpContext http,
            DenMcpOptions options,
            IAgentInstanceBindingRepository repo,
            string? projectId,
            string? status,
            string? role,
            string? agentIdentity,
            string? transportKind,
            int? timeoutMinutes) =>
        {
            if (!IsAuthorized(http, options))
                return Results.Unauthorized();

            AgentInstanceBindingStatus[]? statuses = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                try
                {
                    statuses = status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(EnumExtensions.ParseAgentInstanceBindingStatus)
                        .ToArray();
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }

            var bindings = await repo.ListAsync(new AgentInstanceBindingListOptions
            {
                ProjectId = projectId,
                AgentIdentity = agentIdentity,
                Role = role,
                TransportKind = transportKind,
                Statuses = statuses ??
                [
                    AgentInstanceBindingStatus.Active,
                    AgentInstanceBindingStatus.Degraded
                ],
                TimeoutMinutes = Math.Clamp(timeoutMinutes ?? 5, 1, 120)
            });

            return Results.Ok(new GatewayBindingSnapshotPage
            {
                GeneratedAt = DateTime.UtcNow,
                Items = bindings.Select(ToGatewayBindingSnapshot).ToList()
            });
        });

        group.MapPost("/sentinel/events", async (
            HttpContext http,
            DenMcpOptions options,
            IAgentStreamRepository agentStream,
            GatewaySentinelEventRequest req) =>
        {
            if (!IsAuthorized(http, options))
                return Results.Unauthorized();

            var eventType = NormalizeToken(req.EventType ?? "reconciliation");
            var state = NormalizeToken(req.State ?? "observed");
            var sentinelId = string.IsNullOrWhiteSpace(req.SentinelId) ? "den-gateway-sentinel" : req.SentinelId.Trim();
            var observedAt = req.ObservedAt ?? DateTime.UtcNow;

            if (eventType.Length == 0)
                return Results.BadRequest(new { error = "event_type must contain at least one alphanumeric character" });
            if (state.Length == 0)
                return Results.BadRequest(new { error = "state must contain at least one alphanumeric character" });

            var dedupeKey = string.IsNullOrWhiteSpace(req.DedupeKey)
                ? BuildSentinelDedupeKey(sentinelId, req.ProjectId, eventType, state, req.OutageId, req.Cursor, observedAt)
                : req.DedupeKey.Trim();

            var metadata = new Dictionary<string, object?>
            {
                ["gateway_contract"] = "sentinel_event/v1",
                ["sentinel_id"] = sentinelId,
                ["event_type"] = eventType,
                ["state"] = state,
                ["outage_id"] = req.OutageId,
                ["reason"] = req.Reason,
                ["observed_at"] = observedAt,
                ["cursor"] = req.Cursor
            };
            if (req.Metadata is not null)
                metadata["gateway_metadata"] = req.Metadata;

            var entry = await agentStream.AppendAsync(new AgentStreamEntry
            {
                StreamKind = AgentStreamKind.Ops,
                EventType = $"gateway_sentinel_{eventType}",
                ProjectId = string.IsNullOrWhiteSpace(req.ProjectId) ? null : req.ProjectId.Trim(),
                Sender = sentinelId,
                SenderInstanceId = sentinelId,
                DeliveryMode = state is "outage" or "paused" or "degraded"
                    ? AgentStreamDeliveryMode.Wake
                    : AgentStreamDeliveryMode.RecordOnly,
                Body = BuildSentinelBody(eventType, state, req.ProjectId, req.OutageId, req.Reason),
                Metadata = ToJsonElement(metadata),
                DedupKey = dedupeKey
            });

            return Results.Ok(new GatewaySentinelEventResponse
            {
                Status = "accepted",
                AgentStreamEntryId = entry.Id,
                EventType = $"gateway_sentinel_{eventType}",
                DedupeKey = dedupeKey,
                OutboxCursor = FormatCursor(entry.Id)
            });
        });
    }

    private static async Task AddDatabaseReadinessChecksAsync(DbConnectionFactory db, Dictionary<string, GatewayReadinessCheck> checks)
    {
        try
        {
            await using var conn = await db.CreateConnectionAsync();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT 1";
                await cmd.ExecuteScalarAsync();
            }

            checks["database"] = new GatewayReadinessCheck
            {
                Status = "ready",
                Message = "SQLite database is reachable."
            };

            var requiredTables = new[] { "agent_instance_bindings", "agent_stream_entries", "tasks", "messages" };
            var presentTables = new HashSet<string>(StringComparer.Ordinal);
            await using (var tableCmd = conn.CreateCommand())
            {
                tableCmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
                await using var reader = await tableCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    presentTables.Add(reader.GetString(0));
            }

            var missingTables = requiredTables.Where(t => !presentTables.Contains(t)).ToArray();
            checks["migrations"] = new GatewayReadinessCheck
            {
                Status = missingTables.Length == 0 ? "ready" : "blocked",
                Message = missingTables.Length == 0
                    ? "Gateway-relevant Core tables are present."
                    : "Gateway-relevant Core tables are missing.",
                Metadata = new Dictionary<string, object?>
                {
                    ["required_tables"] = requiredTables,
                    ["missing_tables"] = missingTables
                }
            };

            checks["gateway_contract"] = new GatewayReadinessCheck
            {
                Status = missingTables.Length == 0 ? "ready" : "blocked",
                Message = "Gateway contract endpoints are mapped by Den Core.",
                Metadata = new Dictionary<string, object?>
                {
                    ["endpoints"] = new[]
                    {
                        "/api/gateway/readiness",
                        "/api/gateway/bindings",
                        "/api/gateway/sentinel/events",
                        "/api/source-summaries/{sourceKind}/{sourceId}",
                        "/api/events/outbox"
                    }
                }
            };
        }
        catch (SqliteException ex)
        {
            checks["database"] = new GatewayReadinessCheck
            {
                Status = "blocked",
                Message = "SQLite database is not reachable.",
                Metadata = new Dictionary<string, object?> { ["sqlite_error_code"] = ex.SqliteErrorCode }
            };
            checks["migrations"] = new GatewayReadinessCheck
            {
                Status = "blocked",
                Message = "Migration readiness could not be verified because the database is unreachable."
            };
            checks["gateway_contract"] = new GatewayReadinessCheck
            {
                Status = "blocked",
                Message = "Gateway contract readiness could not be verified because the database is unreachable."
            };
        }
    }

    private static GatewayBindingSnapshot ToGatewayBindingSnapshot(AgentInstanceBinding binding) => new()
    {
        InstanceId = binding.InstanceId,
        ProjectId = binding.ProjectId,
        AgentIdentity = binding.AgentIdentity,
        AgentFamily = binding.AgentFamily,
        Role = binding.Role,
        TransportKind = binding.TransportKind,
        SessionId = binding.SessionId,
        Status = binding.Status.ToDbValue(),
        CheckedInAt = binding.CheckedInAt,
        LastHeartbeat = binding.LastHeartbeat,
        Metadata = ParseMetadata(binding.Metadata)
    };

    private static JsonElement? ParseMetadata(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(metadata);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement ToJsonElement(object value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value, JsonOpts.Default));
        return doc.RootElement.Clone();
    }

    private static bool IsAuthorized(HttpContext http, DenMcpOptions options)
    {
        var configured = options.GatewayContract.ServiceToken;
        if (string.IsNullOrWhiteSpace(configured))
            return true;

        var supplied = GetSuppliedServiceToken(http);
        if (supplied is null)
            return false;

        var configuredBytes = System.Text.Encoding.UTF8.GetBytes(configured);
        var suppliedBytes = System.Text.Encoding.UTF8.GetBytes(supplied);
        return configuredBytes.Length == suppliedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(configuredBytes, suppliedBytes);
    }

    private static bool HasServiceToken(DenMcpOptions options) => !string.IsNullOrWhiteSpace(options.GatewayContract.ServiceToken);

    private static string? GetSuppliedServiceToken(HttpContext http)
    {
        if (http.Request.Headers.TryGetValue("X-Den-Service-Token", out var token) && !string.IsNullOrWhiteSpace(token))
            return token.ToString();

        var auth = http.Request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";
        return auth.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? auth[bearerPrefix.Length..].Trim()
            : null;
    }

    private static string NormalizeToken(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_')
            .ToArray();
        var normalized = new string(chars).Trim('_');
        while (normalized.Contains("__", StringComparison.Ordinal))
            normalized = normalized.Replace("__", "_", StringComparison.Ordinal);
        return normalized;
    }

    private static string BuildSentinelDedupeKey(
        string sentinelId,
        string? projectId,
        string eventType,
        string state,
        string? outageId,
        string? cursor,
        DateTime observedAt)
    {
        var discriminator = !string.IsNullOrWhiteSpace(outageId)
            ? outageId.Trim()
            : !string.IsNullOrWhiteSpace(cursor) ? cursor.Trim() : observedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        return $"gateway_sentinel:{sentinelId}:{projectId ?? "_global"}:{eventType}:{state}:{discriminator}";
    }

    private static string BuildSentinelBody(string eventType, string state, string? projectId, string? outageId, string? reason)
    {
        var parts = new List<string> { $"Gateway sentinel {eventType} is {state}." };
        if (!string.IsNullOrWhiteSpace(projectId)) parts.Add($"project={projectId}");
        if (!string.IsNullOrWhiteSpace(outageId)) parts.Add($"outage={outageId}");
        if (!string.IsNullOrWhiteSpace(reason)) parts.Add($"reason={reason}");
        return string.Join(" ", parts);
    }

    private static string FormatCursor(long id) => id.ToString("D12", CultureInfo.InvariantCulture);
}
