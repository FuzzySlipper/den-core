using System.Text;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using TaskStatus = DenMcp.Core.Models.TaskStatus;

namespace DenMcp.Core.Services;

public interface IReviewFindingTriageService
{
    Task<SplitFindingsToFollowUpResult> SplitFindingsToFollowUpAsync(SplitFindingsToFollowUpInput input);
}

public sealed class SplitFindingsToFollowUpInput
{
    public required int TaskId { get; set; }
    public required string ProjectId { get; set; }
    public required List<int> FindingIds { get; set; }
    public required string SplitBy { get; set; }
    public string? FollowUpTitle { get; set; }
    public int? FollowUpParentTaskId { get; set; }
    public int? FollowUpPriority { get; set; }
    public string? FollowUpAssignedTo { get; set; }
    public List<string>? FollowUpTags { get; set; }
    public bool OverrideBlocking { get; set; }
}

public sealed class SplitFindingsToFollowUpResult
{
    public required ProjectTask FollowUpTask { get; set; }
    public required List<ReviewFinding> UpdatedFindings { get; set; }
    public required List<int> SkippedFindingIds { get; set; }
}

public sealed class ReviewFindingTriageService : IReviewFindingTriageService
{
    private readonly ITaskRepository _tasks;
    private readonly IReviewFindingRepository _findings;
    private readonly DbConnectionFactory _db;
    private readonly ILogger<ReviewFindingTriageService> _logger;

    public ReviewFindingTriageService(
        ITaskRepository tasks,
        IReviewFindingRepository findings,
        DbConnectionFactory db,
        ILogger<ReviewFindingTriageService> logger)
    {
        _tasks = tasks;
        _findings = findings;
        _db = db;
        _logger = logger;
    }

    public async Task<SplitFindingsToFollowUpResult> SplitFindingsToFollowUpAsync(SplitFindingsToFollowUpInput input)
    {
        if (input.FindingIds is not { Count: > 0 })
            throw new ArgumentException("At least one finding ID is required.", nameof(input));

        // Load and validate the source task
        var sourceTask = await _tasks.GetByIdAsync(input.TaskId)
            ?? throw new KeyNotFoundException($"Task {input.TaskId} not found");

        if (!string.Equals(sourceTask.ProjectId, input.ProjectId, StringComparison.Ordinal))
            throw new KeyNotFoundException($"Task {input.TaskId} not found in project {input.ProjectId}");

        // Validate follow-up parent if specified
        if (input.FollowUpParentTaskId is not null)
        {
            var parentTask = await _tasks.GetByIdAsync(input.FollowUpParentTaskId.Value)
                ?? throw new KeyNotFoundException($"Parent task {input.FollowUpParentTaskId.Value} not found");

            if (!string.Equals(parentTask.ProjectId, input.ProjectId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Parent task {input.FollowUpParentTaskId.Value} must be in the same project.");
        }

        // Load findings, validate ownership and blocking status
        var loadedFindings = new List<ReviewFinding>();
        var skippedIds = new List<int>();

        foreach (var findingId in input.FindingIds)
        {
            var finding = await _findings.GetByIdAsync(findingId);
            if (finding is null)
                throw new KeyNotFoundException($"Review finding {findingId} not found");

            if (finding.TaskId != input.TaskId)
                throw new InvalidOperationException(
                    $"Review finding {findingId} does not belong to task {input.TaskId}");

            if (IsBlockingCategory(finding.Category) && !input.OverrideBlocking)
            {
                _logger.LogInformation(
                    "Skipping blocking finding {FindingId} ({Category}). Set override_blocking=true to include.",
                    finding.Id, finding.Category);
                skippedIds.Add(finding.Id);
                continue;
            }

            loadedFindings.Add(finding);
        }

        if (loadedFindings.Count == 0)
            throw new InvalidOperationException(
                "No findings to split. All findings were either blocking (set override_blocking=true to include) or already resolved.");

        // Build the follow-up description with source task context
        var description = BuildFollowUpDescription(loadedFindings, sourceTask);
        var title = input.FollowUpTitle ?? $"Follow up: {loadedFindings.Count} review finding(s) from #{input.TaskId}";
        var splitStatusValue = ReviewFindingStatus.SplitToFollowUp.ToDbValue();

        // Execute task creation + all finding status updates in a single transaction
        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

        // Insert the follow-up task
        ProjectTask followUpTask;
        await using (var taskCmd = conn.CreateCommand())
        {
            taskCmd.CommandText = """
                INSERT INTO tasks (project_id, parent_id, title, description, status, priority, assigned_to, tags)
                VALUES (@projectId, @parentId, @title, @description, @status, @priority, @assignedTo, @tags)
                RETURNING id, project_id, parent_id, title, description, status, priority, assigned_to, tags, created_at, updated_at
                """;
            taskCmd.Parameters.AddWithValue("@projectId", input.ProjectId);
            taskCmd.Parameters.AddWithValue("@parentId", (object?)input.FollowUpParentTaskId ?? DBNull.Value);
            taskCmd.Parameters.AddWithValue("@title", title);
            taskCmd.Parameters.AddWithValue("@description", description);
            taskCmd.Parameters.AddWithValue("@status", TaskStatus.Planned.ToDbValue());
            taskCmd.Parameters.AddWithValue("@priority", input.FollowUpPriority ?? 3);
            taskCmd.Parameters.AddWithValue("@assignedTo", (object?)input.FollowUpAssignedTo ?? DBNull.Value);
            taskCmd.Parameters.AddWithValue("@tags",
                input.FollowUpTags is { Count: > 0 }
                    ? JsonSerializer.Serialize(input.FollowUpTags)
                    : DBNull.Value);

            await using var reader = await taskCmd.ExecuteReaderAsync();
            await reader.ReadAsync();
            followUpTask = TaskRepository.ReadTask(reader);
            await reader.CloseAsync();
        }

        // Update all finding statuses within the same transaction
        var updatedFindings = new List<ReviewFinding>();
        foreach (var finding in loadedFindings)
        {
            var statusNotes = $"Split to follow-up task #{followUpTask.Id}";
            await using var updateCmd = conn.CreateCommand();
            updateCmd.CommandText = """
                UPDATE review_findings
                SET status = @status,
                    status_updated_by = @updatedBy,
                    status_notes = @statusNotes,
                    status_updated_at = datetime('now'),
                    follow_up_task_id = @followUpTaskId,
                    updated_at = datetime('now')
                WHERE id = @id
                RETURNING id, finding_key, task_id, review_round_id, finding_number, created_by,
                          category, summary, notes, file_references, test_commands, status,
                          status_updated_by, status_notes, status_updated_at, response_by,
                          response_notes, response_at, follow_up_task_id, created_at, updated_at,
                          (SELECT round_number FROM review_rounds WHERE id = review_round_id) AS round_number
                """;
            updateCmd.Parameters.AddWithValue("@id", finding.Id);
            updateCmd.Parameters.AddWithValue("@status", splitStatusValue);
            updateCmd.Parameters.AddWithValue("@updatedBy", input.SplitBy);
            updateCmd.Parameters.AddWithValue("@statusNotes", statusNotes);
            updateCmd.Parameters.AddWithValue("@followUpTaskId", followUpTask.Id);

            await using var reader = await updateCmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                throw new KeyNotFoundException($"Review finding {finding.Id} not found during status update");
            updatedFindings.Add(ReviewFindingRepository.ReadReviewFinding(reader));
        }

        // Commit the entire transaction — task + all findings succeed or fail together
        await tx.CommitAsync();

        _logger.LogInformation(
            "Split {Count} findings from task #{TaskId} to follow-up task #{FollowUpTaskId}. Skipped: {SkippedCount}",
            updatedFindings.Count, input.TaskId, followUpTask.Id, skippedIds.Count);

        return new SplitFindingsToFollowUpResult
        {
            FollowUpTask = followUpTask,
            UpdatedFindings = updatedFindings,
            SkippedFindingIds = skippedIds
        };
    }

    private static bool IsBlockingCategory(ReviewFindingCategory category) =>
        category == ReviewFindingCategory.BlockingBug;

    internal static string BuildFollowUpDescription(List<ReviewFinding> findings, ProjectTask sourceTask)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Follow-up task for review findings split from the parent task.");
        sb.AppendLine();
        sb.AppendLine("## Source context");
        sb.AppendLine();
        sb.AppendLine($"- **Source task**: #{sourceTask.Id} — {sourceTask.Title}");
        sb.AppendLine($"- **Project**: `{sourceTask.ProjectId}`");
        sb.AppendLine();
        sb.AppendLine("## Findings");
        sb.AppendLine();

        foreach (var finding in findings.OrderBy(f => f.FindingNumber))
        {
            sb.AppendLine($"### {finding.FindingKey} — {finding.Category.ToDbValue()}");
            sb.AppendLine();
            sb.AppendLine($"**Summary**: {finding.Summary}");
            sb.AppendLine($"**Finding ID**: {finding.Id}");
            sb.AppendLine($"**Original status**: {finding.Status.ToDbValue()}");
            sb.AppendLine($"**Review round**: {finding.ReviewRoundNumber}");

            if (!string.IsNullOrWhiteSpace(finding.Notes))
            {
                sb.AppendLine();
                sb.AppendLine($"**Notes**: {finding.Notes}");
            }

            if (finding.FileReferences is { Count: > 0 })
            {
                sb.AppendLine();
                sb.AppendLine($"**File references**: {string.Join(", ", finding.FileReferences.Select(f => $"`{f}`"))}");
            }

            if (finding.TestCommands is { Count: > 0 })
            {
                sb.AppendLine();
                sb.AppendLine($"**Test commands**: {string.Join(", ", finding.TestCommands.Select(t => $"`{t}`"))}");
            }

            sb.AppendLine();
        }

        sb.AppendLine("## Acceptance criteria");
        sb.AppendLine();
        sb.AppendLine("Each finding listed above should be addressed (fixed, accepted, or further deferred).");
        sb.AppendLine("Close this task once all findings are resolved or explicitly accepted.");

        return sb.ToString().TrimEnd();
    }
}
