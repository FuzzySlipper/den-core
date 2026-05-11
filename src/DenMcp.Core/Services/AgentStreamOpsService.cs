using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using Microsoft.Extensions.Logging;

namespace DenMcp.Core.Services;

public interface IAgentStreamOpsService
{
    Task<AgentStreamEntry> AppendOpsAsync(AgentStreamEntry entry);
    Task RecordDispatchCreatedAsync(DispatchEntry dispatch);
    Task RecordDispatchApprovedAsync(DispatchEntry dispatch, string decidedBy);
    Task RecordDispatchRejectedAsync(DispatchEntry dispatch, string decidedBy);
    Task RecordReviewRequestedAsync(Message message, ReviewRound round, ReviewPacketKind packetKind, string? recipientRole = null);
    Task RecordReviewVerdictAsync(ProjectTask task, ReviewRound round, string recipient, Message? handoffMessage);
}

public sealed class AgentStreamOpsService : IAgentStreamOpsService
{
    private readonly IAgentStreamRepository _stream;
    private readonly ILogger<AgentStreamOpsService> _logger;
    private readonly IAgentRunRepository? _agentRuns;

    public AgentStreamOpsService(
        IAgentStreamRepository stream,
        ILogger<AgentStreamOpsService> logger,
        IAgentRunRepository? agentRuns = null)
    {
        _stream = stream;
        _logger = logger;
        _agentRuns = agentRuns;
    }

    public async Task<AgentStreamEntry> AppendOpsAsync(AgentStreamEntry entry)
    {
        if (entry.StreamKind != AgentStreamKind.Ops)
            throw new InvalidOperationException("Only ops entries can be appended through AgentStreamOpsService.");

        try
        {
            var appended = await _stream.AppendAsync(entry);
            await ProjectAgentRunAsync(appended);
            return appended;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append agent stream ops event {EventType}", entry.EventType);
            throw;
        }
    }

    private async Task ProjectAgentRunAsync(AgentStreamEntry entry)
    {
        if (_agentRuns is null || !entry.EventType.StartsWith("subagent_", StringComparison.Ordinal))
            return;

        try
        {
            await _agentRuns.UpsertFromStreamEntryAsync(entry);
        }
        catch (Exception ex)
        {
            // agent_stream_entries remains the append-only audit log. If the mutable
            // AgentRun projection misses an update, SubagentRunService can rebuild it
            // from the stream during list/detail reads.
            _logger.LogWarning(ex, "Failed to project sub-agent run event {EventType} from stream entry {EntryId}", entry.EventType, entry.Id);
        }
    }

    public Task RecordDispatchCreatedAsync(DispatchEntry dispatch) =>
        AppendSafelyAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "dispatch_created",
            ProjectId = dispatch.ProjectId,
            TaskId = dispatch.TaskId,
            DispatchId = dispatch.Id,
            Sender = "den",
            RecipientAgent = dispatch.TargetAgent,
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Body = dispatch.Summary,
            Metadata = JsonSerializer.SerializeToElement(new
            {
                status = dispatch.Status.ToDbValue(),
                trigger_type = dispatch.TriggerType.ToDbValue(),
                trigger_id = dispatch.TriggerId
            }),
            DedupKey = $"dispatch-created:{dispatch.Id}"
        });

    public async Task RecordDispatchApprovedAsync(DispatchEntry dispatch, string decidedBy)
    {
        await AppendSafelyAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "dispatch_approved",
            ProjectId = dispatch.ProjectId,
            TaskId = dispatch.TaskId,
            DispatchId = dispatch.Id,
            Sender = decidedBy,
            RecipientAgent = dispatch.TargetAgent,
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Body = dispatch.Summary,
            Metadata = JsonSerializer.SerializeToElement(new
            {
                status = dispatch.Status.ToDbValue(),
                trigger_type = dispatch.TriggerType.ToDbValue(),
                trigger_id = dispatch.TriggerId,
                decided_by = decidedBy
            }),
            DedupKey = $"dispatch-approved:{dispatch.Id}"
        });

        await AppendSafelyAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "wake_requested",
            ProjectId = dispatch.ProjectId,
            TaskId = dispatch.TaskId,
            DispatchId = dispatch.Id,
            Sender = decidedBy,
            RecipientAgent = dispatch.TargetAgent,
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Body = dispatch.Summary,
            Metadata = JsonSerializer.SerializeToElement(new
            {
                dispatch_status = dispatch.Status.ToDbValue(),
                trigger_type = dispatch.TriggerType.ToDbValue(),
                trigger_id = dispatch.TriggerId
            }),
            DedupKey = $"wake-requested:{dispatch.Id}"
        });
    }

    public Task RecordDispatchRejectedAsync(DispatchEntry dispatch, string decidedBy) =>
        AppendSafelyAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "dispatch_rejected",
            ProjectId = dispatch.ProjectId,
            TaskId = dispatch.TaskId,
            DispatchId = dispatch.Id,
            Sender = decidedBy,
            RecipientAgent = dispatch.TargetAgent,
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Body = dispatch.Summary,
            Metadata = JsonSerializer.SerializeToElement(new
            {
                status = dispatch.Status.ToDbValue(),
                trigger_type = dispatch.TriggerType.ToDbValue(),
                trigger_id = dispatch.TriggerId,
                decided_by = decidedBy
            }),
            DedupKey = $"dispatch-rejected:{dispatch.Id}"
        });

    public Task RecordReviewRequestedAsync(
        Message message,
        ReviewRound round,
        ReviewPacketKind packetKind,
        string? recipientRole = null)
    {
        var eventType = packetKind == ReviewPacketKind.RereviewRequest
            ? "rereview_requested"
            : "review_requested";

        return AppendSafelyAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = eventType,
            ProjectId = message.ProjectId,
            TaskId = message.TaskId,
            ThreadId = message.ThreadId ?? message.Id,
            Sender = message.Sender,
            RecipientRole = recipientRole,
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Body = BuildRoundBody(round),
            Metadata = JsonSerializer.SerializeToElement(new
            {
                message_id = message.Id,
                review_round_id = round.Id,
                review_round_number = round.RoundNumber,
                packet_kind = packetKind == ReviewPacketKind.RereviewRequest ? "rereview_request" : "review_request",
                branch = round.Branch,
                head_commit = round.HeadCommit
            }),
            DedupKey = $"{eventType}:{round.Id}"
        });
    }

    public async Task RecordReviewVerdictAsync(
        ProjectTask task,
        ReviewRound round,
        string recipient,
        Message? handoffMessage)
    {
        if (round.Verdict is null || round.Verdict == ReviewVerdict.BlockedByDependency)
            return;

        var eventType = round.Verdict == ReviewVerdict.LooksGood
            ? "review_approved"
            : "changes_requested";
        var sender = round.VerdictBy ?? round.RequestedBy;
        var threadId = handoffMessage?.ThreadId ?? handoffMessage?.Id;

        await AppendSafelyAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = eventType,
            ProjectId = task.ProjectId,
            TaskId = task.Id,
            ThreadId = threadId,
            Sender = sender,
            RecipientAgent = recipient,
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Body = BuildRoundBody(round),
            Metadata = JsonSerializer.SerializeToElement(new
            {
                review_round_id = round.Id,
                review_round_number = round.RoundNumber,
                verdict = round.Verdict.Value.ToDbValue(),
                branch = round.Branch,
                head_commit = round.HeadCommit,
                handoff_message_id = handoffMessage?.Id
            }),
            DedupKey = $"{eventType}:{round.Id}"
        });

        if (round.Verdict == ReviewVerdict.LooksGood && handoffMessage is not null)
        {
            await AppendSafelyAsync(new AgentStreamEntry
            {
                StreamKind = AgentStreamKind.Ops,
                EventType = "merge_handoff",
                ProjectId = task.ProjectId,
                TaskId = task.Id,
                ThreadId = threadId,
                Sender = sender,
                RecipientAgent = recipient,
                DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
                Body = BuildRoundBody(round),
                Metadata = JsonSerializer.SerializeToElement(new
                {
                    review_round_id = round.Id,
                    review_round_number = round.RoundNumber,
                    message_id = handoffMessage.Id,
                    verdict = round.Verdict.Value.ToDbValue(),
                    branch = round.Branch,
                    head_commit = round.HeadCommit
                }),
                DedupKey = $"merge-handoff:{round.Id}"
            });
        }
    }

    private async Task AppendSafelyAsync(AgentStreamEntry entry)
    {
        try
        {
            await _stream.AppendAsync(entry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append agent stream ops event {EventType}", entry.EventType);
        }
    }

    private static string BuildRoundBody(ReviewRound round) => $"Round {round.RoundNumber} on {round.Branch}";
}
