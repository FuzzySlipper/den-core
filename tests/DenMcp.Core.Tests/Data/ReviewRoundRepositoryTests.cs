using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Core.Tests.Data;

public class ReviewRoundRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private ReviewRoundRepository _repo = null!;
    private TaskRepository _tasks = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _repo = new ReviewRoundRepository(_testDb.Db);
        _tasks = new TaskRepository(_testDb.Db);
        var projRepo = new ProjectRepository(_testDb.Db);
        await projRepo.CreateAsync(new Project { Id = "proj", Name = "Test" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    [Fact]
    public async Task CreateAsync_AssignsRoundNumbersAndDefaultsLastReviewedHeadCommit()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Review me" });

        var first = await _repo.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = task.Id,
            RequestedBy = "claude-code",
            Branch = "task/594-review-rounds-sha-metadata",
            BaseBranch = "main",
            BaseCommit = "abc123",
            HeadCommit = "def456",
            TestsRun = ["dotnet test den-mcp.slnx --filter ReviewRoundRepositoryTests"],
            Notes = "Initial review request"
        });

        var second = await _repo.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = task.Id,
            RequestedBy = "claude-code",
            Branch = "task/594-review-rounds-sha-metadata",
            BaseBranch = "main",
            BaseCommit = "abc123",
            HeadCommit = "fedcba",
            CommitsSinceLastReview = 2,
            Notes = "Ready for rereview"
        });

        Assert.Equal(1, first.RoundNumber);
        Assert.Null(first.LastReviewedHeadCommit);
        Assert.Equal(2, second.RoundNumber);
        Assert.Equal("def456", second.LastReviewedHeadCommit);
        Assert.Equal(2, second.CommitsSinceLastReview);
        Assert.Single(first.TestsRun!);
        Assert.Equal("main", first.PreferredDiff.BaseRef);
        Assert.Equal("task/594-review-rounds-sha-metadata", first.PreferredDiff.HeadRef);
        Assert.Equal("def456", second.DeltaDiff!.BaseCommit);
        Assert.False(second.IsStackedBranchReview);
    }

    [Fact]
    public async Task CreateAsync_SameHeadAsLastReview_Throws()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "No-op rereview" });

        await _repo.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = task.Id,
            RequestedBy = "claude-code",
            Branch = "task/123",
            BaseBranch = "main",
            BaseCommit = "aaa111",
            HeadCommit = "bbb222"
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => _repo.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = task.Id,
            RequestedBy = "claude-code",
            Branch = "task/123",
            BaseBranch = "main",
            BaseCommit = "aaa111",
            HeadCommit = "bbb222"
        }));
    }

    [Fact]
    public async Task SetVerdictAsync_UpdatesVerdictMetadata()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Verdict me" });
        var round = await _repo.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = task.Id,
            RequestedBy = "claude-code",
            Branch = "task/123",
            BaseBranch = "main",
            BaseCommit = "aaa111",
            HeadCommit = "bbb222"
        });

        var updated = await _repo.SetVerdictAsync(round.Id, ReviewVerdict.ChangesRequested, "codex", "Need another pass");

        Assert.Equal(ReviewVerdict.ChangesRequested, updated.Verdict);
        Assert.Equal("codex", updated.VerdictBy);
        Assert.Equal("Need another pass", updated.VerdictNotes);
        Assert.NotNull(updated.VerdictAt);
    }

    [Fact]
    public async Task GetDetailAsync_IncludesReviewRounds()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Timeline" });
        await _repo.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = task.Id,
            RequestedBy = "claude-code",
            Branch = "task/594-review-rounds-sha-metadata",
            BaseBranch = "main",
            BaseCommit = "abc123",
            HeadCommit = "def456"
        });

        var detail = await _tasks.GetDetailAsync(task.Id);

        Assert.Single(detail.ReviewRounds);
        Assert.Equal("def456", detail.ReviewRounds[0].HeadCommit);
    }

    [Fact]
    public async Task CreateAsync_PersistsExplicitDiffMetadataAndBranchComposition()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Stacked review" });

        var round = await _repo.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = task.Id,
            RequestedBy = "claude-code",
            Branch = "task/544",
            BaseBranch = "main",
            BaseCommit = "aaa111",
            HeadCommit = "ddd444",
            PreferredDiffBaseRef = "task/543",
            PreferredDiffBaseCommit = "bbb222",
            PreferredDiffHeadRef = "task/544",
            PreferredDiffHeadCommit = "ddd444",
            AlternateDiffBaseRef = "main",
            AlternateDiffBaseCommit = "aaa111",
            AlternateDiffHeadRef = "task/544",
            AlternateDiffHeadCommit = "ddd444",
            DeltaBaseCommit = "ccc333",
            InheritedCommitCount = 3,
            TaskLocalCommitCount = 2
        });

        Assert.Equal("task/543", round.PreferredDiff.BaseRef);
        Assert.Equal("bbb222", round.PreferredDiff.BaseCommit);
        Assert.NotNull(round.AlternateDiff);
        Assert.Equal("main", round.AlternateDiff!.BaseRef);
        Assert.Equal("ccc333", round.DeltaDiff!.BaseCommit);
        Assert.True(round.IsStackedBranchReview);
        Assert.Equal(3, round.BranchComposition.InheritedCommitCount);
        Assert.Equal(2, round.BranchComposition.TaskLocalCommitCount);
        Assert.True(round.BranchComposition.HasInheritedChanges);
        Assert.True(round.BranchComposition.HasTaskLocalChanges);
    }

    [Fact]
    public async Task CreateAsync_AlternateDiffWithoutBaseRef_Throws()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Invalid alternate diff" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => _repo.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = task.Id,
            RequestedBy = "claude-code",
            Branch = "task/596",
            BaseBranch = "main",
            BaseCommit = "abc123",
            HeadCommit = "def456",
            AlternateDiffHeadRef = "task/596"
        }));
    }

    [Fact]
    public async Task CreateAsync_MissingRequiredString_ThrowsActionableFieldError()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Missing commit" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _repo.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = task.Id,
            RequestedBy = "claude-code",
            Branch = "task/596",
            BaseBranch = "main",
            BaseCommit = null!,
            HeadCommit = "def456"
        }));

        Assert.Contains("base_commit is required", ex.Message);
        Assert.DoesNotContain("Value must be set", ex.Message);
    }

    [Fact]
    public async Task CreateAsync_NegativeCommitCounts_Throws()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Invalid counts" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => _repo.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = task.Id,
            RequestedBy = "claude-code",
            Branch = "task/596",
            BaseBranch = "main",
            BaseCommit = "abc123",
            HeadCommit = "def456",
            InheritedCommitCount = -1
        }));
    }
}
