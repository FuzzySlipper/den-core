using System.Text.Json;
using DenCore.Data;
using DenCore.Models;
using Microsoft.Extensions.Logging;

namespace DenCore.Services;

/// <summary>
/// Result from routing stale worker attention events.
/// </summary>
public sealed class StaleAttentionRoutingResult
{
    /// <summary>Number of critical conditions routed to planner/runner wake.</summary>
    public int PlannerRouted { get; set; }

    /// <summary>Number of warning conditions routed to user notification.</summary>
    public int UserNotified { get; set; }

    /// <summary>Number of info conditions that were logged only.</summary>
    public int InfoLogged { get; set; }

    /// <summary>Number of conditions where no planner binding was found (fallback notification).</summary>
    public int FallbackNotified { get; set; }

    /// <summary>Total conditions processed.</summary>
    public int TotalProcessed { get; set; }
}

/// <summary>
/// Routes stale worker attention conditions to the appropriate notification channel
/// based on severity. Critical conditions wake a planner/runner binding; warning
/// conditions create user notifications; info conditions are logged only.
/// The service is independently testable with injected sweep results.
/// </summary>
public interface IStaleAttentionRoutingService
{
    /// <summary>
    /// Route stale conditions from a reconciliation result to appropriate attention channels.
    /// Returns a routing result with counts per channel.
    /// </summary>
    Task<StaleAttentionRoutingResult> RouteAsync(
        StaleReconciliationResult result,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Route a single stale condition. Useful for testing individual conditions
    /// without running a full sweep.
    /// </summary>
    Task<StaleAttentionRoutingResult> RouteSingleAsync(
        StaleWorkerCondition condition,
        CancellationToken cancellationToken = default);
}

public sealed class StaleAttentionRoutingService : IStaleAttentionRoutingService
{
    private readonly IMessageRepository _messages;
    private readonly IAgentInstanceBindingRepository _bindings;
    private readonly IProjectRepository _projects;
    private readonly ILogger<StaleAttentionRoutingService> _logger;

    public StaleAttentionRoutingService(
        IMessageRepository messages,
        IAgentInstanceBindingRepository bindings,
        IProjectRepository projects,
        ILogger<StaleAttentionRoutingService> logger)
    {
        _messages = messages;
        _bindings = bindings;
        _projects = projects;
        _logger = logger;
    }

    public async Task<StaleAttentionRoutingResult> RouteAsync(
        StaleReconciliationResult result,
        CancellationToken cancellationToken = default)
    {
        var routingResult = new StaleAttentionRoutingResult();

        foreach (var condition in result.NewConditions)
        {
            var single = await RouteSingleAsync(condition, cancellationToken);
            routingResult.PlannerRouted += single.PlannerRouted;
            routingResult.FallbackNotified += single.FallbackNotified;
            routingResult.InfoLogged += single.InfoLogged;
        }

        routingResult.TotalProcessed = routingResult.PlannerRouted + routingResult.InfoLogged + routingResult.FallbackNotified;
        return routingResult;
    }

    public async Task<StaleAttentionRoutingResult> RouteSingleAsync(
        StaleWorkerCondition condition,
        CancellationToken cancellationToken = default)
    {
        var result = new StaleAttentionRoutingResult();

        if (string.IsNullOrWhiteSpace(condition.ProjectId))
        {
            _logger.LogWarning("Stale condition {Signature} has no project — logged only",
                condition.StaleSignature);
            result.InfoLogged = 1;
            result.TotalProcessed = 1;
            return result;
        }

        var severity = (condition.Severity ?? "warning").ToLowerInvariant();

        // ── Info: log only ──────────────────────────────────────────
        if (severity == "info")
        {
            _logger.LogInformation("Stale worker info: {Classification} in {ProjectId} — {Reason}",
                condition.Classification, condition.ProjectId, condition.StateReason);
            result.InfoLogged = 1;
            result.TotalProcessed = 1;
            return result;
        }

        // ── Critical / Warning: resolve owner + planner ──────────────
        var ownerIdentity = await ResolveProjectOwnerAsync(condition.ProjectId, cancellationToken);

        var plannerCandidates = await _bindings.ListAsync(new AgentInstanceBindingListOptions
        {
            ProjectId = condition.ProjectId,
            Statuses = [AgentInstanceBindingStatus.Active, AgentInstanceBindingStatus.Degraded],
            Role = null,
        });

        var plannerBinding = plannerCandidates.FirstOrDefault(b =>
            b.Role is "planner" or "conductor" or "runner");

        var urgency = severity == "critical" ? "high" : "normal";
        var ownerReachable = ownerIdentity is not null;

        if (plannerBinding is not null)
        {
            // Route to planner; single message encodes both planner/routing
            // and human/operator visibility for critical+owner-missing cases.
            var content = BuildStaleAlertContent(condition, ownerIdentity);
            await _messages.CreateAsync(new Message
            {
                ProjectId = condition.ProjectId,
                TaskId = condition.TaskId,
                Sender = "den-core",
                Content = content,
                Intent = MessageIntent.Notification,
                Metadata = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                {
                    ["type"] = "stale_worker_alert",
                    ["stale_signature"] = condition.StaleSignature,
                    ["classification"] = condition.Classification,
                    ["severity"] = condition.Severity,
                    ["urgency"] = urgency,
                    ["recipient"] = plannerBinding.AgentIdentity,
                    ["target_role"] = plannerBinding.Role,
                    ["recipient_instance_id"] = plannerBinding.InstanceId,
                    ["owner_identity"] = ownerIdentity,
                    ["owner_reachable"] = ownerReachable,
                    ["routed_at"] = DateTime.UtcNow.ToString("o"),
                }),
            });

            result.PlannerRouted = 1;

            // If owner is unreachable, flag as fallback for operator visibility
            if (!ownerReachable)
                result.FallbackNotified = 1;
        }
        else
        {
            // No planner binding — fallback notification regardless of owner
            var content = BuildFallbackAlertContent(condition, ownerIdentity);
            await _messages.CreateAsync(new Message
            {
                ProjectId = condition.ProjectId,
                TaskId = condition.TaskId,
                Sender = "den-core",
                Content = content,
                Intent = MessageIntent.Notification,
                Metadata = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                {
                    ["type"] = "stale_worker_alert_no_owner",
                    ["stale_signature"] = condition.StaleSignature,
                    ["classification"] = condition.Classification,
                    ["severity"] = condition.Severity,
                    ["urgency"] = urgency,
                    ["owner_identity"] = ownerIdentity,
                    ["owner_reachable"] = ownerReachable,
                    ["routed_at"] = DateTime.UtcNow.ToString("o"),
                }),
            });

            result.FallbackNotified = 1;
        }

        result.TotalProcessed = 1;
        return result;
    }

    private async Task<string?> ResolveProjectOwnerAsync(string projectId, CancellationToken cancellationToken)
    {
        try
        {
            var project = await _projects.GetByIdAsync(projectId);
            return project?.Owner;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildStaleAlertContent(StaleWorkerCondition condition, string? owner)
    {
        var ownerLine = owner is not null ? $"- **Project owner**: {owner}" : "- **Project owner**: none";
        return $"## Stale worker detected — {condition.Classification}\n\n"
            + $"- **Severity**: {condition.Severity}\n"
            + $"- **Project**: {condition.ProjectId}\n"
            + (condition.TaskId is not null ? $"- **Task**: #{condition.TaskId}\n" : "")
            + (condition.WorkerIdentity is not null ? $"- **Worker**: {condition.WorkerIdentity}\n" : "")
            + (condition.RunId is not null ? $"- **Run**: {condition.RunId}\n" : "")
            + $"- **State**: {condition.CurrentState ?? "unknown"}\n"
            + $"- **Last activity**: {condition.LastActivityAt ?? "unknown"}\n"
            + ownerLine + "\n"
            + $"- **Reason**: {condition.StateReason}\n"
            + $"- **Suggested action**: {condition.SuggestedNextAction}\n\n"
            + $"*Deduped by signature: `{condition.StaleSignature}`*";
    }

    private static string BuildFallbackAlertContent(StaleWorkerCondition condition, string? owner)
    {
        return $"## Stale worker alert — no planner reachable for `{condition.ProjectId}`\n\n"
            + $"**Classification**: {condition.Classification}\n"
            + $"**Severity**: {condition.Severity}\n"
            + $"**Reason**: {condition.StateReason}\n"
            + $"**Suggested action**: {condition.SuggestedNextAction}\n"
            + (owner is not null ? $"**Project owner**: {owner}\n\n" : "\n")
            + $"*Deduped by signature: `{condition.StaleSignature}`*";
    }
}
