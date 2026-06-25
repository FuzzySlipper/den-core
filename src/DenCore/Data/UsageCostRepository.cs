using System.Text.Json;
using System.Data.Common;
using DenCore.Models;

namespace DenCore.Data;

/// <summary>
/// Core repository for worker model usage event records and pricing snapshots.
/// Usage events are append-only-ish; they are never overwritten via normal
/// ingest paths. Pricing snapshots are versioned and immutable after creation.
/// </summary>
public interface IUsageCostRepository
{
    // ── Usage Events ──────────────────────────────────────────────────

    /// <summary>Record a usage event. Returns the assigned ID and any pricing applied.</summary>
    Task<ModelUsageEvent> RecordUsageEventAsync(ModelUsageEvent e);

    /// <summary>Batch-record usage events. Returns the assigned IDs.</summary>
    Task<List<ModelUsageEvent>> RecordUsageEventsAsync(List<ModelUsageEvent> events);

    /// <summary>List usage events with optional filters.</summary>
    Task<List<ModelUsageEvent>> ListUsageEventsAsync(UsageCostQueryOptions options);

    /// <summary>Get a single usage event by ID.</summary>
    Task<ModelUsageEvent?> GetUsageEventAsync(int id);

    // ── Pricing Snapshots ─────────────────────────────────────────────

    /// <summary>Create a pricing snapshot with entries. The snapshot is immutable after creation.</summary>
    Task<PricingSnapshot> CreatePricingSnapshotAsync(PricingSnapshot snapshot);

    /// <summary>Get a pricing snapshot by ID.</summary>
    Task<PricingSnapshot?> GetPricingSnapshotAsync(int id);

    /// <summary>Get the latest (most recent) pricing snapshot.</summary>
    Task<PricingSnapshot?> GetLatestPricingSnapshotAsync();

    /// <summary>List pricing snapshots, newest first.</summary>
    Task<List<PricingSnapshot>> ListPricingSnapshotsAsync(int limit = 20);

    /// <summary>
    /// Find the best-matching pricing entry for a provider/model pair.
    /// Returns null when pricing is unknown.
    /// </summary>
    Task<PricingEntry?> ResolvePricingAsync(int pricingSnapshotId, string provider, string model);

    // ── Reports ───────────────────────────────────────────────────────

    /// <summary>Run an aggregated usage/cost report.</summary>
    Task<UsageCostReport> RunReportAsync(UsageCostQueryOptions options);

    /// <summary>Seed the default pricing snapshot if none exists.</summary>
    Task<PricingSnapshot> EnsureDefaultPricingSnapshotAsync();
}

public sealed class UsageCostRepository : IUsageCostRepository
{
    private readonly DbConnectionFactory _db;

    public UsageCostRepository(DbConnectionFactory db)
    {
        _db = db;
    }

    // ── Usage Events ──────────────────────────────────────────────────

    public async Task<ModelUsageEvent> RecordUsageEventAsync(ModelUsageEvent e)
    {
        // Resolve pricing if a snapshot ID is provided
        if (e.PricingSnapshotId.HasValue && !e.ApproximateCostMicroCents.HasValue)
        {
            var pricing = await ResolvePricingAsync(e.PricingSnapshotId.Value, e.Provider, e.Model);
            e.ApproximateCostMicroCents = ComputeCost(e, pricing);
        }

        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO usage_events (
                occurred_at, project_id, task_id, assignment_id, run_id, session_id,
                agent_identity, profile_identity, worker_role, worker_identity,
                operation_kind,
                provider, model, model_alias, resolved_model, endpoint_kind,
                input_tokens, output_tokens, cache_read_tokens, cache_write_tokens,
                reasoning_tokens, tool_result_tokens,
                request_count, retry_count, streaming, error_kind,
                pricing_snapshot_id, approximate_cost_micro_cents,
                provenance, adapter_version, raw_usage_source, request_id_hint
            ) VALUES (
                @occurredAt, @projectId, @taskId, @assignmentId, @runId, @sessionId,
                @agentIdentity, @profileIdentity, @workerRole, @workerIdentity,
                @operationKind,
                @provider, @model, @modelAlias, @resolvedModel, @endpointKind,
                @inputTokens, @outputTokens, @cacheReadTokens, @cacheWriteTokens,
                @reasoningTokens, @toolResultTokens,
                @requestCount, @retryCount, @streaming, @errorKind,
                @pricingSnapshotId, @approximateCostMicroCents,
                @provenance, @adapterVersion, @rawUsageSource, @requestIdHint
            )
            {_db.Sql.ReturningIdClause()}
            """;

        BindEventParams(cmd, e);
        e.Id = Convert.ToInt32((await cmd.ExecuteScalarAsync())!);
        e.CreatedAt = DateTime.UtcNow;
        return e;
    }

    public async Task<List<ModelUsageEvent>> RecordUsageEventsAsync(List<ModelUsageEvent> events)
    {
        // For batch, resolve pricing for all events first
        var latestSnapshot = await GetLatestPricingSnapshotAsync();

        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = conn.BeginTransaction();

        try
        {
            foreach (var e in events)
            {
                if (!e.PricingSnapshotId.HasValue && latestSnapshot is not null)
                    e.PricingSnapshotId = latestSnapshot.Id;

                if (e.PricingSnapshotId.HasValue && !e.ApproximateCostMicroCents.HasValue)
                {
                    var pricing = await ResolvePricingInTxAsync(conn, e.PricingSnapshotId.Value, e.Provider, e.Model);
                    e.ApproximateCostMicroCents = ComputeCost(e, pricing);
                }

                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"""
                    INSERT INTO usage_events (
                        occurred_at, project_id, task_id, assignment_id, run_id, session_id,
                        agent_identity, profile_identity, worker_role, worker_identity,
                        operation_kind,
                        provider, model, model_alias, resolved_model, endpoint_kind,
                        input_tokens, output_tokens, cache_read_tokens, cache_write_tokens,
                        reasoning_tokens, tool_result_tokens,
                        request_count, retry_count, streaming, error_kind,
                        pricing_snapshot_id, approximate_cost_micro_cents,
                        provenance, adapter_version, raw_usage_source, request_id_hint
                    ) VALUES (
                        @occurredAt, @projectId, @taskId, @assignmentId, @runId, @sessionId,
                        @agentIdentity, @profileIdentity, @workerRole, @workerIdentity,
                        @operationKind,
                        @provider, @model, @modelAlias, @resolvedModel, @endpointKind,
                        @inputTokens, @outputTokens, @cacheReadTokens, @cacheWriteTokens,
                        @reasoningTokens, @toolResultTokens,
                        @requestCount, @retryCount, @streaming, @errorKind,
                        @pricingSnapshotId, @approximateCostMicroCents,
                        @provenance, @adapterVersion, @rawUsageSource, @requestIdHint
                    )
                    {_db.Sql.ReturningIdClause()}
                    """;
                BindEventParams(cmd, e);
                e.Id = Convert.ToInt32((await cmd.ExecuteScalarAsync())!);
                e.CreatedAt = DateTime.UtcNow;
            }

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }

        return events;
    }

    public async Task<List<ModelUsageEvent>> ListUsageEventsAsync(UsageCostQueryOptions options)
    {
        var (where, parameters) = BuildWhereClause(options);
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = $"""
            SELECT id, occurred_at, project_id, task_id, assignment_id, run_id, session_id,
                   agent_identity, profile_identity, worker_role, worker_identity,
                   operation_kind,
                   provider, model, model_alias, resolved_model, endpoint_kind,
                   input_tokens, output_tokens, cache_read_tokens, cache_write_tokens,
                   reasoning_tokens, tool_result_tokens,
                   request_count, retry_count, streaming, error_kind,
                   pricing_snapshot_id, approximate_cost_micro_cents,
                   provenance, adapter_version, raw_usage_source, request_id_hint,
                   created_at
            FROM usage_events
            {where}
            ORDER BY occurred_at DESC, id DESC
            LIMIT @limit
            """;

        foreach (var (name, value) in parameters)
            cmd.AddParameterWithValue(name, value);
        cmd.AddParameterWithValue("@limit", options.Limit);

        var results = new List<ModelUsageEvent>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(MapEvent(reader));

        return results;
    }

    public async Task<ModelUsageEvent?> GetUsageEventAsync(int id)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, occurred_at, project_id, task_id, assignment_id, run_id, session_id,
                   agent_identity, profile_identity, worker_role, worker_identity,
                   operation_kind,
                   provider, model, model_alias, resolved_model, endpoint_kind,
                   input_tokens, output_tokens, cache_read_tokens, cache_write_tokens,
                   reasoning_tokens, tool_result_tokens,
                   request_count, retry_count, streaming, error_kind,
                   pricing_snapshot_id, approximate_cost_micro_cents,
                   provenance, adapter_version, raw_usage_source, request_id_hint,
                   created_at
            FROM usage_events
            WHERE id = @id
            """;
        cmd.AddParameterWithValue("@id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            return MapEvent(reader);

        return null;
    }

    // ── Pricing Snapshots ─────────────────────────────────────────────

    public async Task<PricingSnapshot> CreatePricingSnapshotAsync(PricingSnapshot snapshot)
    {
        // Validate entries JSON by deserializing
        _ = JsonSerializer.Deserialize<List<PricingEntry>>(snapshot.EntriesJson);

        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO pricing_snapshots (snapshot_label, snapshot_version, effective_at, entries_json, created_by, notes)
            VALUES (@label, @version, @effectiveAt, @entriesJson, @createdBy, @notes)
            {_db.Sql.ReturningIdClause()}
            """;

        cmd.AddParameterWithValue("@label", snapshot.SnapshotLabel);
        cmd.AddParameterWithValue("@version", snapshot.SnapshotVersion);
        cmd.AddParameterWithValue("@effectiveAt", (object?)snapshot.EffectiveAt ?? DBNull.Value);
        cmd.AddParameterWithValue("@entriesJson", snapshot.EntriesJson);
        cmd.AddParameterWithValue("@createdBy", (object?)snapshot.CreatedBy ?? DBNull.Value);
        cmd.AddParameterWithValue("@notes", (object?)snapshot.Notes ?? DBNull.Value);

        snapshot.Id = Convert.ToInt32((await cmd.ExecuteScalarAsync())!);
        snapshot.CreatedAt = DateTime.UtcNow;
        return snapshot;
    }

    public async Task<PricingSnapshot?> GetPricingSnapshotAsync(int id)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, snapshot_label, snapshot_version, effective_at, entries_json, created_by, notes, created_at
            FROM pricing_snapshots WHERE id = @id
            """;
        cmd.AddParameterWithValue("@id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            return MapSnapshot(reader);

        return null;
    }

    public async Task<PricingSnapshot?> GetLatestPricingSnapshotAsync()
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, snapshot_label, snapshot_version, effective_at, entries_json, created_by, notes, created_at
            FROM pricing_snapshots ORDER BY id DESC LIMIT 1
            """;

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            return MapSnapshot(reader);

        return null;
    }

    public async Task<List<PricingSnapshot>> ListPricingSnapshotsAsync(int limit = 20)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, snapshot_label, snapshot_version, effective_at, entries_json, created_by, notes, created_at
            FROM pricing_snapshots ORDER BY id DESC LIMIT @limit
            """;
        cmd.AddParameterWithValue("@limit", limit);

        var results = new List<PricingSnapshot>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(MapSnapshot(reader));

        return results;
    }

    public async Task<PricingEntry?> ResolvePricingAsync(int pricingSnapshotId, string provider, string model)
    {
        var snapshot = await GetPricingSnapshotAsync(pricingSnapshotId);
        if (snapshot is null) return null;

        var entries = JsonSerializer.Deserialize<List<PricingEntry>>(snapshot.EntriesJson);
        if (entries is null) return null;

        return MatchPricing(entries, provider, model);
    }

    public async Task<PricingSnapshot> EnsureDefaultPricingSnapshotAsync()
    {
        var existing = await GetLatestPricingSnapshotAsync();
        if (existing is not null) return existing;

        var entriesJson = JsonSerializer.Serialize(
            UsageCostConstants.DefaultPricingCatalog,
            new JsonSerializerOptions { WriteIndented = false });

        return await CreatePricingSnapshotAsync(new PricingSnapshot
        {
            SnapshotLabel = "initial-seed",
            SnapshotVersion = "1.0.0",
            Notes = "Default pricing catalog seeded at startup",
            CreatedBy = "core.seed",
            EntriesJson = entriesJson
        });
    }

    // ── Reports ───────────────────────────────────────────────────────

    public async Task<UsageCostReport> RunReportAsync(UsageCostQueryOptions options)
    {
        var (where, parameters) = BuildWhereClause(options);

        // Determine group-by columns
        var groupCols = options.GroupBy switch
        {
            "role" => "worker_role",
            "model" => "model, provider",
            "provider" => "provider",
            "project" => "project_id",
            _ => "task_id, project_id"
        };

        var selectCols = options.GroupBy switch
        {
            "role" => "worker_role AS group_key, NULL AS sub_key",
            "model" => "provider AS group_key, model AS sub_key",
            "provider" => "provider AS group_key, NULL AS sub_key",
            "project" => "project_id AS group_key, NULL AS sub_key",
            _ => "CAST(COALESCE(task_id, 0) AS TEXT) AS group_key, project_id AS sub_key"
        };

        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = $"""
            WITH filtered AS (
                SELECT * FROM usage_events {where}
            ),
            aggregated AS (
                SELECT
                    {groupCols},
                    COUNT(*) AS event_count,
                    SUM(COALESCE(input_tokens, 0)) AS total_input_tokens,
                    SUM(COALESCE(output_tokens, 0)) AS total_output_tokens,
                    SUM(COALESCE(cache_read_tokens, 0)) AS total_cache_read_tokens,
                    SUM(COALESCE(reasoning_tokens, 0)) AS total_reasoning_tokens,
                    SUM(approximate_cost_micro_cents) AS approx_cost,
                    SUM(CASE WHEN approximate_cost_micro_cents IS NOT NULL THEN 1 ELSE 0 END) AS known_cost_count,
                    SUM(CASE WHEN approximate_cost_micro_cents IS NULL THEN 1 ELSE 0 END) AS unknown_cost_count,
                    {selectCols}
                FROM filtered
                GROUP BY {groupCols}
                ORDER BY approx_cost DESC NULLS LAST
                LIMIT @limit
            ),
            totals AS (
                SELECT
                    COUNT(*) AS total_events,
                    SUM(COALESCE(input_tokens, 0)) AS total_in_tok,
                    SUM(COALESCE(output_tokens, 0)) AS total_out_tok,
                    SUM(approximate_cost_micro_cents) AS total_cost
                FROM filtered
            )
            SELECT * FROM aggregated, totals
            """;

        foreach (var (name, value) in parameters)
            cmd.AddParameterWithValue(name, value);
        cmd.AddParameterWithValue("@limit", options.Limit);

        var report = new UsageCostReport
        {
            GroupBy = options.GroupBy,
            Query = options
        };

        await using var reader = await cmd.ExecuteReaderAsync();
        bool hasRows = false;
        while (await reader.ReadAsync())
        {
            hasRows = true;

            // The totals columns come from the CROSS JOIN on the single-row 'totals' CTE
            if (!hasRows || report.TotalCostMicroCents is null)
            {
                report.TotalEvents = reader.GetInt32(reader.GetOrdinal("total_events"));
                report.TotalInputTokens = reader.IsDBNull(reader.GetOrdinal("total_in_tok"))
                    ? null : reader.GetInt64(reader.GetOrdinal("total_in_tok"));
                report.TotalOutputTokens = reader.IsDBNull(reader.GetOrdinal("total_out_tok"))
                    ? null : reader.GetInt64(reader.GetOrdinal("total_out_tok"));
                report.TotalCostMicroCents = reader.IsDBNull(reader.GetOrdinal("total_cost"))
                    ? null : reader.GetInt64(reader.GetOrdinal("total_cost"));
            }

            var row = new UsageCostReportRow();
            var groupKey = reader.GetString(reader.GetOrdinal("group_key"));
            var subKey = reader.IsDBNull(reader.GetOrdinal("sub_key"))
                ? null : reader.GetString(reader.GetOrdinal("sub_key"));

            switch (options.GroupBy)
            {
                case "role":
                    row.WorkerRole = groupKey;
                    break;
                case "model":
                    row.Provider = groupKey;
                    row.Model = subKey;
                    break;
                case "provider":
                    row.Provider = groupKey;
                    break;
                case "project":
                    row.ProjectId = groupKey;
                    break;
                default: // task
                    row.TaskId = groupKey == "0" ? null : int.Parse(groupKey);
                    row.ProjectId = subKey;
                    break;
            }

            row.EventCount = reader.GetInt32(reader.GetOrdinal("event_count"));
            row.TotalInputTokens = reader.IsDBNull(reader.GetOrdinal("total_input_tokens"))
                ? null : reader.GetInt64(reader.GetOrdinal("total_input_tokens"));
            row.TotalOutputTokens = reader.IsDBNull(reader.GetOrdinal("total_output_tokens"))
                ? null : reader.GetInt64(reader.GetOrdinal("total_output_tokens"));
            row.TotalCacheReadTokens = reader.IsDBNull(reader.GetOrdinal("total_cache_read_tokens"))
                ? null : reader.GetInt64(reader.GetOrdinal("total_cache_read_tokens"));
            row.TotalReasoningTokens = reader.IsDBNull(reader.GetOrdinal("total_reasoning_tokens"))
                ? null : reader.GetInt64(reader.GetOrdinal("total_reasoning_tokens"));
            row.ApproximateCostMicroCents = reader.IsDBNull(reader.GetOrdinal("approx_cost"))
                ? null : reader.GetInt64(reader.GetOrdinal("approx_cost"));
            row.EventsWithKnownCost = reader.IsDBNull(reader.GetOrdinal("known_cost_count"))
                ? null : reader.GetInt32(reader.GetOrdinal("known_cost_count"));
            row.EventsWithUnknownCost = reader.IsDBNull(reader.GetOrdinal("unknown_cost_count"))
                ? null : reader.GetInt32(reader.GetOrdinal("unknown_cost_count"));

            report.Rows.Add(row);
        }

        // If no rows but totals exist from the CTE (because totals is cross-joined
        // and there are filtered events but the aggregation returned something),
        // we still want totals. Let's handle the empty case.
        if (!hasRows)
        {
            // Run a simple totals query
            await using var totCmd = conn.CreateCommand();
            totCmd.CommandText = $"""
                SELECT
                    COUNT(*) AS total_events,
                    SUM(COALESCE(input_tokens, 0)) AS total_in_tok,
                    SUM(COALESCE(output_tokens, 0)) AS total_out_tok,
                    SUM(approximate_cost_micro_cents) AS total_cost
                FROM usage_events {where}
                """;
            foreach (var (name, value) in parameters)
                totCmd.AddParameterWithValue(name, value);

            await using var totReader = await totCmd.ExecuteReaderAsync();
            if (await totReader.ReadAsync())
            {
                report.TotalEvents = totReader.GetInt32(0);
                report.TotalInputTokens = totReader.IsDBNull(1) ? null : totReader.GetInt64(1);
                report.TotalOutputTokens = totReader.IsDBNull(2) ? null : totReader.GetInt64(2);
                report.TotalCostMicroCents = totReader.IsDBNull(3) ? null : totReader.GetInt64(3);
            }
        }

        return report;
    }

    // ── Private helpers ───────────────────────────────────────────────

    private static (string where, List<(string Name, object? Value)> parameters) BuildWhereClause(UsageCostQueryOptions options)
    {
        var conditions = new List<string>();
        var parameters = new List<(string, object?)>();

        if (!string.IsNullOrWhiteSpace(options.ProjectId))
        {
            conditions.Add("project_id = @projectId");
            parameters.Add(("@projectId", options.ProjectId));
        }

        if (options.TaskId.HasValue)
        {
            conditions.Add("task_id = @taskId");
            parameters.Add(("@taskId", options.TaskId.Value));
        }

        if (!string.IsNullOrWhiteSpace(options.FromOccurredAt))
        {
            conditions.Add("occurred_at >= @fromOccurredAt");
            parameters.Add(("@fromOccurredAt", options.FromOccurredAt));
        }

        if (!string.IsNullOrWhiteSpace(options.ToOccurredAt))
        {
            conditions.Add("occurred_at <= @toOccurredAt");
            parameters.Add(("@toOccurredAt", options.ToOccurredAt));
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        return (where, parameters);
    }

    private static async Task<PricingEntry?> ResolvePricingInTxAsync(
        DbConnection conn, int pricingSnapshotId, string provider, string model)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT entries_json FROM pricing_snapshots WHERE id = @id
            """;
        cmd.AddParameterWithValue("@id", pricingSnapshotId);

        var json = (string?)await cmd.ExecuteScalarAsync();
        if (json is null) return null;

        var entries = JsonSerializer.Deserialize<List<PricingEntry>>(json);
        if (entries is null) return null;

        return MatchPricing(entries, provider, model);
    }

    private static PricingEntry? MatchPricing(List<PricingEntry> entries, string provider, string model)
    {
        // Best match: exact provider + exact model
        var match = entries.FirstOrDefault(e =>
            string.Equals(e.Provider, provider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.Model, model, StringComparison.OrdinalIgnoreCase));

        // Fallback: exact provider with wildcard model
        match ??= entries.FirstOrDefault(e =>
            string.Equals(e.Provider, provider, StringComparison.OrdinalIgnoreCase) &&
            e.Model == "*");

        // Fallback: wildcard provider + wildcard model
        match ??= entries.FirstOrDefault(e =>
            e.Provider == "*" && e.Model == "*");

        return match;
    }

    internal static long? ComputeCost(ModelUsageEvent e, PricingEntry? pricing)
    {
        if (pricing is null) return null;

        if (pricing.PricingKind == UsageCostConstants.PricingKindFree ||
            pricing.PricingKind == UsageCostConstants.PricingKindLocal)
            return 0;

        if (pricing.PricingKind == UsageCostConstants.PricingKindUnknown)
            return null;

        // API pricing: sum of token counts * per-million prices
        // Pure integer arithmetic: tokens * price / 1_000_000 with rounding to nearest micro-cent
        // Rounding: add half the divisor before division: (tokens * price + 500_000) / 1_000_000
        long cost = 0;
        bool hasAnyData = false;

        if (e.InputTokens.HasValue && pricing.InputPriceMicroCentsPerMillion.HasValue)
        {
            cost += (e.InputTokens.Value * (long)pricing.InputPriceMicroCentsPerMillion.Value + 500_000) / 1_000_000;
            hasAnyData = true;
        }

        if (e.OutputTokens.HasValue && pricing.OutputPriceMicroCentsPerMillion.HasValue)
        {
            cost += (e.OutputTokens.Value * (long)pricing.OutputPriceMicroCentsPerMillion.Value + 500_000) / 1_000_000;
            hasAnyData = true;
        }

        if (e.CacheReadTokens.HasValue && pricing.CacheReadPriceMicroCentsPerMillion.HasValue)
        {
            cost += (e.CacheReadTokens.Value * (long)pricing.CacheReadPriceMicroCentsPerMillion.Value + 500_000) / 1_000_000;
            hasAnyData = true;
        }

        if (e.CacheWriteTokens.HasValue && pricing.CacheWritePriceMicroCentsPerMillion.HasValue)
        {
            cost += (e.CacheWriteTokens.Value * (long)pricing.CacheWritePriceMicroCentsPerMillion.Value + 500_000) / 1_000_000;
            hasAnyData = true;
        }

        if (e.ReasoningTokens.HasValue && pricing.ReasoningPriceMicroCentsPerMillion.HasValue)
        {
            cost += (e.ReasoningTokens.Value * (long)pricing.ReasoningPriceMicroCentsPerMillion.Value + 500_000) / 1_000_000;
            hasAnyData = true;
        }

        if (pricing.PerRequestPriceMicroCents.HasValue)
        {
            cost += pricing.PerRequestPriceMicroCents.Value * e.RequestCount;
            hasAnyData = true;
        }

        return hasAnyData ? cost : null;
    }

    private static void BindEventParams(DbCommand cmd, ModelUsageEvent e)
    {
        cmd.AddParameterWithValue("@occurredAt", e.OccurredAt);
        cmd.AddParameterWithValue("@projectId", e.ProjectId);
        cmd.AddParameterWithValue("@taskId", (object?)e.TaskId ?? DBNull.Value);
        cmd.AddParameterWithValue("@assignmentId", (object?)e.AssignmentId ?? DBNull.Value);
        cmd.AddParameterWithValue("@runId", (object?)e.RunId ?? DBNull.Value);
        cmd.AddParameterWithValue("@sessionId", (object?)e.SessionId ?? DBNull.Value);
        cmd.AddParameterWithValue("@agentIdentity", (object?)e.AgentIdentity ?? DBNull.Value);
        cmd.AddParameterWithValue("@profileIdentity", (object?)e.ProfileIdentity ?? DBNull.Value);
        cmd.AddParameterWithValue("@workerRole", (object?)e.WorkerRole ?? DBNull.Value);
        cmd.AddParameterWithValue("@workerIdentity", (object?)e.WorkerIdentity ?? DBNull.Value);
        cmd.AddParameterWithValue("@operationKind", e.OperationKind);
        cmd.AddParameterWithValue("@provider", e.Provider);
        cmd.AddParameterWithValue("@model", e.Model);
        cmd.AddParameterWithValue("@modelAlias", (object?)e.ModelAlias ?? DBNull.Value);
        cmd.AddParameterWithValue("@resolvedModel", (object?)e.ResolvedModel ?? DBNull.Value);
        cmd.AddParameterWithValue("@endpointKind", (object?)e.EndpointKind ?? DBNull.Value);
        cmd.AddParameterWithValue("@inputTokens", (object?)e.InputTokens ?? DBNull.Value);
        cmd.AddParameterWithValue("@outputTokens", (object?)e.OutputTokens ?? DBNull.Value);
        cmd.AddParameterWithValue("@cacheReadTokens", (object?)e.CacheReadTokens ?? DBNull.Value);
        cmd.AddParameterWithValue("@cacheWriteTokens", (object?)e.CacheWriteTokens ?? DBNull.Value);
        cmd.AddParameterWithValue("@reasoningTokens", (object?)e.ReasoningTokens ?? DBNull.Value);
        cmd.AddParameterWithValue("@toolResultTokens", (object?)e.ToolResultTokens ?? DBNull.Value);
        cmd.AddParameterWithValue("@requestCount", e.RequestCount);
        cmd.AddParameterWithValue("@retryCount", e.RetryCount);
        cmd.AddParameterWithValue("@streaming", e.Streaming ? 1 : 0);
        cmd.AddParameterWithValue("@errorKind", (object?)e.ErrorKind ?? DBNull.Value);
        cmd.AddParameterWithValue("@pricingSnapshotId", (object?)e.PricingSnapshotId ?? DBNull.Value);
        cmd.AddParameterWithValue("@approximateCostMicroCents", (object?)e.ApproximateCostMicroCents ?? DBNull.Value);
        cmd.AddParameterWithValue("@provenance", (object?)e.Provenance ?? DBNull.Value);
        cmd.AddParameterWithValue("@adapterVersion", (object?)e.AdapterVersion ?? DBNull.Value);
        cmd.AddParameterWithValue("@rawUsageSource", (object?)e.RawUsageSource ?? DBNull.Value);
        cmd.AddParameterWithValue("@requestIdHint", (object?)e.RequestIdHint ?? DBNull.Value);
    }

    private static ModelUsageEvent MapEvent(DbDataReader reader)
    {
        return new ModelUsageEvent
        {
            Id = reader.GetInt32(reader.GetOrdinal("id")),
            OccurredAt = reader.GetString(reader.GetOrdinal("occurred_at")),
            ProjectId = reader.GetString(reader.GetOrdinal("project_id")),
            TaskId = reader.IsDBNull(reader.GetOrdinal("task_id")) ? null : reader.GetInt32(reader.GetOrdinal("task_id")),
            AssignmentId = reader.IsDBNull(reader.GetOrdinal("assignment_id")) ? null : reader.GetInt32(reader.GetOrdinal("assignment_id")),
            RunId = reader.IsDBNull(reader.GetOrdinal("run_id")) ? null : reader.GetString(reader.GetOrdinal("run_id")),
            SessionId = reader.IsDBNull(reader.GetOrdinal("session_id")) ? null : reader.GetString(reader.GetOrdinal("session_id")),
            AgentIdentity = reader.IsDBNull(reader.GetOrdinal("agent_identity")) ? null : reader.GetString(reader.GetOrdinal("agent_identity")),
            ProfileIdentity = reader.IsDBNull(reader.GetOrdinal("profile_identity")) ? null : reader.GetString(reader.GetOrdinal("profile_identity")),
            WorkerRole = reader.IsDBNull(reader.GetOrdinal("worker_role")) ? null : reader.GetString(reader.GetOrdinal("worker_role")),
            WorkerIdentity = reader.IsDBNull(reader.GetOrdinal("worker_identity")) ? null : reader.GetString(reader.GetOrdinal("worker_identity")),
            OperationKind = reader.GetString(reader.GetOrdinal("operation_kind")),
            Provider = reader.GetString(reader.GetOrdinal("provider")),
            Model = reader.GetString(reader.GetOrdinal("model")),
            ModelAlias = reader.IsDBNull(reader.GetOrdinal("model_alias")) ? null : reader.GetString(reader.GetOrdinal("model_alias")),
            ResolvedModel = reader.IsDBNull(reader.GetOrdinal("resolved_model")) ? null : reader.GetString(reader.GetOrdinal("resolved_model")),
            EndpointKind = reader.IsDBNull(reader.GetOrdinal("endpoint_kind")) ? null : reader.GetString(reader.GetOrdinal("endpoint_kind")),
            InputTokens = reader.IsDBNull(reader.GetOrdinal("input_tokens")) ? null : reader.GetInt32(reader.GetOrdinal("input_tokens")),
            OutputTokens = reader.IsDBNull(reader.GetOrdinal("output_tokens")) ? null : reader.GetInt32(reader.GetOrdinal("output_tokens")),
            CacheReadTokens = reader.IsDBNull(reader.GetOrdinal("cache_read_tokens")) ? null : reader.GetInt32(reader.GetOrdinal("cache_read_tokens")),
            CacheWriteTokens = reader.IsDBNull(reader.GetOrdinal("cache_write_tokens")) ? null : reader.GetInt32(reader.GetOrdinal("cache_write_tokens")),
            ReasoningTokens = reader.IsDBNull(reader.GetOrdinal("reasoning_tokens")) ? null : reader.GetInt32(reader.GetOrdinal("reasoning_tokens")),
            ToolResultTokens = reader.IsDBNull(reader.GetOrdinal("tool_result_tokens")) ? null : reader.GetInt32(reader.GetOrdinal("tool_result_tokens")),
            RequestCount = reader.GetInt32(reader.GetOrdinal("request_count")),
            RetryCount = reader.GetInt32(reader.GetOrdinal("retry_count")),
            Streaming = reader.GetInt32(reader.GetOrdinal("streaming")) == 1,
            ErrorKind = reader.IsDBNull(reader.GetOrdinal("error_kind")) ? null : reader.GetString(reader.GetOrdinal("error_kind")),
            PricingSnapshotId = reader.IsDBNull(reader.GetOrdinal("pricing_snapshot_id")) ? null : reader.GetInt32(reader.GetOrdinal("pricing_snapshot_id")),
            ApproximateCostMicroCents = reader.IsDBNull(reader.GetOrdinal("approximate_cost_micro_cents")) ? null : reader.GetInt64(reader.GetOrdinal("approximate_cost_micro_cents")),
            Provenance = reader.IsDBNull(reader.GetOrdinal("provenance")) ? null : reader.GetString(reader.GetOrdinal("provenance")),
            AdapterVersion = reader.IsDBNull(reader.GetOrdinal("adapter_version")) ? null : reader.GetString(reader.GetOrdinal("adapter_version")),
            RawUsageSource = reader.IsDBNull(reader.GetOrdinal("raw_usage_source")) ? null : reader.GetString(reader.GetOrdinal("raw_usage_source")),
            RequestIdHint = reader.IsDBNull(reader.GetOrdinal("request_id_hint")) ? null : reader.GetString(reader.GetOrdinal("request_id_hint")),
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at")))
        };
    }

    private static PricingSnapshot MapSnapshot(DbDataReader reader)
    {
        return new PricingSnapshot
        {
            Id = reader.GetInt32(reader.GetOrdinal("id")),
            SnapshotLabel = reader.GetString(reader.GetOrdinal("snapshot_label")),
            SnapshotVersion = reader.GetString(reader.GetOrdinal("snapshot_version")),
            EffectiveAt = reader.IsDBNull(reader.GetOrdinal("effective_at")) ? null : reader.GetString(reader.GetOrdinal("effective_at")),
            EntriesJson = reader.GetString(reader.GetOrdinal("entries_json")),
            CreatedBy = reader.IsDBNull(reader.GetOrdinal("created_by")) ? null : reader.GetString(reader.GetOrdinal("created_by")),
            Notes = reader.IsDBNull(reader.GetOrdinal("notes")) ? null : reader.GetString(reader.GetOrdinal("notes")),
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at")))
        };
    }
}
