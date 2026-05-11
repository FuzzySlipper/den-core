using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Core.Tests.Data;

public class ReviewFindingRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private TaskRepository _tasks = null!;
    private ReviewRoundRepository _rounds = null!;
    private ReviewFindingRepository _findings = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _tasks = new TaskRepository(_testDb.Db);
        _rounds = new ReviewRoundRepository(_testDb.Db);
        _findings = new ReviewFindingRepository(_testDb.Db);

        var projects = new ProjectRepository(_testDb.Db);
        await projects.CreateAsync(new Project { Id = "proj", Name = "Test" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    [Fact]
    public async Task CreateAsync_AssignsStableTaskScopedFindingKeysAcrossRounds()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Review target" });
        var firstRound = await _rounds.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = task.Id,
            RequestedBy = "claude-code",
            Branch = "task/595",
            BaseBranch = "main",
            BaseCommit = "aaa111",
            HeadCommit = "bbb222"
        });
        var secondRound = await _rounds.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = task.Id,
            RequestedBy = "claude-code",
            Branch = "task/595",
            BaseBranch = "main",
            BaseCommit = "aaa111",
            HeadCommit = "ccc333"
        });

        var firstFinding = await _findings.CreateAsync(new CreateReviewFindingInput
        {
            ReviewRoundId = firstRound.Id,
            CreatedBy = "codex",
            Category = ReviewFindingCategory.BlockingBug,
            Summary = "Primary issue",
            FileReferences = ["src/Foo.cs:12"]
        });
        var secondFinding = await _findings.CreateAsync(new CreateReviewFindingInput
        {
            ReviewRoundId = secondRound.Id,
            CreatedBy = "codex",
            Category = ReviewFindingCategory.TestWeakness,
            Summary = "Secondary issue",
            TestCommands = ["dotnet test den-mcp.slnx --filter ReviewFindingRepositoryTests"]
        });

        Assert.Equal($"R{task.Id}-1", firstFinding.FindingKey);
        Assert.Equal($"R{task.Id}-2", secondFinding.FindingKey);
        Assert.Equal(1, firstFinding.ReviewRoundNumber);
        Assert.Equal(2, secondFinding.ReviewRoundNumber);
        Assert.Single(firstFinding.FileReferences!);
        Assert.Single(secondFinding.TestCommands!);
    }

    [Fact]
    public async Task RespondAsync_PreservesResponseNotesAndClaimedFixedStatus()
    {
        var round = await CreateRoundAsync();
        var finding = await _findings.CreateAsync(new CreateReviewFindingInput
        {
            ReviewRoundId = round.Id,
            CreatedBy = "codex",
            Category = ReviewFindingCategory.AcceptanceGap,
            Summary = "Need clearer lifecycle tracking"
        });

        var updated = await _findings.RespondAsync(finding.Id, new RespondToReviewFindingInput
        {
            RespondedBy = "claude-code",
            ResponseNotes = "Fixed in follow-up commit",
            Status = ReviewFindingStatus.ClaimedFixed,
            StatusNotes = "Ready for rereview"
        });

        Assert.Equal(ReviewFindingStatus.ClaimedFixed, updated.Status);
        Assert.Equal("claude-code", updated.ResponseBy);
        Assert.Equal("Fixed in follow-up commit", updated.ResponseNotes);
        Assert.Equal("claude-code", updated.StatusUpdatedBy);
        Assert.Equal("Ready for rereview", updated.StatusNotes);
        Assert.NotNull(updated.ResponseAt);
        Assert.NotNull(updated.StatusUpdatedAt);
    }

    [Fact]
    public async Task SetStatusAsync_SplitToFollowUp_RequiresFollowUpTask()
    {
        var round = await CreateRoundAsync();
        var finding = await _findings.CreateAsync(new CreateReviewFindingInput
        {
            ReviewRoundId = round.Id,
            CreatedBy = "codex",
            Category = ReviewFindingCategory.FollowUpCandidate,
            Summary = "Better handled separately"
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => _findings.SetStatusAsync(finding.Id,
            new UpdateReviewFindingStatusInput
            {
                Status = ReviewFindingStatus.SplitToFollowUp,
                UpdatedBy = "codex"
            }));
    }

    [Fact]
    public async Task RespondAsync_SplitToFollowUp_RequiresFollowUpTask()
    {
        var round = await CreateRoundAsync();
        var finding = await _findings.CreateAsync(new CreateReviewFindingInput
        {
            ReviewRoundId = round.Id,
            CreatedBy = "codex",
            Category = ReviewFindingCategory.FollowUpCandidate,
            Summary = "Better handled separately"
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => _findings.RespondAsync(finding.Id,
            new RespondToReviewFindingInput
            {
                RespondedBy = "claude-code",
                ResponseNotes = "Split out separately",
                Status = ReviewFindingStatus.SplitToFollowUp
            }));
    }

    [Fact]
    public async Task SetStatusAsync_RejectsFollowUpTaskForNonSplitStatus()
    {
        var round = await CreateRoundAsync();
        var finding = await _findings.CreateAsync(new CreateReviewFindingInput
        {
            ReviewRoundId = round.Id,
            CreatedBy = "codex",
            Category = ReviewFindingCategory.BlockingBug,
            Summary = "Needs verification"
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => _findings.SetStatusAsync(finding.Id,
            new UpdateReviewFindingStatusInput
            {
                Status = ReviewFindingStatus.VerifiedFixed,
                UpdatedBy = "codex",
                FollowUpTaskId = 123
            }));
    }

    [Fact]
    public async Task NonSplitStatusTransitions_ClearStaleFollowUpTask()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Split then verify" });
        var followUp = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Follow-up task" });
        var round = await _rounds.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = task.Id,
            RequestedBy = "claude-code",
            Branch = "task/595",
            BaseBranch = "main",
            BaseCommit = "aaa111",
            HeadCommit = "bbb222"
        });
        var finding = await _findings.CreateAsync(new CreateReviewFindingInput
        {
            ReviewRoundId = round.Id,
            CreatedBy = "codex",
            Category = ReviewFindingCategory.FollowUpCandidate,
            Summary = "Originally split"
        });

        var split = await _findings.SetStatusAsync(finding.Id, new UpdateReviewFindingStatusInput
        {
            Status = ReviewFindingStatus.SplitToFollowUp,
            UpdatedBy = "codex",
            Notes = "Tracked separately",
            FollowUpTaskId = followUp.Id
        });
        Assert.Equal(followUp.Id, split.FollowUpTaskId);

        var verified = await _findings.SetStatusAsync(finding.Id, new UpdateReviewFindingStatusInput
        {
            Status = ReviewFindingStatus.VerifiedFixed,
            UpdatedBy = "codex"
        });

        Assert.Equal(ReviewFindingStatus.VerifiedFixed, verified.Status);
        Assert.Null(verified.FollowUpTaskId);
    }

    [Fact]
    public async Task RespondAsync_NonSplitStatusTransition_ClearsStaleFollowUpTaskAndPreservesResponseNotes()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Split then respond" });
        var followUp = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Follow-up task" });
        var round = await _rounds.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = task.Id,
            RequestedBy = "claude-code",
            Branch = "task/595",
            BaseBranch = "main",
            BaseCommit = "aaa111",
            HeadCommit = "bbb222"
        });
        var finding = await _findings.CreateAsync(new CreateReviewFindingInput
        {
            ReviewRoundId = round.Id,
            CreatedBy = "codex",
            Category = ReviewFindingCategory.FollowUpCandidate,
            Summary = "Originally split"
        });

        await _findings.SetStatusAsync(finding.Id, new UpdateReviewFindingStatusInput
        {
            Status = ReviewFindingStatus.SplitToFollowUp,
            UpdatedBy = "codex",
            Notes = "Tracked separately",
            FollowUpTaskId = followUp.Id
        });

        var updated = await _findings.RespondAsync(finding.Id, new RespondToReviewFindingInput
        {
            RespondedBy = "claude-code",
            ResponseNotes = "Fixed directly instead",
            Status = ReviewFindingStatus.ClaimedFixed
        });

        Assert.Equal(ReviewFindingStatus.ClaimedFixed, updated.Status);
        Assert.Equal("Fixed directly instead", updated.ResponseNotes);
        Assert.Null(updated.FollowUpTaskId);
    }

    [Fact]
    public async Task GetDetailAsync_GroupsOpenAndResolvedFindingsSeparately()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Grouping target" });
        var followUp = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Follow-up task" });
        var round = await _rounds.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = task.Id,
            RequestedBy = "claude-code",
            Branch = "task/595",
            BaseBranch = "main",
            BaseCommit = "aaa111",
            HeadCommit = "bbb222"
        });

        await _findings.CreateAsync(new CreateReviewFindingInput
        {
            ReviewRoundId = round.Id,
            CreatedBy = "codex",
            Category = ReviewFindingCategory.BlockingBug,
            Summary = "Still open"
        });

        var resolved = await _findings.CreateAsync(new CreateReviewFindingInput
        {
            ReviewRoundId = round.Id,
            CreatedBy = "codex",
            Category = ReviewFindingCategory.FollowUpCandidate,
            Summary = "Moved elsewhere"
        });

        await _findings.SetStatusAsync(resolved.Id, new UpdateReviewFindingStatusInput
        {
            Status = ReviewFindingStatus.SplitToFollowUp,
            UpdatedBy = "codex",
            Notes = "Captured as follow-up",
            FollowUpTaskId = followUp.Id
        });

        var detail = await _tasks.GetDetailAsync(task.Id);

        Assert.Single(detail.OpenReviewFindings);
        Assert.Single(detail.ResolvedReviewFindings);
        Assert.Equal("Still open", detail.OpenReviewFindings[0].Summary);
        Assert.Equal("Moved elsewhere", detail.ResolvedReviewFindings[0].Summary);
        Assert.Equal(followUp.Id, detail.ResolvedReviewFindings[0].FollowUpTaskId);
    }

    private async Task<ReviewRound> CreateRoundAsync()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Review me" });
        return await _rounds.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = task.Id,
            RequestedBy = "claude-code",
            Branch = "task/595",
            BaseBranch = "main",
            BaseCommit = "aaa111",
            HeadCommit = Guid.NewGuid().ToString("N")[..7]
        });
    }
}
