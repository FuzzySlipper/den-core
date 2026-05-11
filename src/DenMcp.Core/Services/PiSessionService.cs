using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Core.Services;

public interface IPiSessionService
{
    Task<PiSessionDetail> LaunchAsync(string projectId, PiSessionLaunchRequest request, CancellationToken cancellationToken = default);
    Task<List<PiSessionSummary>> ListAsync(PiSessionListOptions options, CancellationToken cancellationToken = default);
    Task<PiSessionDetail?> GetAsync(string projectId, string sessionId, CancellationToken cancellationToken = default);
    Task<PiSessionDetail?> TerminateAsync(string projectId, string sessionId, PiSessionControlRequest request, CancellationToken cancellationToken = default);
    Task<PiSessionDetail?> CleanupAsync(string projectId, string sessionId, PiSessionControlRequest request, CancellationToken cancellationToken = default);
    Task<PiSessionAttachInfo?> GetAttachInfoAsync(string projectId, string sessionId, PiSessionAttachRequest request, CancellationToken cancellationToken = default);
}

public sealed class PiSessionService : IPiSessionService
{
    private const string LaunchProfileKind = "pi_docker_compose";
    private const int OutputTailMaxChars = 12000;
    private static readonly TimeSpan StalledActivityThreshold = TimeSpan.FromMinutes(30);
    private readonly IPiSessionRepository _sessions;
    private readonly IPiDockerLaunchProfileRenderer _renderer;
    private readonly ITaskRepository _tasks;
    private readonly IPiSessionHost _host;
    private readonly IAgentStreamOpsService _ops;
    private readonly PiDockerLaunchProfileOptions _options;
    private readonly Func<DateTime> _utcNow;

    public PiSessionService(
        IPiSessionRepository sessions,
        IPiDockerLaunchProfileRenderer renderer,
        ITaskRepository tasks,
        IPiSessionHost host,
        IAgentStreamOpsService ops,
        PiDockerLaunchProfileOptions options,
        Func<DateTime>? utcNow = null)
    {
        _sessions = sessions;
        _renderer = renderer;
        _tasks = tasks;
        _host = host;
        _ops = ops;
        _options = options;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public async Task<PiSessionDetail> LaunchAsync(string projectId, PiSessionLaunchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException("project_id is required.");
        if (request.TaskId is null)
            throw new InvalidOperationException("task_id is required for Den-owned Pi sessions.");
        var task = await _tasks.GetByIdAsync(request.TaskId.Value).ConfigureAwait(false);
        if (task is null || !string.Equals(task.ProjectId, projectId.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException($"task_id {request.TaskId.Value} was not found in project {projectId.Trim()}.");

        var sessionId = NormalizeIdentifier(request.SessionId) ?? PiSessionNaming.NewSessionId();
        var requestedBy = NormalizeText(request.RequestedBy) ?? "den-api";
        var profile = _renderer.Render(new PiDockerLaunchRenderRequest
        {
            ProjectId = projectId.Trim(),
            SessionId = sessionId,
            TaskId = request.TaskId,
            WorkspaceId = request.WorkspaceId,
            Title = request.Title,
            DevDir = request.DevDir,
            PiStateDir = request.PiStateDir,
            ComposeFile = request.ComposeFile,
            Service = request.Service,
            Image = request.Image,
            PiVersion = request.PiVersion,
            NodeVersion = request.NodeVersion,
            GitConfigPath = request.GitConfigPath,
            SshDir = request.SshDir,
            GhConfigDir = request.GhConfigDir,
            CallbackPorts = request.CallbackPorts,
            WorkerRole = request.ToolProfile,
            WorkerRunId = request.RunId,
            PromptPacketMessageId = request.PromptPacketMessageId,
            StateFileRef = request.StateFileRef,
            StartupPrompt = request.StartupPrompt,
            TimeoutSeconds = request.TimeoutSeconds,
        });

        var launchCommand = BuildLaunchCommand(_options.DockerExecutable, profile);
        var now = _utcNow();
        var record = new PiSessionRecord
        {
            SessionId = sessionId,
            ProjectId = projectId.Trim(),
            TaskId = request.TaskId,
            WorkspaceId = NormalizeText(request.WorkspaceId),
            RunId = NormalizeText(request.RunId),
            Title = NormalizeText(request.Title),
            ToolProfile = NormalizeText(request.ToolProfile),
            Model = NormalizeText(request.Model),
            Provider = NormalizeText(request.Provider),
            HostId = _host.HostId,
            TmuxSessionName = PiSessionNaming.CreateTmuxSessionName(projectId, sessionId),
            ContainerName = PiSessionContainerNames.Extract(profile),
            State = PiSessionStates.Launching,
            LaunchProfileKind = LaunchProfileKind,
            LaunchProfileId = profile.ProfileId,
            LaunchProfileJson = JsonSerializer.Serialize(profile, PiSessionJson.Options),
            LaunchCommandJson = JsonSerializer.Serialize(launchCommand, PiSessionJson.Options),
            LaunchCommandDisplay = string.Join(" ", launchCommand),
            CreatedAt = now,
            UpdatedAt = now,
        };

        record = await _sessions.CreateAsync(record).ConfigureAwait(false);
        await AuditAsync(record, "pi_session_launch_requested", requestedBy, null, new { profile.ProfileId, request.ToolProfile, request.Model, request.Provider }, cancellationToken).ConfigureAwait(false);

        var launch = await _host.LaunchAsync(new PiSessionLaunchPlan
        {
            Record = record,
            LaunchProfile = profile,
            LaunchCommand = launchCommand,
        }, cancellationToken).ConfigureAwait(false);

        record = await _sessions.UpdateStateAsync(
            record.ProjectId,
            record.SessionId,
            launch.State,
            launch.StateReason,
            launch.StartedAt,
            launch.LastActivityAt,
            launch.State == PiSessionStates.Failed ? _utcNow() : null,
            launch.ContainerId,
            launch.ContainerName).ConfigureAwait(false);

        await AuditAsync(
            record,
            launch.State == PiSessionStates.Failed ? "pi_session_launch_failed" : "pi_session_started",
            requestedBy,
            launch.StateReason,
            new { record.HostId, record.TmuxSessionName, record.ContainerName, record.LaunchProfileId },
            cancellationToken).ConfigureAwait(false);

        return ToDetail(record, profile);
    }

    public async Task<List<PiSessionSummary>> ListAsync(PiSessionListOptions options, CancellationToken cancellationToken = default)
    {
        var requestedLimit = Math.Clamp(options.Limit, 1, 200);
        var hasAttentionFilter = !string.IsNullOrWhiteSpace(options.AttentionState) || options.NeedsUserInput is not null;
        var records = await _sessions.ListAsync(new PiSessionListOptions
        {
            ProjectId = options.ProjectId,
            TaskId = options.TaskId,
            State = options.State,
            Limit = hasAttentionFilter ? 200 : requestedLimit,
        }).ConfigureAwait(false);

        var refreshed = new List<PiSessionSummary>(records.Count);
        foreach (var record in records)
        {
            refreshed.Add(ToSummary(await RefreshIfActiveAsync(record, cancellationToken).ConfigureAwait(false)));
        }

        if (!hasAttentionFilter)
            return refreshed;

        IEnumerable<PiSessionSummary> filtered = refreshed;
        var attentionState = NormalizeText(options.AttentionState);
        if (attentionState is not null)
            filtered = filtered.Where(session => string.Equals(session.AttentionState, attentionState, StringComparison.Ordinal));
        if (options.NeedsUserInput is { } needsUserInput)
            filtered = filtered.Where(session => session.NeedsUserInput == needsUserInput);
        return filtered.Take(requestedLimit).ToList();
    }

    public async Task<PiSessionDetail?> GetAsync(string projectId, string sessionId, CancellationToken cancellationToken = default)
    {
        var record = await _sessions.GetAsync(projectId, sessionId).ConfigureAwait(false);
        if (record is null)
            return null;
        record = await RefreshIfActiveAsync(record, cancellationToken).ConfigureAwait(false);
        return ToDetail(record, DeserializeProfile(record));
    }

    public async Task<PiSessionDetail?> TerminateAsync(string projectId, string sessionId, PiSessionControlRequest request, CancellationToken cancellationToken = default)
    {
        var record = await _sessions.GetAsync(projectId, sessionId).ConfigureAwait(false);
        if (record is null)
            return null;
        if (record.State == PiSessionStates.Completed)
            throw new InvalidOperationException($"Pi session {sessionId} is already completed.");

        var requestedBy = NormalizeText(request.RequestedBy) ?? "den-api";
        record = await _sessions.MarkTerminationRequestedAsync(projectId, sessionId, requestedBy, request.Reason).ConfigureAwait(false);
        await AuditAsync(record, "pi_session_terminate_requested", requestedBy, request.Reason, null, cancellationToken).ConfigureAwait(false);

        var terminated = await _host.TerminateAsync(record, cancellationToken).ConfigureAwait(false);
        record = await _sessions.UpdateStateAsync(
            projectId,
            sessionId,
            terminated.State,
            terminated.StateReason,
            endedAt: terminated.EndedAt ?? (terminated.Succeeded ? _utcNow() : null)).ConfigureAwait(false);
        await AuditAsync(record,
            terminated.Succeeded ? "pi_session_terminate_completed" : "pi_session_terminate_failed",
            requestedBy,
            terminated.StateReason,
            null,
            cancellationToken).ConfigureAwait(false);

        return ToDetail(record, DeserializeProfile(record));
    }

    public async Task<PiSessionDetail?> CleanupAsync(string projectId, string sessionId, PiSessionControlRequest request, CancellationToken cancellationToken = default)
    {
        var record = await _sessions.GetAsync(projectId, sessionId).ConfigureAwait(false);
        if (record is null)
            return null;
        if (PiSessionStates.IsActive(record.State))
            throw new InvalidOperationException($"Pi session {sessionId} is {record.State}; terminate it before cleanup.");

        var requestedBy = NormalizeText(request.RequestedBy) ?? "den-api";
        record = await _sessions.MarkCleanupRequestedAsync(projectId, sessionId, requestedBy, request.Reason).ConfigureAwait(false);
        await AuditAsync(record, "pi_session_cleanup_requested", requestedBy, request.Reason, null, cancellationToken).ConfigureAwait(false);

        var profile = DeserializeProfile(record);
        var cleanup = await _host.CleanupAsync(record, profile, cancellationToken).ConfigureAwait(false);
        if (!cleanup.Succeeded)
        {
            record = await _sessions.UpdateStateAsync(projectId, sessionId, cleanup.State, cleanup.StateReason).ConfigureAwait(false);
            await AuditAsync(record, "pi_session_cleanup_failed", requestedBy, cleanup.StateReason, null, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(cleanup.StateReason ?? "Pi session cleanup failed.");
        }

        record = await _sessions.MarkCleanupCompletedAsync(projectId, sessionId, cleanup.StateReason).ConfigureAwait(false);
        await AuditAsync(record, "pi_session_cleanup_completed", requestedBy, cleanup.StateReason, null, cancellationToken).ConfigureAwait(false);
        return ToDetail(record, profile);
    }

    public async Task<PiSessionAttachInfo?> GetAttachInfoAsync(string projectId, string sessionId, PiSessionAttachRequest request, CancellationToken cancellationToken = default)
    {
        var record = await _sessions.GetAsync(projectId, sessionId).ConfigureAwait(false);
        if (record is null)
            return null;
        record = await RefreshIfActiveAsync(record, cancellationToken).ConfigureAwait(false);
        if (record.State != PiSessionStates.Running)
            throw new InvalidOperationException($"Pi session {sessionId} is {record.State}; only running sessions expose attach info.");

        var requestedBy = NormalizeText(request.RequestedBy) ?? "den-api";
        await AuditAsync(record, "pi_session_attach_info_requested", requestedBy, null, new { mode = request.Mode ?? "external_attach_info" }, cancellationToken).ConfigureAwait(false);
        return BuildAttachInfo(record);
    }

    private async Task<PiSessionRecord> RefreshIfActiveAsync(PiSessionRecord record, CancellationToken cancellationToken)
    {
        if (!PiSessionStates.IsActive(record.State))
            return record;

        var status = await _host.GetStatusAsync(record, cancellationToken).ConfigureAwait(false);
        var attention = DetectAttention(status);
        var outputTail = NormalizeOutputTail(status.OutputTail);
        var outputCaptured = outputTail is not null;
        var outputSha256 = outputCaptured
            ? status.OutputTailSha256 ?? ComputeSha256(outputTail!)
            : null;
        var outputChanged = outputCaptured && !string.Equals(outputSha256, record.OutputTailSha256, StringComparison.Ordinal);
        var attentionChanged = !string.Equals(attention.State, record.AttentionState, StringComparison.Ordinal)
            || attention.NeedsUserInput != record.NeedsUserInput;
        var stateChanged = status.State != record.State;
        var stateReasonChanged = !string.Equals(status.StateReason, record.StateReason, StringComparison.Ordinal);
        var lastActivityChanged = status.LastActivityAt is { } observedActivity
            && (record.LastActivityAt is null || observedActivity.ToUniversalTime() > record.LastActivityAt.Value.ToUniversalTime());
        var containerIdChanged = status.ContainerId is not null && !string.Equals(status.ContainerId, record.ContainerId, StringComparison.Ordinal);
        var containerNameChanged = status.ContainerName is not null && !string.Equals(status.ContainerName, record.ContainerName, StringComparison.Ordinal);

        if (!stateChanged
            && !stateReasonChanged
            && !lastActivityChanged
            && !containerIdChanged
            && !containerNameChanged
            && !outputChanged
            && !attentionChanged)
        {
            return record;
        }

        var updated = await _sessions.UpdateRuntimeAsync(
            record.ProjectId,
            record.SessionId,
            status.State,
            status.StateReason,
            lastActivityChanged ? status.LastActivityAt : null,
            containerIdChanged ? status.ContainerId : null,
            containerNameChanged ? status.ContainerName : null,
            outputCaptured,
            outputTail,
            outputCaptured ? status.OutputTailCapturedAt ?? _utcNow() : null,
            outputCaptured && status.OutputTailTruncated,
            outputSha256,
            attention.State,
            attention.Reason,
            attention.NeedsUserInput,
            attention.State is null ? null : _utcNow()).ConfigureAwait(false);

        if (stateChanged)
            await AuditAsync(updated, "pi_session_status_changed", "den", status.StateReason, new { from_state = record.State, to_state = status.State }, cancellationToken).ConfigureAwait(false);
        if (outputChanged)
            await AuditAsync(updated, "pi_session_output_tail_updated", "den", null, new { output_tail_sha256 = outputSha256, output_tail_truncated = updated.OutputTailTruncated }, cancellationToken).ConfigureAwait(false);
        if (attentionChanged && attention.State is not null)
            await AuditAsync(updated, "pi_session_attention_needed", "den", attention.Reason, new { attention_state = attention.State, needs_user_input = attention.NeedsUserInput }, cancellationToken).ConfigureAwait(false);
        if (attentionChanged && attention.State is null && record.AttentionState is not null)
            await AuditAsync(updated, "pi_session_attention_cleared", "den", null, new { previous_attention_state = record.AttentionState }, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private AttentionObservation DetectAttention(PiSessionHostStatus status)
    {
        var output = status.OutputTail ?? string.Empty;
        if (ContainsAny(output,
                "waiting for direction",
                "waiting for user",
                "needs user input",
                "need user input",
                "please respond",
                "approval required"))
        {
            return new AttentionObservation(
                PiSessionAttentionStates.WaitingForDirection,
                "Recent output indicates the session is waiting for operator direction.",
                NeedsUserInput: true);
        }

        if (ContainsAny(output,
                "do you want to continue",
                "proceed?",
                "continue?",
                "[y/n]",
                "[y/N]",
                "[Y/n]"))
        {
            return new AttentionObservation(
                PiSessionAttentionStates.UserInputNeeded,
                "Recent output appears to be prompting for user input.",
                NeedsUserInput: true);
        }

        if (ContainsAny(output,
                "blocked:",
                "blocked by",
                "cannot proceed",
                "unable to proceed"))
        {
            return new AttentionObservation(
                PiSessionAttentionStates.Blocked,
                "Recent output indicates the session is blocked.",
                NeedsUserInput: true);
        }

        if (status.State == PiSessionStates.Running
            && status.LastActivityAt is { } lastActivityAt
            && _utcNow() - lastActivityAt.ToUniversalTime() >= StalledActivityThreshold)
        {
            return new AttentionObservation(
                PiSessionAttentionStates.Stalled,
                $"No host-reported activity for at least {StalledActivityThreshold.TotalMinutes:0} minutes.",
                NeedsUserInput: false);
        }

        return AttentionObservation.None;
    }

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string? NormalizeOutputTail(string? outputTail)
    {
        if (outputTail is null)
            return null;
        var normalized = outputTail.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n', '\r');
        return normalized.Length <= OutputTailMaxChars ? normalized : normalized[^OutputTailMaxChars..];
    }

    private static string ComputeSha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private async Task AuditAsync(PiSessionRecord record, string eventType, string requestedBy, string? reason, object? payload, CancellationToken cancellationToken)
    {
        var payloadJson = payload is null ? null : JsonSerializer.Serialize(payload, PiSessionJson.Options);
        var sessionEvent = await _sessions.AppendEventAsync(new PiSessionEvent
        {
            ProjectId = record.ProjectId,
            TaskId = record.TaskId,
            WorkspaceId = record.WorkspaceId,
            SessionId = record.SessionId,
            EventType = eventType,
            Payload = payloadJson,
            RequestedBy = requestedBy,
            Reason = reason,
        }).ConfigureAwait(false);

        await _ops.AppendOpsAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = eventType,
            ProjectId = record.ProjectId,
            TaskId = record.TaskId,
            Sender = requestedBy,
            DeliveryMode = eventType == "pi_session_attention_needed" ? AgentStreamDeliveryMode.Notify : AgentStreamDeliveryMode.RecordOnly,
            Body = $"Pi session {record.SessionId} {eventType.Replace("pi_session_", string.Empty, StringComparison.Ordinal).Replace('_', ' ')}.",
            Metadata = JsonSerializer.SerializeToElement(new
            {
                schema = "den_pi_session",
                schema_version = 1,
                session_id = record.SessionId,
                run_id = record.RunId,
                workspace_id = record.WorkspaceId,
                host_id = record.HostId,
                tmux_session_name = record.TmuxSessionName,
                container_id = record.ContainerId,
                container_name = record.ContainerName,
                state = record.State,
                last_activity_at = record.LastActivityAt,
                output_tail_captured_at = record.OutputTailCapturedAt,
                output_tail_sha256 = record.OutputTailSha256,
                output_tail_truncated = record.OutputTailTruncated,
                attention_state = record.AttentionState,
                attention_reason = record.AttentionReason,
                attention_since_at = record.AttentionSinceAt,
                needs_user_input = record.NeedsUserInput,
                launch_profile_kind = record.LaunchProfileKind,
                launch_profile_id = record.LaunchProfileId,
                requested_by = requestedBy,
                reason
            }),
            DedupKey = $"pi-session-event:{sessionEvent.Id}"
        }).ConfigureAwait(false);
    }

    private static PiSessionDetail ToDetail(PiSessionRecord record, PiDockerLaunchProfile? profile) => new()
    {
        Session = ToSummary(record),
        LaunchProfile = profile,
        Attach = record.State == PiSessionStates.Running ? BuildAttachInfo(record) : null,
    };

    private static PiSessionSummary ToSummary(PiSessionRecord record) => new()
    {
        SessionId = record.SessionId,
        ProjectId = record.ProjectId,
        TaskId = record.TaskId,
        WorkspaceId = record.WorkspaceId,
        RunId = record.RunId,
        Title = record.Title,
        ToolProfile = record.ToolProfile,
        Model = record.Model,
        Provider = record.Provider,
        HostId = record.HostId,
        TmuxSessionName = record.TmuxSessionName,
        ContainerId = record.ContainerId,
        ContainerName = record.ContainerName,
        State = record.State,
        StateReason = record.StateReason,
        LaunchProfileKind = record.LaunchProfileKind,
        LaunchProfileId = record.LaunchProfileId,
        LaunchCommand = DeserializeCommand(record.LaunchCommandJson),
        LaunchCommandDisplay = record.LaunchCommandDisplay,
        CreatedAt = record.CreatedAt,
        StartedAt = record.StartedAt,
        LastActivityAt = record.LastActivityAt,
        OutputTail = record.OutputTail,
        OutputTailCapturedAt = record.OutputTailCapturedAt,
        OutputTailTruncated = record.OutputTailTruncated,
        AttentionState = record.AttentionState,
        AttentionReason = record.AttentionReason,
        AttentionSinceAt = record.AttentionSinceAt,
        AttentionUpdatedAt = record.AttentionUpdatedAt,
        NeedsUserInput = record.NeedsUserInput,
        EndedAt = record.EndedAt,
        UpdatedAt = record.UpdatedAt,
        TerminationRequestedAt = record.TerminationRequestedAt,
        TerminationRequestedBy = record.TerminationRequestedBy,
        TerminationReason = record.TerminationReason,
        CleanupRequestedAt = record.CleanupRequestedAt,
        CleanupRequestedBy = record.CleanupRequestedBy,
        CleanupReason = record.CleanupReason,
        CleanupCompletedAt = record.CleanupCompletedAt,
    };

    private static PiSessionAttachInfo BuildAttachInfo(PiSessionRecord record) => new()
    {
        Mode = "external_attach_info",
        Backend = "tmux",
        TmuxSessionName = record.TmuxSessionName,
        CommandExecutable = "tmux",
        CommandArgs = ["attach-session", "-t", record.TmuxSessionName],
        Warnings = ["Attach info exposes the server host tmux session name; bounded output and attention snapshots are available on the Den pi-session record."],
    };

    private static PiDockerLaunchProfile? DeserializeProfile(PiSessionRecord record)
    {
        try
        {
            return JsonSerializer.Deserialize<PiDockerLaunchProfile>(record.LaunchProfileJson, PiSessionJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> DeserializeCommand(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, PiSessionJson.Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<string> BuildLaunchCommand(string dockerExecutable, PiDockerLaunchProfile profile)
    {
        var command = new List<string>();
        var environmentAssignments = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in profile.Environment)
            environmentAssignments[pair.Key] = pair.Value;
        if (!string.IsNullOrWhiteSpace(profile.DockerHost))
            environmentAssignments["DOCKER_HOST"] = profile.DockerHost.Trim();
        foreach (var name in profile.ScrubbedEnvironmentVariables)
            environmentAssignments[name] = string.Empty;

        if (environmentAssignments.Count > 0)
        {
            command.Add("env");
            command.AddRange(environmentAssignments.Select(pair => $"{pair.Key}={pair.Value}"));
        }
        command.Add(dockerExecutable);
        command.AddRange(profile.DockerComposeRunArgs);
        return command;
    }

    private static string? NormalizeIdentifier(string? value)
    {
        if (value is null)
            return null;
        var normalized = value.Trim();
        if (normalized.Length == 0)
            throw new InvalidOperationException("session_id must not be empty.");
        if (normalized.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("session_id must not contain whitespace.");
        return normalized;
    }

    private static string? NormalizeText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record AttentionObservation(string? State, string? Reason, bool NeedsUserInput)
    {
        public static readonly AttentionObservation None = new(null, null, false);
    }
}
