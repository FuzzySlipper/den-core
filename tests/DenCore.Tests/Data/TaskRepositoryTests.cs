using DenCore.Data;
using DenCore.Models;
using TaskStatus = DenCore.Models.TaskStatus;

namespace DenCore.Tests.Data;

public class TaskRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private TaskRepository _repo = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _repo = new TaskRepository(_testDb.Db);
        var projRepo = new ProjectRepository(_testDb.Db);
        await projRepo.CreateAsync(new Project { Id = "proj", Name = "Test" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    [Fact]
    public async Task CreateTask_ReturnsWithId()
    {
        var task = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Build it" });
        Assert.True(task.Id > 0);
        Assert.Equal("Build it", task.Title);
        Assert.Equal(TaskStatus.Planned, task.Status);
    }

    [Fact]
    public async Task CreateSubtask_SetsParentId()
    {
        var parent = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Parent" });
        var child = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Child", ParentId = parent.Id });
        Assert.Equal(parent.Id, child.ParentId);
    }

    [Fact]
    public async Task ListTasks_FiltersTopLevel()
    {
        var parent = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Parent" });
        await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Child", ParentId = parent.Id });

        var topLevel = await _repo.ListAsync("proj");
        Assert.Single(topLevel);
        Assert.Equal("Parent", topLevel[0].Title);
        Assert.Equal(1, topLevel[0].SubtaskCount);
    }

    [Fact]
    public async Task ListTasks_FiltersByStatus()
    {
        await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "A" });
        var b = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "B" });
        await _repo.UpdateAsync(b.Id, new Dictionary<string, object?> { ["status"] = TaskStatus.Done }, "test");

        var planned = await _repo.ListAsync("proj", statuses: [TaskStatus.Planned]);
        Assert.Single(planned);
        Assert.Equal("A", planned[0].Title);
    }

    [Fact]
    public async Task UpdateTask_RecordsHistory()
    {
        var task = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Original" });
        var updated = await _repo.UpdateAsync(task.Id, new Dictionary<string, object?> { ["title"] = "Updated" }, "agent1");

        Assert.Equal("Updated", updated.Title);

        // Verify history was written
        var detail = await _repo.GetDetailAsync(task.Id);
        Assert.Equal("Updated", detail.Task.Title);
    }

    [Fact]
    public async Task AddDependency_Works()
    {
        var a = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "A" });
        var b = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "B" });
        await _repo.AddDependencyAsync(b.Id, a.Id);

        var detail = await _repo.GetDetailAsync(b.Id);
        Assert.Single(detail.Dependencies);
        Assert.Equal(a.Id, detail.Dependencies[0].TaskId);
    }

    [Fact]
    public async Task AddDependency_RejectsCycle()
    {
        var a = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "A" });
        var b = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "B" });
        await _repo.AddDependencyAsync(b.Id, a.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _repo.AddDependencyAsync(a.Id, b.Id));
    }

    [Fact]
    public async Task AddDependency_RejectsTransitiveCycle()
    {
        var a = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "A" });
        var b = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "B" });
        var c = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "C" });
        await _repo.AddDependencyAsync(b.Id, a.Id); // B depends on A
        await _repo.AddDependencyAsync(c.Id, b.Id); // C depends on B

        await Assert.ThrowsAsync<InvalidOperationException>(() => _repo.AddDependencyAsync(a.Id, c.Id)); // A -> C would cycle
    }

    [Fact]
    public async Task NextTask_ReturnsHighestPriorityUnblocked()
    {
        var low = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Low", Priority = 5 });
        var high = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "High", Priority = 1 });

        var next = await _repo.GetNextTaskAsync("proj");
        Assert.NotNull(next);
        Assert.Equal("High", next.Title);
    }

    [Fact]
    public async Task NextTask_SkipsBlockedTasks()
    {
        var a = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "A", Priority = 1 });
        var b = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "B", Priority = 2 });
        await _repo.AddDependencyAsync(a.Id, b.Id); // A depends on B (A is blocked)

        var next = await _repo.GetNextTaskAsync("proj");
        Assert.NotNull(next);
        Assert.Equal("B", next.Title); // B is unblocked, even though A has higher priority
    }

    [Fact]
    public async Task NextTask_PrefersSubtasksOfInProgressParents()
    {
        var parent = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Parent", Priority = 3 });
        var child = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Child", ParentId = parent.Id });
        var standalone = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Standalone", Priority = 1 });

        await _repo.UpdateAsync(parent.Id, new Dictionary<string, object?> { ["status"] = TaskStatus.InProgress }, "test");

        var next = await _repo.GetNextTaskAsync("proj");
        Assert.NotNull(next);
        Assert.Equal("Child", next.Title); // Child of in-progress parent beats standalone P1
    }

    [Fact]
    public async Task NextTask_ReturnsNull_WhenAllDone()
    {
        var a = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "A" });
        await _repo.UpdateAsync(a.Id, new Dictionary<string, object?> { ["status"] = TaskStatus.Done }, "test");

        var next = await _repo.GetNextTaskAsync("proj");
        Assert.Null(next);
    }

    [Fact]
    public async Task ListTasks_TagFilter_DoesNotMatchSubstrings()
    {
        await _repo.CreateAsync(new ProjectTask
        {
            ProjectId = "proj", Title = "CLI task",
            Tags = ["cli"]
        });
        await _repo.CreateAsync(new ProjectTask
        {
            ProjectId = "proj", Title = "Client task",
            Tags = ["client"]
        });

        var cliTasks = await _repo.ListAsync("proj", tags: ["cli"]);
        Assert.Single(cliTasks);
        Assert.Equal("CLI task", cliTasks[0].Title);

        var clientTasks = await _repo.ListAsync("proj", tags: ["client"]);
        Assert.Single(clientTasks);
        Assert.Equal("Client task", clientTasks[0].Title);
    }

    [Fact]
    public async Task ListTasks_DoesNotReturnTasksFromOtherProject()
    {
        var projRepo = new ProjectRepository(_testDb.Db);
        await projRepo.CreateAsync(new Project { Id = "other", Name = "Other" });

        await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Mine" });
        await _repo.CreateAsync(new ProjectTask { ProjectId = "other", Title = "Theirs" });

        var mine = await _repo.ListAsync("proj");
        Assert.Single(mine);
        Assert.Equal("Mine", mine[0].Title);
    }

    [Fact]
    public async Task GetDetailAsync_BuildsReviewWorkflowSummary()
    {
        var rounds = new ReviewRoundRepository(_testDb.Db);
        var findings = new ReviewFindingRepository(_testDb.Db);
        var task = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Review summary" });
        var round = await rounds.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = task.Id,
            RequestedBy = "claude-code",
            Branch = "task/597-review-packet-ux",
            BaseBranch = "main",
            BaseCommit = "aaa111",
            HeadCommit = "bbb222"
        });

        await findings.CreateAsync(new CreateReviewFindingInput
        {
            ReviewRoundId = round.Id,
            CreatedBy = "codex",
            Category = ReviewFindingCategory.BlockingBug,
            Summary = "Still open"
        });

        var resolvedFinding = await findings.CreateAsync(new CreateReviewFindingInput
        {
            ReviewRoundId = round.Id,
            CreatedBy = "codex",
            Category = ReviewFindingCategory.TestWeakness,
            Summary = "Covered later"
        });

        await findings.SetStatusAsync(resolvedFinding.Id, new UpdateReviewFindingStatusInput
        {
            Status = ReviewFindingStatus.VerifiedFixed,
            UpdatedBy = "codex",
            Notes = "Confirmed in review"
        });

        var detail = await _repo.GetDetailAsync(task.Id);

        Assert.NotNull(detail.ReviewWorkflow.CurrentRound);
        Assert.Equal(1, detail.ReviewWorkflow.ReviewRoundCount);
        Assert.Equal(1, detail.ReviewWorkflow.UnresolvedFindingCount);
        Assert.Equal(1, detail.ReviewWorkflow.ResolvedFindingCount);
        Assert.Single(detail.ReviewWorkflow.Timeline);
        Assert.Equal(2, detail.ReviewWorkflow.Timeline[0].TotalFindingCount);
        Assert.Equal(1, detail.ReviewWorkflow.Timeline[0].OpenFindingCount);
        Assert.Equal(1, detail.ReviewWorkflow.Timeline[0].ResolvedFindingCount);
    }

    #region Dependency-waiting availability

    [Fact]
    public async Task PlannedTaskWithUnfinishedDependency_HasWaitingOnDependenciesAvailability()
    {
        var dep = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Dependency", Status = TaskStatus.InProgress });
        var task = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Waiting", Status = TaskStatus.Planned });
        await _repo.AddDependencyAsync(task.Id, dep.Id);

        var list = await _repo.ListAsync("proj");
        var waiting = list.Single(t => t.Id == task.Id);

        Assert.Equal("waiting_on_dependencies", waiting.Availability);
        Assert.Equal(1, waiting.DependencyCount);
        Assert.Equal(1, waiting.UnfinishedDependencyCount);
    }

    [Fact]
    public async Task PlannedTaskWithAllDependenciesDone_HasAvailableAvailability()
    {
        var dep = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Dependency", Status = TaskStatus.Planned });
        await _repo.UpdateAsync(dep.Id, new Dictionary<string, object?> { ["status"] = TaskStatus.Done }, "test");
        var task = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Ready", Status = TaskStatus.Planned });
        await _repo.AddDependencyAsync(task.Id, dep.Id);

        var list = await _repo.ListAsync("proj");
        var ready = list.Single(t => t.Id == task.Id);

        Assert.Equal("available", ready.Availability);
        Assert.Equal(1, ready.DependencyCount);
        Assert.Equal(0, ready.UnfinishedDependencyCount);
    }

    [Fact]
    public async Task PlannedTaskWithCancelledDependency_HasAvailableAvailability()
    {
        var dep = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Dependency", Status = TaskStatus.Planned });
        await _repo.UpdateAsync(dep.Id, new Dictionary<string, object?> { ["status"] = TaskStatus.Cancelled }, "test");
        var task = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Ready", Status = TaskStatus.Planned });
        await _repo.AddDependencyAsync(task.Id, dep.Id);

        var list = await _repo.ListAsync("proj");
        var ready = list.Single(t => t.Id == task.Id);

        Assert.Equal("available", ready.Availability);
        Assert.Equal(0, ready.UnfinishedDependencyCount);
    }

    [Fact]
    public async Task BlockedTask_StaysBlocked_WhenDependencyDone()
    {
        var dep = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Dependency", Status = TaskStatus.Planned });
        var task = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "BlockedTask", Status = TaskStatus.Planned });
        await _repo.AddDependencyAsync(task.Id, dep.Id);

        // Mark the task as manually blocked
        await _repo.UpdateAsync(task.Id, new Dictionary<string, object?> { ["status"] = TaskStatus.Blocked }, "test");

        // Now complete the dependency
        await _repo.UpdateAsync(dep.Id, new Dictionary<string, object?> { ["status"] = TaskStatus.Done }, "test");

        var list = await _repo.ListAsync("proj");
        var blocked = list.Single(t => t.Id == task.Id);

        Assert.Equal("blocked", blocked.Availability);
    }

    [Fact]
    public async Task DependencyDone_AutoAvailable_IsClaimableByNextTask()
    {
        var dep = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Dependency", Status = TaskStatus.Planned, Priority = 2 });
        var task = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Waiting", Status = TaskStatus.Planned, Priority = 1 });
        await _repo.AddDependencyAsync(task.Id, dep.Id);

        // Task should not appear in next_task while dependency is unfinished
        var nextBefore = await _repo.GetNextTaskAsync("proj");
        // dep is top-level planned, so it should be returned (higher priority task is blocked on dep)
        Assert.NotNull(nextBefore);
        Assert.Equal(dep.Id, nextBefore!.Id); // dep (P2) is available; task (P1) is waiting on dep

        // Complete the dependency
        await _repo.UpdateAsync(dep.Id, new Dictionary<string, object?> { ["status"] = TaskStatus.Done }, "test");

        // Now the task should be available via next_task (priority 1, no unfinished deps)
        var nextAfter = await _repo.GetNextTaskAsync("proj");
        Assert.NotNull(nextAfter);
        Assert.Equal(task.Id, nextAfter!.Id); // Priority 1, all deps done
    }

    [Fact]
    public async Task WorkflowSummary_ExposesAvailability()
    {
        var dep = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Dependency", Status = TaskStatus.Planned });
        var task = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Waiting", Status = TaskStatus.Planned });
        await _repo.AddDependencyAsync(task.Id, dep.Id);

        var summary = await _repo.GetWorkflowSummaryAsync(task.Id);

        Assert.Equal("waiting_on_dependencies", summary.Availability);
        Assert.Single(summary.Dependencies);
    }

    [Fact]
    public async Task InProgressTask_HasInProgressAvailability_RegardlessOfDependencies()
    {
        var dep = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Dependency", Status = TaskStatus.Planned });
        var task = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Working", Status = TaskStatus.Planned });
        await _repo.AddDependencyAsync(task.Id, dep.Id);
        await _repo.UpdateAsync(task.Id, new Dictionary<string, object?> { ["status"] = TaskStatus.InProgress }, "test");

        var list = await _repo.ListAsync("proj");
        var working = list.Single(t => t.Id == task.Id);

        Assert.Equal("in_progress", working.Availability);
    }

    [Fact]
    public async Task PlannedTaskWithoutDependencies_HasAvailableAvailability()
    {
        var task = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Simple", Status = TaskStatus.Planned });

        var list = await _repo.ListAsync("proj");
        var simple = list.Single(t => t.Id == task.Id);

        Assert.Equal("available", simple.Availability);
        Assert.Equal(0, simple.DependencyCount);
        Assert.Equal(0, simple.UnfinishedDependencyCount);
    }

    [Fact]
    public async Task ReviewTask_HasReviewAvailability()
    {
        var task = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Under review", Status = TaskStatus.Planned });
        await _repo.UpdateAsync(task.Id, new Dictionary<string, object?> { ["status"] = TaskStatus.Review }, "test");

        var list = await _repo.ListAsync("proj");
        var review = list.Single(t => t.Id == task.Id);

        Assert.Equal("review", review.Availability);
    }

    [Fact]
    public async Task DoneTask_HasDoneAvailability()
    {
        var task = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Finished", Status = TaskStatus.Planned });
        await _repo.UpdateAsync(task.Id, new Dictionary<string, object?> { ["status"] = TaskStatus.Done }, "test");

        var list = await _repo.ListAsync("proj");
        var done = list.Single(t => t.Id == task.Id);

        Assert.Equal("done", done.Availability);
    }

    [Fact]
    public async Task CancelledTask_HasCancelledAvailability()
    {
        var task = await _repo.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Cancelled", Status = TaskStatus.Planned });
        await _repo.UpdateAsync(task.Id, new Dictionary<string, object?> { ["status"] = TaskStatus.Cancelled }, "test");

        var list = await _repo.ListAsync("proj");
        var cancelled = list.Single(t => t.Id == task.Id);

        Assert.Equal("cancelled", cancelled.Availability);
    }

    #endregion
}
