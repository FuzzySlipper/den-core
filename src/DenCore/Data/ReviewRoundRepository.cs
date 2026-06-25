using System.Text.Json;
using System.Data.Common;
using DenCore.Models;

namespace DenCore.Data;

public interface IReviewRoundRepository
{
    Task<ReviewRound> CreateAsync(CreateReviewRoundInput input);
    Task<ReviewRound?> GetByIdAsync(int id);
    Task<List<ReviewRound>> ListByTaskAsync(int taskId);
    Task<ReviewRound?> GetLatestByTaskAsync(int taskId);
    Task<ReviewRound> SetVerdictAsync(int id, ReviewVerdict verdict, string decidedBy, string? notes = null);
}

public sealed class ReviewRoundRepository : IReviewRoundRepository
{
    private readonly DbConnectionFactory _db;

    public ReviewRoundRepository(DbConnectionFactory db) => _db = db;

    public async Task<ReviewRound> CreateAsync(CreateReviewRoundInput input)
    {
        ValidateCreateInput(input);

        return await SerializableTransactionRetry.ExecuteAsync(async () =>
        {
            await using var conn = await _db.CreateConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

            var latest = await GetLatestByTaskWithConnectionAsync(conn, input.TaskId);
            var roundNumber = (latest?.RoundNumber ?? 0) + 1;
            var lastReviewedHead = input.LastReviewedHeadCommit ?? latest?.HeadCommit;
            var preferredDiffBaseRef = input.PreferredDiffBaseRef ?? input.BaseBranch;
            var preferredDiffBaseCommit = input.PreferredDiffBaseCommit ?? input.BaseCommit;
            var preferredDiffHeadRef = input.PreferredDiffHeadRef ?? input.Branch;
            var preferredDiffHeadCommit = input.PreferredDiffHeadCommit ?? input.HeadCommit;
            var alternateDiffHeadRef = input.AlternateDiffBaseRef is null && input.AlternateDiffHeadRef is null &&
                input.AlternateDiffBaseCommit is null && input.AlternateDiffHeadCommit is null
                ? null
                : input.AlternateDiffHeadRef ?? input.Branch;
            var alternateDiffHeadCommit = input.AlternateDiffBaseRef is null && input.AlternateDiffHeadRef is null &&
                input.AlternateDiffBaseCommit is null && input.AlternateDiffHeadCommit is null
                ? null
                : input.AlternateDiffHeadCommit ?? input.HeadCommit;
            var deltaBaseCommit = input.DeltaBaseCommit ?? lastReviewedHead;

            if (lastReviewedHead is not null &&
                string.Equals(lastReviewedHead, input.HeadCommit, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Task {input.TaskId} review head {input.HeadCommit} matches the last reviewed head commit.");
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
            INSERT INTO review_rounds (
                task_id, round_number, requested_by, branch, base_branch, base_commit, head_commit,
                last_reviewed_head_commit, commits_since_last_review, tests_run, notes,
                preferred_diff_base_ref, preferred_diff_base_commit, preferred_diff_head_ref, preferred_diff_head_commit,
                alternate_diff_base_ref, alternate_diff_base_commit, alternate_diff_head_ref, alternate_diff_head_commit,
                delta_base_commit, inherited_commit_count, task_local_commit_count
            )
            VALUES (
                @taskId, @roundNumber, @requestedBy, @branch, @baseBranch, @baseCommit, @headCommit,
                @lastReviewedHeadCommit, @commitsSinceLastReview, @testsRun, @notes,
                @preferredDiffBaseRef, @preferredDiffBaseCommit, @preferredDiffHeadRef, @preferredDiffHeadCommit,
                @alternateDiffBaseRef, @alternateDiffBaseCommit, @alternateDiffHeadRef, @alternateDiffHeadCommit,
                @deltaBaseCommit, @inheritedCommitCount, @taskLocalCommitCount
            )
            RETURNING id, task_id, round_number, requested_by, branch, base_branch, base_commit,
                      head_commit, last_reviewed_head_commit, commits_since_last_review, tests_run,
                      notes, preferred_diff_base_ref, preferred_diff_base_commit, preferred_diff_head_ref,
                      preferred_diff_head_commit, alternate_diff_base_ref, alternate_diff_base_commit,
                      alternate_diff_head_ref, alternate_diff_head_commit, delta_base_commit,
                      inherited_commit_count, task_local_commit_count, verdict, verdict_by, verdict_notes,
                      requested_at, verdict_at
            """;
            cmd.AddParameterWithValue("@taskId", input.TaskId);
            cmd.AddParameterWithValue("@roundNumber", roundNumber);
            cmd.AddParameterWithValue("@requestedBy", input.RequestedBy);
            cmd.AddParameterWithValue("@branch", input.Branch);
            cmd.AddParameterWithValue("@baseBranch", input.BaseBranch);
            cmd.AddParameterWithValue("@baseCommit", input.BaseCommit);
            cmd.AddParameterWithValue("@headCommit", input.HeadCommit);
            cmd.AddParameterWithValue("@lastReviewedHeadCommit", (object?)lastReviewedHead ?? DBNull.Value);
            cmd.AddParameterWithValue("@commitsSinceLastReview", (object?)input.CommitsSinceLastReview ?? DBNull.Value);
            cmd.AddParameterWithValue("@testsRun", input.TestsRun is { Count: > 0 } ? JsonSerializer.Serialize(input.TestsRun) : DBNull.Value);
            cmd.AddParameterWithValue("@notes", (object?)input.Notes ?? DBNull.Value);
            cmd.AddParameterWithValue("@preferredDiffBaseRef", preferredDiffBaseRef);
            cmd.AddParameterWithValue("@preferredDiffBaseCommit", preferredDiffBaseCommit);
            cmd.AddParameterWithValue("@preferredDiffHeadRef", preferredDiffHeadRef);
            cmd.AddParameterWithValue("@preferredDiffHeadCommit", preferredDiffHeadCommit);
            cmd.AddParameterWithValue("@alternateDiffBaseRef", (object?)input.AlternateDiffBaseRef ?? DBNull.Value);
            cmd.AddParameterWithValue("@alternateDiffBaseCommit", (object?)input.AlternateDiffBaseCommit ?? DBNull.Value);
            cmd.AddParameterWithValue("@alternateDiffHeadRef", (object?)alternateDiffHeadRef ?? DBNull.Value);
            cmd.AddParameterWithValue("@alternateDiffHeadCommit", (object?)alternateDiffHeadCommit ?? DBNull.Value);
            cmd.AddParameterWithValue("@deltaBaseCommit", (object?)deltaBaseCommit ?? DBNull.Value);
            cmd.AddParameterWithValue("@inheritedCommitCount", (object?)input.InheritedCommitCount ?? DBNull.Value);
            cmd.AddParameterWithValue("@taskLocalCommitCount", (object?)input.TaskLocalCommitCount ?? DBNull.Value);

            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.ReadAsync();
            var created = ReadReviewRound(reader);
            await reader.CloseAsync();

            await tx.CommitAsync();
            return created;
        });
    }

    public async Task<ReviewRound?> GetByIdAsync(int id)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, task_id, round_number, requested_by, branch, base_branch, base_commit,
                   head_commit, last_reviewed_head_commit, commits_since_last_review, tests_run,
                   notes, preferred_diff_base_ref, preferred_diff_base_commit, preferred_diff_head_ref,
                   preferred_diff_head_commit, alternate_diff_base_ref, alternate_diff_base_commit,
                   alternate_diff_head_ref, alternate_diff_head_commit, delta_base_commit,
                   inherited_commit_count, task_local_commit_count, verdict, verdict_by, verdict_notes,
                   requested_at, verdict_at
            FROM review_rounds WHERE id = @id
            """;
        cmd.AddParameterWithValue("@id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadReviewRound(reader) : null;
    }

    public async Task<List<ReviewRound>> ListByTaskAsync(int taskId)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, task_id, round_number, requested_by, branch, base_branch, base_commit,
                   head_commit, last_reviewed_head_commit, commits_since_last_review, tests_run,
                   notes, preferred_diff_base_ref, preferred_diff_base_commit, preferred_diff_head_ref,
                   preferred_diff_head_commit, alternate_diff_base_ref, alternate_diff_base_commit,
                   alternate_diff_head_ref, alternate_diff_head_commit, delta_base_commit,
                   inherited_commit_count, task_local_commit_count, verdict, verdict_by, verdict_notes,
                   requested_at, verdict_at
            FROM review_rounds
            WHERE task_id = @taskId
            ORDER BY round_number ASC
            """;
        cmd.AddParameterWithValue("@taskId", taskId);

        var rounds = new List<ReviewRound>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rounds.Add(ReadReviewRound(reader));
        return rounds;
    }

    public async Task<ReviewRound?> GetLatestByTaskAsync(int taskId)
    {
        await using var conn = await _db.CreateConnectionAsync();
        return await GetLatestByTaskWithConnectionAsync(conn, taskId);
    }

    public async Task<ReviewRound> SetVerdictAsync(int id, ReviewVerdict verdict, string decidedBy, string? notes = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE review_rounds
            SET verdict = @verdict,
                verdict_by = @verdictBy,
                verdict_notes = @verdictNotes,
                verdict_at = datetime('now')
            WHERE id = @id
            RETURNING id, task_id, round_number, requested_by, branch, base_branch, base_commit,
                      head_commit, last_reviewed_head_commit, commits_since_last_review, tests_run,
                      notes, preferred_diff_base_ref, preferred_diff_base_commit, preferred_diff_head_ref,
                      preferred_diff_head_commit, alternate_diff_base_ref, alternate_diff_base_commit,
                      alternate_diff_head_ref, alternate_diff_head_commit, delta_base_commit,
                      inherited_commit_count, task_local_commit_count, verdict, verdict_by, verdict_notes,
                      requested_at, verdict_at
            """;
        cmd.AddParameterWithValue("@id", id);
        cmd.AddParameterWithValue("@verdict", verdict.ToDbValue());
        cmd.AddParameterWithValue("@verdictBy", decidedBy);
        cmd.AddParameterWithValue("@verdictNotes", (object?)notes ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new KeyNotFoundException($"Review round {id} not found");
        return ReadReviewRound(reader);
    }

    private static async Task<ReviewRound?> GetLatestByTaskWithConnectionAsync(DbConnection conn, int taskId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, task_id, round_number, requested_by, branch, base_branch, base_commit,
                   head_commit, last_reviewed_head_commit, commits_since_last_review, tests_run,
                   notes, preferred_diff_base_ref, preferred_diff_base_commit, preferred_diff_head_ref,
                   preferred_diff_head_commit, alternate_diff_base_ref, alternate_diff_base_commit,
                   alternate_diff_head_ref, alternate_diff_head_commit, delta_base_commit,
                   inherited_commit_count, task_local_commit_count, verdict, verdict_by, verdict_notes,
                   requested_at, verdict_at
            FROM review_rounds
            WHERE task_id = @taskId
            ORDER BY round_number DESC
            LIMIT 1
            """;
        cmd.AddParameterWithValue("@taskId", taskId);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadReviewRound(reader) : null;
    }

    internal static ReviewRound ReadReviewRound(DbDataReader reader)
    {
        var testsRunJson = reader.IsDBNull(10) ? null : reader.GetString(10);
        var verdictValue = reader.IsDBNull(23) ? null : reader.GetString(23);

        return new ReviewRound
        {
            Id = reader.GetInt32(0),
            TaskId = reader.GetInt32(1),
            RoundNumber = reader.GetInt32(2),
            RequestedBy = reader.GetString(3),
            Branch = reader.GetString(4),
            BaseBranch = reader.GetString(5),
            BaseCommit = reader.GetString(6),
            HeadCommit = reader.GetString(7),
            LastReviewedHeadCommit = reader.IsDBNull(8) ? null : reader.GetString(8),
            CommitsSinceLastReview = reader.IsDBNull(9) ? null : reader.GetInt32(9),
            TestsRun = testsRunJson is not null ? JsonSerializer.Deserialize<List<string>>(testsRunJson) : null,
            Notes = reader.IsDBNull(11) ? null : reader.GetString(11),
            PreferredDiffBaseRef = reader.IsDBNull(12) ? null : reader.GetString(12),
            PreferredDiffBaseCommit = reader.IsDBNull(13) ? null : reader.GetString(13),
            PreferredDiffHeadRef = reader.IsDBNull(14) ? null : reader.GetString(14),
            PreferredDiffHeadCommit = reader.IsDBNull(15) ? null : reader.GetString(15),
            AlternateDiffBaseRef = reader.IsDBNull(16) ? null : reader.GetString(16),
            AlternateDiffBaseCommit = reader.IsDBNull(17) ? null : reader.GetString(17),
            AlternateDiffHeadRef = reader.IsDBNull(18) ? null : reader.GetString(18),
            AlternateDiffHeadCommit = reader.IsDBNull(19) ? null : reader.GetString(19),
            DeltaBaseCommit = reader.IsDBNull(20) ? null : reader.GetString(20),
            InheritedCommitCount = reader.IsDBNull(21) ? null : reader.GetInt32(21),
            TaskLocalCommitCount = reader.IsDBNull(22) ? null : reader.GetInt32(22),
            Verdict = verdictValue is not null ? EnumExtensions.ParseReviewVerdict(verdictValue) : null,
            VerdictBy = reader.IsDBNull(24) ? null : reader.GetString(24),
            VerdictNotes = reader.IsDBNull(25) ? null : reader.GetString(25),
            RequestedAt = DateTime.Parse(reader.GetString(26)),
            VerdictAt = reader.IsDBNull(27) ? null : DateTime.Parse(reader.GetString(27))
        };
    }

    private static void ValidateCreateInput(CreateReviewRoundInput input)
    {
        ValidateRequired(input.RequestedBy, "requested_by");
        ValidateRequired(input.Branch, "branch");
        ValidateRequired(input.BaseBranch, "base_branch");
        ValidateRequired(input.BaseCommit, "base_commit");
        ValidateRequired(input.HeadCommit, "head_commit");
        ValidateNonNegative(input.CommitsSinceLastReview, nameof(input.CommitsSinceLastReview));
        ValidateNonNegative(input.InheritedCommitCount, nameof(input.InheritedCommitCount));
        ValidateNonNegative(input.TaskLocalCommitCount, nameof(input.TaskLocalCommitCount));

        var hasAlternateDiffMetadata = input.AlternateDiffBaseRef is not null ||
            input.AlternateDiffBaseCommit is not null ||
            input.AlternateDiffHeadRef is not null ||
            input.AlternateDiffHeadCommit is not null;
        if (hasAlternateDiffMetadata && string.IsNullOrWhiteSpace(input.AlternateDiffBaseRef))
        {
            throw new InvalidOperationException(
                "alternate_diff_base_ref is required when alternate diff metadata is supplied.");
        }
    }

    private static void ValidateRequired(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{name} is required and must be a non-empty string.");
    }

    private static void ValidateNonNegative(int? value, string name)
    {
        if (value is < 0)
            throw new InvalidOperationException($"{name} cannot be negative.");
    }
}
