using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Core.Services;

public interface IAgentRecipientResolver
{
    Task<AgentRecipientResolution> ResolveAsync(AgentStreamEntry entry, bool recordFailures = true);
}

public sealed class AgentRecipientResolver : IAgentRecipientResolver
{
    private readonly IAgentInstanceBindingRepository _bindings;
    private readonly IAgentStreamRepository _stream;

    public AgentRecipientResolver(IAgentInstanceBindingRepository bindings, IAgentStreamRepository stream)
    {
        _bindings = bindings;
        _stream = stream;
    }

    public async Task<AgentRecipientResolution> ResolveAsync(AgentStreamEntry entry, bool recordFailures = true)
    {
        AgentRecipientResolution result;

        if (!string.IsNullOrWhiteSpace(entry.RecipientInstanceId))
        {
            var binding = await _bindings.GetActiveByInstanceIdAsync(entry.RecipientInstanceId);
            result = binding is not null
                ? Resolved(binding)
                : MissingBinding($"No active binding found for instance '{entry.RecipientInstanceId}'.");
        }
        else if (!string.IsNullOrWhiteSpace(entry.ProjectId) && !string.IsNullOrWhiteSpace(entry.RecipientRole))
        {
            var candidates = await _bindings.ListAsync(new AgentInstanceBindingListOptions
            {
                ProjectId = entry.ProjectId,
                Role = entry.RecipientRole,
                Statuses =
                [
                    AgentInstanceBindingStatus.Active,
                    AgentInstanceBindingStatus.Degraded
                ]
            });

            result = ResolveCandidates(
                candidates,
                $"No active binding found for role '{entry.RecipientRole}' in project '{entry.ProjectId}'.",
                $"Multiple active bindings found for role '{entry.RecipientRole}' in project '{entry.ProjectId}'.");
        }
        else if (!string.IsNullOrWhiteSpace(entry.ProjectId) && !string.IsNullOrWhiteSpace(entry.RecipientAgent))
        {
            var candidates = await _bindings.ListAsync(new AgentInstanceBindingListOptions
            {
                ProjectId = entry.ProjectId,
                AgentIdentity = entry.RecipientAgent,
                Statuses =
                [
                    AgentInstanceBindingStatus.Active,
                    AgentInstanceBindingStatus.Degraded
                ]
            });

            result = ResolveCandidates(
                candidates,
                $"No active binding found for agent '{entry.RecipientAgent}' in project '{entry.ProjectId}'.",
                $"Multiple active bindings found for agent '{entry.RecipientAgent}' in project '{entry.ProjectId}'.");
        }
        else if (!string.IsNullOrWhiteSpace(entry.RecipientAgent))
        {
            var candidates = await _bindings.ListAsync(new AgentInstanceBindingListOptions
            {
                AgentIdentity = entry.RecipientAgent,
                Statuses =
                [
                    AgentInstanceBindingStatus.Active,
                    AgentInstanceBindingStatus.Degraded
                ]
            });

            result = ResolveCandidates(
                candidates,
                $"No active binding found for agent '{entry.RecipientAgent}'.",
                $"Multiple active bindings found for agent '{entry.RecipientAgent}'.");
        }
        else
        {
            result = new AgentRecipientResolution
            {
                Status = AgentRecipientResolutionStatus.MissingRecipient,
                Reason = "Recipient resolution requires recipient_instance_id or project-scoped recipient_role/recipient_agent."
            };
        }

        if (recordFailures && entry.DeliveryMode == AgentStreamDeliveryMode.Wake && result.Status != AgentRecipientResolutionStatus.Resolved)
            result.RecordedAgentStreamEntryId = (await RecordWakeDropAsync(entry, result)).Id;

        return result;
    }

    private static AgentRecipientResolution Resolved(AgentInstanceBinding binding) => new()
    {
        Status = AgentRecipientResolutionStatus.Resolved,
        Binding = binding
    };

    private static AgentRecipientResolution MissingBinding(string reason) => new()
    {
        Status = AgentRecipientResolutionStatus.MissingBinding,
        Reason = reason
    };

    private static AgentRecipientResolution ResolveCandidates(
        List<AgentInstanceBinding> candidates,
        string missingReason,
        string ambiguousReason)
    {
        return candidates.Count switch
        {
            0 => new AgentRecipientResolution
            {
                Status = AgentRecipientResolutionStatus.MissingBinding,
                Reason = missingReason
            },
            1 => Resolved(candidates[0]),
            _ => new AgentRecipientResolution
            {
                Status = AgentRecipientResolutionStatus.Ambiguous,
                Reason = ambiguousReason,
                CandidateInstanceIds = candidates.Select(candidate => candidate.InstanceId).ToList()
            }
        };
    }

    private async Task<AgentStreamEntry> RecordWakeDropAsync(AgentStreamEntry sourceEntry, AgentRecipientResolution resolution)
    {
        var statusValue = resolution.Status.ToApiValue();
        var metadata = JsonSerializer.SerializeToElement(new
        {
            source_entry_id = sourceEntry.Id > 0 ? sourceEntry.Id : (int?)null,
            resolution_status = statusValue,
            candidate_instance_ids = resolution.CandidateInstanceIds
        });

        return await _stream.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "wake_dropped",
            ProjectId = sourceEntry.ProjectId,
            TaskId = sourceEntry.TaskId,
            ThreadId = sourceEntry.ThreadId,
            DispatchId = sourceEntry.DispatchId,
            Sender = "den",
            RecipientAgent = sourceEntry.RecipientAgent,
            RecipientRole = sourceEntry.RecipientRole,
            RecipientInstanceId = sourceEntry.RecipientInstanceId,
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Body = resolution.Reason,
            Metadata = metadata,
            DedupKey = sourceEntry.Id > 0
                ? $"wake-dropped:{sourceEntry.Id}:{statusValue}"
                : null
        });
    }

}
