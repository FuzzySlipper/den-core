using System.Globalization;
using System.Text.Json;
using DenCore.Data;
using DenCore.Models;
using Microsoft.Data.Sqlite;

namespace DenCore.Service.Routes;

/// <summary>
/// Core-owned Direct Delivery contract endpoints.
///
/// These routes provide generic Den projections for Channels/local runtime adapters
/// to coordinate direct agent wake/delivery without making Gateway the conceptual owner.
///
/// Core owns canonical workflow truth and binding records/projections;
/// Channels owns visible ops/direct-agent request recording;
/// local adapter/Bridge owns harness/local process/session mechanics.
/// Core stays harness-agnostic.
///
/// Relationship to /api/gateway/bindings:
///   /api/gateway/bindings remains a stable compatibility alias that projects raw
///   AgentInstanceBinding data. /api/direct-delivery/bindings enriches bindings with
///   pool member disambiguation (pool_member_id vs profile_identity), active assignment/
///   run linkage, and session-owner correlation. Gateway-bound consumers should continue
///   using /api/gateway/bindings if they only need raw binding fields.
/// </summary>
public static class DirectDeliveryContractRoutes
{
    public static void MapDirectDeliveryContractRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/direct-delivery");

        // ── Readiness ─────────────────────────────────────────────────

        group.MapGet("/readiness", async (DbConnectionFactory db) =>
        {
            var checkedAt = DateTime.UtcNow;
            var checks = new Dictionary<string, DirectDeliveryReadinessCheck>
            {
                ["process"] = new()
                {
                    Status = "ready",
                    Message = "Den Core process is reachable."
                },
                ["direct_delivery_contract"] = new()
                {
                    Status = "ready",
                    Message = "Direct Delivery contract endpoints are mapped by Den Core.",
                    Metadata = new Dictionary<string, object?>
                    {
                        ["endpoints"] = new[]
                        {
                            "/api/direct-delivery/readiness",
                            "/api/direct-delivery/bindings"
                        }
                    }
                }
            };

            await AddDatabaseCheckAsync(db, checks);

            var status = checks.Values.Any(c => c.Status == "blocked")
                ? "blocked"
                : checks.Values.Any(c => c.Status == "degraded") ? "degraded" : "ready";

            return Results.Ok(new DirectDeliveryReadinessResponse
            {
                Status = status,
                Service = "den-core-direct-delivery-contract",
                CheckedAt = checkedAt,
                Checks = checks
            });
        });

        // ── Binding projections ───────────────────────────────────────

        group.MapGet("/bindings", async (
            IAgentInstanceBindingRepository bindingsRepo,
            IWorkerPoolRepository poolRepo,
            string? projectId,
            string? profileIdentity,
            string? workerRole,
            string? agentIdentity,
            string? transportKind,
            string? role) =>
        {
            // 1. Fetch bindings (active + degraded by default)
            var bindings = await bindingsRepo.ListAsync(new AgentInstanceBindingListOptions
            {
                ProjectId = projectId,
                AgentIdentity = agentIdentity,
                Role = role,
                TransportKind = transportKind,
                Statuses =
                [
                    AgentInstanceBindingStatus.Active,
                    AgentInstanceBindingStatus.Degraded
                ]
            });

            // 2. Fetch all pool members (for enrichment join)
            var allMembers = await poolRepo.ListMembersAsync(new WorkerPoolMemberListOptions
            {
                Limit = 1000 // practical upper bound for in-memory join
            });

            // Build lookup by AgentInstanceId
            var membersByAgentInstanceId = new Dictionary<string, WorkerPoolMember>(StringComparer.Ordinal);
            var membersByWorkerIdentity = new Dictionary<string, WorkerPoolMember>(StringComparer.Ordinal);
            foreach (var m in allMembers)
            {
                if (!string.IsNullOrWhiteSpace(m.AgentInstanceId))
                    membersByAgentInstanceId[m.AgentInstanceId] = m;
                if (!string.IsNullOrWhiteSpace(m.WorkerIdentity))
                    membersByWorkerIdentity[m.WorkerIdentity] = m;
            }

            // Apply profile/workerRole filters on pool members if requested
            var filteredMemberIds = new HashSet<string>();
            bool hasPoolFilters = !string.IsNullOrWhiteSpace(profileIdentity) || !string.IsNullOrWhiteSpace(workerRole);
            if (hasPoolFilters)
            {
                foreach (var m in allMembers)
                {
                    bool matches = true;
                    if (!string.IsNullOrWhiteSpace(profileIdentity) &&
                        !string.Equals(m.ProfileIdentity, profileIdentity, StringComparison.Ordinal))
                        matches = false;
                    if (!string.IsNullOrWhiteSpace(workerRole) &&
                        !string.Equals(m.WorkerRole, workerRole, StringComparison.Ordinal))
                        matches = false;
                    if (matches)
                        filteredMemberIds.Add(m.WorkerIdentity);
                }
            }

            // 3. Fetch all non-terminal assignments (for run linkage)
            var allAssignments = await poolRepo.ListAssignmentsAsync(new WorkerAssignmentListOptions
            {
                Limit = 1000
            });
            // Keep only non-terminal assignments; index by WorkerIdentity
            var activeAssignmentByWorker = new Dictionary<string, WorkerAssignment>(StringComparer.Ordinal);
            foreach (var a in allAssignments)
            {
                if (WorkerPoolStates.IsNonTerminal(a.State) &&
                    !activeAssignmentByWorker.ContainsKey(a.WorkerIdentity))
                {
                    activeAssignmentByWorker[a.WorkerIdentity] = a;
                }
            }

            // 4. Build enriched projection
            var snapshots = new List<DirectDeliveryBindingSnapshot>();
            foreach (var binding in bindings)
            {
                // Try to find a pool member by agent instance id
                membersByAgentInstanceId.TryGetValue(binding.InstanceId, out var poolMember);

                // If pool filters are active, only pool-linked bindings can match.
                // Raw adapter bindings without a pool member have no profile_identity
                // or worker_role to compare, so they must not leak through filtered
                // direct-delivery projections.
                if (hasPoolFilters &&
                    (poolMember == null || !filteredMemberIds.Contains(poolMember.WorkerIdentity)))
                {
                    continue;
                }

                // Find active assignment for this worker
                WorkerAssignment? activeAssignment = null;
                if (poolMember != null)
                    activeAssignmentByWorker.TryGetValue(poolMember.WorkerIdentity, out activeAssignment);

                snapshots.Add(new DirectDeliveryBindingSnapshot
                {
                    // Adapter binding identity & freshness
                    AgentInstanceId = binding.InstanceId,
                    ProjectId = binding.ProjectId,
                    AgentFamily = binding.AgentFamily,
                    AgentIdentity = binding.AgentIdentity,
                    Role = binding.Role,
                    TransportKind = binding.TransportKind,
                    Status = binding.Status.ToDbValue(),
                    CheckedInAt = binding.CheckedInAt,
                    LastHeartbeat = binding.LastHeartbeat,

                    // Session owner / lane (#1885 slice)
                    SessionOwnerId = poolMember?.AgentInstanceId ?? binding.InstanceId,
                    SessionId = binding.SessionId ?? poolMember?.SessionId,
                    ChannelId = poolMember?.ChannelId,

                    // Lane derived from profile + role
                    Lane = poolMember != null && !string.IsNullOrWhiteSpace(poolMember.ProfileIdentity)
                        ? $"{poolMember.ProfileIdentity}/{poolMember.WorkerRole ?? binding.Role ?? "unknown"}"
                        : null,

                    // Pool member disambiguation
                    PoolMemberId = poolMember?.PoolMemberId ?? poolMember?.WorkerIdentity,
                    ProfileIdentity = poolMember?.ProfileIdentity,
                    WorkerRole = poolMember?.WorkerRole,
                    AdapterInstanceId = poolMember?.AdapterInstanceId,

                    // Active assignment / run linkage
                    AssignmentId = activeAssignment?.Id,
                    WorkerRunId = activeAssignment?.RunId,
                    AssignmentState = activeAssignment?.State,
                    AssignmentRole = activeAssignment?.Role,
                    TaskId = activeAssignment?.TaskId,

                    // Capability / metadata
                    Capabilities = poolMember?.Capabilities,
                    Metadata = ParseMetadata(binding.Metadata),

                    // Updated timestamp
                    UpdatedAt = poolMember?.UpdatedAt ?? binding.LastHeartbeat
                });
            }

            return Results.Ok(new DirectDeliveryBindingSnapshotPage
            {
                GeneratedAt = DateTime.UtcNow,
                Items = snapshots
            });
        });
    }

    // ── helpers ───────────────────────────────────────────────────────

    private static async Task AddDatabaseCheckAsync(
        DbConnectionFactory db,
        Dictionary<string, DirectDeliveryReadinessCheck> checks)
    {
        try
        {
            await using var conn = await db.CreateConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync();

            checks["database"] = new DirectDeliveryReadinessCheck
            {
                Status = "ready",
                Message = "SQLite database is reachable."
            };
        }
        catch (SqliteException)
        {
            checks["database"] = new DirectDeliveryReadinessCheck
            {
                Status = "blocked",
                Message = "SQLite database is not reachable."
            };
            checks["direct_delivery_contract"] = new DirectDeliveryReadinessCheck
            {
                Status = "blocked",
                Message = "Direct Delivery contract readiness could not be verified because the database is unreachable."
            };
        }
    }

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
}
