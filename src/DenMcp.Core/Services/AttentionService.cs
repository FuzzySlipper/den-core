using System.Globalization;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using Microsoft.Data.Sqlite;

namespace DenMcp.Core.Services;

public interface IAttentionService
{
    Task<List<AttentionItem>> ListAsync(AttentionListOptions options);
}

public sealed class AttentionService : IAttentionService
{
    private static readonly string[] ActiveRunStates =
    [
        "running",
        "retrying",
        "aborting",
        "rerun_requested"
    ];

    private static readonly string[] ProblemRunStates =
    [
        "failed",
        "timeout",
        "aborted"
    ];

    private readonly DbConnectionFactory _db;

    public AttentionService(DbConnectionFactory db) => _db = db;

    public TimeSpan StaleRunThreshold { get; set; } = TimeSpan.FromMinutes(15);
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public async Task<List<AttentionItem>> ListAsync(AttentionListOptions options)
    {
        var normalized = NormalizeOptions(options);
        await using var conn = await _db.CreateConnectionAsync();
        var items = new List<AttentionItem>();

        await AddPendingDispatchesAsync(conn, normalized, items);
        await AddProblemRunsAsync(conn, normalized, items);
        await AddStaleActiveRunsAsync(conn, normalized, items);
        await AddRerunUnavailableAsync(conn, normalized, items);
        await AddBlockedTasksAsync(conn, normalized, items);
        await AddQuestionMessagesAsync(conn, normalized, items);
        await AddOpenReviewFindingsAsync(conn, normalized, items);
        await AddPendingReviewsWithFailedReviewerRunsAsync(conn, normalized, items);

        return items
            .Where(item => MatchesKindSeverity(item, normalized))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.LatestAt).First())
            .OrderBy(item => SeverityRank(item.Severity))
            .ThenByDescending(item => item.LatestAt)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Take(normalized.Limit)
            .ToList();
    }

    private async Task AddPendingDispatchesAsync(SqliteConnection conn, AttentionListOptions options, List<AttentionItem> items)
    {
        await using var cmd = conn.CreateCommand();
        var where = new List<string> { "status = 'pending'" };
        AddProjectTaskFilter(cmd, where, "project_id", "task_id", options);
        cmd.CommandText = $"""
            SELECT id, project_id, task_id, target_agent, summary, trigger_type, created_at, expires_at
            FROM dispatch_entries
            WHERE {string.Join(" AND ", where)}
            """;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetInt32(0);
            var projectId = reader.GetString(1);
            var taskId = NullableInt(reader, 2);
            var targetAgent = reader.GetString(3);
            var summary = NullableString(reader, 4);
            var triggerType = reader.GetString(5);
            var createdAt = ReadDate(reader, 6);
            var expiresAt = ReadDate(reader, 7);
            items.Add(new AttentionItem
            {
                Id = $"dispatch:{id}",
                ProjectId = projectId,
                TaskId = taskId,
                DispatchId = id,
                Kind = "pending_dispatch",
                Severity = "info",
                Title = $"Pending legacy dispatch for {targetAgent}",
                Summary = summary ?? $"Legacy dispatch from {triggerType} is pending.",
                CreatedAt = createdAt,
                LatestAt = createdAt,
                SuggestedAction = expiresAt <= UtcNow()
                    ? "Expire or complete this legacy dispatch if it is obsolete."
                    : "Inspect this legacy dispatch only if debugging the old bridge path."
            });
        }
    }

    private async Task AddProblemRunsAsync(SqliteConnection conn, AttentionListOptions options, List<AttentionItem> items)
    {
        await using var cmd = conn.CreateCommand();
        var where = new List<string>();
        AddInFilter(cmd, where, "state", ProblemRunStates, "problemState");
        AddProjectTaskFilter(cmd, where, "project_id", "task_id", options);
        cmd.CommandText = $"""
            SELECT run_id, project_id, task_id, role, state, model, timeout_kind,
                   infrastructure_failure_reason, started_at, ended_at, updated_at, created_at
            FROM agent_runs
            WHERE {string.Join(" AND ", where)}
            """;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var runId = reader.GetString(0);
            var projectId = NullableString(reader, 1);
            if (projectId is null)
                continue;

            var taskId = NullableInt(reader, 2);
            var role = NullableString(reader, 3) ?? "sub-agent";
            var state = reader.GetString(4);
            var model = NullableString(reader, 5);
            var timeoutKind = NullableString(reader, 6);
            var failureReason = NullableString(reader, 7);
            var startedAt = NullableDate(reader, 8) ?? ReadDate(reader, 11);
            var latestAt = NullableDate(reader, 9) ?? ReadDate(reader, 10);
            var details = new List<string> { $"{role} run {runId} is {state}" };
            if (model is not null) details.Add(model);
            if (timeoutKind is not null) details.Add($"timeout: {timeoutKind}");
            if (failureReason is not null) details.Add($"infra: {failureReason.Replace('_', ' ')}");

            items.Add(new AttentionItem
            {
                Id = $"run:{runId}:problem",
                ProjectId = projectId,
                TaskId = taskId,
                RunId = runId,
                Kind = "subagent_run_problem",
                Severity = state == "aborted" ? "warning" : "critical",
                Title = $"Sub-agent run {state}",
                Summary = string.Join(" · ", details),
                CreatedAt = startedAt,
                LatestAt = latestAt,
                SuggestedAction = "Open the run detail, inspect artifacts, then rerun, fix the task, or split follow-up work."
            });
        }
    }

    private async Task AddStaleActiveRunsAsync(SqliteConnection conn, AttentionListOptions options, List<AttentionItem> items)
    {
        await using var cmd = conn.CreateCommand();
        var where = new List<string>();
        AddInFilter(cmd, where, "state", ActiveRunStates, "activeState");
        AddProjectTaskFilter(cmd, where, "project_id", "task_id", options);
        cmd.CommandText = $"""
            SELECT run_id, project_id, task_id, role, state, model,
                   started_at, last_heartbeat_at, updated_at, created_at
            FROM agent_runs
            WHERE {string.Join(" AND ", where)}
            """;

        var now = UtcNow();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var runId = reader.GetString(0);
            var projectId = NullableString(reader, 1);
            if (projectId is null)
                continue;

            var taskId = NullableInt(reader, 2);
            var role = NullableString(reader, 3) ?? "sub-agent";
            var state = reader.GetString(4);
            var model = NullableString(reader, 5);
            var startedAt = NullableDate(reader, 6) ?? ReadDate(reader, 9);
            var lastProgressAt = NullableDate(reader, 7) ?? startedAt;
            var quietFor = now - lastProgressAt;
            if (quietFor < StaleRunThreshold)
                continue;

            items.Add(new AttentionItem
            {
                Id = $"run:{runId}:stale",
                ProjectId = projectId,
                TaskId = taskId,
                RunId = runId,
                Kind = "stale_subagent_run",
                Severity = "warning",
                Title = "Stale active sub-agent run",
                Summary = $"{role} run {runId} is still {state} but has been quiet for {FormatDuration(quietFor)}{(model is null ? string.Empty : $" · {model}")}.",
                CreatedAt = startedAt,
                LatestAt = lastProgressAt,
                SuggestedAction = "Open the run detail and either wait, abort, or investigate the child process."
            });
        }
    }

    private async Task AddRerunUnavailableAsync(SqliteConnection conn, AttentionListOptions options, List<AttentionItem> items)
    {
        await using var cmd = conn.CreateCommand();
        var where = new List<string> { "stream_kind = 'ops'", "event_type = 'subagent_rerun_unavailable'" };
        AddProjectTaskFilter(cmd, where, "project_id", "task_id", options);
        cmd.CommandText = $"""
            SELECT id, project_id, task_id, body, json_extract(metadata, '$.run_id') AS run_id, created_at
            FROM agent_stream_entries
            WHERE {string.Join(" AND ", where)}
            """;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var streamId = reader.GetInt32(0);
            var projectId = NullableString(reader, 1);
            if (projectId is null)
                continue;

            var taskId = NullableInt(reader, 2);
            var body = NullableString(reader, 3);
            var runId = NullableString(reader, 4);
            var createdAt = ReadDate(reader, 5);
            items.Add(new AttentionItem
            {
                Id = $"stream:{streamId}:rerun_unavailable",
                ProjectId = projectId,
                TaskId = taskId,
                RunId = runId,
                Kind = "subagent_rerun_unavailable",
                Severity = "warning",
                Title = "Sub-agent rerun unavailable",
                Summary = body ?? $"Run {runId ?? "unknown"} could not be rerun by the live conductor.",
                CreatedAt = createdAt,
                LatestAt = createdAt,
                SuggestedAction = "Start a fresh run manually or retry from a conductor instance that still has the run snapshot."
            });
        }
    }

    private async Task AddBlockedTasksAsync(SqliteConnection conn, AttentionListOptions options, List<AttentionItem> items)
    {
        await using var cmd = conn.CreateCommand();
        var where = new List<string> { "status = 'blocked'" };
        AddProjectTaskFilter(cmd, where, "project_id", "id", options);
        cmd.CommandText = $"""
            SELECT id, project_id, title, created_at, updated_at
            FROM tasks
            WHERE {string.Join(" AND ", where)}
            """;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var taskId = reader.GetInt32(0);
            var projectId = reader.GetString(1);
            var title = reader.GetString(2);
            items.Add(new AttentionItem
            {
                Id = $"task:{taskId}:blocked",
                ProjectId = projectId,
                TaskId = taskId,
                Kind = "blocked_task",
                Severity = "warning",
                Title = $"Blocked task #{taskId}",
                Summary = title,
                CreatedAt = ReadDate(reader, 3),
                LatestAt = ReadDate(reader, 4),
                SuggestedAction = "Resolve dependencies, update the task with the blocker, or unblock it if the blocker is gone."
            });
        }
    }

    private async Task AddQuestionMessagesAsync(SqliteConnection conn, AttentionListOptions options, List<AttentionItem> items)
    {
        await using var cmd = conn.CreateCommand();
        var where = new List<string> { "q.intent = 'question'" };
        AddProjectTaskFilter(cmd, where, "q.project_id", "q.task_id", options);
        cmd.CommandText = $"""
            SELECT q.id, q.project_id, q.task_id, q.sender, q.content, q.created_at
            FROM messages q
            WHERE {string.Join(" AND ", where)}
              AND NOT EXISTS (
                  SELECT 1
                  FROM messages a
                  WHERE a.project_id = q.project_id
                    AND COALESCE(a.thread_id, a.id) = COALESCE(q.thread_id, q.id)
                    AND (
                        a.created_at > q.created_at
                        OR (a.created_at = q.created_at AND a.id > q.id)
                    )
                    AND a.intent = 'answer'
              )
            """;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var messageId = reader.GetInt32(0);
            var projectId = reader.GetString(1);
            var taskId = NullableInt(reader, 2);
            var sender = reader.GetString(3);
            var content = reader.GetString(4);
            var createdAt = ReadDate(reader, 5);
            items.Add(new AttentionItem
            {
                Id = $"message:{messageId}:question",
                ProjectId = projectId,
                TaskId = taskId,
                MessageId = messageId,
                Kind = "question_message",
                Severity = "info",
                Title = $"Question from {sender}",
                Summary = NormalizeSummary(content),
                CreatedAt = createdAt,
                LatestAt = createdAt,
                SuggestedAction = "Answer the question in the task thread or mark the decision in Den."
            });
        }
    }

    private async Task AddOpenReviewFindingsAsync(SqliteConnection conn, AttentionListOptions options, List<AttentionItem> items)
    {
        await using var cmd = conn.CreateCommand();
        var where = new List<string> { "rf.status IN ('open', 'claimed_fixed', 'not_fixed')" };
        AddProjectTaskFilter(cmd, where, "t.project_id", "rf.task_id", options);
        cmd.CommandText = $"""
            SELECT rf.id, rf.finding_key, t.project_id, rf.task_id, rf.review_round_id,
                   rf.category, rf.summary, rf.created_at, rf.updated_at
            FROM review_findings rf
            JOIN tasks t ON t.id = rf.task_id
            WHERE {string.Join(" AND ", where)}
            """;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var findingId = reader.GetInt32(0);
            var findingKey = reader.GetString(1);
            var projectId = reader.GetString(2);
            var taskId = reader.GetInt32(3);
            var reviewRoundId = reader.GetInt32(4);
            var category = reader.GetString(5);
            var summary = reader.GetString(6);
            items.Add(new AttentionItem
            {
                Id = $"finding:{findingId}:open",
                ProjectId = projectId,
                TaskId = taskId,
                ReviewRoundId = reviewRoundId,
                Kind = "open_review_finding",
                Severity = category is "blocking_bug" or "acceptance_gap" ? "critical" : "warning",
                Title = $"Open review finding {findingKey}",
                Summary = summary,
                CreatedAt = ReadDate(reader, 7),
                LatestAt = ReadDate(reader, 8),
                SuggestedAction = "Address the finding, update its status, or split an explicit follow-up task."
            });
        }
    }

    private async Task AddPendingReviewsWithFailedReviewerRunsAsync(SqliteConnection conn, AttentionListOptions options, List<AttentionItem> items)
    {
        await using var cmd = conn.CreateCommand();
        var where = new List<string>
        {
            "rr.verdict IS NULL",
            "ar.role = 'reviewer'"
        };
        AddInFilter(cmd, where, "ar.state", ProblemRunStates, "reviewerState");
        AddProjectTaskFilter(cmd, where, "t.project_id", "rr.task_id", options);
        cmd.CommandText = $"""
            SELECT rr.id, rr.task_id, rr.round_number, rr.branch, rr.requested_at,
                   t.project_id, ar.run_id, ar.state, ar.started_at, ar.ended_at, ar.updated_at, ar.created_at
            FROM review_rounds rr
            JOIN tasks t ON t.id = rr.task_id
            JOIN agent_runs ar ON ar.task_id = rr.task_id
            WHERE {string.Join(" AND ", where)}
              AND (
                  ar.review_round_id = rr.id
                  OR (
                      ar.review_round_id IS NULL
                      AND COALESCE(ar.started_at, ar.created_at) >= rr.requested_at
                  )
              )
            """;

        var byRound = new Dictionary<int, AttentionItem>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var roundId = reader.GetInt32(0);
            var taskId = reader.GetInt32(1);
            var roundNumber = reader.GetInt32(2);
            var branch = reader.GetString(3);
            var requestedAt = ReadDate(reader, 4);
            var projectId = reader.GetString(5);
            var runId = reader.GetString(6);
            var state = reader.GetString(7);
            var latestAt = NullableDate(reader, 9) ?? NullableDate(reader, 10) ?? ReadDate(reader, 11);
            var item = new AttentionItem
            {
                Id = $"review:{roundId}:reviewer_failed",
                ProjectId = projectId,
                TaskId = taskId,
                RunId = runId,
                ReviewRoundId = roundId,
                Kind = "pending_review_reviewer_failed",
                Severity = "critical",
                Title = $"Review round R{roundNumber} has failed reviewer run",
                Summary = $"Reviewer run {runId} is {state} while review for {branch} is still pending.",
                CreatedAt = requestedAt,
                LatestAt = latestAt,
                SuggestedAction = "Launch a fresh reviewer, review manually, or update the review round with a valid verdict."
            };

            if (!byRound.TryGetValue(roundId, out var existing) || item.LatestAt > existing.LatestAt)
                byRound[roundId] = item;
        }

        items.AddRange(byRound.Values);
    }

    private static AttentionListOptions NormalizeOptions(AttentionListOptions options) => new()
    {
        ProjectId = string.IsNullOrWhiteSpace(options.ProjectId) ? null : options.ProjectId.Trim(),
        TaskId = options.TaskId,
        Kind = string.IsNullOrWhiteSpace(options.Kind) ? null : options.Kind.Trim(),
        Severity = string.IsNullOrWhiteSpace(options.Severity) ? null : options.Severity.Trim(),
        Limit = Math.Clamp(options.Limit, 1, 200)
    };

    private static bool MatchesKindSeverity(AttentionItem item, AttentionListOptions options) =>
        (options.Kind is null || string.Equals(item.Kind, options.Kind, StringComparison.Ordinal)) &&
        (options.Severity is null || string.Equals(item.Severity, options.Severity, StringComparison.Ordinal));

    private static void AddProjectTaskFilter(
        SqliteCommand cmd,
        List<string> where,
        string projectExpression,
        string taskExpression,
        AttentionListOptions options)
    {
        if (options.ProjectId is not null)
        {
            where.Add($"{projectExpression} = @projectId");
            cmd.Parameters.AddWithValue("@projectId", options.ProjectId);
        }

        if (options.TaskId is not null)
        {
            where.Add($"{taskExpression} = @taskId");
            cmd.Parameters.AddWithValue("@taskId", options.TaskId.Value);
        }
    }

    private static void AddInFilter(
        SqliteCommand cmd,
        List<string> where,
        string expression,
        IReadOnlyList<string> values,
        string parameterPrefix)
    {
        var parameters = new List<string>();
        for (var i = 0; i < values.Count; i++)
        {
            var name = $"@{parameterPrefix}{i}";
            parameters.Add(name);
            cmd.Parameters.AddWithValue(name, values[i]);
        }

        where.Add($"{expression} IN ({string.Join(", ", parameters)})");
    }

    private static int SeverityRank(string severity) => severity switch
    {
        "critical" => 0,
        "warning" => 1,
        "info" => 2,
        _ => 3
    };

    private static int? NullableInt(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTime? NullableDate(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseDate(reader.GetString(ordinal));

    private static DateTime ReadDate(SqliteDataReader reader, int ordinal) =>
        ParseDate(reader.GetString(ordinal));

    private static DateTime ParseDate(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static string NormalizeSummary(string text)
    {
        var normalized = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 240 ? normalized : normalized[..237] + "...";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays}d {duration.Hours}h";
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes}m";
        return $"{Math.Max(0, (int)duration.TotalSeconds)}s";
    }
}
