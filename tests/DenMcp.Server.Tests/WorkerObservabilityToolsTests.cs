using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using DenMcp.Server.Tools;

namespace DenMcp.Server.Tests;

public class WorkerObservabilityToolsTests
{
    [Fact]
    public void PiDockerLaunchProfileRenderer_IncludesBoundedWorkerStartupEnvironment()
    {
        var renderer = new PiDockerLaunchProfileRenderer(new PiDockerLaunchProfileOptions
        {
            ComposeFile = "/tmp/den-mcp/compose.yaml",
            DevDir = "/tmp/den-mcp/dev",
            PiStateRootDir = "/tmp/den-mcp/pi-state",
            CredentialFallbackRootDir = "/tmp/den-mcp/creds",
        });

        var profile = renderer.Render(new PiDockerLaunchRenderRequest
        {
            ProjectId = "proj",
            SessionId = "session-1",
            TaskId = 1245,
            WorkerRole = "validator",
            WorkerRunId = "run-1",
            PromptPacketMessageId = 5549,
            StateFileRef = "den-state://worker/run-1/startup.json",
            StartupPrompt = "Read Den packet #5549 before work.",
            TimeoutSeconds = 600,
            CallbackPorts = [new PiDockerCallbackPort { HostPort = 21455, ContainerPort = 1455 }]
        });

        Assert.Equal("5549", profile.Environment["DEN_WORKER_PROMPT_PACKET_MESSAGE_ID"]);
        Assert.Equal("den-state://worker/run-1/startup.json", profile.Environment["DEN_WORKER_STATE_FILE_REF"]);
        Assert.Equal("Read Den packet #5549 before work.", profile.Environment["DEN_WORKER_STARTUP_PROMPT"]);
        Assert.Equal("validator", profile.Environment["DEN_WORKER_ROLE"]);
        Assert.Equal("run-1", profile.Environment["DEN_WORKER_RUN_ID"]);
        Assert.DoesNotContain("Read Den packet #5549", profile.DockerComposeRunArgs);
        Assert.Equal(5549, profile.PromptPacketMessageId);
        Assert.Equal(600, profile.TimeoutSeconds);
    }

    [Fact]
    public async Task LaunchPiWorker_ProjectsStartupContractIntoWorkerRun()
    {
        var service = new CapturingPiSessionService();
        var json = await WorkerTools.LaunchPiWorker(
            service,
            project_id: "proj",
            requested_by: "runner",
            role: "validator",
            task_id: 1245,
            prompt_packet_message_id: 5549,
            run_id: "run-1",
            session_id: "session-1",
            timeout_seconds: 600,
            callback_ports: "[{\"host_port\":21455,\"container_port\":1455}]",
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        var worker = doc.RootElement.GetProperty("worker_run");
        Assert.Equal("5549", worker.GetProperty("startup_contract").GetProperty("prompt_packet_message_id").GetRawText());
        Assert.True(worker.GetProperty("startup_contract").GetProperty("delivered_via_environment").GetBoolean());
        Assert.Contains("DEN_WORKER_STARTUP_PROMPT", worker.GetProperty("startup_contract").GetProperty("environment_keys").EnumerateArray().Select(e => e.GetString()));
        Assert.NotNull(service.CapturedRequest);
        Assert.Contains("prompt_packet_message_id: `5549`", service.CapturedRequest!.StartupPrompt);
        Assert.Contains("post_worker_completion_packet", service.CapturedRequest!.StartupPrompt);
        Assert.Contains("DEN_WORKER_RUN_ID", service.CapturedRequest!.StartupPrompt);
        Assert.Contains("send_message", service.CapturedRequest!.StartupPrompt);
    }

    [Fact]
    public async Task RegisterWorkerRun_CreatesTrackedSpawnedHermesRunWithoutLaunchingPiHost()
    {
        var service = new CapturingPiSessionService();

        var json = await WorkerTools.RegisterWorkerRun(
            service,
            project_id: "proj",
            task_id: 1245,
            requested_by: "runner",
            role: "coder",
            substrate: "spawned_hermes",
            run_id: "run-1",
            session_id: "session-1",
            branch: "task/1245-demo",
            base_branch: "main",
            base_commit: "abc123",
            head_commit: "def456",
            profile: "den-hermes-worker",
            provider: "openrouter",
            model: "anthropic/claude-sonnet-4",
            toolsets: "terminal,file",
            workdir: "/home/dev/den-hermes",
            host: "den-k8plus",
            timeout_seconds: 600,
            artifact_path: "/tmp/den-hermes/run-1/completion.json",
            log_path: "/tmp/den-hermes/run-1/worker.log",
            prompt_packet_message_id: 5791,
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        var worker = doc.RootElement.GetProperty("worker_run");
        Assert.Equal("registered worker run-1 (registered)", doc.RootElement.GetProperty("summary").GetString());
        Assert.Equal("spawned_hermes", worker.GetProperty("substrate").GetString());
        Assert.Equal("run-1", worker.GetProperty("run_id").GetString());
        Assert.Equal("session-1", worker.GetProperty("session_id").GetString());
        Assert.Equal("coder", worker.GetProperty("role").GetString());
        Assert.Equal("registered", worker.GetProperty("status").GetString());
        Assert.Equal("spawned_hermes", worker.GetProperty("launch_metadata").GetProperty("substrate").GetString());
        Assert.Equal("/tmp/den-hermes/run-1/completion.json", worker.GetProperty("artifact_handles")[0].GetProperty("handle").GetString());
        Assert.False(worker.GetProperty("session").TryGetProperty("tmux_session", out var tmux) && tmux.ValueKind != JsonValueKind.Null);
        Assert.False(worker.GetProperty("session").TryGetProperty("container_name", out var container) && container.ValueKind != JsonValueKind.Null);
        Assert.NotNull(service.CapturedRegistration);
        Assert.Null(service.CapturedRequest);
    }

    [Fact]
    public async Task RegisterWorkerRun_IsIdempotentByDedupeKey()
    {
        var service = new CapturingPiSessionService();

        var first = await WorkerTools.RegisterWorkerRun(
            service,
            project_id: "proj",
            task_id: 1245,
            requested_by: "runner",
            role: "coder",
            substrate: "spawned_hermes",
            run_id: "run-1",
            dedupe_key: "same-registration",
            verbose: true);
        var second = await WorkerTools.RegisterWorkerRun(
            service,
            project_id: "proj",
            task_id: 1245,
            requested_by: "runner",
            role: "coder",
            substrate: "spawned_hermes",
            run_id: "run-1",
            verbose: true);

        using var firstDoc = JsonDocument.Parse(first);
        using var secondDoc = JsonDocument.Parse(second);
        Assert.Equal("created", firstDoc.RootElement.GetProperty("idempotency").GetProperty("status").GetString());
        Assert.Equal("existing", secondDoc.RootElement.GetProperty("idempotency").GetProperty("status").GetString());
        Assert.Equal("run-1", secondDoc.RootElement.GetProperty("worker_run").GetProperty("run_id").GetString());
        Assert.Equal(1, service.RegisterCalls);
    }

    [Fact]
    public async Task RegisterWorkerRun_RejectsInvalidSubstrateWithDiagnostics()
    {
        var service = new CapturingPiSessionService();

        var json = await WorkerTools.RegisterWorkerRun(
            service,
            project_id: "proj",
            task_id: 1245,
            requested_by: "runner",
            role: "coder",
            substrate: "pi_docker_compose",
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        Assert.Contains("spawned_hermes", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal(0, service.RegisterCalls);
    }

    [Fact]
    public async Task RegisteredSpawnedHermesRun_AcceptsCompletionPacketAndStatusShowsSubstrate()
    {
        var service = new CapturingPiSessionService();
        var sessions = new CapturingPiSessionRepository(service);
        var messages = new CapturingMessageRepository();

        await WorkerTools.RegisterWorkerRun(
            service,
            project_id: "proj",
            task_id: 1245,
            requested_by: "runner",
            role: "coder",
            substrate: "spawned_hermes",
            run_id: "run-1",
            session_id: "session-1",
            verbose: true);

        var completionJson = await CompletionTools.PostWorkerCompletionPacket(
            service,
            sessions,
            messages,
            project_id: "proj",
            run_id: "run-1",
            requested_by: "runner",
            status: "completed",
            role: "coder",
            packet_type: "implementation_packet",
            summary: "done",
            branch: "task/1245-demo",
            head_commit: "0123456789abcdef0123456789abcdef01234567",
            tests_run: "[\"pytest: passed\"]",
            dedupe_key: "run-1:completed",
            verbose: true);
        using var completionDoc = JsonDocument.Parse(completionJson);
        Assert.Equal("present", completionDoc.RootElement.GetProperty("completion_state").GetString());

        var statusJson = await WorkerTools.GetWorkerRunStatus(service, messages, "proj", "run-1", task_id: 1245, verbose: true);
        using var statusDoc = JsonDocument.Parse(statusJson);
        Assert.Equal("spawned_hermes", statusDoc.RootElement.GetProperty("worker_run").GetProperty("substrate").GetString());
        Assert.Equal("completed", statusDoc.RootElement.GetProperty("worker_run").GetProperty("status").GetString());
        Assert.Equal("completed", statusDoc.RootElement.GetProperty("completion").GetProperty("status").GetString());
    }

    [Fact]
    public async Task RegisteredSpawnedHermesRun_NonSuccessCompletionLeavesRuntimeFailed()
    {
        var service = new CapturingPiSessionService();
        var sessions = new CapturingPiSessionRepository(service);
        var messages = new CapturingMessageRepository();

        await WorkerTools.RegisterWorkerRun(
            service,
            project_id: "proj",
            task_id: 1245,
            requested_by: "runner",
            role: "coder",
            substrate: "spawned_hermes",
            run_id: "run-1",
            session_id: "session-1",
            verbose: true);

        await CompletionTools.PostWorkerCompletionPacket(
            service,
            sessions,
            messages,
            project_id: "proj",
            run_id: "run-1",
            requested_by: "runner",
            status: "blocked",
            role: "coder",
            packet_type: "implementation_packet",
            summary: "blocked",
            recovery_guidance: "need operator decision",
            verbose: true);

        var statusJson = await WorkerTools.GetWorkerRunStatus(service, messages, "proj", "run-1", task_id: 1245, verbose: true);
        using var statusDoc = JsonDocument.Parse(statusJson);
        Assert.Equal("failed", statusDoc.RootElement.GetProperty("worker_run").GetProperty("status").GetString());
        Assert.Equal("blocked", statusDoc.RootElement.GetProperty("completion").GetProperty("status").GetString());
    }

    [Fact]
    public async Task CleanupWorkerRun_WhenAlreadyCleaned_IsIdempotentNoop()
    {
        var service = new CapturingPiSessionService
        {
            Detail = CapturingPiSessionService.CreateDetail(state: PiSessionStates.Completed, cleanupCompleted: DateTime.UtcNow)
        };

        var json = await WorkerTools.CleanupWorkerRun(service, "proj", "run-1", "runner", verbose: true);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("noop", doc.RootElement.GetProperty("cleanup").GetProperty("status").GetString());
        Assert.Equal("cleaned_up", doc.RootElement.GetProperty("cleanup").GetProperty("state").GetString());
        Assert.Equal(0, service.CleanupCalls);
    }

    private sealed class CapturingPiSessionRepository(CapturingPiSessionService service) : IPiSessionRepository
    {
        public Task<PiSessionRecord> CreateAsync(PiSessionRecord record) => throw new NotSupportedException();
        public Task<PiSessionRecord?> GetAsync(string projectId, string sessionId) => throw new NotSupportedException();
        public Task<List<PiSessionRecord>> ListAsync(PiSessionListOptions options) => throw new NotSupportedException();
        public Task<PiSessionRecord> UpdateStateAsync(string projectId, string sessionId, string state, string? stateReason = null, DateTime? startedAt = null, DateTime? lastActivityAt = null, DateTime? endedAt = null, string? containerId = null, string? containerName = null)
        {
            var current = service.Detail;
            var currentSession = current.Session;
            service.Detail = new PiSessionDetail
            {
                Session = new PiSessionSummary
                {
                    ProjectId = currentSession.ProjectId,
                    SessionId = currentSession.SessionId,
                    WorkspaceId = currentSession.WorkspaceId,
                    RunId = currentSession.RunId,
                    Title = currentSession.Title,
                    TaskId = currentSession.TaskId,
                    ToolProfile = currentSession.ToolProfile,
                    Model = currentSession.Model,
                    Provider = currentSession.Provider,
                    HostId = currentSession.HostId,
                    TmuxSessionName = currentSession.TmuxSessionName,
                    ContainerId = currentSession.ContainerId,
                    ContainerName = currentSession.ContainerName,
                    State = state,
                    StateReason = stateReason,
                    LaunchProfileKind = currentSession.LaunchProfileKind,
                    CreatedAt = currentSession.CreatedAt,
                    StartedAt = currentSession.StartedAt,
                    LastActivityAt = lastActivityAt ?? currentSession.LastActivityAt,
                    EndedAt = endedAt ?? currentSession.EndedAt,
                    UpdatedAt = DateTime.UtcNow,
                    CleanupCompletedAt = currentSession.CleanupCompletedAt,
                },
                LaunchProfile = current.LaunchProfile,
            };
            return Task.FromResult(ToRecord(service.Detail.Session));
        }

        private static PiSessionRecord ToRecord(PiSessionSummary current)
            => new()
            {
                ProjectId = current.ProjectId,
                SessionId = current.SessionId,
                TaskId = current.TaskId,
                WorkspaceId = current.WorkspaceId,
                RunId = current.RunId,
                Title = current.Title,
                ToolProfile = current.ToolProfile,
                Model = current.Model,
                Provider = current.Provider,
                HostId = current.HostId,
                TmuxSessionName = current.TmuxSessionName,
                ContainerId = current.ContainerId,
                ContainerName = current.ContainerName,
                State = current.State,
                StateReason = current.StateReason,
                LaunchProfileKind = current.LaunchProfileKind ?? "spawned_hermes",
                LaunchProfileJson = "{}",
                LaunchCommandJson = "[]",
                LaunchCommandDisplay = "hermes chat -q <bounded Den worker prompt>",
                CreatedAt = current.CreatedAt,
                StartedAt = current.StartedAt,
                LastActivityAt = current.LastActivityAt,
                EndedAt = current.EndedAt,
                UpdatedAt = current.UpdatedAt,
            };

        public Task<PiSessionRecord> UpdateRuntimeAsync(string projectId, string sessionId, string state, string? stateReason, DateTime? lastActivityAt, string? containerId, string? containerName, bool outputCaptured, string? outputTail, DateTime? outputTailCapturedAt, bool outputTailTruncated, string? outputTailSha256, string? attentionState, string? attentionReason, bool needsUserInput, DateTime? attentionObservedAt) => throw new NotSupportedException();
        public Task<PiSessionRecord> MarkTerminationRequestedAsync(string projectId, string sessionId, string requestedBy, string? reason) => throw new NotSupportedException();
        public Task<PiSessionRecord> MarkCleanupRequestedAsync(string projectId, string sessionId, string requestedBy, string? reason) => throw new NotSupportedException();
        public Task<PiSessionRecord> MarkCleanupCompletedAsync(string projectId, string sessionId, string? stateReason = null) => throw new NotSupportedException();
        public Task<PiSessionEvent> AppendEventAsync(PiSessionEvent evt) => throw new NotSupportedException();

        public Task<PiSessionRecord> MarkCompletionObservedAsync(string projectId, string sessionId, string stateReason, DateTime? lastActivityAt = null)
        {
            var current = service.Detail.Session;
            return Task.FromResult(new PiSessionRecord
            {
                ProjectId = current.ProjectId,
                SessionId = current.SessionId,
                TaskId = current.TaskId,
                WorkspaceId = current.WorkspaceId,
                RunId = current.RunId,
                Title = current.Title,
                ToolProfile = current.ToolProfile,
                Model = current.Model,
                Provider = current.Provider,
                HostId = current.HostId,
                TmuxSessionName = current.TmuxSessionName,
                ContainerName = current.ContainerName,
                State = current.State,
                StateReason = stateReason,
                LaunchProfileKind = current.LaunchProfileKind ?? "spawned_hermes",
                LaunchProfileJson = "{}",
                LaunchCommandJson = "[]",
                LaunchCommandDisplay = "hermes chat -q <bounded Den worker prompt>",
                CreatedAt = current.CreatedAt,
                UpdatedAt = DateTime.UtcNow,
            });
        }
    }

    private sealed class CapturingMessageRepository : IMessageRepository
    {
        private readonly List<Message> _messages = [];

        public Task<Message> CreateAsync(Message message)
        {
            message.Id = _messages.Count + 1;
            message.CreatedAt = DateTime.UtcNow;
            _messages.Insert(0, message);
            return Task.FromResult(message);
        }

        public Task<Message?> GetByIdAsync(int id) => Task.FromResult(_messages.FirstOrDefault(message => message.Id == id));
        public Task<List<Message>> GetMessagesAsync(string projectId, int? taskId = null, DateTime? since = null, string? unreadFor = null, int limit = 20, MessageIntent? intent = null)
            => Task.FromResult(_messages.Where(message => message.ProjectId == projectId && (taskId is null || message.TaskId == taskId)).Take(limit).ToList());
        public Task<List<MessageFeedItem>> GetFeedAsync(string projectId, int limit = 20, MessageIntent? intent = null) => throw new NotSupportedException();
        public Task<DenMcp.Core.Models.Thread> GetThreadAsync(int threadId) => throw new NotSupportedException();
        public Task<int> MarkReadAsync(string agent, int[] messageIds) => throw new NotSupportedException();
    }

    private sealed class CapturingPiSessionService : IPiSessionService
    {
        public PiSessionLaunchRequest? CapturedRequest { get; private set; }
        public PiSessionRegistrationRequest? CapturedRegistration { get; private set; }
        public int RegisterCalls { get; private set; }
        public int CleanupCalls { get; private set; }
        private PiSessionDetail? _detail;
        public PiSessionDetail Detail
        {
            get => _detail ?? CreateDetail();
            set => _detail = value;
        }

        public Task<PiSessionDetail> LaunchAsync(string projectId, PiSessionLaunchRequest request, CancellationToken cancellationToken = default)
        {
            CapturedRequest = request;
            Detail = CreateDetail(projectId, request.SessionId ?? "session-1", request.RunId ?? "run-1", request.TaskId, PiSessionStates.Running, request.PromptPacketMessageId, request.StateFileRef, request.StartupPrompt, request.TimeoutSeconds);
            return Task.FromResult(Detail);
        }

        public Task<PiSessionDetail> RegisterAsync(string projectId, PiSessionRegistrationRequest request, CancellationToken cancellationToken = default)
        {
            RegisterCalls++;
            CapturedRegistration = request;
            Detail = CreateDetail(
                projectId,
                request.SessionId ?? "session-1",
                request.RunId ?? "run-1",
                request.TaskId,
                PiSessionStates.Launching,
                request.PromptPacketMessageId,
                request.StateFileRef,
                timeoutSeconds: request.TimeoutSeconds,
                launchProfileKind: "spawned_hermes",
                hostId: request.Host ?? "test-host",
                toolProfile: request.Role ?? "coder",
                model: request.Model,
                provider: request.Provider);
            return Task.FromResult(Detail);
        }

        public Task<List<PiSessionSummary>> ListAsync(PiSessionListOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(_detail is null ? new List<PiSessionSummary>() : new List<PiSessionSummary> { _detail.Session });

        public Task<PiSessionDetail?> GetAsync(string projectId, string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<PiSessionDetail?>(_detail is not null && _detail.Session.ProjectId == projectId && (_detail.Session.SessionId == sessionId || _detail.Session.RunId == sessionId) ? _detail : null);

        public Task<PiSessionDetail?> TerminateAsync(string projectId, string sessionId, PiSessionControlRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<PiSessionDetail?>(Detail);

        public Task<PiSessionDetail?> CleanupAsync(string projectId, string sessionId, PiSessionControlRequest request, CancellationToken cancellationToken = default)
        {
            CleanupCalls++;
            Detail = CreateDetail(projectId, sessionId, Detail.Session.RunId ?? "run-1", Detail.Session.TaskId, PiSessionStates.Completed, cleanupCompleted: DateTime.UtcNow);
            return Task.FromResult<PiSessionDetail?>(Detail);
        }

        public Task<PiSessionAttachInfo?> GetAttachInfoAsync(string projectId, string sessionId, PiSessionAttachRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<PiSessionAttachInfo?>(null);

        public static PiSessionDetail CreateDetail(
            string projectId = "proj",
            string sessionId = "session-1",
            string runId = "run-1",
            int? taskId = 1245,
            string state = PiSessionStates.Completed,
            int? promptPacketMessageId = null,
            string? stateFileRef = null,
            string? startupPrompt = null,
            int? timeoutSeconds = null,
            DateTime? cleanupCompleted = null,
            string launchProfileKind = "pi_docker_compose",
            string hostId = "test-host",
            string toolProfile = "validator",
            string? model = null,
            string? provider = null)
        {
            var env = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["DEN_WORKER_PROJECT_ID"] = projectId,
                ["DEN_WORKER_SESSION_ID"] = sessionId,
            };
            if (promptPacketMessageId is not null)
                env["DEN_WORKER_PROMPT_PACKET_MESSAGE_ID"] = promptPacketMessageId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (startupPrompt is not null)
                env["DEN_WORKER_STARTUP_PROMPT"] = startupPrompt;

            return new PiSessionDetail
            {
                Session = new PiSessionSummary
                {
                    ProjectId = projectId,
                    SessionId = sessionId,
                    RunId = runId,
                    TaskId = taskId,
                    ToolProfile = toolProfile,
                    Model = model,
                    Provider = provider,
                    HostId = hostId,
                    TmuxSessionName = launchProfileKind == "spawned_hermes" ? "" : "den-proj-session-1",
                    ContainerName = launchProfileKind == "spawned_hermes" ? null : "den-proj-session-1-pi",
                    State = state,
                    StateReason = state == PiSessionStates.Completed ? "worker completion packet #1: completed" : null,
                    LaunchProfileKind = launchProfileKind,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    CleanupCompletedAt = cleanupCompleted,
                },
                LaunchProfile = new PiDockerLaunchProfile
                {
                    ProfileId = "profile-1",
                    ProjectId = projectId,
                    SessionId = sessionId,
                    ComposeProjectName = "den-proj-session-1",
                    ComposeFile = "/tmp/compose.yaml",
                    Service = "pi",
                    DevDir = "/tmp/dev",
                    PiStateDir = "/tmp/pi-state",
                    Image = "pi-sandbox:latest",
                    PiVersion = "0.71.0",
                    NodeVersion = "22",
                    Environment = env,
                    PromptPacketMessageId = promptPacketMessageId,
                    StateFileRef = stateFileRef,
                    StartupPrompt = startupPrompt,
                    TimeoutSeconds = timeoutSeconds,
                }
            };
        }
    }
}
