using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Core.Services;

public sealed class AgentStreamMessageCreateRequest
{
    public string? ProjectId { get; set; }
    public int? TaskId { get; set; }
    public int? ThreadId { get; set; }
    public int? DispatchId { get; set; }
    public required string Sender { get; set; }
    public string? SenderInstanceId { get; set; }
    public required string EventType { get; set; }
    public string? RecipientAgent { get; set; }
    public string? RecipientRole { get; set; }
    public string? RecipientInstanceId { get; set; }
    public AgentStreamDeliveryMode? DeliveryMode { get; set; }
    public required string Body { get; set; }
    public JsonElement? Metadata { get; set; }
    public string? DedupKey { get; set; }
}

public sealed class AgentStreamMessageCreateResult
{
    public required AgentStreamEntry Entry { get; set; }
    public AgentRecipientResolution? WakeResolution { get; set; }
}

public interface IAgentStreamMessageService
{
    Task<AgentStreamMessageCreateResult> CreateAsync(AgentStreamMessageCreateRequest request);
}

public sealed class AgentStreamMessageService : IAgentStreamMessageService
{
    private static readonly HashSet<string> AllowedEventTypes =
    [
        "question",
        "answer",
        "note",
        "nudge"
    ];

    private readonly IAgentStreamRepository _stream;
    private readonly IAgentRecipientResolver _resolver;

    public AgentStreamMessageService(IAgentStreamRepository stream, IAgentRecipientResolver resolver)
    {
        _stream = stream;
        _resolver = resolver;
    }

    public async Task<AgentStreamMessageCreateResult> CreateAsync(AgentStreamMessageCreateRequest request)
    {
        var eventType = NormalizeRequired(request.EventType, nameof(request.EventType));
        if (!AllowedEventTypes.Contains(eventType))
            throw new InvalidOperationException(
                $"Unsupported agent stream message event_type '{eventType}'. Allowed values: answer, note, nudge, question.");

        var sender = NormalizeRequired(request.Sender, nameof(request.Sender));
        var body = NormalizeRequired(request.Body, nameof(request.Body));
        var projectId = NormalizeOptional(request.ProjectId);
        var senderInstanceId = NormalizeOptional(request.SenderInstanceId);
        var recipientAgent = NormalizeOptional(request.RecipientAgent);
        var recipientRole = NormalizeOptional(request.RecipientRole);
        var recipientInstanceId = NormalizeOptional(request.RecipientInstanceId);
        var dedupKey = NormalizeOptional(request.DedupKey);
        var deliveryMode = request.DeliveryMode ?? DefaultDeliveryMode(eventType);

        if (recipientAgent is null && recipientRole is null && recipientInstanceId is null)
            throw new InvalidOperationException(
                "Targeted agent stream messages require recipient_agent, recipient_role, or recipient_instance_id.");

        if (recipientRole is not null && projectId is null && recipientInstanceId is null)
            throw new InvalidOperationException(
                "recipient_role requires project_id unless recipient_instance_id is specified.");

        var created = await _stream.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Message,
            EventType = eventType,
            ProjectId = projectId,
            TaskId = request.TaskId,
            ThreadId = request.ThreadId,
            DispatchId = request.DispatchId,
            Sender = sender,
            SenderInstanceId = senderInstanceId,
            RecipientAgent = recipientAgent,
            RecipientRole = recipientRole,
            RecipientInstanceId = recipientInstanceId,
            DeliveryMode = deliveryMode,
            Body = body,
            Metadata = request.Metadata,
            DedupKey = dedupKey
        });

        AgentRecipientResolution? wakeResolution = null;
        if (deliveryMode == AgentStreamDeliveryMode.Wake)
            wakeResolution = await _resolver.ResolveAsync(created);

        return new AgentStreamMessageCreateResult
        {
            Entry = created,
            WakeResolution = wakeResolution
        };
    }

    private static AgentStreamDeliveryMode DefaultDeliveryMode(string eventType) => eventType switch
    {
        "note" => AgentStreamDeliveryMode.RecordOnly,
        _ => AgentStreamDeliveryMode.Notify
    };

    private static string NormalizeRequired(string? value, string paramName)
    {
        var normalized = NormalizeOptional(value);
        return normalized ?? throw new InvalidOperationException($"{paramName} is required.");
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
