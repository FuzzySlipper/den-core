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

        Assert.Equal("dry_run", result.Status);
        Assert.True(result.Succeeded);
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

    private static DenPublishFacadeService BuildService(FakeRepositories repos, HttpMessageHandler handler)
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
            client,
            new DenPublishFacadeOptions { Endpoint = "http://127.0.0.1:5090" },
            NullLogger<DenPublishFacadeService>.Instance);
    }

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

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            RequestBodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
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
