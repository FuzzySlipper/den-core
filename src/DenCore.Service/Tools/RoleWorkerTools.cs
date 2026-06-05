using System.ComponentModel;
using DenCore.Mcp;
using System.Text.Json;
using DenCore.Data;
using ModelContextProtocol.Server;

namespace DenCore.Service.Tools;

[McpServerToolType]
public sealed class RoleWorkerTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Prepare a coder context packet and return the packet reference.
    /// Use register_worker_run for the worker registration step.
    /// </summary>
    public static async Task<PacketRef> PrepareCoderPacketRef(ITaskRepository tasks, IMessageRepository messages, string projectId, int taskId, string requestedBy, string? branch, string? baseBranch, string? baseCommit, string? allowedScope, string? notes, string completionReportingMode)
    {
        var packetJson = await PacketTools.PrepareCoderContextPacket(
            tasks, messages, projectId, taskId, requestedBy,
            branch, baseBranch, baseCommit, allowedScope, notes,
            completionReportingMode, verbose: true).ConfigureAwait(false);
        return ParsePacketRef(packetJson, "coder_context_packet");
    }

    /// <summary>
    /// Prepare a reviewer context packet and return the packet reference.
    /// Use register_worker_run for the worker registration step.
    /// </summary>
    public static async Task<PacketRef> PrepareReviewerPacketRef(ITaskRepository tasks, IMessageRepository messages, string projectId, int taskId, string requestedBy, int? reviewRoundId, string? branch, string? baseBranch, string? baseCommit, string? headCommit, string? allowedScope, string? notes, string completionReportingMode)
    {
        var packetJson = await PacketTools.PrepareReviewerContextPacket(
            tasks, messages, projectId, taskId, requestedBy,
            reviewRoundId, branch, baseBranch, baseCommit, headCommit, allowedScope, notes,
            completionReportingMode, verbose: true).ConfigureAwait(false);
        return ParsePacketRef(packetJson, "reviewer_context_packet");
    }

    /// <summary>
    /// Prepare a validator context packet and return the packet reference.
    /// Use register_worker_run for the worker registration step.
    /// </summary>
    public static async Task<PacketRef> PrepareValidatorPacketRef(ITaskRepository tasks, IMessageRepository messages, string projectId, int taskId, string requestedBy, string? branch, string? baseBranch, string? baseCommit, string? headCommit, string? allowedScope, string? notes, string completionReportingMode)
    {
        var packetJson = await PacketTools.PrepareValidatorContextPacket(tasks, messages, projectId, taskId, requestedBy, branch, baseBranch, baseCommit, headCommit, allowedScope, notes, completionReportingMode, verbose: true).ConfigureAwait(false);
        return ParsePacketRef(packetJson, "validator_context_packet");
    }

    /// <summary>
    /// Prepare a drift-checker context packet and return the packet reference.
    /// Use register_worker_run for the worker registration step.
    /// </summary>
    public static async Task<PacketRef> PrepareDriftCheckerPacketRef(ITaskRepository tasks, IMessageRepository messages, string projectId, int taskId, string requestedBy, string? branch, string? baseBranch, string? baseCommit, string? headCommit, string? allowedScope, string? notes, string completionReportingMode)
    {
        var packetJson = await PacketTools.PrepareDriftCheckerContextPacket(tasks, messages, projectId, taskId, requestedBy, branch, baseBranch, baseCommit, headCommit, allowedScope, notes, completionReportingMode, verbose: true).ConfigureAwait(false);
        return ParsePacketRef(packetJson, "drift_checker_context_packet");
    }

    /// <summary>
    /// Prepare a packet-auditor context packet and return the packet reference.
    /// Use register_worker_run for the worker registration step.
    /// </summary>
    public static async Task<PacketRef> PreparePacketAuditorPacketRef(ITaskRepository tasks, IMessageRepository messages, string projectId, int taskId, string requestedBy, string? branch, string? baseBranch, string? baseCommit, string? headCommit, string? allowedScope, string? notes, string completionReportingMode)
    {
        var packetJson = await PacketTools.PreparePacketAuditorContextPacket(tasks, messages, projectId, taskId, requestedBy, branch, baseBranch, baseCommit, headCommit, allowedScope, notes, completionReportingMode, verbose: true).ConfigureAwait(false);
        return ParsePacketRef(packetJson, "packet_auditor_context_packet");
    }

    private static PacketRef ParsePacketRef(string packetJson, string fallbackType)
    {
        using var doc = JsonDocument.Parse(packetJson);
        if (doc.RootElement.TryGetProperty("error", out var error))
            throw new InvalidOperationException(error.GetString() ?? "Packet preparation failed.");
        var packet = doc.RootElement.GetProperty("packet");
        var messageId = packet.GetProperty("message_id").GetInt32();
        var packetType = packet.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? fallbackType : fallbackType;
        int? reviewRoundId = null;
        if (packet.TryGetProperty("metadata", out var metadata)
            && metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty("review_round_id", out var rr)
            && rr.ValueKind == JsonValueKind.Number
            && rr.TryGetInt32(out var parsed))
        {
            reviewRoundId = parsed;
        }
        return new PacketRef(messageId, packetType, reviewRoundId);
    }

    public sealed record PacketRef(int MessageId, string PacketType, int? ReviewRoundId);
}
