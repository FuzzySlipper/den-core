using System.ComponentModel;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Services;
using ModelContextProtocol.Server;

namespace DenMcp.Server.Tools;

[McpServerToolType]
public sealed class RoleWorkerTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    [McpServerTool(Name = "launch_coder_worker"), Description("Prepare or accept a coder context packet reference, then launch a Pi worker with coder role defaults through Den raw worker lifecycle primitives.")]
    public static async Task<string> LaunchCoderWorker(
        ITaskRepository tasks,
        IMessageRepository messages,
        IPiSessionService service,
        [Description("Project ID.")] string project_id,
        [Description("Task ID.")] int task_id,
        [Description("Agent/user launching the worker.")] string requested_by,
        [Description("Optional existing coder context packet message id. If omitted, this tool prepares one first.")] int? prompt_packet_message_id = null,
        [Description("Optional implementation branch/worktree guidance.")] string? branch = null,
        [Description("Optional base branch.")] string? base_branch = null,
        [Description("Optional base commit.")] string? base_commit = null,
        [Description("Optional allowed scope guidance for packet creation.")] string? allowed_scope = null,
        [Description("Optional packet notes.")] string? notes = null,
        [Description("Optional explicit run id.")] string? run_id = null,
        [Description("Optional explicit session id.")] string? session_id = null,
        [Description("Optional workspace id.")] string? workspace_id = null,
        [Description("Optional model hint.")] string? model_hint = null,
        [Description("Optional provider hint.")] string? provider_hint = null,
        [Description("Optional timeout seconds.")] int? timeout_seconds = null,
        [Description("Optional idempotency key for launch.")] string? dedupe_key = null,
        [Description("Optional callback ports JSON array.")] string? callback_ports = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var packetRef = prompt_packet_message_id is int existingId
            ? new PacketRef(existingId, "coder_context_packet", null)
            : await PrepareCoderPacketRef(tasks, messages, project_id, task_id, requested_by, branch, base_branch, base_commit, allowed_scope, notes).ConfigureAwait(false);

        var launchJson = await WorkerTools.LaunchPiWorker(
            service,
            project_id,
            requested_by,
            role: "coder",
            task_id: task_id,
            prompt_packet_message_id: packetRef.MessageId,
            workspace_id: workspace_id,
            branch: branch,
            base_branch: base_branch,
            base_commit: base_commit,
            model_hint: model_hint,
            provider_hint: provider_hint,
            session_mode: "fresh",
            timeout_seconds: timeout_seconds,
            dedupe_key: dedupe_key,
            session_id: session_id,
            run_id: run_id,
            callback_ports: callback_ports,
            verbose: true).ConfigureAwait(false);
        return MergeLaunchWithPacketRef(launchJson, packetRef, "coder");
    }

    [McpServerTool(Name = "launch_reviewer_worker"), Description("Prepare or accept a reviewer context packet reference, then launch a Pi worker with reviewer role defaults through Den raw worker lifecycle primitives.")]
    public static async Task<string> LaunchReviewerWorker(
        ITaskRepository tasks,
        IMessageRepository messages,
        IPiSessionService service,
        [Description("Project ID.")] string project_id,
        [Description("Task ID.")] int task_id,
        [Description("Agent/user launching the worker.")] string requested_by,
        [Description("Optional review round id for packet metadata.")] int? review_round_id = null,
        [Description("Optional existing reviewer context packet message id. If omitted, this tool prepares one first.")] int? prompt_packet_message_id = null,
        [Description("Optional branch under review.")] string? branch = null,
        [Description("Optional base branch.")] string? base_branch = null,
        [Description("Optional base commit.")] string? base_commit = null,
        [Description("Optional head commit under review.")] string? head_commit = null,
        [Description("Optional allowed scope guidance for packet creation.")] string? allowed_scope = null,
        [Description("Optional packet notes.")] string? notes = null,
        [Description("Optional explicit run id.")] string? run_id = null,
        [Description("Optional explicit session id.")] string? session_id = null,
        [Description("Optional workspace id.")] string? workspace_id = null,
        [Description("Optional model hint.")] string? model_hint = null,
        [Description("Optional provider hint.")] string? provider_hint = null,
        [Description("Optional timeout seconds.")] int? timeout_seconds = null,
        [Description("Optional idempotency key for launch.")] string? dedupe_key = null,
        [Description("Optional callback ports JSON array.")] string? callback_ports = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var packetRef = prompt_packet_message_id is int existingId
            ? new PacketRef(existingId, "reviewer_context_packet", review_round_id)
            : await PrepareReviewerPacketRef(tasks, messages, project_id, task_id, requested_by, review_round_id, branch, base_branch, base_commit, head_commit, allowed_scope, notes).ConfigureAwait(false);

        var launchJson = await WorkerTools.LaunchPiWorker(
            service,
            project_id,
            requested_by,
            role: "reviewer",
            task_id: task_id,
            prompt_packet_message_id: packetRef.MessageId,
            workspace_id: workspace_id,
            branch: branch,
            base_branch: base_branch,
            base_commit: base_commit,
            head_commit: head_commit,
            model_hint: model_hint,
            provider_hint: provider_hint,
            session_mode: "fresh",
            timeout_seconds: timeout_seconds,
            dedupe_key: dedupe_key,
            session_id: session_id,
            run_id: run_id,
            callback_ports: callback_ports,
            verbose: true).ConfigureAwait(false);
        return MergeLaunchWithPacketRef(launchJson, packetRef, "reviewer");
    }



    [McpServerTool(Name = "launch_validator_worker"), Description("Prepare or accept a validator context packet reference, then launch a Pi worker with validator role defaults.")]
    public static async Task<string> LaunchValidatorWorker(
        ITaskRepository tasks,
        IMessageRepository messages,
        IPiSessionService service,
        [Description("Project ID.")] string project_id,
        [Description("Task ID.")] int task_id,
        [Description("Agent/user launching the worker.")] string requested_by,
        [Description("Optional existing validator context packet message id. If omitted, this tool prepares one first.")] int? prompt_packet_message_id = null,
        [Description("Optional branch under validation.")] string? branch = null,
        [Description("Optional base branch.")] string? base_branch = null,
        [Description("Optional base commit.")] string? base_commit = null,
        [Description("Optional head commit under validation.")] string? head_commit = null,
        [Description("Optional allowed scope guidance for packet creation.")] string? allowed_scope = null,
        [Description("Optional packet notes.")] string? notes = null,
        [Description("Optional explicit run id.")] string? run_id = null,
        [Description("Optional explicit session id.")] string? session_id = null,
        [Description("Optional workspace id.")] string? workspace_id = null,
        [Description("Optional model hint.")] string? model_hint = null,
        [Description("Optional provider hint.")] string? provider_hint = null,
        [Description("Optional timeout seconds.")] int? timeout_seconds = null,
        [Description("Optional idempotency key for launch.")] string? dedupe_key = null,
        [Description("Optional callback ports JSON array.")] string? callback_ports = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var packetRef = prompt_packet_message_id is int existingId
            ? new PacketRef(existingId, "validator_context_packet", null)
            : await PrepareValidatorPacketRef(tasks, messages, project_id, task_id, requested_by, branch, base_branch, base_commit, head_commit, allowed_scope, notes).ConfigureAwait(false);
        return await LaunchSpecializedWorker(service, project_id, requested_by, task_id, "validator", packetRef, branch, base_branch, base_commit, head_commit, run_id, session_id, workspace_id, model_hint, provider_hint, timeout_seconds, dedupe_key, callback_ports).ConfigureAwait(false);
    }

    [McpServerTool(Name = "launch_drift_checker_worker"), Description("Prepare or accept a drift-checker context packet reference, then launch a Pi worker with drift_checker role defaults.")]
    public static async Task<string> LaunchDriftCheckerWorker(
        ITaskRepository tasks,
        IMessageRepository messages,
        IPiSessionService service,
        [Description("Project ID.")] string project_id,
        [Description("Task ID.")] int task_id,
        [Description("Agent/user launching the worker.")] string requested_by,
        [Description("Optional existing drift-checker context packet message id. If omitted, this tool prepares one first.")] int? prompt_packet_message_id = null,
        [Description("Optional branch under drift check.")] string? branch = null,
        [Description("Optional base branch.")] string? base_branch = null,
        [Description("Optional base commit.")] string? base_commit = null,
        [Description("Optional head commit under drift check.")] string? head_commit = null,
        [Description("Optional allowed scope guidance for packet creation.")] string? allowed_scope = null,
        [Description("Optional packet notes.")] string? notes = null,
        [Description("Optional explicit run id.")] string? run_id = null,
        [Description("Optional explicit session id.")] string? session_id = null,
        [Description("Optional workspace id.")] string? workspace_id = null,
        [Description("Optional model hint.")] string? model_hint = null,
        [Description("Optional provider hint.")] string? provider_hint = null,
        [Description("Optional timeout seconds.")] int? timeout_seconds = null,
        [Description("Optional idempotency key for launch.")] string? dedupe_key = null,
        [Description("Optional callback ports JSON array.")] string? callback_ports = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var packetRef = prompt_packet_message_id is int existingId
            ? new PacketRef(existingId, "drift_checker_context_packet", null)
            : await PrepareDriftCheckerPacketRef(tasks, messages, project_id, task_id, requested_by, branch, base_branch, base_commit, head_commit, allowed_scope, notes).ConfigureAwait(false);
        return await LaunchSpecializedWorker(service, project_id, requested_by, task_id, "drift_checker", packetRef, branch, base_branch, base_commit, head_commit, run_id, session_id, workspace_id, model_hint, provider_hint, timeout_seconds, dedupe_key, callback_ports).ConfigureAwait(false);
    }

    [McpServerTool(Name = "launch_packet_auditor_worker"), Description("Prepare or accept a packet-auditor context packet reference, then launch a Pi worker with packet_auditor role defaults.")]
    public static async Task<string> LaunchPacketAuditorWorker(
        ITaskRepository tasks,
        IMessageRepository messages,
        IPiSessionService service,
        [Description("Project ID.")] string project_id,
        [Description("Task ID.")] int task_id,
        [Description("Agent/user launching the worker.")] string requested_by,
        [Description("Optional existing packet-auditor context packet message id. If omitted, this tool prepares one first.")] int? prompt_packet_message_id = null,
        [Description("Optional branch under packet audit.")] string? branch = null,
        [Description("Optional base branch.")] string? base_branch = null,
        [Description("Optional base commit.")] string? base_commit = null,
        [Description("Optional head commit under packet audit.")] string? head_commit = null,
        [Description("Optional allowed scope guidance for packet creation.")] string? allowed_scope = null,
        [Description("Optional packet notes.")] string? notes = null,
        [Description("Optional explicit run id.")] string? run_id = null,
        [Description("Optional explicit session id.")] string? session_id = null,
        [Description("Optional workspace id.")] string? workspace_id = null,
        [Description("Optional model hint.")] string? model_hint = null,
        [Description("Optional provider hint.")] string? provider_hint = null,
        [Description("Optional timeout seconds.")] int? timeout_seconds = null,
        [Description("Optional idempotency key for launch.")] string? dedupe_key = null,
        [Description("Optional callback ports JSON array.")] string? callback_ports = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var packetRef = prompt_packet_message_id is int existingId
            ? new PacketRef(existingId, "packet_auditor_context_packet", null)
            : await PreparePacketAuditorPacketRef(tasks, messages, project_id, task_id, requested_by, branch, base_branch, base_commit, head_commit, allowed_scope, notes).ConfigureAwait(false);
        return await LaunchSpecializedWorker(service, project_id, requested_by, task_id, "packet_auditor", packetRef, branch, base_branch, base_commit, head_commit, run_id, session_id, workspace_id, model_hint, provider_hint, timeout_seconds, dedupe_key, callback_ports).ConfigureAwait(false);
    }



    private static async Task<string> LaunchSpecializedWorker(
        IPiSessionService service,
        string projectId,
        string requestedBy,
        int taskId,
        string role,
        PacketRef packetRef,
        string? branch,
        string? baseBranch,
        string? baseCommit,
        string? headCommit,
        string? runId,
        string? sessionId,
        string? workspaceId,
        string? modelHint,
        string? providerHint,
        int? timeoutSeconds,
        string? dedupeKey,
        string? callbackPorts)
    {
        var launchJson = await WorkerTools.LaunchPiWorker(
            service,
            projectId,
            requestedBy,
            role,
            taskId,
            packetRef.MessageId,
            workspace_id: workspaceId,
            branch: branch,
            base_branch: baseBranch,
            base_commit: baseCommit,
            head_commit: headCommit,
            model_hint: modelHint,
            provider_hint: providerHint,
            session_mode: "fresh",
            timeout_seconds: timeoutSeconds,
            dedupe_key: dedupeKey,
            session_id: sessionId,
            run_id: runId,
            callback_ports: callbackPorts,
            verbose: true).ConfigureAwait(false);
        return MergeLaunchWithPacketRef(launchJson, packetRef, role);
    }

    private static async Task<PacketRef> PrepareValidatorPacketRef(ITaskRepository tasks, IMessageRepository messages, string projectId, int taskId, string requestedBy, string? branch, string? baseBranch, string? baseCommit, string? headCommit, string? allowedScope, string? notes)
    {
        var packetJson = await PacketTools.PrepareValidatorContextPacket(tasks, messages, projectId, taskId, requestedBy, branch, baseBranch, baseCommit, headCommit, allowedScope, notes, verbose: true).ConfigureAwait(false);
        return ParsePacketRef(packetJson, "validator_context_packet");
    }

    private static async Task<PacketRef> PrepareDriftCheckerPacketRef(ITaskRepository tasks, IMessageRepository messages, string projectId, int taskId, string requestedBy, string? branch, string? baseBranch, string? baseCommit, string? headCommit, string? allowedScope, string? notes)
    {
        var packetJson = await PacketTools.PrepareDriftCheckerContextPacket(tasks, messages, projectId, taskId, requestedBy, branch, baseBranch, baseCommit, headCommit, allowedScope, notes, verbose: true).ConfigureAwait(false);
        return ParsePacketRef(packetJson, "drift_checker_context_packet");
    }

    private static async Task<PacketRef> PreparePacketAuditorPacketRef(ITaskRepository tasks, IMessageRepository messages, string projectId, int taskId, string requestedBy, string? branch, string? baseBranch, string? baseCommit, string? headCommit, string? allowedScope, string? notes)
    {
        var packetJson = await PacketTools.PreparePacketAuditorContextPacket(tasks, messages, projectId, taskId, requestedBy, branch, baseBranch, baseCommit, headCommit, allowedScope, notes, verbose: true).ConfigureAwait(false);
        return ParsePacketRef(packetJson, "packet_auditor_context_packet");
    }

    private static async Task<PacketRef> PrepareCoderPacketRef(ITaskRepository tasks, IMessageRepository messages, string projectId, int taskId, string requestedBy, string? branch, string? baseBranch, string? baseCommit, string? allowedScope, string? notes)
    {
        var packetJson = await PacketTools.PrepareCoderContextPacket(
            tasks,
            messages,
            projectId,
            taskId,
            requestedBy,
            branch,
            baseBranch,
            baseCommit,
            allowedScope,
            notes,
            verbose: true).ConfigureAwait(false);
        return ParsePacketRef(packetJson, "coder_context_packet");
    }

    private static async Task<PacketRef> PrepareReviewerPacketRef(ITaskRepository tasks, IMessageRepository messages, string projectId, int taskId, string requestedBy, int? reviewRoundId, string? branch, string? baseBranch, string? baseCommit, string? headCommit, string? allowedScope, string? notes)
    {
        var packetJson = await PacketTools.PrepareReviewerContextPacket(
            tasks,
            messages,
            projectId,
            taskId,
            requestedBy,
            reviewRoundId,
            branch,
            baseBranch,
            baseCommit,
            headCommit,
            allowedScope,
            notes,
            verbose: true).ConfigureAwait(false);
        return ParsePacketRef(packetJson, "reviewer_context_packet");
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

    private static string MergeLaunchWithPacketRef(string launchJson, PacketRef packetRef, string role)
    {
        using var doc = JsonDocument.Parse(launchJson);
        if (doc.RootElement.TryGetProperty("error", out var error))
            return JsonSerializer.Serialize(new { error = error.GetString(), packet_ref = packetRef }, JsonOptions);
        var root = doc.RootElement;
        var result = new
        {
            summary = $"launched {role} worker {root.GetProperty("worker_run").GetProperty("run_id").GetString()}",
            idempotency = root.GetProperty("idempotency"),
            packet_ref = new
            {
                kind = "task_message",
                message_id = packetRef.MessageId,
                packet_type = packetRef.PacketType,
                review_round_id = packetRef.ReviewRoundId,
            },
            worker_run = root.GetProperty("worker_run"),
            recovery_guidance = "Monitor with get_worker_run/get_latest_worker_completion; process exit without a completion packet is not success."
        };
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private sealed record PacketRef(int MessageId, string PacketType, int? ReviewRoundId);
}
