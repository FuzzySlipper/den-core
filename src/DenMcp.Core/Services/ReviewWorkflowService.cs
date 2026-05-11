using System.Text;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using Microsoft.Extensions.Logging;

namespace DenMcp.Core.Services;

public interface IReviewWorkflowService
{
    Task<ReviewPacketResult> RequestReviewAsync(string projectId, RequestReviewInput input);
    Task<ReviewPacketResult> PostReviewFindingsAsync(string projectId, PostReviewFindingsInput input);
    Task<ReviewVerdictResult> SetReviewVerdictAsync(SetReviewVerdictInput input);
}

public sealed class ReviewWorkflowService : IReviewWorkflowService
{
    private readonly ITaskRepository _tasks;
    private readonly IReviewRoundRepository _reviewRounds;
    private readonly IReviewFindingRepository _reviewFindings;
    private readonly IMessageRepository _messages;
    private readonly IDispatchRepository _dispatches;
    private readonly IDispatchDetectionService _detection;
    private readonly IAgentStreamOpsService _ops;
    private readonly ILogger<ReviewWorkflowService> _logger;

    public ReviewWorkflowService(
        ITaskRepository tasks,
        IReviewRoundRepository reviewRounds,
        IReviewFindingRepository reviewFindings,
        IMessageRepository messages,
        IDispatchRepository dispatches,
        IDispatchDetectionService detection,
        IAgentStreamOpsService ops,
        ILogger<ReviewWorkflowService> logger)
    {
        _tasks = tasks;
        _reviewRounds = reviewRounds;
        _reviewFindings = reviewFindings;
        _messages = messages;
        _dispatches = dispatches;
        _detection = detection;
        _ops = ops;
        _logger = logger;
    }

    public async Task<ReviewPacketResult> RequestReviewAsync(string projectId, RequestReviewInput input)
    {
        var detail = await _tasks.GetDetailAsync(input.TaskId);
        ValidateTaskProject(detail, projectId, input.TaskId);
        await ValidateThreadAsync(projectId, input.TaskId, input.ThreadId);

        var round = await _reviewRounds.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = input.TaskId,
            RequestedBy = input.RequestedBy,
            Branch = input.Branch,
            BaseBranch = input.BaseBranch,
            BaseCommit = input.BaseCommit,
            HeadCommit = input.HeadCommit,
            LastReviewedHeadCommit = input.LastReviewedHeadCommit,
            CommitsSinceLastReview = input.CommitsSinceLastReview,
            TestsRun = input.TestsRun,
            Notes = input.Notes,
            PreferredDiffBaseRef = input.PreferredDiffBaseRef,
            PreferredDiffBaseCommit = input.PreferredDiffBaseCommit,
            PreferredDiffHeadRef = input.PreferredDiffHeadRef,
            PreferredDiffHeadCommit = input.PreferredDiffHeadCommit,
            AlternateDiffBaseRef = input.AlternateDiffBaseRef,
            AlternateDiffBaseCommit = input.AlternateDiffBaseCommit,
            AlternateDiffHeadRef = input.AlternateDiffHeadRef,
            AlternateDiffHeadCommit = input.AlternateDiffHeadCommit,
            DeltaBaseCommit = input.DeltaBaseCommit,
            InheritedCommitCount = input.InheritedCommitCount,
            TaskLocalCommitCount = input.TaskLocalCommitCount
        });

        var addressedFindingDetails = detail.OpenReviewFindings
            .Concat(detail.ResolvedReviewFindings)
            .Where(finding => finding.ResponseAt is not null || finding.Status != ReviewFindingStatus.Open)
            .OrderBy(finding => finding.FindingNumber)
            .ToList();
        var openFindingDetails = detail.OpenReviewFindings
            .OrderBy(finding => finding.FindingNumber)
            .ToList();
        var findingsAddressed = GetAddressedFindings(detail);
        var openFindings = openFindingDetails.Select(FormatFindingOverviewLine).ToList();

        var packet = BuildReviewRequestPacket(round, input.Notes, findingsAddressed, openFindings);
        var message = await _messages.CreateAsync(new Message
        {
            ProjectId = projectId,
            TaskId = input.TaskId,
            ThreadId = input.ThreadId,
            Sender = input.RequestedBy,
            Content = packet.Content,
            Intent = MessageIntent.ReviewRequest,
            Metadata = BuildRequestReviewMetadata(round, packet, addressedFindingDetails, openFindingDetails, input.RunId)
        });

        try
        {
            await _detection.OnMessageCreatedAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dispatch detection failed for review request message {MessageId}", message.Id);
        }

        await _ops.RecordReviewRequestedAsync(message, round, packet.Kind, recipientRole: "reviewer");

        return new ReviewPacketResult
        {
            ReviewRound = round,
            Message = message,
            Packet = packet,
            FindingsAddressed = findingsAddressed,
            OpenFindings = openFindings,
            TestCommands = round.TestsRun?.ToList() ?? []
        };
    }

    public async Task<ReviewPacketResult> PostReviewFindingsAsync(string projectId, PostReviewFindingsInput input)
    {
        var detail = await _tasks.GetDetailAsync(input.TaskId);
        ValidateTaskProject(detail, projectId, input.TaskId);
        await ValidateThreadAsync(projectId, input.TaskId, input.ThreadId);

        var round = detail.ReviewRounds.FirstOrDefault(reviewRound => reviewRound.Id == input.ReviewRoundId);
        if (round is null)
            throw new KeyNotFoundException($"Review round {input.ReviewRoundId} not found for task {input.TaskId}");

        var findings = await _reviewFindings.ListByReviewRoundAsync(input.ReviewRoundId);
        var openFindingDetails = detail.OpenReviewFindings
            .OrderBy(finding => finding.FindingNumber)
            .ToList();
        var openFindings = openFindingDetails.Select(FormatFindingOverviewLine).ToList();
        var packet = BuildReviewFindingsPacket(round, findings, openFindings, input.Notes);
        var reviewerCommands = findings
            .SelectMany(finding => finding.TestCommands ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var metadataObj = new Dictionary<string, object?>
        {
            ["type"] = "review_findings_packet",
            ["packet_kind"] = "review_findings",
            ["review_round_id"] = round.Id,
            ["review_round_number"] = round.RoundNumber,
            ["branch"] = round.Branch,
            ["verdict"] = round.Verdict?.ToDbValue(),
            ["findings"] = findings
                .OrderBy(finding => finding.FindingNumber)
                .Select(ToMetadataFinding)
                .ToList(),
            ["open_findings"] = openFindingDetails.Select(ToMetadataFinding).ToList(),
            ["reviewer_test_commands"] = reviewerCommands
        };

        if (input.RunId is not null)
            metadataObj["run_id"] = input.RunId;

        var message = await _messages.CreateAsync(new Message
        {
            ProjectId = projectId,
            TaskId = input.TaskId,
            ThreadId = input.ThreadId,
            Sender = input.Sender,
            Content = packet.Content,
            Intent = MessageIntent.ReviewFeedback,
            Metadata = JsonSerializer.SerializeToElement(metadataObj)
        });

        return new ReviewPacketResult
        {
            ReviewRound = round,
            Message = message,
            Packet = packet,
            FindingsAddressed = findings
                .OrderBy(finding => finding.FindingNumber)
                .Select(FormatFindingOverviewLine)
                .ToList(),
            OpenFindings = openFindings,
            TestCommands = reviewerCommands
        };
    }

    public async Task<ReviewVerdictResult> SetReviewVerdictAsync(SetReviewVerdictInput input)
    {
        var round = await _reviewRounds.GetByIdAsync(input.ReviewRoundId)
            ?? throw new KeyNotFoundException($"Review round {input.ReviewRoundId} not found");
        var previousVerdict = round.Verdict;

        if (input.TaskId is not null && round.TaskId != input.TaskId.Value)
            throw new KeyNotFoundException($"Review round {input.ReviewRoundId} not found for task {input.TaskId.Value}");

        var detail = await _tasks.GetDetailAsync(round.TaskId);
        if (input.ProjectId is not null)
            ValidateTaskProject(detail, input.ProjectId, detail.Task.Id);

        var updated = await _reviewRounds.SetVerdictAsync(
            input.ReviewRoundId,
            input.Verdict,
            input.DecidedBy,
            input.Notes);

        Message? handoffMessage = null;
        if (ShouldEmitVerdictHandoff(updated.Verdict))
        {
            handoffMessage = await FindExistingVerdictHandoffMessageAsync(
                detail.Task.ProjectId,
                detail.Task.Id,
                updated.Id,
                GetVerdictHandoffKind(updated));

            if (previousVerdict != updated.Verdict || handoffMessage is null)
                handoffMessage = await CreateVerdictHandoffMessageAsync(detail, updated);
        }

        if (ShouldEmitVerdictHandoff(updated.Verdict))
            await _ops.RecordReviewVerdictAsync(detail.Task, updated, ResolveImplementer(detail.Task, updated), handoffMessage);

        var completedDispatches = await ResolveReviewerDispatchesAsync(
            detail.Task.ProjectId,
            detail.Task.Id,
            input.DecidedBy);

        return new ReviewVerdictResult
        {
            ReviewRound = updated,
            HandoffMessage = handoffMessage,
            CompletedDispatches = completedDispatches
        };
    }

    private static void ValidateTaskProject(TaskDetail detail, string projectId, int taskId)
    {
        if (!string.Equals(detail.Task.ProjectId, projectId, StringComparison.Ordinal))
            throw new KeyNotFoundException($"Task {taskId} not found in project {projectId}");
    }

    private async Task ValidateThreadAsync(string projectId, int taskId, int? threadId)
    {
        if (threadId is null)
            return;

        var thread = await _messages.GetThreadAsync(threadId.Value);
        if (!string.Equals(thread.Root.ProjectId, projectId, StringComparison.Ordinal) ||
            thread.Root.TaskId != taskId)
        {
            throw new InvalidOperationException(
                $"Thread {threadId.Value} does not belong to task {taskId} in project {projectId}.");
        }
    }

    private static List<string> GetAddressedFindings(TaskDetail detail)
    {
        return detail.OpenReviewFindings
            .Concat(detail.ResolvedReviewFindings)
            .Where(finding => finding.ResponseAt is not null || finding.Status != ReviewFindingStatus.Open)
            .OrderBy(finding => finding.FindingNumber)
            .Select(FormatFindingOverviewLine)
            .ToList();
    }

    private static ReviewPacket BuildReviewRequestPacket(
        ReviewRound round,
        string? notes,
        IReadOnlyList<string> findingsAddressed,
        IReadOnlyList<string> openFindings)
    {
        var isRereview = round.RoundNumber > 1 || round.LastReviewedHeadCommit is not null;
        var title = isRereview ? "Ready for rereview" : "Review request";
        var sb = new StringBuilder();

        sb.AppendLine(title);
        sb.AppendLine();
        sb.AppendLine($"Review round: `{round.RoundNumber}`");
        sb.AppendLine($"Reviewed diff: `{round.PreferredDiff.BaseRef}...{round.PreferredDiff.HeadRef}`");
        if (round.AlternateDiff is not null)
            sb.AppendLine($"Alternate diff: `{round.AlternateDiff.BaseRef}...{round.AlternateDiff.HeadRef}`");
        if (round.DeltaDiff?.BaseCommit is not null)
            sb.AppendLine($"Delta since last review: `{round.DeltaDiff.BaseCommit}..{round.HeadCommit}`");
        sb.AppendLine($"Base SHA: `{round.BaseCommit}`");
        if (round.LastReviewedHeadCommit is not null)
            sb.AppendLine($"Last reviewed SHA: `{round.LastReviewedHeadCommit}`");
        sb.AppendLine($"Current SHA: `{round.HeadCommit}`");
        if (round.CommitsSinceLastReview is not null)
            sb.AppendLine($"New commits: `{round.CommitsSinceLastReview.Value}`");

        var branchComposition = FormatBranchComposition(round);
        if (branchComposition is not null)
            sb.AppendLine($"Branch composition: {branchComposition}");

        AppendListSection(sb, "Findings addressed", findingsAddressed, skipIfEmpty: !isRereview);
        AppendListSection(sb, "Open findings", openFindings, skipIfEmpty: !isRereview);
        AppendListSection(sb, "Tests run by implementer", round.TestsRun ?? [], skipIfEmpty: false);
        AppendOptionalNotes(sb, notes ?? round.Notes);

        return new ReviewPacket
        {
            Kind = isRereview ? ReviewPacketKind.RereviewRequest : ReviewPacketKind.ReviewRequest,
            Title = title,
            Content = sb.ToString().TrimEnd()
        };
    }

    private static ReviewPacket BuildReviewFindingsPacket(
        ReviewRound round,
        IReadOnlyList<ReviewFinding> findings,
        IReadOnlyList<string> openFindings,
        string? notes)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Review findings");
        sb.AppendLine();
        sb.AppendLine($"Review round: `{round.RoundNumber}`");
        sb.AppendLine($"Verdict: `{round.Verdict?.ToDbValue() ?? "pending"}`");
        sb.AppendLine($"Reviewed diff: `{round.PreferredDiff.BaseRef}...{round.PreferredDiff.HeadRef}`");
        if (round.AlternateDiff is not null)
            sb.AppendLine($"Alternate diff: `{round.AlternateDiff.BaseRef}...{round.AlternateDiff.HeadRef}`");
        if (round.DeltaDiff?.BaseCommit is not null)
            sb.AppendLine($"Delta since last review: `{round.DeltaDiff.BaseCommit}..{round.HeadCommit}`");

        var reviewerCommands = findings
            .SelectMany(finding => finding.TestCommands ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToList();
        AppendListSection(sb, "Reviewer test commands", reviewerCommands, skipIfEmpty: false);

        sb.AppendLine("Findings:");
        if (findings.Count == 0)
        {
            sb.AppendLine("- (none)");
        }
        else
        {
            foreach (var finding in findings.OrderBy(finding => finding.FindingNumber))
            {
                sb.AppendLine();
                sb.AppendLine($"{finding.FindingKey} - {finding.Category.ToDbValue()}");
                sb.AppendLine($"Status: {finding.Status.ToDbValue()}");
                sb.AppendLine($"Summary: {finding.Summary}");
                if (finding.FileReferences is { Count: > 0 })
                    sb.AppendLine($"Files: {string.Join(", ", finding.FileReferences.Select(value => $"`{value}`"))}");
                if (finding.TestCommands is { Count: > 0 })
                    sb.AppendLine($"Tests: {string.Join(", ", finding.TestCommands.Select(value => $"`{value}`"))}");
                if (!string.IsNullOrWhiteSpace(finding.Notes))
                    sb.AppendLine($"Notes: {CollapseWhitespace(finding.Notes)}");
            }
        }

        AppendListSection(sb, "Open findings after review", openFindings, skipIfEmpty: false);
        AppendOptionalNotes(sb, notes);

        return new ReviewPacket
        {
            Kind = ReviewPacketKind.ReviewFindings,
            Title = "Review findings",
            Content = sb.ToString().TrimEnd()
        };
    }

    private static string FormatFindingOverviewLine(ReviewFinding finding)
    {
        var line = $"`{finding.FindingKey}` `{finding.Category.ToDbValue()}` `{finding.Status.ToDbValue()}` {finding.Summary}";
        var detail = GetCurrentFindingDisplayNote(finding);
        return string.IsNullOrWhiteSpace(detail)
            ? line
            : $"{line} ({CollapseWhitespace(detail)})";
    }

    private static string? GetCurrentFindingDisplayNote(ReviewFinding finding)
    {
        if (!string.IsNullOrWhiteSpace(finding.StatusNotes))
            return finding.StatusNotes;

        if (string.IsNullOrWhiteSpace(finding.ResponseNotes))
            return null;

        if (finding.StatusUpdatedAt is null)
            return finding.ResponseNotes;

        if (finding.ResponseAt > finding.StatusUpdatedAt)
            return finding.ResponseNotes;

        if (finding.Status == ReviewFindingStatus.ClaimedFixed &&
            string.Equals(finding.StatusUpdatedBy, finding.ResponseBy, StringComparison.Ordinal))
        {
            return finding.ResponseNotes;
        }

        return null;
    }

    private static string? FormatBranchComposition(ReviewRound round)
    {
        var parts = new List<string>();
        if (round.InheritedCommitCount is not null)
            parts.Add($"`{round.InheritedCommitCount.Value}` inherited");
        if (round.TaskLocalCommitCount is not null)
            parts.Add($"`{round.TaskLocalCommitCount.Value}` task-local");

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static void AppendListSection(
        StringBuilder sb,
        string heading,
        IReadOnlyCollection<string> items,
        bool skipIfEmpty)
    {
        if (skipIfEmpty && items.Count == 0)
            return;

        sb.AppendLine();
        sb.AppendLine($"{heading}:");
        if (items.Count == 0)
        {
            sb.AppendLine("- (none recorded)");
            return;
        }

        foreach (var item in items)
            sb.AppendLine($"- {item}");
    }

    private static void AppendOptionalNotes(StringBuilder sb, string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
            return;

        sb.AppendLine();
        sb.AppendLine("Notes:");
        sb.AppendLine($"- {CollapseWhitespace(notes)}");
    }

    private static string CollapseWhitespace(string value) =>
        value.ReplaceLineEndings(" ").Trim();

    private static object ToMetadataFinding(ReviewFinding finding) => new
    {
        finding_key = finding.FindingKey,
        review_round_number = finding.ReviewRoundNumber,
        category = finding.Category.ToDbValue(),
        status = finding.Status.ToDbValue(),
        summary = finding.Summary,
        file_references = finding.FileReferences,
        test_commands = finding.TestCommands,
        follow_up_task_id = finding.FollowUpTaskId
    };

    private static JsonElement BuildRequestReviewMetadata(
        ReviewRound round,
        ReviewPacket packet,
        IReadOnlyList<ReviewFinding> addressedFindingDetails,
        IReadOnlyList<ReviewFinding> openFindingDetails,
        string? runId)
    {
        var meta = new Dictionary<string, object?>
        {
            ["type"] = packet.Kind == ReviewPacketKind.RereviewRequest ? "rereview_packet" : "review_request_packet",
            ["packet_kind"] = packet.Kind == ReviewPacketKind.RereviewRequest ? "rereview_request" : "review_request",
            ["target_role"] = "reviewer",
            ["review_round_id"] = round.Id,
            ["review_round_number"] = round.RoundNumber,
            ["branch"] = round.Branch,
            ["base_branch"] = round.BaseBranch,
            ["base_commit"] = round.BaseCommit,
            ["head_commit"] = round.HeadCommit,
            ["last_reviewed_head_commit"] = round.LastReviewedHeadCommit,
            ["commits_since_last_review"] = round.CommitsSinceLastReview,
            ["preferred_diff"] = new Dictionary<string, object?>
            {
                ["base_ref"] = round.PreferredDiff.BaseRef,
                ["base_commit"] = round.PreferredDiff.BaseCommit,
                ["head_ref"] = round.PreferredDiff.HeadRef,
                ["head_commit"] = round.PreferredDiff.HeadCommit
            },
            ["alternate_diff"] = round.AlternateDiff is null ? null : new Dictionary<string, object?>
            {
                ["base_ref"] = round.AlternateDiff.BaseRef,
                ["base_commit"] = round.AlternateDiff.BaseCommit,
                ["head_ref"] = round.AlternateDiff.HeadRef,
                ["head_commit"] = round.AlternateDiff.HeadCommit
            },
            ["delta_base_commit"] = round.DeltaDiff?.BaseCommit,
            ["findings_addressed"] = addressedFindingDetails.Select(ToMetadataFinding).ToList(),
            ["open_findings"] = openFindingDetails.Select(ToMetadataFinding).ToList(),
            ["tests_run"] = round.TestsRun
        };

        if (runId is not null)
            meta["run_id"] = runId;

        return JsonSerializer.SerializeToElement(meta);
    }

    private static bool ShouldEmitVerdictHandoff(ReviewVerdict? verdict) =>
        verdict is ReviewVerdict.ChangesRequested or ReviewVerdict.FollowUpNeeded or ReviewVerdict.LooksGood;

    private async Task<Message> CreateVerdictHandoffMessageAsync(TaskDetail detail, ReviewRound round)
    {
        var recipient = ResolveImplementer(detail.Task, round);
        var threadId = await ResolveThreadIdAsync(detail.Task.ProjectId, detail.Task.Id, round.Id);
        var metadata = BuildVerdictHandoffMetadata(recipient, round);

        var created = await _messages.CreateAsync(new Message
        {
            ProjectId = detail.Task.ProjectId,
            TaskId = detail.Task.Id,
            ThreadId = threadId,
            Sender = round.VerdictBy ?? round.RequestedBy,
            Content = BuildVerdictHandoffContent(detail, round),
            Intent = GetVerdictHandoffIntent(round),
            Metadata = metadata
        });

        try
        {
            await _detection.OnMessageCreatedAsync(created);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dispatch detection failed for review verdict handoff message {MessageId}", created.Id);
        }

        return created;
    }

    private async Task<int?> ResolveThreadIdAsync(string projectId, int taskId, int reviewRoundId)
    {
        var messages = await _messages.GetMessagesAsync(projectId, taskId: taskId, limit: 100);
        foreach (var message in messages)
        {
            if (!TryGetReviewRoundId(message.Metadata, out var messageRoundId) || messageRoundId != reviewRoundId)
                continue;

            return message.ThreadId ?? message.Id;
        }

        return null;
    }

    private static bool TryGetReviewRoundId(JsonElement? metadata, out int reviewRoundId)
    {
        reviewRoundId = default;
        if (metadata is not JsonElement element ||
            !element.TryGetProperty("review_round_id", out var roundIdElement) ||
            roundIdElement.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        return roundIdElement.TryGetInt32(out reviewRoundId);
    }

    private async Task<List<DispatchEntry>> ResolveReviewerDispatchesAsync(
        string projectId,
        int taskId,
        string reviewer)
    {
        var open = await _dispatches.ListAsync(projectId, reviewer, [DispatchStatus.Pending, DispatchStatus.Approved]);
        var completed = new List<DispatchEntry>();

        foreach (var entry in open.Where(entry => entry.TaskId == taskId))
        {
            try
            {
                if (entry.Status == DispatchStatus.Approved)
                {
                    completed.Add(await _dispatches.CompleteAsync(entry.Id, "review-workflow"));
                }
                else
                {
                    await _dispatches.ExpireAsync(entry.Id);
                }
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Failed to resolve reviewer dispatch {DispatchId} after verdict", entry.Id);
            }
        }

        return completed;
    }

    private static string ResolveImplementer(ProjectTask task, ReviewRound round)
    {
        if (!string.IsNullOrWhiteSpace(task.AssignedTo))
            return task.AssignedTo;

        return round.RequestedBy;
    }

    private async Task<Message?> FindExistingVerdictHandoffMessageAsync(
        string projectId,
        int taskId,
        int reviewRoundId,
        string handoffKind)
    {
        var messages = await _messages.GetMessagesAsync(projectId, taskId: taskId, limit: 100);
        return messages.FirstOrDefault(message =>
            TryGetReviewRoundId(message.Metadata, out var messageRoundId) &&
            messageRoundId == reviewRoundId &&
            TryGetHandoffKind(message.Metadata, out var existingHandoffKind) &&
            string.Equals(existingHandoffKind, handoffKind, StringComparison.Ordinal));
    }

    private static bool TryGetHandoffKind(JsonElement? metadata, out string? handoffKind)
    {
        if (MessageIntentCompatibility.TryGetSubtype(metadata, "handoff_kind", out handoffKind))
            return true;

        return MessageIntentCompatibility.TryGetLegacyType(metadata, out handoffKind);
    }

    private static MessageIntent GetVerdictHandoffIntent(ReviewRound round) =>
        round.Verdict == ReviewVerdict.LooksGood ? MessageIntent.ReviewApproval : MessageIntent.ReviewFeedback;

    private static string GetVerdictHandoffKind(ReviewRound round) =>
        round.Verdict == ReviewVerdict.LooksGood ? "merge_request" : "review_feedback";

    private static JsonElement BuildVerdictHandoffMetadata(string recipient, ReviewRound round)
    {
        var handoffKind = GetVerdictHandoffKind(round);
        return JsonSerializer.SerializeToElement(new
        {
            type = handoffKind,
            handoff_kind = handoffKind,
            recipient,
            review_round_id = round.Id,
            review_round_number = round.RoundNumber,
            verdict = round.Verdict?.ToDbValue(),
            reviewer = round.VerdictBy,
            branch = round.Branch,
            base_branch = round.BaseBranch,
            base_commit = round.BaseCommit,
            head_commit = round.HeadCommit,
            last_reviewed_head_commit = round.LastReviewedHeadCommit,
            tests_run = round.TestsRun,
            preferred_diff = new
            {
                base_ref = round.PreferredDiff.BaseRef,
                base_commit = round.PreferredDiff.BaseCommit,
                head_ref = round.PreferredDiff.HeadRef,
                head_commit = round.PreferredDiff.HeadCommit
            },
            alternate_diff = round.AlternateDiff is null ? null : new
            {
                base_ref = round.AlternateDiff.BaseRef,
                base_commit = round.AlternateDiff.BaseCommit,
                head_ref = round.AlternateDiff.HeadRef,
                head_commit = round.AlternateDiff.HeadCommit
            }
        });
    }

    private static string BuildVerdictHandoffContent(TaskDetail detail, ReviewRound round)
    {
        var sb = new StringBuilder();
        var isApproval = round.Verdict == ReviewVerdict.LooksGood;
        var title = isApproval ? "Review approved" : "Review follow-up";

        sb.AppendLine(title);
        sb.AppendLine();
        sb.AppendLine($"**Task #{detail.Task.Id}**: {detail.Task.Title}");
        sb.AppendLine($"Review round: `{round.RoundNumber}`");
        sb.AppendLine($"Verdict: `{round.Verdict?.ToDbValue() ?? "pending"}`");
        sb.AppendLine($"Reviewed diff: `{round.PreferredDiff.BaseRef}...{round.PreferredDiff.HeadRef}`");
        sb.AppendLine($"Base SHA: `{round.BaseCommit}`");
        sb.AppendLine($"Reviewed head SHA: `{round.HeadCommit}`");
        sb.AppendLine($"Branch: `{round.Branch}`");
        if (round.VerdictBy is not null)
            sb.AppendLine($"Reviewer: `{round.VerdictBy}`");

        AppendListSection(
            sb,
            isApproval ? "Reviewer test commands" : "Open findings",
            isApproval
                ? round.TestsRun ?? []
                : detail.OpenReviewFindings
                    .OrderBy(finding => finding.FindingNumber)
                    .Select(FormatFindingOverviewLine)
                    .ToList(),
            skipIfEmpty: false);

        AppendOptionalNotes(sb, round.VerdictNotes);

        sb.AppendLine();
        sb.AppendLine("Next step:");
        if (isApproval)
        {
            sb.AppendLine($"- Confirm `{round.Branch}` is still at reviewed head `{round.HeadCommit}`.");
            sb.AppendLine($"- If it matches, merge to `{round.BaseBranch}`, mark the task done, and pick up your next task.");
            sb.AppendLine("- If the branch has new commits beyond that reviewed head, request review again with the new head SHA and tests run instead of merging.");
            sb.AppendLine("- If there is no next task, send a work-complete update through your configured operator-notification path.");
        }
        else
        {
            sb.AppendLine("- Read the task thread and evaluate the review feedback.");
            sb.AppendLine($"- Address the needed changes on `{round.Branch}` if the path back to green is straightforward and still fits the plan.");
            sb.AppendLine("- Stop and ask for guidance if reality no longer matches the plan, the plan is too vague to implement confidently, scope needs to expand in a non-obvious way, repeated failed attempts suggest the assumptions are wrong, or you are inventing a complex workaround mainly to cope with local mess.");
            sb.AppendLine("- Creating or updating Den tasks is cheap; prefer a follow-up task over landing thin interfaces, deceptive scaffolding, or code TODOs that leave the real behavior unwired.");
            sb.AppendLine("- When ready, request review again with the new head commit and tests run.");
        }

        return sb.ToString().TrimEnd();
    }
}
