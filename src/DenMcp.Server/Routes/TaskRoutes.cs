using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using Microsoft.Extensions.Logging;
using TaskStatus = DenMcp.Core.Models.TaskStatus;

namespace DenMcp.Server.Routes;

public static class TaskRoutes
{
    public static void MapTaskRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/tasks");

        group.MapPost("/", async (ITaskRepository repo, string projectId, CreateTaskRequest req) =>
        {
            var task = await repo.CreateAsync(new ProjectTask
            {
                ProjectId = projectId,
                Title = req.Title,
                Description = req.Description,
                Priority = req.Priority ?? 3,
                Tags = req.Tags,
                AssignedTo = req.AssignedTo,
                ParentId = req.ParentId
            }, req.DependsOn);
            return Results.Created($"/api/projects/{projectId}/tasks/{task.Id}", task);
        });

        group.MapGet("/", async (ITaskRepository repo, string projectId,
            string? status, string? assignedTo, string? tags, int? priority, int? parentId, bool? tree) =>
        {
            var statuses = status?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(EnumExtensions.ParseTaskStatus).ToArray();
            var tagList = tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var tasks = await repo.ListAsync(projectId, statuses, assignedTo, tagList, priority, parentId,
                includeAll: tree == true && parentId is null);
            return Results.Ok(tasks);
        });

        group.MapGet("/{taskId:int}", async (ITaskRepository repo, string projectId, int taskId) =>
        {
            try
            {
                var detail = await repo.GetDetailAsync(taskId);
                if (detail.Task.ProjectId != projectId)
                    return Results.NotFound(new { error = $"Task {taskId} not found" });
                return Results.Ok(detail);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Task {taskId} not found" });
            }
        });

        group.MapPut("/{taskId:int}", async (ITaskRepository repo, IDispatchDetectionService detection,
            INotificationChannel notifications, ILoggerFactory loggers, string projectId, int taskId, UpdateTaskRequest req) =>
        {
            var task = await repo.GetByIdAsync(taskId);
            if (task is null || task.ProjectId != projectId)
                return Results.NotFound(new { error = $"Task {taskId} not found" });

            var oldStatus = task.Status.ToDbValue();

            var changes = new Dictionary<string, object?>();
            if (req.Title is not null) changes["title"] = req.Title;
            if (req.Description is not null) changes["description"] = req.Description;
            if (req.Status is not null) changes["status"] = EnumExtensions.ParseTaskStatus(req.Status);
            if (req.Priority is not null) changes["priority"] = req.Priority.Value;
            if (req.AssignedTo is not null) changes["assigned_to"] = req.AssignedTo;
            if (req.Tags is not null) changes["tags"] = req.Tags;
            if (req.ParentId is not null) changes["parent_id"] = req.ParentId.Value;

            try
            {
                var updated = await repo.UpdateAsync(taskId, changes, req.Agent);

                if (req.Status is not null && req.Status != oldStatus)
                {
                    try
                    {
                        await detection.OnTaskStatusChangedAsync(updated, oldStatus, req.Status, req.Agent);
                    }
                    catch (Exception ex)
                    {
                        loggers.CreateLogger("DispatchDetection")
                            .LogError(ex, "Dispatch detection failed for task {TaskId}", taskId);
                    }

                    try
                    {
                        await notifications.SendAgentStatusAsync(projectId, req.Agent, req.Status, taskId);
                    }
                    catch (Exception ex)
                    {
                        loggers.CreateLogger("Notifications")
                            .LogError(ex, "Task status notification failed for task {TaskId}", taskId);
                    }
                }

                return Results.Ok(updated);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Task {taskId} not found" });
            }
        });

        group.MapGet("/{taskId:int}/review-rounds", async (ITaskRepository taskRepo, IReviewRoundRepository reviewRepo,
            string projectId, int taskId) =>
        {
            var task = await taskRepo.GetByIdAsync(taskId);
            if (task is null || task.ProjectId != projectId)
                return Results.NotFound(new { error = $"Task {taskId} not found" });

            var rounds = await reviewRepo.ListByTaskAsync(taskId);
            return Results.Ok(rounds);
        });

        group.MapPost("/{taskId:int}/review-rounds", async (ITaskRepository taskRepo, IReviewRoundRepository reviewRepo,
            string projectId, int taskId, CreateReviewRoundRequest req) =>
        {
            var task = await taskRepo.GetByIdAsync(taskId);
            if (task is null || task.ProjectId != projectId)
                return Results.NotFound(new { error = $"Task {taskId} not found" });

            try
            {
                var round = await reviewRepo.CreateAsync(new CreateReviewRoundInput
                {
                    TaskId = taskId,
                    RequestedBy = req.RequestedBy,
                    Branch = req.Branch,
                    BaseBranch = req.BaseBranch,
                    BaseCommit = req.BaseCommit,
                    HeadCommit = req.HeadCommit,
                    LastReviewedHeadCommit = req.LastReviewedHeadCommit,
                    CommitsSinceLastReview = req.CommitsSinceLastReview,
                    TestsRun = req.TestsRun,
                    Notes = req.Notes,
                    PreferredDiffBaseRef = req.PreferredDiffBaseRef,
                    PreferredDiffBaseCommit = req.PreferredDiffBaseCommit,
                    PreferredDiffHeadRef = req.PreferredDiffHeadRef,
                    PreferredDiffHeadCommit = req.PreferredDiffHeadCommit,
                    AlternateDiffBaseRef = req.AlternateDiffBaseRef,
                    AlternateDiffBaseCommit = req.AlternateDiffBaseCommit,
                    AlternateDiffHeadRef = req.AlternateDiffHeadRef,
                    AlternateDiffHeadCommit = req.AlternateDiffHeadCommit,
                    DeltaBaseCommit = req.DeltaBaseCommit,
                    InheritedCommitCount = req.InheritedCommitCount,
                    TaskLocalCommitCount = req.TaskLocalCommitCount
                });
                return Results.Created($"/api/projects/{projectId}/tasks/{taskId}/review-rounds/{round.Id}", round);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/{taskId:int}/review-rounds/{roundId:int}/verdict", async (
            IReviewWorkflowService workflow,
            string projectId,
            int taskId,
            int roundId,
            SetReviewVerdictRequest req) =>
        {
            try
            {
                var result = await workflow.SetReviewVerdictAsync(new SetReviewVerdictInput
                {
                    ProjectId = projectId,
                    TaskId = taskId,
                    ReviewRoundId = roundId,
                    Verdict = EnumExtensions.ParseReviewVerdict(req.Verdict),
                    DecidedBy = req.DecidedBy,
                    Notes = req.Notes
                });
                return Results.Ok(result.ReviewRound);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapGet("/{taskId:int}/review-findings", async (ITaskRepository taskRepo, IReviewRoundRepository reviewRepo,
            IReviewFindingRepository findingRepo, string projectId, int taskId, string? status, int? roundId, bool? resolved) =>
        {
            var task = await taskRepo.GetByIdAsync(taskId);
            if (task is null || task.ProjectId != projectId)
                return Results.NotFound(new { error = $"Task {taskId} not found" });

            try
            {
                var statuses = EnumExtensions.GetReviewFindingStatuses(status, resolved);

                if (roundId is not null)
                {
                    var round = await reviewRepo.GetByIdAsync(roundId.Value);
                    if (round is null || round.TaskId != taskId)
                        return Results.NotFound(new { error = $"Review round {roundId.Value} not found" });

                    var roundFindings = await findingRepo.ListByReviewRoundAsync(roundId.Value, statuses);
                    return Results.Ok(roundFindings);
                }

                var findings = await findingRepo.ListByTaskAsync(taskId, statuses);
                return Results.Ok(findings);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/{taskId:int}/review-rounds/{roundId:int}/findings", async (ITaskRepository taskRepo,
            IReviewRoundRepository reviewRepo, IReviewFindingRepository findingRepo,
            string projectId, int taskId, int roundId, CreateReviewFindingRequest req) =>
        {
            var task = await taskRepo.GetByIdAsync(taskId);
            if (task is null || task.ProjectId != projectId)
                return Results.NotFound(new { error = $"Task {taskId} not found" });

            var round = await reviewRepo.GetByIdAsync(roundId);
            if (round is null || round.TaskId != taskId)
                return Results.NotFound(new { error = $"Review round {roundId} not found" });

            try
            {
                var finding = await findingRepo.CreateAsync(new CreateReviewFindingInput
                {
                    ReviewRoundId = roundId,
                    CreatedBy = req.CreatedBy,
                    Category = EnumExtensions.ParseReviewFindingCategory(req.Category),
                    Summary = req.Summary,
                    Notes = req.Notes,
                    FileReferences = req.FileReferences,
                    TestCommands = req.TestCommands
                });
                return Results.Created($"/api/projects/{projectId}/tasks/{taskId}/review-findings/{finding.Id}", finding);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/{taskId:int}/review-findings/{findingId:int}/response", async (ITaskRepository taskRepo,
            IReviewFindingRepository findingRepo, string projectId, int taskId, int findingId,
            RespondToReviewFindingRequest req) =>
        {
            var task = await taskRepo.GetByIdAsync(taskId);
            if (task is null || task.ProjectId != projectId)
                return Results.NotFound(new { error = $"Task {taskId} not found" });

            var finding = await findingRepo.GetByIdAsync(findingId);
            if (finding is null || finding.TaskId != taskId)
                return Results.NotFound(new { error = $"Review finding {findingId} not found" });

            try
            {
                var parsedRespondStatus = req.Status is not null ? EnumExtensions.ParseReviewFindingStatus(req.Status) : (ReviewFindingStatus?)null;
                if (req.FollowUpTaskId is not null && parsedRespondStatus != ReviewFindingStatus.SplitToFollowUp)
                    return Results.BadRequest(new { error = "follow_up_task_id can only be supplied when status is split_to_follow_up." });

                if (req.FollowUpTaskId is not null)
                {
                    var followUp = await taskRepo.GetByIdAsync(req.FollowUpTaskId.Value);
                    if (followUp is null || followUp.ProjectId != projectId)
                        return Results.BadRequest(new { error = $"Follow-up task {req.FollowUpTaskId.Value} not found in project {projectId}." });
                }

                var updated = await findingRepo.RespondAsync(findingId, new RespondToReviewFindingInput
                {
                    RespondedBy = req.RespondedBy,
                    ResponseNotes = req.ResponseNotes,
                    Status = parsedRespondStatus,
                    StatusNotes = req.StatusNotes,
                    FollowUpTaskId = req.FollowUpTaskId
                });
                return Results.Ok(updated);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/{taskId:int}/review-findings/{findingId:int}/status", async (ITaskRepository taskRepo,
            IReviewFindingRepository findingRepo, string projectId, int taskId, int findingId,
            SetReviewFindingStatusRequest req) =>
        {
            var task = await taskRepo.GetByIdAsync(taskId);
            if (task is null || task.ProjectId != projectId)
                return Results.NotFound(new { error = $"Task {taskId} not found" });

            var finding = await findingRepo.GetByIdAsync(findingId);
            if (finding is null || finding.TaskId != taskId)
                return Results.NotFound(new { error = $"Review finding {findingId} not found" });

            try
            {
                var parsedSetStatus = EnumExtensions.ParseReviewFindingStatus(req.Status);
                if (req.FollowUpTaskId is not null && parsedSetStatus != ReviewFindingStatus.SplitToFollowUp)
                    return Results.BadRequest(new { error = "follow_up_task_id can only be supplied when status is split_to_follow_up." });

                if (req.FollowUpTaskId is not null)
                {
                    var followUp = await taskRepo.GetByIdAsync(req.FollowUpTaskId.Value);
                    if (followUp is null || followUp.ProjectId != projectId)
                        return Results.BadRequest(new { error = $"Follow-up task {req.FollowUpTaskId.Value} not found in project {projectId}." });
                }

                var updated = await findingRepo.SetStatusAsync(findingId, new UpdateReviewFindingStatusInput
                {
                    Status = parsedSetStatus,
                    UpdatedBy = req.UpdatedBy,
                    Notes = req.Notes,
                    FollowUpTaskId = req.FollowUpTaskId
                });
                return Results.Ok(updated);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/{taskId:int}/review/request", async (ITaskRepository taskRepo,
            IReviewWorkflowService workflow, string projectId, int taskId, RequestReviewPacketRequest req) =>
        {
            var task = await taskRepo.GetByIdAsync(taskId);
            if (task is null || task.ProjectId != projectId)
                return Results.NotFound(new { error = $"Task {taskId} not found" });

            try
            {
                var result = await workflow.RequestReviewAsync(projectId, new RequestReviewInput
                {
                    TaskId = taskId,
                    RequestedBy = req.RequestedBy,
                    Branch = req.Branch,
                    BaseBranch = req.BaseBranch,
                    BaseCommit = req.BaseCommit,
                    HeadCommit = req.HeadCommit,
                    LastReviewedHeadCommit = req.LastReviewedHeadCommit,
                    CommitsSinceLastReview = req.CommitsSinceLastReview,
                    TestsRun = req.TestsRun,
                    Notes = req.Notes,
                    PreferredDiffBaseRef = req.PreferredDiffBaseRef,
                    PreferredDiffBaseCommit = req.PreferredDiffBaseCommit,
                    PreferredDiffHeadRef = req.PreferredDiffHeadRef,
                    PreferredDiffHeadCommit = req.PreferredDiffHeadCommit,
                    AlternateDiffBaseRef = req.AlternateDiffBaseRef,
                    AlternateDiffBaseCommit = req.AlternateDiffBaseCommit,
                    AlternateDiffHeadRef = req.AlternateDiffHeadRef,
                    AlternateDiffHeadCommit = req.AlternateDiffHeadCommit,
                    DeltaBaseCommit = req.DeltaBaseCommit,
                    InheritedCommitCount = req.InheritedCommitCount,
                    TaskLocalCommitCount = req.TaskLocalCommitCount,
                    ThreadId = req.ThreadId
                });
                return Results.Created($"/api/projects/{projectId}/messages/{result.Message.Id}", result);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/{taskId:int}/review/findings/post", async (ITaskRepository taskRepo,
            IReviewWorkflowService workflow, string projectId, int taskId, PostReviewFindingsPacketRequest req) =>
        {
            var task = await taskRepo.GetByIdAsync(taskId);
            if (task is null || task.ProjectId != projectId)
                return Results.NotFound(new { error = $"Task {taskId} not found" });

            try
            {
                var result = await workflow.PostReviewFindingsAsync(projectId, new PostReviewFindingsInput
                {
                    TaskId = taskId,
                    ReviewRoundId = req.ReviewRoundId,
                    Sender = req.Sender,
                    ThreadId = req.ThreadId,
                    Notes = req.Notes
                });
                return Results.Created($"/api/projects/{projectId}/messages/{result.Message.Id}", result);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapGet("/next", async (ITaskRepository repo, string projectId, string? assignedTo) =>
        {
            var next = await repo.GetNextTaskAsync(projectId, assignedTo);
            return next is not null
                ? Results.Ok(next)
                : Results.Ok(new { message = "No unblocked tasks available." });
        });

        group.MapPost("/{taskId:int}/dependencies", async (ITaskRepository repo, string projectId, int taskId, AddDependencyRequest req) =>
        {
            var task = await repo.GetByIdAsync(taskId);
            if (task is null || task.ProjectId != projectId)
                return Results.NotFound(new { error = $"Task {taskId} not found" });
            var dep = await repo.GetByIdAsync(req.DependsOn);
            if (dep is null || dep.ProjectId != projectId)
                return Results.BadRequest(new { error = $"Dependency target task {req.DependsOn} not found in project {projectId}." });

            try
            {
                await repo.AddDependencyAsync(taskId, req.DependsOn);
                return Results.Ok(new { message = $"Task {taskId} now depends on task {req.DependsOn}." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapDelete("/{taskId:int}/dependencies/{dependsOn:int}", async (ITaskRepository repo, string projectId, int taskId, int dependsOn) =>
        {
            var task = await repo.GetByIdAsync(taskId);
            if (task is null || task.ProjectId != projectId)
                return Results.NotFound(new { error = $"Task {taskId} not found" });

            await repo.RemoveDependencyAsync(taskId, dependsOn);
            return Results.Ok(new { message = "Removed dependency." });
        });
    }

}

public record CreateTaskRequest(
    string Title,
    string? Description = null,
    int? Priority = null,
    List<string>? Tags = null,
    string? AssignedTo = null,
    int[]? DependsOn = null,
    int? ParentId = null);

public record UpdateTaskRequest(
    string Agent,
    string? Title = null,
    string? Description = null,
    string? Status = null,
    int? Priority = null,
    string? AssignedTo = null,
    List<string>? Tags = null,
    int? ParentId = null);

public record CreateReviewRoundRequest(
    string RequestedBy,
    string Branch,
    string BaseBranch,
    string BaseCommit,
    string HeadCommit,
    string? LastReviewedHeadCommit = null,
    int? CommitsSinceLastReview = null,
    List<string>? TestsRun = null,
    string? Notes = null,
    string? PreferredDiffBaseRef = null,
    string? PreferredDiffBaseCommit = null,
    string? PreferredDiffHeadRef = null,
    string? PreferredDiffHeadCommit = null,
    string? AlternateDiffBaseRef = null,
    string? AlternateDiffBaseCommit = null,
    string? AlternateDiffHeadRef = null,
    string? AlternateDiffHeadCommit = null,
    string? DeltaBaseCommit = null,
    int? InheritedCommitCount = null,
    int? TaskLocalCommitCount = null);

public record SetReviewVerdictRequest(
    string Verdict,
    string DecidedBy,
    string? Notes = null);

public record CreateReviewFindingRequest(
    string CreatedBy,
    string Category,
    string Summary,
    string? Notes = null,
    List<string>? FileReferences = null,
    List<string>? TestCommands = null);

public record RespondToReviewFindingRequest(
    string RespondedBy,
    string? ResponseNotes = null,
    string? Status = null,
    string? StatusNotes = null,
    int? FollowUpTaskId = null);

public record SetReviewFindingStatusRequest(
    string Status,
    string UpdatedBy,
    string? Notes = null,
    int? FollowUpTaskId = null);

public record RequestReviewPacketRequest(
    string RequestedBy,
    string Branch,
    string BaseBranch,
    string BaseCommit,
    string HeadCommit,
    string? LastReviewedHeadCommit = null,
    int? CommitsSinceLastReview = null,
    List<string>? TestsRun = null,
    string? Notes = null,
    string? PreferredDiffBaseRef = null,
    string? PreferredDiffBaseCommit = null,
    string? PreferredDiffHeadRef = null,
    string? PreferredDiffHeadCommit = null,
    string? AlternateDiffBaseRef = null,
    string? AlternateDiffBaseCommit = null,
    string? AlternateDiffHeadRef = null,
    string? AlternateDiffHeadCommit = null,
    string? DeltaBaseCommit = null,
    int? InheritedCommitCount = null,
    int? TaskLocalCommitCount = null,
    int? ThreadId = null);

public record PostReviewFindingsPacketRequest(
    int ReviewRoundId,
    string Sender,
    int? ThreadId = null,
    string? Notes = null);

public record AddDependencyRequest(int DependsOn);
