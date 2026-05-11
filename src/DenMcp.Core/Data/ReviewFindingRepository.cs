using System.Text.Json;
using DenMcp.Core.Models;
using Microsoft.Data.Sqlite;

namespace DenMcp.Core.Data;

public interface IReviewFindingRepository
{
    Task<ReviewFinding> CreateAsync(CreateReviewFindingInput input);
    Task<ReviewFinding?> GetByIdAsync(int id);
    Task<List<ReviewFinding>> ListByTaskAsync(int taskId, ReviewFindingStatus[]? statuses = null);
    Task<List<ReviewFinding>> ListByReviewRoundAsync(int reviewRoundId, ReviewFindingStatus[]? statuses = null);
    Task<ReviewFinding> RespondAsync(int id, RespondToReviewFindingInput input);
    Task<ReviewFinding> SetStatusAsync(int id, UpdateReviewFindingStatusInput input);
}

public sealed class ReviewFindingRepository : IReviewFindingRepository
{
    private readonly DbConnectionFactory _db;

    public ReviewFindingRepository(DbConnectionFactory db) => _db = db;

    public async Task<ReviewFinding> CreateAsync(CreateReviewFindingInput input)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

        var round = await GetRoundContextAsync(conn, input.ReviewRoundId)
            ?? throw new KeyNotFoundException($"Review round {input.ReviewRoundId} not found");
        var findingNumber = await GetNextFindingNumberAsync(conn, round.TaskId);
        var findingKey = $"R{round.TaskId}-{findingNumber}";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO review_findings (
                finding_key, task_id, review_round_id, finding_number, created_by, category,
                summary, notes, file_references, test_commands
            )
            VALUES (
                @findingKey, @taskId, @reviewRoundId, @findingNumber, @createdBy, @category,
                @summary, @notes, @fileReferences, @testCommands
            )
            RETURNING id, finding_key, task_id, review_round_id, finding_number, created_by,
                      category, summary, notes, file_references, test_commands, status,
                      status_updated_by, status_notes, status_updated_at, response_by,
                      response_notes, response_at, follow_up_task_id, created_at, updated_at
            """;
        cmd.Parameters.AddWithValue("@findingKey", findingKey);
        cmd.Parameters.AddWithValue("@taskId", round.TaskId);
        cmd.Parameters.AddWithValue("@reviewRoundId", input.ReviewRoundId);
        cmd.Parameters.AddWithValue("@findingNumber", findingNumber);
        cmd.Parameters.AddWithValue("@createdBy", input.CreatedBy);
        cmd.Parameters.AddWithValue("@category", input.Category.ToDbValue());
        cmd.Parameters.AddWithValue("@summary", input.Summary);
        cmd.Parameters.AddWithValue("@notes", (object?)input.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fileReferences", SerializeList(input.FileReferences));
        cmd.Parameters.AddWithValue("@testCommands", SerializeList(input.TestCommands));

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var finding = ReadReviewFinding(reader, round.RoundNumber);
        await reader.CloseAsync();

        await tx.CommitAsync();
        return finding;
    }

    public async Task<ReviewFinding?> GetByIdAsync(int id)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = CreateSelectCommand(conn);
        cmd.CommandText += " WHERE rf.id = @id";
        cmd.Parameters.AddWithValue("@id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadReviewFinding(reader) : null;
    }

    public async Task<List<ReviewFinding>> ListByTaskAsync(int taskId, ReviewFindingStatus[]? statuses = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = CreateSelectCommand(conn);
        cmd.CommandText += " WHERE rf.task_id = @taskId";
        cmd.Parameters.AddWithValue("@taskId", taskId);
        AppendStatusFilter(cmd, statuses);
        cmd.CommandText += " ORDER BY rf.finding_number ASC";

        return await ReadFindingsAsync(cmd);
    }

    public async Task<List<ReviewFinding>> ListByReviewRoundAsync(int reviewRoundId, ReviewFindingStatus[]? statuses = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = CreateSelectCommand(conn);
        cmd.CommandText += " WHERE rf.review_round_id = @reviewRoundId";
        cmd.Parameters.AddWithValue("@reviewRoundId", reviewRoundId);
        AppendStatusFilter(cmd, statuses);
        cmd.CommandText += " ORDER BY rf.finding_number ASC";

        return await ReadFindingsAsync(cmd);
    }

    public async Task<ReviewFinding> RespondAsync(int id, RespondToReviewFindingInput input)
    {
        ValidateFollowUp(input.Status, input.FollowUpTaskId);

        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE review_findings
            SET response_by = @responseBy,
                response_notes = @responseNotes,
                response_at = datetime('now'),
                status = COALESCE(@status, status),
                status_updated_by = CASE
                    WHEN @status IS NULL THEN status_updated_by
                    ELSE @responseBy
                END,
                status_notes = CASE
                    WHEN @status IS NULL THEN status_notes
                    ELSE @statusNotes
                END,
                status_updated_at = CASE
                    WHEN @status IS NULL THEN status_updated_at
                    ELSE datetime('now')
                END,
                follow_up_task_id = CASE
                    WHEN @status IS NULL THEN follow_up_task_id
                    WHEN @status = @splitStatus THEN @followUpTaskId
                    ELSE NULL
                END,
                updated_at = datetime('now')
            WHERE id = @id
            RETURNING id, finding_key, task_id, review_round_id, finding_number, created_by,
                      category, summary, notes, file_references, test_commands, status,
                      status_updated_by, status_notes, status_updated_at, response_by,
                      response_notes, response_at, follow_up_task_id, created_at, updated_at,
                      (SELECT round_number FROM review_rounds WHERE id = review_round_id) AS round_number
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@responseBy", input.RespondedBy);
        cmd.Parameters.AddWithValue("@responseNotes", (object?)input.ResponseNotes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", input.Status is not null ? input.Status.Value.ToDbValue() : DBNull.Value);
        cmd.Parameters.AddWithValue("@statusNotes", (object?)input.StatusNotes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@followUpTaskId", (object?)input.FollowUpTaskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@splitStatus", ReviewFindingStatus.SplitToFollowUp.ToDbValue());

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new KeyNotFoundException($"Review finding {id} not found");
        return ReadReviewFinding(reader);
    }

    public async Task<ReviewFinding> SetStatusAsync(int id, UpdateReviewFindingStatusInput input)
    {
        ValidateFollowUp(input.Status, input.FollowUpTaskId);

        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE review_findings
            SET status = @status,
                status_updated_by = @updatedBy,
                status_notes = @statusNotes,
                status_updated_at = datetime('now'),
                follow_up_task_id = CASE
                    WHEN @status = @splitStatus THEN @followUpTaskId
                    ELSE NULL
                END,
                updated_at = datetime('now')
            WHERE id = @id
            RETURNING id, finding_key, task_id, review_round_id, finding_number, created_by,
                      category, summary, notes, file_references, test_commands, status,
                      status_updated_by, status_notes, status_updated_at, response_by,
                      response_notes, response_at, follow_up_task_id, created_at, updated_at,
                      (SELECT round_number FROM review_rounds WHERE id = review_round_id) AS round_number
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@status", input.Status.ToDbValue());
        cmd.Parameters.AddWithValue("@updatedBy", input.UpdatedBy);
        cmd.Parameters.AddWithValue("@statusNotes", (object?)input.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@followUpTaskId", (object?)input.FollowUpTaskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@splitStatus", ReviewFindingStatus.SplitToFollowUp.ToDbValue());

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new KeyNotFoundException($"Review finding {id} not found");
        return ReadReviewFinding(reader);
    }

    private static void ValidateFollowUp(ReviewFindingStatus? status, int? followUpTaskId)
    {
        if (status == ReviewFindingStatus.SplitToFollowUp && followUpTaskId is null)
            throw new InvalidOperationException("split_to_follow_up requires follow_up_task_id.");

        if (status != ReviewFindingStatus.SplitToFollowUp && followUpTaskId is not null)
            throw new InvalidOperationException("follow_up_task_id can only be supplied when status is split_to_follow_up.");
    }

    private static async Task<int> GetNextFindingNumberAsync(SqliteConnection conn, int taskId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(MAX(finding_number), 0)
            FROM review_findings
            WHERE task_id = @taskId
            """;
        cmd.Parameters.AddWithValue("@taskId", taskId);
        var max = (long)(await cmd.ExecuteScalarAsync())!;
        return (int)max + 1;
    }

    private static async Task<(int TaskId, int RoundNumber)?> GetRoundContextAsync(SqliteConnection conn, int reviewRoundId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT task_id, round_number
            FROM review_rounds
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", reviewRoundId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private static SqliteCommand CreateSelectCommand(SqliteConnection conn)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT rf.id, rf.finding_key, rf.task_id, rf.review_round_id, rf.finding_number, rf.created_by,
                   rf.category, rf.summary, rf.notes, rf.file_references, rf.test_commands, rf.status,
                   rf.status_updated_by, rf.status_notes, rf.status_updated_at, rf.response_by,
                   rf.response_notes, rf.response_at, rf.follow_up_task_id, rf.created_at, rf.updated_at,
                   rr.round_number
            FROM review_findings rf
            JOIN review_rounds rr ON rr.id = rf.review_round_id
            """;
        return cmd;
    }

    private static void AppendStatusFilter(SqliteCommand cmd, ReviewFindingStatus[]? statuses)
    {
        if (statuses is not { Length: > 0 })
            return;

        var placeholders = new List<string>();
        for (var i = 0; i < statuses.Length; i++)
        {
            var parameterName = $"@status{i}";
            placeholders.Add(parameterName);
            cmd.Parameters.AddWithValue(parameterName, statuses[i].ToDbValue());
        }

        cmd.CommandText += $" AND rf.status IN ({string.Join(", ", placeholders)})";
    }

    private static async Task<List<ReviewFinding>> ReadFindingsAsync(SqliteCommand cmd)
    {
        var findings = new List<ReviewFinding>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            findings.Add(ReadReviewFinding(reader));
        return findings;
    }

    private static object SerializeList(List<string>? values) =>
        values is { Count: > 0 } ? JsonSerializer.Serialize(values) : DBNull.Value;

    internal static ReviewFinding ReadReviewFinding(SqliteDataReader reader, int? roundNumberOverride = null)
    {
        var fileReferencesJson = reader.IsDBNull(9) ? null : reader.GetString(9);
        var testCommandsJson = reader.IsDBNull(10) ? null : reader.GetString(10);

        return new ReviewFinding
        {
            Id = reader.GetInt32(0),
            FindingKey = reader.GetString(1),
            TaskId = reader.GetInt32(2),
            ReviewRoundId = reader.GetInt32(3),
            FindingNumber = reader.GetInt32(4),
            CreatedBy = reader.GetString(5),
            Category = EnumExtensions.ParseReviewFindingCategory(reader.GetString(6)),
            Summary = reader.GetString(7),
            Notes = reader.IsDBNull(8) ? null : reader.GetString(8),
            FileReferences = fileReferencesJson is not null ? JsonSerializer.Deserialize<List<string>>(fileReferencesJson) : null,
            TestCommands = testCommandsJson is not null ? JsonSerializer.Deserialize<List<string>>(testCommandsJson) : null,
            Status = EnumExtensions.ParseReviewFindingStatus(reader.GetString(11)),
            StatusUpdatedBy = reader.IsDBNull(12) ? null : reader.GetString(12),
            StatusNotes = reader.IsDBNull(13) ? null : reader.GetString(13),
            StatusUpdatedAt = reader.IsDBNull(14) ? null : DateTime.Parse(reader.GetString(14)),
            ResponseBy = reader.IsDBNull(15) ? null : reader.GetString(15),
            ResponseNotes = reader.IsDBNull(16) ? null : reader.GetString(16),
            ResponseAt = reader.IsDBNull(17) ? null : DateTime.Parse(reader.GetString(17)),
            FollowUpTaskId = reader.IsDBNull(18) ? null : reader.GetInt32(18),
            CreatedAt = DateTime.Parse(reader.GetString(19)),
            UpdatedAt = DateTime.Parse(reader.GetString(20)),
            ReviewRoundNumber = roundNumberOverride ?? reader.GetInt32(21)
        };
    }
}
