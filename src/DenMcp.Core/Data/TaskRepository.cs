using System.Text.Json;
using DenMcp.Core.Models;
using Microsoft.Data.Sqlite;
using TaskStatus = DenMcp.Core.Models.TaskStatus;

namespace DenMcp.Core.Data;

public interface ITaskRepository
{
    Task<ProjectTask> CreateAsync(ProjectTask task, int[]? dependsOn = null);
    Task<ProjectTask?> GetByIdAsync(int id);
    Task<TaskDetail> GetDetailAsync(int id);
    Task<TaskWorkflowSummary> GetWorkflowSummaryAsync(int id);
    Task<List<TaskSummary>> ListAsync(string projectId, TaskStatus[]? statuses = null,
        string? assignedTo = null, string[]? tags = null, int? maxPriority = null, int? parentId = null,
        bool includeAll = false);
    Task<ProjectTask> UpdateAsync(int id, Dictionary<string, object?> changes, string agent);
    Task AddDependencyAsync(int taskId, int dependsOn);
    Task RemoveDependencyAsync(int taskId, int dependsOn);
    Task<ProjectTask?> GetNextTaskAsync(string projectId, string? assignedTo = null);
}

public sealed class TaskRepository : ITaskRepository
{
    private readonly DbConnectionFactory _db;

    public TaskRepository(DbConnectionFactory db) => _db = db;

    public async Task<ProjectTask> CreateAsync(ProjectTask task, int[]? dependsOn = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tasks (project_id, parent_id, title, description, status, priority, assigned_to, tags)
            VALUES (@projectId, @parentId, @title, @description, @status, @priority, @assignedTo, @tags)
            RETURNING id, project_id, parent_id, title, description, status, priority, assigned_to, tags, created_at, updated_at
            """;
        cmd.Parameters.AddWithValue("@projectId", task.ProjectId);
        cmd.Parameters.AddWithValue("@parentId", (object?)task.ParentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@title", task.Title);
        cmd.Parameters.AddWithValue("@description", (object?)task.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", task.Status.ToDbValue());
        cmd.Parameters.AddWithValue("@priority", task.Priority);
        cmd.Parameters.AddWithValue("@assignedTo", (object?)task.AssignedTo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tags", task.Tags is { Count: > 0 } ? JsonSerializer.Serialize(task.Tags) : DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var created = ReadTask(reader);
        await reader.CloseAsync();

        if (dependsOn is { Length: > 0 })
        {
            foreach (var depId in dependsOn)
            {
                await using var depCmd = conn.CreateCommand();
                depCmd.CommandText = "INSERT INTO task_dependencies (task_id, depends_on) VALUES (@taskId, @dependsOn)";
                depCmd.Parameters.AddWithValue("@taskId", created.Id);
                depCmd.Parameters.AddWithValue("@dependsOn", depId);
                await depCmd.ExecuteNonQueryAsync();
            }
        }

        await tx.CommitAsync();
        return created;
    }

    public async Task<ProjectTask?> GetByIdAsync(int id)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, project_id, parent_id, title, description, status, priority, assigned_to, tags, created_at, updated_at FROM tasks WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadTask(reader) : null;
    }

    public async Task<TaskDetail> GetDetailAsync(int id)
    {
        await using var conn = await _db.CreateConnectionAsync();

        // Main task
        await using var taskCmd = conn.CreateCommand();
        taskCmd.CommandText = "SELECT id, project_id, parent_id, title, description, status, priority, assigned_to, tags, created_at, updated_at FROM tasks WHERE id = @id";
        taskCmd.Parameters.AddWithValue("@id", id);
        await using var taskReader = await taskCmd.ExecuteReaderAsync();
        if (!await taskReader.ReadAsync())
            throw new KeyNotFoundException($"Task {id} not found");
        var task = ReadTask(taskReader);
        await taskReader.CloseAsync();

        // Dependencies
        await using var depCmd = conn.CreateCommand();
        depCmd.CommandText = """
            SELECT t.id, t.title, t.status
            FROM task_dependencies td
            JOIN tasks t ON t.id = td.depends_on
            WHERE td.task_id = @id
            """;
        depCmd.Parameters.AddWithValue("@id", id);
        var deps = new List<TaskDependencyInfo>();
        await using var depReader = await depCmd.ExecuteReaderAsync();
        while (await depReader.ReadAsync())
        {
            deps.Add(new TaskDependencyInfo
            {
                TaskId = depReader.GetInt32(0),
                Title = depReader.GetString(1),
                Status = EnumExtensions.ParseTaskStatus(depReader.GetString(2))
            });
        }
        await depReader.CloseAsync();

        // Subtasks
        await using var subCmd = conn.CreateCommand();
        subCmd.CommandText = """
            SELECT t.id, t.project_id, t.title, t.status, t.priority, t.assigned_to, t.parent_id, t.tags,
                   (SELECT COUNT(*) FROM task_dependencies WHERE task_id = t.id) as dep_count,
                   (SELECT COUNT(*) FROM tasks WHERE parent_id = t.id) as sub_count
            FROM tasks t WHERE t.parent_id = @id ORDER BY t.priority, t.id
            """;
        subCmd.Parameters.AddWithValue("@id", id);
        var subtasks = new List<TaskSummary>();
        await using var subReader = await subCmd.ExecuteReaderAsync();
        while (await subReader.ReadAsync())
            subtasks.Add(ReadTaskSummary(subReader));
        await subReader.CloseAsync();

        // Recent messages on this task
        await using var msgCmd = conn.CreateCommand();
        msgCmd.CommandText = """
            SELECT id, project_id, task_id, thread_id, sender, content, intent, metadata, created_at
            FROM messages WHERE task_id = @id
            ORDER BY created_at DESC LIMIT 10
            """;
        msgCmd.Parameters.AddWithValue("@id", id);
        var messages = new List<Message>();
        await using var msgReader = await msgCmd.ExecuteReaderAsync();
        while (await msgReader.ReadAsync())
            messages.Add(ReadMessage(msgReader));
        await msgReader.CloseAsync();

        // Review rounds
        await using var reviewCmd = conn.CreateCommand();
        reviewCmd.CommandText = """
            SELECT id, task_id, round_number, requested_by, branch, base_branch, base_commit,
                   head_commit, last_reviewed_head_commit, commits_since_last_review, tests_run,
                   notes, preferred_diff_base_ref, preferred_diff_base_commit, preferred_diff_head_ref,
                   preferred_diff_head_commit, alternate_diff_base_ref, alternate_diff_base_commit,
                   alternate_diff_head_ref, alternate_diff_head_commit, delta_base_commit,
                   inherited_commit_count, task_local_commit_count, verdict, verdict_by, verdict_notes,
                   requested_at, verdict_at
            FROM review_rounds WHERE task_id = @id
            ORDER BY round_number ASC
            """;
        reviewCmd.Parameters.AddWithValue("@id", id);
        var reviewRounds = new List<ReviewRound>();
        await using var reviewReader = await reviewCmd.ExecuteReaderAsync();
        while (await reviewReader.ReadAsync())
            reviewRounds.Add(ReviewRoundRepository.ReadReviewRound(reviewReader));
        await reviewReader.CloseAsync();

        // Review findings
        await using var findingCmd = conn.CreateCommand();
        findingCmd.CommandText = """
            SELECT rf.id, rf.finding_key, rf.task_id, rf.review_round_id, rf.finding_number, rf.created_by,
                   rf.category, rf.summary, rf.notes, rf.file_references, rf.test_commands, rf.status,
                   rf.status_updated_by, rf.status_notes, rf.status_updated_at, rf.response_by,
                   rf.response_notes, rf.response_at, rf.follow_up_task_id, rf.created_at, rf.updated_at,
                   rr.round_number
            FROM review_findings rf
            JOIN review_rounds rr ON rr.id = rf.review_round_id
            WHERE rf.task_id = @id
            ORDER BY rf.finding_number ASC
            """;
        findingCmd.Parameters.AddWithValue("@id", id);
        var openReviewFindings = new List<ReviewFinding>();
        var resolvedReviewFindings = new List<ReviewFinding>();
        await using var findingReader = await findingCmd.ExecuteReaderAsync();
        while (await findingReader.ReadAsync())
        {
            var finding = ReviewFindingRepository.ReadReviewFinding(findingReader);
            if (finding.Status.IsResolved())
                resolvedReviewFindings.Add(finding);
            else
                openReviewFindings.Add(finding);
        }
        await findingReader.CloseAsync();

        return new TaskDetail
        {
            Task = task,
            Dependencies = deps,
            Subtasks = subtasks,
            RecentMessages = messages,
            ReviewRounds = reviewRounds,
            OpenReviewFindings = openReviewFindings,
            ResolvedReviewFindings = resolvedReviewFindings,
            ReviewWorkflow = ReviewWorkflowSummaryBuilder.Build(
                reviewRounds,
                openReviewFindings,
                resolvedReviewFindings)
        };
    }

    public async Task<TaskWorkflowSummary> GetWorkflowSummaryAsync(int id)
    {
        await using var conn = await _db.CreateConnectionAsync();

        // Main task
        await using var taskCmd = conn.CreateCommand();
        taskCmd.CommandText = "SELECT id, project_id, parent_id, title, description, status, priority, assigned_to, tags, created_at, updated_at FROM tasks WHERE id = @id";
        taskCmd.Parameters.AddWithValue("@id", id);
        await using var taskReader = await taskCmd.ExecuteReaderAsync();
        if (!await taskReader.ReadAsync())
            throw new KeyNotFoundException($"Task {id} not found");
        var task = ReadTask(taskReader);
        await taskReader.CloseAsync();

        // Dependencies
        await using var depCmd = conn.CreateCommand();
        depCmd.CommandText = """
            SELECT t.id, t.title, t.status
            FROM task_dependencies td
            JOIN tasks t ON t.id = td.depends_on
            WHERE td.task_id = @id
            """;
        depCmd.Parameters.AddWithValue("@id", id);
        var deps = new List<TaskDependencyInfo>();
        await using var depReader = await depCmd.ExecuteReaderAsync();
        while (await depReader.ReadAsync())
        {
            deps.Add(new TaskDependencyInfo
            {
                TaskId = depReader.GetInt32(0),
                Title = depReader.GetString(1),
                Status = EnumExtensions.ParseTaskStatus(depReader.GetString(2))
            });
        }
        await depReader.CloseAsync();

        // Subtasks (compact)
        await using var subCmd = conn.CreateCommand();
        subCmd.CommandText = """
            SELECT t.id, t.title, t.status, t.priority
            FROM tasks t WHERE t.parent_id = @id ORDER BY t.priority, t.id
            """;
        subCmd.Parameters.AddWithValue("@id", id);
        var subtasks = new List<CompactSubtaskEntry>();
        await using var subReader = await subCmd.ExecuteReaderAsync();
        while (await subReader.ReadAsync())
        {
            subtasks.Add(new CompactSubtaskEntry
            {
                Id = subReader.GetInt32(0),
                Title = subReader.GetString(1),
                Status = EnumExtensions.ParseTaskStatus(subReader.GetString(2)).ToDbValue(),
                Priority = subReader.GetInt32(3)
            });
        }
        await subReader.CloseAsync();

        // Recent messages — only header fields, no content body
        await using var msgCmd = conn.CreateCommand();
        msgCmd.CommandText = """
            SELECT id, sender, intent, metadata, thread_id, created_at, content
            FROM messages WHERE task_id = @id
            ORDER BY created_at DESC LIMIT 10
            """;
        msgCmd.Parameters.AddWithValue("@id", id);
        var messageHeaders = new List<CompactMessageHeader>();
        await using var msgReader = await msgCmd.ExecuteReaderAsync();
        while (await msgReader.ReadAsync())
        {
            var metaJson = msgReader.IsDBNull(3) ? null : msgReader.GetString(3);
            var content = msgReader.IsDBNull(6) ? "" : msgReader.GetString(6);
            var firstLine = ExtractFirstLine(content);

            string? metadataType = null;
            string? metadataBranch = null;
            string? metadataHeadCommit = null;
            string? metadataReviewRoundId = null;

            if (metaJson is not null)
            {
                var meta = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(metaJson);
                if (meta.TryGetProperty("type", out var typeEl)) metadataType = typeEl.GetString();
                if (meta.TryGetProperty("branch", out var branchEl)) metadataBranch = branchEl.GetString();
                if (meta.TryGetProperty("head_commit", out var hcEl)) metadataHeadCommit = hcEl.GetString();
                if (meta.TryGetProperty("review_round_id", out var rrEl))
                    metadataReviewRoundId = rrEl.ValueKind == System.Text.Json.JsonValueKind.Number
                        ? rrEl.GetInt32().ToString()
                        : rrEl.GetString();
            }

            messageHeaders.Add(new CompactMessageHeader
            {
                Id = msgReader.GetInt32(0),
                Sender = msgReader.GetString(1),
                Intent = msgReader.IsDBNull(2) ? null : EnumExtensions.ParseMessageIntent(msgReader.GetString(2)).ToDbValue(),
                MetadataType = metadataType,
                MetadataBranch = metadataBranch,
                MetadataHeadCommit = metadataHeadCommit,
                MetadataReviewRoundId = metadataReviewRoundId,
                FirstLine = firstLine,
                ThreadId = msgReader.IsDBNull(4) ? null : msgReader.GetInt32(4),
                CreatedAt = DateTime.Parse(msgReader.GetString(5))
            });
        }
        await msgReader.CloseAsync();

        // Review rounds
        await using var reviewCmd = conn.CreateCommand();
        reviewCmd.CommandText = """
            SELECT id, task_id, round_number, requested_by, branch, base_branch, base_commit,
                   head_commit, last_reviewed_head_commit, commits_since_last_review, tests_run,
                   notes, preferred_diff_base_ref, preferred_diff_base_commit, preferred_diff_head_ref,
                   preferred_diff_head_commit, alternate_diff_base_ref, alternate_diff_base_commit,
                   alternate_diff_head_ref, alternate_diff_head_commit, delta_base_commit,
                   inherited_commit_count, task_local_commit_count, verdict, verdict_by, verdict_notes,
                   requested_at, verdict_at
            FROM review_rounds WHERE task_id = @id
            ORDER BY round_number ASC
            """;
        reviewCmd.Parameters.AddWithValue("@id", id);
        var reviewRounds = new List<ReviewRound>();
        await using var reviewReader = await reviewCmd.ExecuteReaderAsync();
        while (await reviewReader.ReadAsync())
            reviewRounds.Add(ReviewRoundRepository.ReadReviewRound(reviewReader));
        await reviewReader.CloseAsync();

        // Review findings — split open vs resolved
        await using var findingCmd = conn.CreateCommand();
        findingCmd.CommandText = """
            SELECT rf.id, rf.finding_key, rf.task_id, rf.review_round_id, rf.finding_number, rf.created_by,
                   rf.category, rf.summary, rf.notes, rf.file_references, rf.test_commands, rf.status,
                   rf.status_updated_by, rf.status_notes, rf.status_updated_at, rf.response_by,
                   rf.response_notes, rf.response_at, rf.follow_up_task_id, rf.created_at, rf.updated_at,
                   rr.round_number
            FROM review_findings rf
            JOIN review_rounds rr ON rr.id = rf.review_round_id
            WHERE rf.task_id = @id
            ORDER BY rf.finding_number ASC
            """;
        findingCmd.Parameters.AddWithValue("@id", id);
        var openFindings = new List<ReviewFinding>();
        var resolvedFindings = new List<ReviewFinding>();
        await using var findingReader = await findingCmd.ExecuteReaderAsync();
        while (await findingReader.ReadAsync())
        {
            var finding = ReviewFindingRepository.ReadReviewFinding(findingReader);
            if (finding.Status.IsResolved())
                resolvedFindings.Add(finding);
            else
                openFindings.Add(finding);
        }
        await findingReader.CloseAsync();

        // Build compact review workflow
        var allFindings = openFindings.Concat(resolvedFindings).ToList();
        var findingsByRound = allFindings
            .GroupBy(f => f.ReviewRoundId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var currentRound = reviewRounds
            .OrderByDescending(r => r.RoundNumber)
            .FirstOrDefault();

        var timeline = reviewRounds
            .OrderByDescending(r => r.RoundNumber)
            .Select(r =>
            {
                var rf = findingsByRound.GetValueOrDefault(r.Id, []);
                return new CompactReviewRoundRef
                {
                    ReviewRoundId = r.Id,
                    ReviewRoundNumber = r.RoundNumber,
                    Branch = r.Branch,
                    HeadCommit = r.HeadCommit,
                    Verdict = r.Verdict?.ToDbValue(),
                    TotalFindingCount = rf.Count,
                    OpenFindingCount = rf.Count(f => !f.Status.IsResolved()),
                    ResolvedFindingCount = rf.Count(f => f.Status.IsResolved())
                };
            })
            .ToList();

        var compactWorkflow = new CompactReviewWorkflow
        {
            ReviewRoundCount = reviewRounds.Count,
            CurrentVerdict = currentRound?.Verdict?.ToDbValue(),
            CurrentRound = currentRound is not null
                ? new CompactReviewRoundRef
                {
                    ReviewRoundId = currentRound.Id,
                    ReviewRoundNumber = currentRound.RoundNumber,
                    Branch = currentRound.Branch,
                    HeadCommit = currentRound.HeadCommit,
                    Verdict = currentRound.Verdict?.ToDbValue(),
                    TotalFindingCount = findingsByRound.GetValueOrDefault(currentRound.Id, []).Count,
                    OpenFindingCount = findingsByRound.GetValueOrDefault(currentRound.Id, []).Count(f => !f.Status.IsResolved()),
                    ResolvedFindingCount = findingsByRound.GetValueOrDefault(currentRound.Id, []).Count(f => f.Status.IsResolved())
                }
                : null,
            UnresolvedFindingCount = openFindings.Count,
            ResolvedFindingCount = resolvedFindings.Count,
            AddressedFindingCount = allFindings.Count(f =>
                f.ResponseAt is not null || f.Status != ReviewFindingStatus.Open),
            Timeline = timeline
        };

        // Unresolved findings (compact)
        var unresolvedEntries = openFindings.Select(f => new CompactFindingEntry
        {
            Id = f.Id,
            FindingKey = f.FindingKey,
            Category = f.Category.ToDbValue(),
            Summary = f.Summary,
            Status = f.Status.ToDbValue(),
            ReviewRoundId = f.ReviewRoundId,
            ReviewRoundNumber = f.ReviewRoundNumber
        }).ToList();

        return new TaskWorkflowSummary
        {
            Id = task.Id,
            ProjectId = task.ProjectId,
            Title = task.Title,
            Status = task.Status.ToDbValue(),
            Priority = task.Priority,
            AssignedTo = task.AssignedTo,
            ParentId = task.ParentId,
            Tags = task.Tags,
            Dependencies = deps,
            Subtasks = subtasks,
            ReviewWorkflow = compactWorkflow,
            RecentMessages = messageHeaders,
            UnresolvedFindings = unresolvedEntries,
            DeepReadHint = $"Use get_task(task_id={id}) or get_thread(thread_id=...) for full content."
        };
    }

    private static string? ExtractFirstLine(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return null;

        // Find first newline
        var newlineIdx = content.IndexOf('\n');
        var firstLine = newlineIdx >= 0 ? content[..newlineIdx] : content;

        // Truncate to a reasonable length for header display
        const int maxLength = 120;
        if (firstLine.Length > maxLength)
            firstLine = string.Concat(firstLine.AsSpan(0, maxLength - 1), "…");

        return string.IsNullOrWhiteSpace(firstLine) ? null : firstLine.Trim();
    }

    public async Task<List<TaskSummary>> ListAsync(string projectId, TaskStatus[]? statuses = null,
        string? assignedTo = null, string[]? tags = null, int? maxPriority = null, int? parentId = null,
        bool includeAll = false)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var where = new List<string> { "t.project_id = @projectId" };
        cmd.Parameters.AddWithValue("@projectId", projectId);

        if (statuses is { Length: > 0 })
        {
            var placeholders = new List<string>();
            for (var i = 0; i < statuses.Length; i++)
            {
                var p = $"@status{i}";
                placeholders.Add(p);
                cmd.Parameters.AddWithValue(p, statuses[i].ToDbValue());
            }
            where.Add($"t.status IN ({string.Join(", ", placeholders)})");
        }

        if (assignedTo is not null)
        {
            where.Add("t.assigned_to = @assignedTo");
            cmd.Parameters.AddWithValue("@assignedTo", assignedTo);
        }

        if (maxPriority is not null)
        {
            where.Add("t.priority <= @maxPriority");
            cmd.Parameters.AddWithValue("@maxPriority", maxPriority.Value);
        }

        if (parentId is not null)
        {
            where.Add("t.parent_id = @parentId");
            cmd.Parameters.AddWithValue("@parentId", parentId.Value);
        }
        else if (!includeAll)
        {
            where.Add("t.parent_id IS NULL");
        }

        // Tag filtering: task must have ALL specified tags
        if (tags is { Length: > 0 })
        {
            for (var i = 0; i < tags.Length; i++)
            {
                var p = $"@tag{i}";
                where.Add($"EXISTS (SELECT 1 FROM json_each(t.tags) WHERE json_each.value = {p})");
                cmd.Parameters.AddWithValue(p, tags[i]);
            }
        }

        cmd.CommandText = $"""
            SELECT t.id, t.project_id, t.title, t.status, t.priority, t.assigned_to, t.parent_id, t.tags,
                   (SELECT COUNT(*) FROM task_dependencies WHERE task_id = t.id) as dep_count,
                   (SELECT COUNT(*) FROM tasks WHERE parent_id = t.id) as sub_count
            FROM tasks t
            WHERE {string.Join(" AND ", where)}
            ORDER BY t.priority, t.id
            """;

        var results = new List<TaskSummary>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(ReadTaskSummary(reader));
        return results;
    }

    public async Task<ProjectTask> UpdateAsync(int id, Dictionary<string, object?> changes, string agent)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // Get current values for history
        var current = await GetByIdWithConnectionAsync(conn, id)
            ?? throw new KeyNotFoundException($"Task {id} not found");

        var sets = new List<string>();
        var paramIdx = 0;
        await using var cmd = conn.CreateCommand();

        foreach (var (field, newValue) in changes)
        {
            var (column, oldDbVal, newDbVal) = field switch
            {
                "title" => ("title", current.Title, (object?)(string?)newValue),
                "description" => ("description", (object?)current.Description, newValue),
                "status" => ("status", current.Status.ToDbValue(), ((TaskStatus)newValue!).ToDbValue()),
                "priority" => ("priority", (object)current.Priority, newValue),
                "assigned_to" => ("assigned_to", (object?)current.AssignedTo, newValue),
                "tags" => ("tags", current.Tags is { Count: > 0 } ? JsonSerializer.Serialize(current.Tags) : null,
                    newValue is List<string> t && t.Count > 0 ? JsonSerializer.Serialize(t) : null),
                "parent_id" => ("parent_id", (object?)current.ParentId, newValue),
                _ => throw new ArgumentException($"Unknown field: {field}")
            };

            var p = $"@p{paramIdx++}";
            sets.Add($"{column} = {p}");
            cmd.Parameters.AddWithValue(p, newDbVal ?? DBNull.Value);

            // Write history
            await using var histCmd = conn.CreateCommand();
            histCmd.CommandText = """
                INSERT INTO task_history (task_id, field, old_value, new_value, changed_by)
                VALUES (@taskId, @field, @oldValue, @newValue, @agent)
                """;
            histCmd.Parameters.AddWithValue("@taskId", id);
            histCmd.Parameters.AddWithValue("@field", field);
            histCmd.Parameters.AddWithValue("@oldValue", oldDbVal?.ToString() ?? (object)DBNull.Value);
            histCmd.Parameters.AddWithValue("@newValue", newDbVal?.ToString() ?? (object)DBNull.Value);
            histCmd.Parameters.AddWithValue("@agent", agent);
            await histCmd.ExecuteNonQueryAsync();
        }

        sets.Add("updated_at = datetime('now')");
        cmd.CommandText = $"""
            UPDATE tasks SET {string.Join(", ", sets)} WHERE id = @id
            RETURNING id, project_id, parent_id, title, description, status, priority, assigned_to, tags, created_at, updated_at
            """;
        cmd.Parameters.AddWithValue("@id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var updated = ReadTask(reader);
        await reader.CloseAsync();

        await tx.CommitAsync();
        return updated;
    }

    public async Task AddDependencyAsync(int taskId, int dependsOn)
    {
        await using var conn = await _db.CreateConnectionAsync();

        // Cycle detection: check if dependsOn can reach taskId
        if (await WouldCreateCycleAsync(conn, taskId, dependsOn))
            throw new InvalidOperationException($"Adding dependency {taskId} -> {dependsOn} would create a cycle");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO task_dependencies (task_id, depends_on) VALUES (@taskId, @dependsOn)";
        cmd.Parameters.AddWithValue("@taskId", taskId);
        cmd.Parameters.AddWithValue("@dependsOn", dependsOn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RemoveDependencyAsync(int taskId, int dependsOn)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM task_dependencies WHERE task_id = @taskId AND depends_on = @dependsOn";
        cmd.Parameters.AddWithValue("@taskId", taskId);
        cmd.Parameters.AddWithValue("@dependsOn", dependsOn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<ProjectTask?> GetNextTaskAsync(string projectId, string? assignedTo = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        // Two-tier: first subtasks of in-progress parents, then top-level planned tasks.
        // Both filtered to tasks whose dependencies are all done.
        var assignedFilter = assignedTo is not null ? "AND t.assigned_to = @assignedTo" : "";

        cmd.CommandText = $"""
            WITH unblocked AS (
                SELECT t.id
                FROM tasks t
                WHERE t.project_id = @projectId
                  AND NOT EXISTS (
                    SELECT 1 FROM task_dependencies td
                    JOIN tasks dep ON dep.id = td.depends_on
                    WHERE td.task_id = t.id AND dep.status != 'done'
                  )
            ),
            candidates AS (
                -- Tier 1: subtasks of in-progress parents
                SELECT t.id, t.project_id, t.parent_id, t.title, t.description, t.status, t.priority, t.assigned_to, t.tags, t.created_at, t.updated_at,
                       0 as tier,
                       (SELECT COUNT(*) FROM task_dependencies WHERE task_id = t.id) as dep_count
                FROM tasks t
                JOIN tasks parent ON parent.id = t.parent_id AND parent.status = 'in_progress'
                WHERE t.project_id = @projectId
                  AND t.status IN ('planned', 'in_progress')
                  AND t.id IN (SELECT id FROM unblocked)
                  {assignedFilter}
                UNION ALL
                -- Tier 2: top-level planned tasks
                SELECT t.id, t.project_id, t.parent_id, t.title, t.description, t.status, t.priority, t.assigned_to, t.tags, t.created_at, t.updated_at,
                       1 as tier,
                       (SELECT COUNT(*) FROM task_dependencies WHERE task_id = t.id) as dep_count
                FROM tasks t
                WHERE t.project_id = @projectId
                  AND t.parent_id IS NULL
                  AND t.status = 'planned'
                  AND t.id IN (SELECT id FROM unblocked)
                  {assignedFilter}
            )
            SELECT id, project_id, parent_id, title, description, status, priority, assigned_to, tags, created_at, updated_at
            FROM candidates
            ORDER BY tier ASC, priority ASC, dep_count ASC, id ASC
            LIMIT 1
            """;

        cmd.Parameters.AddWithValue("@projectId", projectId);
        if (assignedTo is not null)
            cmd.Parameters.AddWithValue("@assignedTo", assignedTo);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadTask(reader) : null;
    }

    private static async Task<bool> WouldCreateCycleAsync(SqliteConnection conn, int taskId, int dependsOn)
    {
        // BFS from dependsOn through its dependencies. If we reach taskId, it's a cycle.
        var visited = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(dependsOn);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == taskId)
                return true;
            if (!visited.Add(current))
                continue;

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT depends_on FROM task_dependencies WHERE task_id = @id";
            cmd.Parameters.AddWithValue("@id", current);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                queue.Enqueue(reader.GetInt32(0));
        }

        return false;
    }

    private static async Task<ProjectTask?> GetByIdWithConnectionAsync(SqliteConnection conn, int id)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, project_id, parent_id, title, description, status, priority, assigned_to, tags, created_at, updated_at FROM tasks WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadTask(reader) : null;
    }

    internal static ProjectTask ReadTask(SqliteDataReader reader)
    {
        var tagsJson = reader.IsDBNull(8) ? null : reader.GetString(8);
        return new ProjectTask
        {
            Id = reader.GetInt32(0),
            ProjectId = reader.GetString(1),
            ParentId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
            Title = reader.GetString(3),
            Description = reader.IsDBNull(4) ? null : reader.GetString(4),
            Status = EnumExtensions.ParseTaskStatus(reader.GetString(5)),
            Priority = reader.GetInt32(6),
            AssignedTo = reader.IsDBNull(7) ? null : reader.GetString(7),
            Tags = tagsJson is not null ? JsonSerializer.Deserialize<List<string>>(tagsJson) : null,
            CreatedAt = DateTime.Parse(reader.GetString(9)),
            UpdatedAt = DateTime.Parse(reader.GetString(10))
        };
    }

    private static TaskSummary ReadTaskSummary(SqliteDataReader reader)
    {
        var tagsJson = reader.IsDBNull(7) ? null : reader.GetString(7);
        return new TaskSummary
        {
            Id = reader.GetInt32(0),
            ProjectId = reader.GetString(1),
            Title = reader.GetString(2),
            Status = EnumExtensions.ParseTaskStatus(reader.GetString(3)),
            Priority = reader.GetInt32(4),
            AssignedTo = reader.IsDBNull(5) ? null : reader.GetString(5),
            ParentId = reader.IsDBNull(6) ? null : reader.GetInt32(6),
            Tags = tagsJson is not null ? JsonSerializer.Deserialize<List<string>>(tagsJson) : null,
            DependencyCount = reader.GetInt32(8),
            SubtaskCount = reader.GetInt32(9)
        };
    }

    internal static Message ReadMessage(SqliteDataReader reader)
    {
        var metaJson = reader.IsDBNull(7) ? null : reader.GetString(7);
        return new Message
        {
            Id = reader.GetInt32(0),
            ProjectId = reader.GetString(1),
            TaskId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
            ThreadId = reader.IsDBNull(3) ? null : reader.GetInt32(3),
            Sender = reader.GetString(4),
            Content = reader.GetString(5),
            Intent = EnumExtensions.ParseMessageIntent(reader.GetString(6)),
            Metadata = metaJson is not null ? JsonSerializer.Deserialize<JsonElement>(metaJson) : null,
            CreatedAt = DateTime.Parse(reader.GetString(8))
        };
    }
}
