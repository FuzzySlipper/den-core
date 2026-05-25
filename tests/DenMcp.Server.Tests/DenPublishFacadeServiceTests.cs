using System.Net;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Thread = DenMcp.Core.Models.Thread;

namespace DenMcp.Server.Tests;

public sealed class DenPublishFacadeServiceTests
{
    private const string BaseSha = "1111111111111111111111111111111111111111";
    private const string HeadSha = "2222222222222222222222222222222222222222";

    [Fact]
    public async Task DryRunPromotion_BuildsCamelCaseApiPayloadAndAuditsSuccess()
    {
        var repos = FakeRepositories.Success();
        repos.Findings.Add(new ReviewFinding
        {
            Id = 55,
            FindingKey = "R123-1",
            TaskId = 123,
            ReviewRoundId = 77,
            ReviewRoundNumber = 1,
            FindingNumber = 1,
            CreatedBy = "reviewer",
            Category = ReviewFindingCategory.AcceptanceGap,
            Summary = "Resolved acceptance gap",
            Status = ReviewFindingStatus.Superseded,
        });
        var handler = new CapturingDenPublishHandler(HttpStatusCode.OK, """
            {
              "succeeded": true,
              "publishStatus": "dry_run",
              "validation": {
                "status": "validated",
                "isPublishable": true,
                "fetchedHeadCommit": "2222222222222222222222222222222222222222",
                "localRef": "refs/den-publish/submissions/sub_123_1"
              },
              "audit": {
                "decisionId": "pub_123_sub_123_1"
              }
            }
            """);
        var service = BuildService(repos, handler);

        var result = await service.RequestDryRunAsync(DefaultRequest());

        Assert.Equal("validated", result.Status);
        Assert.True(result.Succeeded);
        Assert.Equal("dry_run", result.PublishStatus);
        Assert.Equal("validated", result.ValidationStatus);
        Assert.Equal("pub_123_sub_123_1", result.DecisionId);
        Assert.Single(repos.Messages.Created);
        var capturedRequest = Assert.Single(handler.Requests);
        Assert.Equal("http://127.0.0.1:5090/promotion/dry-run", capturedRequest.RequestUri!.ToString());

        using var payload = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.True(payload.RootElement.TryGetProperty("workspacePath", out _));
        Assert.True(payload.RootElement.TryGetProperty("allowedPathPrefixes", out _));
        Assert.True(payload.RootElement.GetProperty("decision").TryGetProperty("expectedHeadCommit", out _));
        Assert.True(payload.RootElement.GetProperty("decision").GetProperty("validateOnly").GetBoolean());
        Assert.True(payload.RootElement.GetProperty("submission").TryGetProperty("headCommit", out _));
        var reviewPayload = payload.RootElement.GetProperty("submission").GetProperty("review");
        Assert.True(reviewPayload.TryGetProperty("reviewRoundId", out _));
        var findingPayload = Assert.Single(reviewPayload.GetProperty("findings").EnumerateArray());
        Assert.Equal("55", findingPayload.GetProperty("findingId").GetString());
        Assert.True(findingPayload.GetProperty("blocking").GetBoolean());
        Assert.True(findingPayload.GetProperty("resolved").GetBoolean());
        Assert.False(findingPayload.TryGetProperty("overrideId", out _));
        Assert.DoesNotContain("category", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("findingKey", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("summary", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("expected_head_commit", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("head_commit", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.Equal("worker", result.CallerTrust);
        Assert.Equal("strict", result.EffectivePolicyMode);
        Assert.Equal("worker", Assert.Single(handler.HeaderValues("X-Den-Caller-Trust")));
        Assert.Equal("strict", Assert.Single(handler.HeaderValues("X-Den-Promotion-Policy-Mode")));
    }

    [Fact]
    public async Task DryRunPromotion_SurfacesAuditWarnWarningsInResultAndAuditMessage()
    {
        var repos = FakeRepositories.Success();
        var handler = new CapturingDenPublishHandler(HttpStatusCode.OK, """
            {
              "succeeded": true,
              "publishStatus": "dry_run",
              "validation": {
                "status": "validated",
                "isPublishable": true,
                "summary": "allowed with warnings",
                "warnings": [
                  {
                    "code": "unclassified_soft_failure",
                    "message": "Packet completeness was downgraded to audit warning",
                    "reason": "trusted orchestrator audit_warn policy",
                    "severity": "warning",
                    "strictAction": "reject",
                    "permissiveAction": "allow_with_warning",
                    "observedValues": {
                      "policy_mode": "audit_warn",
                      "caller_trust": "trusted_orchestrator"
                    }
                  }
                ],
                "fetchedHeadCommit": "2222222222222222222222222222222222222222",
                "localRef": "refs/den-publish/submissions/sub_123_1"
              },
              "audit": {
                "decisionId": "pub_123_sub_123_1"
              }
            }
            """);
        var service = BuildService(repos, handler);

        var result = await service.RequestDryRunAsync(DefaultRequest());

        Assert.Equal("allowed_with_warnings", result.Status);
        Assert.True(result.Succeeded);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("unclassified_soft_failure", warning.Code);
        Assert.Contains("audit warning", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("warning", warning.Severity);
        Assert.Equal("reject", warning.StrictAction);
        Assert.Equal("allow_with_warning", warning.PermissiveAction);
        Assert.Equal("audit_warn", warning.ObservedValues["policy_mode"]);
        Assert.Contains(result.HardeningHints, hint => hint.Contains("stricter policy", StringComparison.OrdinalIgnoreCase));
        var message = Assert.Single(repos.Messages.Created);
        Assert.Contains("with 1 warning", message.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Resolve warning(s) before canonical publish when practical", message.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("switch to a stricter policy", message.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, message.Metadata!.Value.GetProperty("warning_count").GetInt32());
        var metadataWarning = message.Metadata.Value.GetProperty("warnings")[0];
        Assert.Equal("unclassified_soft_failure", metadataWarning.GetProperty("code").GetString());
        Assert.Equal("reject", metadataWarning.GetProperty("strict_action").GetString());
        Assert.Equal("allow_with_warning", metadataWarning.GetProperty("permissive_action").GetString());
        Assert.Equal("audit_warn", metadataWarning.GetProperty("observed_values").GetProperty("policy_mode").GetString());
        var hints = message.Metadata.Value.GetProperty("hardening_hints").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains(hints, hint => hint!.Contains("Resolve warning", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DryRunPromotion_ForwardsOrchestratorOverrideOnlyForConfiguredTrustedOrchestrator()
    {
        var repos = FakeRepositories.Success();
        var handler = SuccessfulHandler();
        var options = new DenPublishFacadeOptions
        {
            Endpoint = "http://127.0.0.1:5090",
            TrustedOrchestrators = ["sysadmin"],
            TrustedOrchestratorPolicyMode = "audit_warn"
        };
        var service = BuildService(repos, handler, options);
        var request = WithOrchestratorOverride(DefaultRequest());

        var result = await service.RequestDryRunAsync(request);

        Assert.Equal("trusted_orchestrator", result.CallerTrust);
        Assert.Equal("audit_warn", result.EffectivePolicyMode);
        Assert.Equal("trusted_orchestrator", Assert.Single(handler.HeaderValues("X-Den-Caller-Trust")));
        using var payload = JsonDocument.Parse(handler.RequestBodies[0]);
        var orchestratorOverride = payload.RootElement.GetProperty("decision").GetProperty("orchestratorOverride");
        Assert.Equal("audit_warn", orchestratorOverride.GetProperty("unclassifiedFailurePolicy").GetString());
        Assert.Equal("operator approved audit-warn retry", orchestratorOverride.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task DryRunPromotion_RequestedByAloneCannotSpoofTrustedOrchestratorOverride()
    {
        var repos = FakeRepositories.Success();
        var handler = SuccessfulHandler();
        var service = BuildService(repos, handler);

        var result = await service.RequestDryRunAsync(WithOrchestratorOverride(DefaultRequest()));

        Assert.Equal("rejected", result.Status);
        Assert.False(result.Succeeded);
        Assert.Equal("worker", result.CallerTrust);
        Assert.Contains(result.Diagnostics, d => d.Contains("requestedBy alone is not trusted", StringComparison.Ordinal));
        Assert.Empty(handler.Requests);
        Assert.Empty(repos.Messages.Created);
    }

    [Fact]
    public async Task DryRunPromotion_ActiveTrustedOrchestratorRoleCanForwardOverride()
    {
        var repos = FakeRepositories.Success();
        repos.AgentBindings.Bindings.Add(new AgentInstanceBinding
        {
            InstanceId = "sysadmin-orchestrator-1",
            ProjectId = "den-channels",
            AgentIdentity = "sysadmin",
            AgentFamily = "hermes",
            Role = "orchestrator",
            TransportKind = "hermes",
            Status = AgentInstanceBindingStatus.Active,
        });
        var handler = SuccessfulHandler();
        var service = BuildService(repos, handler);

        var result = await service.RequestDryRunAsync(WithOrchestratorOverride(DefaultRequest()));

        Assert.True(result.Succeeded);
        Assert.Equal("trusted_orchestrator", result.CallerTrust);
        using var payload = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.True(payload.RootElement.GetProperty("decision").TryGetProperty("orchestratorOverride", out var orchestratorOverride));
        Assert.Equal("audit_warn", orchestratorOverride.GetProperty("unclassifiedFailurePolicy").GetString());
    }

    [Fact]
    public async Task DryRunPromotion_RejectsMissingReviewBeforeCallingDenPublish()
    {
        var repos = FakeRepositories.Success();
        repos.ReviewRound = null;
        var handler = new CapturingDenPublishHandler(HttpStatusCode.OK, "{}");
        var service = BuildService(repos, handler);

        var result = await service.RequestDryRunAsync(DefaultRequest());

        Assert.Equal("rejected", result.Status);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Contains("review round", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(handler.Requests);
        Assert.Empty(repos.Messages.Created);
    }

    [Fact]
    public async Task DryRunPromotion_RejectsStaleReviewHeadBeforeCallingDenPublish()
    {
        var repos = FakeRepositories.Success();
        repos.ReviewRound!.HeadCommit = "3333333333333333333333333333333333333333";
        var handler = new CapturingDenPublishHandler(HttpStatusCode.OK, "{}");
        var service = BuildService(repos, handler);

        var result = await service.RequestDryRunAsync(DefaultRequest());

        Assert.Equal("rejected", result.Status);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Contains("review head", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(handler.Requests);
        Assert.Empty(repos.Messages.Created);
    }

    [Fact]
    public async Task DryRunPromotion_RejectsUnresolvedBlockingFindingWithoutStructuredOverride()
    {
        var repos = FakeRepositories.Success();
        repos.Findings.Add(new ReviewFinding
        {
            Id = 55,
            FindingKey = "R123-1",
            TaskId = 123,
            ReviewRoundId = 77,
            ReviewRoundNumber = 1,
            FindingNumber = 1,
            CreatedBy = "reviewer",
            Category = ReviewFindingCategory.BlockingBug,
            Summary = "Blocking bug",
            Status = ReviewFindingStatus.Open,
        });
        var handler = new CapturingDenPublishHandler(HttpStatusCode.OK, "{}");
        var service = BuildService(repos, handler);

        var result = await service.RequestDryRunAsync(DefaultRequest());

        Assert.Equal("rejected", result.Status);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Contains("unresolved blocking finding", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(handler.Requests);
        Assert.Empty(repos.Messages.Created);
    }

    private static DenPublishFacadeService BuildService(
        FakeRepositories repos,
        CapturingDenPublishHandler handler,
        DenPublishFacadeOptions? options = null)
    {
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:5090")
        };
        return new DenPublishFacadeService(
            repos.Projects,
            repos.ReviewRounds,
            repos.ReviewFindings,
            repos.Messages,
            repos.AgentBindings,
            client,
            options ?? new DenPublishFacadeOptions { Endpoint = "http://127.0.0.1:5090" },
            NullLogger<DenPublishFacadeService>.Instance);
    }

    private static CapturingDenPublishHandler SuccessfulHandler() => new(HttpStatusCode.OK, """
        {
          "succeeded": true,
          "publishStatus": "dry_run",
          "validation": {
            "status": "validated",
            "isPublishable": true,
            "fetchedHeadCommit": "2222222222222222222222222222222222222222",
            "localRef": "refs/den-publish/submissions/sub_123_1"
          },
          "audit": {
            "decisionId": "pub_123_sub_123_1"
          }
        }
        """);

    private static DenPublishDryRunRequest WithOrchestratorOverride(DenPublishDryRunRequest request) => new()
    {
        ProjectId = request.ProjectId,
        TaskId = request.TaskId,
        SubmissionId = request.SubmissionId,
        WorkerRunId = request.WorkerRunId,
        RequestedBy = request.RequestedBy,
        SubmittedBy = request.SubmittedBy,
        Role = request.Role,
        AttemptOrdinal = request.AttemptOrdinal,
        ParentSubmissionId = request.ParentSubmissionId,
        CodeGateInstance = request.CodeGateInstance,
        CodeGateRepo = request.CodeGateRepo,
        CodeGateRemoteUrl = request.CodeGateRemoteUrl,
        IngressRef = request.IngressRef,
        ConvenienceRef = request.ConvenienceRef,
        BaseBranch = request.BaseBranch,
        BaseCommit = request.BaseCommit,
        HeadCommit = request.HeadCommit,
        CanonicalRemoteUrl = request.CanonicalRemoteUrl,
        TargetBranch = request.TargetBranch,
        ReviewRoundId = request.ReviewRoundId,
        ChangedFilesClaim = request.ChangedFilesClaim,
        AllowedPathPrefixes = request.AllowedPathPrefixes,
        TestsRun = request.TestsRun,
        ScopeOverrides = request.ScopeOverrides,
        Operation = request.Operation,
        TargetRemote = request.TargetRemote,
        DecisionId = request.DecisionId,
        WorkspacePath = request.WorkspacePath,
        OrchestratorOverride = new DenPublishOrchestratorOverride
        {
            UnclassifiedFailurePolicy = "audit_warn",
            Reason = "operator approved audit-warn retry",
            ExpectedRiskCategories = ["unclassified_soft_failure"]
        }
    };

    private static DenPublishDryRunRequest DefaultRequest() => new()
    {
        ProjectId = "den-channels",
        TaskId = 123,
        SubmissionId = "sub_123_1",
        WorkerRunId = "run-123",
        RequestedBy = "sysadmin",
        SubmittedBy = "coder",
        AttemptOrdinal = 1,
        CodeGateInstance = "den-code-gate",
        CodeGateRepo = "den-channels/den-channels",
        CodeGateRemoteUrl = "ssh://git@192.168.1.10:3022/den-channels/den-channels.git",
        IngressRef = "refs/heads/submissions/den-channels/tasks/123/runs/run-123/attempt-001",
        ConvenienceRef = "refs/heads/submissions/den-channels/tasks/123/current",
        BaseBranch = "main",
        BaseCommit = BaseSha,
        HeadCommit = HeadSha,
        CanonicalRemoteUrl = "git@github.com:FuzzySlipper/den-channels.git",
        TargetBranch = "task/123-den-publish-facade",
        ReviewRoundId = 77,
        ChangedFilesClaim = ["src/Foo.cs"],
        AllowedPathPrefixes = ["src/"],
        TestsRun = ["dotnet test: passed"],
    };

    private sealed class CapturingDenPublishHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> RequestBodies { get; } = [];
        public List<Dictionary<string, string[]>> RequestHeaders { get; } = [];

        public IReadOnlyList<string> HeaderValues(string name) =>
            RequestHeaders.SelectMany(headers => headers.TryGetValue(name, out var values) ? values : []).ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            RequestBodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            RequestHeaders.Add(request.Headers.ToDictionary(header => header.Key, header => header.Value.ToArray(), StringComparer.OrdinalIgnoreCase));
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody)
            };
        }
    }

    private sealed class FakeRepositories
    {
        public FakeProjectRepository Projects { get; } = new();
        public FakeReviewRoundRepository ReviewRounds { get; } = new();
        public FakeReviewFindingRepository ReviewFindings { get; } = new();
        public FakeMessageRepository Messages { get; } = new();
        public FakeAgentInstanceBindingRepository AgentBindings { get; } = new();
        public List<ReviewFinding> Findings => ReviewFindings.Findings;
        public ReviewRound? ReviewRound
        {
            get => ReviewRounds.Round;
            set => ReviewRounds.Round = value;
        }

        public static FakeRepositories Success()
        {
            var repos = new FakeRepositories();
            repos.Projects.Project = new Project { Id = "den-channels", Name = "Den Channels", RootPath = "den-channels" };
            repos.ReviewRound = new ReviewRound
            {
                Id = 77,
                TaskId = 123,
                RoundNumber = 1,
                RequestedBy = "sysadmin",
                Branch = "task/123-den-publish-facade",
                BaseBranch = "main",
                BaseCommit = BaseSha,
                HeadCommit = HeadSha,
                Verdict = ReviewVerdict.LooksGood,
                VerdictBy = "reviewer",
            };
            return repos;
        }
    }

    private sealed class FakeProjectRepository : IProjectRepository
    {
        public Project? Project { get; set; }
        public Task<Project> CreateAsync(Project project) => throw new NotSupportedException();
        public Task<Project?> GetByIdAsync(string id) => Task.FromResult(Project?.Id == id ? Project : null);
        public Task<List<Project>> GetAllAsync() => throw new NotSupportedException();
        public Task<List<Project>> ListAsync(string? kind = null, bool includeHidden = false, bool includeArchived = false) => throw new NotSupportedException();
        public Task<ProjectWithStats> GetWithStatsAsync(string id, string? agent = null) => throw new NotSupportedException();
        public Task<Project> UpdateVisibilityAsync(string id, string visibility) => throw new NotSupportedException();
        public Task<Dictionary<string, int>> GetDependentRecordCountsAsync(string id) => Task.FromResult(new Dictionary<string, int>());
        public Task DeleteSpaceAsync(string id) => Task.CompletedTask;
    }

    private sealed class FakeReviewRoundRepository : IReviewRoundRepository
    {
        public ReviewRound? Round { get; set; }
        public Task<ReviewRound> CreateAsync(CreateReviewRoundInput input) => throw new NotSupportedException();
        public Task<ReviewRound?> GetByIdAsync(int id) => Task.FromResult(Round?.Id == id ? Round : null);
        public Task<List<ReviewRound>> ListByTaskAsync(int taskId) => throw new NotSupportedException();
        public Task<ReviewRound?> GetLatestByTaskAsync(int taskId) => throw new NotSupportedException();
        public Task<ReviewRound> SetVerdictAsync(int id, ReviewVerdict verdict, string decidedBy, string? notes = null) => throw new NotSupportedException();
    }

    private sealed class FakeReviewFindingRepository : IReviewFindingRepository
    {
        public List<ReviewFinding> Findings { get; } = [];
        public Task<ReviewFinding> CreateAsync(CreateReviewFindingInput input) => throw new NotSupportedException();
        public Task<ReviewFinding?> GetByIdAsync(int id) => throw new NotSupportedException();
        public Task<List<ReviewFinding>> ListByTaskAsync(int taskId, ReviewFindingStatus[]? statuses = null) => throw new NotSupportedException();
        public Task<List<ReviewFinding>> ListByReviewRoundAsync(int reviewRoundId, ReviewFindingStatus[]? statuses = null) =>
            Task.FromResult(Findings.Where(f => f.ReviewRoundId == reviewRoundId).ToList());
        public Task<ReviewFinding> RespondAsync(int id, RespondToReviewFindingInput input) => throw new NotSupportedException();
        public Task<ReviewFinding> SetStatusAsync(int id, UpdateReviewFindingStatusInput input) => throw new NotSupportedException();
    }

    private sealed class FakeAgentInstanceBindingRepository : IAgentInstanceBindingRepository
    {
        public List<AgentInstanceBinding> Bindings { get; } = [];

        public Task<AgentInstanceBinding> UpsertAsync(AgentInstanceBinding binding) => throw new NotSupportedException();
        public Task<bool> HeartbeatAsync(string instanceId) => throw new NotSupportedException();
        public Task<bool> CheckOutAsync(string instanceId) => throw new NotSupportedException();
        public Task<int> CheckOutBySessionAsync(string sessionId) => throw new NotSupportedException();
        public Task<AgentInstanceBinding?> GetActiveByInstanceIdAsync(string instanceId, int timeoutMinutes = 5) => throw new NotSupportedException();

        public Task<List<AgentInstanceBinding>> ListAsync(AgentInstanceBindingListOptions? options = null)
        {
            IEnumerable<AgentInstanceBinding> query = Bindings;
            if (!string.IsNullOrWhiteSpace(options?.ProjectId))
                query = query.Where(binding => binding.ProjectId == options.ProjectId);
            if (!string.IsNullOrWhiteSpace(options?.AgentIdentity))
                query = query.Where(binding => binding.AgentIdentity == options.AgentIdentity);
            if (!string.IsNullOrWhiteSpace(options?.Role))
                query = query.Where(binding => binding.Role == options.Role);
            if (!string.IsNullOrWhiteSpace(options?.TransportKind))
                query = query.Where(binding => binding.TransportKind == options.TransportKind);
            if (options?.Statuses is { Length: > 0 } statuses)
                query = query.Where(binding => statuses.Contains(binding.Status));
            return Task.FromResult(query.ToList());
        }

        public Task<int> CleanupStaleAsync(int timeoutMinutes = 5) => Task.FromResult(0);
    }

    private sealed class FakeMessageRepository : IMessageRepository
    {
        public List<Message> Created { get; } = [];
        public Task<Message> CreateAsync(Message message)
        {
            message.Id = Created.Count + 1;
            Created.Add(message);
            return Task.FromResult(message);
        }
        public Task<Message?> GetByIdAsync(int id) => throw new NotSupportedException();
        public Task<List<Message>> GetMessagesAsync(string projectId, int? taskId = null, DateTime? since = null, string? unreadFor = null, int limit = 20, MessageIntent? intent = null) => throw new NotSupportedException();
        public Task<List<MessageFeedItem>> GetFeedAsync(string projectId, int limit = 20, MessageIntent? intent = null) => throw new NotSupportedException();
        public Task<Thread> GetThreadAsync(int threadId) => throw new NotSupportedException();
        public Task<int> MarkReadAsync(string agent, int[] messageIds) => throw new NotSupportedException();
    }
}
