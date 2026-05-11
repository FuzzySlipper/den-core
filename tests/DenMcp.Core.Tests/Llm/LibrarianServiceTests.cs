using DenMcp.Core.Data;
using DenMcp.Core.Llm;
using DenMcp.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenMcp.Core.Tests.Llm;

public class LibrarianServiceTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private LibrarianService _service = null!;
    private StubLlmClient _llmClient = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        var taskRepo = new TaskRepository(_testDb.Db);
        var docRepo = new DocumentRepository(_testDb.Db);
        var msgRepo = new MessageRepository(_testDb.Db);
        var projRepo = new ProjectRepository(_testDb.Db);

        await projRepo.CreateAsync(new Project { Id = "proj", Name = "Test" });

        await docRepo.UpsertAsync(new Document
        {
            ProjectId = "proj",
            Slug = "test-spec",
            Title = "Test Specification",
            Content = "This specification covers the testing approach.",
            DocType = DocType.Spec
        });

        await taskRepo.CreateAsync(new ProjectTask
        {
            ProjectId = "proj",
            Title = "Write integration tests",
            Description = "Cover all librarian components"
        });

        var gatherer = new LibrarianGatherer(taskRepo, docRepo, msgRepo);
        _llmClient = new StubLlmClient();
        var config = new LlmConfig
        {
            Endpoint = "http://test",
            Model = "test",
            MaxTokens = 512,
            ContextTokenBudget = 4096
        };
        _service = new LibrarianService(gatherer, taskRepo, _llmClient, config, NullLogger<LibrarianService>.Instance);
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    [Fact]
    public async Task QueryAsync_ReturnsStructuredResponse()
    {
        _llmClient.CannedResponse = """
            {
              "relevant_items": [
                {
                  "type": "document",
                  "source_id": "proj/test-spec",
                  "project_id": "proj",
                  "summary": "Test specification",
                  "why_relevant": "Covers the testing approach you need",
                  "snippet": "This specification covers the testing approach."
                }
              ],
              "recommendations": ["Start with the test spec"],
              "confidence": "high"
            }
""";

        var result = await _service.QueryAsync("proj", "testing approach");

        Assert.Equal(LibrarianConfidence.High, result.Confidence);
        Assert.Single(result.RelevantItems);
        Assert.Equal("document", result.RelevantItems[0].Type);
        Assert.Equal("proj/test-spec", result.RelevantItems[0].SourceId);
        Assert.Single(result.Recommendations);
    }

    [Fact]
    public async Task QueryAsync_PassesGatheredContextToLlm()
    {
        _llmClient.CannedResponse = """{"relevant_items": [], "recommendations": [], "confidence": "low"}""";

        await _service.QueryAsync("proj", "testing specification");

        // Verify the LLM received a system prompt containing gathered context
        Assert.Contains("Test Specification", _llmClient.LastSystemPrompt);
        Assert.Contains("testing specification", _llmClient.LastUserMessage);
    }

    [Fact]
    public async Task QueryAsync_EmptyProject_ReturnsEmpty()
    {
        _llmClient.CannedResponse = "should not be called";

        var result = await _service.QueryAsync("nonexistent-project", "anything");

        Assert.Equal(LibrarianConfidence.Low, result.Confidence);
        Assert.Empty(result.RelevantItems);
        Assert.Null(_llmClient.LastUserMessage); // LLM should not have been called
    }

    [Fact]
    public async Task QueryAsync_MalformedLlmResponse_DegradeGracefully()
    {
        _llmClient.CannedResponse = "I'm sorry, I can't provide structured JSON right now.";

        var result = await _service.QueryAsync("proj", "testing");

        Assert.Equal(LibrarianConfidence.Low, result.Confidence);
        Assert.Empty(result.RelevantItems);
        Assert.Single(result.Recommendations);
        Assert.Contains("sorry", result.Recommendations[0]);
    }

    [Fact]
    public async Task QueryAsync_MissingTask_ThrowsKeyNotFound()
    {
        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _service.QueryAsync("proj", "testing", taskId: 99999));

        Assert.Equal("Task 99999 not found", ex.Message);
        Assert.Null(_llmClient.LastUserMessage);
    }

    [Fact]
    public async Task QueryAsync_TaskFromDifferentProject_ThrowsInvalidOperation()
    {
        var taskRepo = new TaskRepository(_testDb.Db);
        var projRepo = new ProjectRepository(_testDb.Db);
        await projRepo.CreateAsync(new Project { Id = "other", Name = "Other" });
        var otherTask = await taskRepo.CreateAsync(new ProjectTask
        {
            ProjectId = "other",
            Title = "Other project task"
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.QueryAsync("proj", "testing", taskId: otherTask.Id));

        Assert.Equal($"Task {otherTask.Id} does not belong to project proj", ex.Message);
        Assert.Null(_llmClient.LastUserMessage);
    }

    [Fact]
    public async Task QueryAsync_NonProjectSpace_ReturnsStructuredResponse()
    {
        var taskRepo = new TaskRepository(_testDb.Db);
        var docRepo = new DocumentRepository(_testDb.Db);
        var msgRepo = new MessageRepository(_testDb.Db);
        var projRepo = new ProjectRepository(_testDb.Db);

        await projRepo.CreateAsync(new Project { Id = "knowledge", Name = "Knowledge Base", Kind = "knowledge_base" });
        await docRepo.UpsertAsync(new Document
        {
            ProjectId = "knowledge",
            Slug = "architecture",
            Title = "System Architecture",
            Content = "The system uses a modular design with clear boundaries.",
            DocType = DocType.Spec
        });
        await taskRepo.CreateAsync(new ProjectTask
        {
            ProjectId = "knowledge",
            Title = "Document architecture decisions",
            Description = "Keep ADRs up to date"
        });

        // Use a fresh service instance seeded with the new space
        var gatherer = new LibrarianGatherer(taskRepo, docRepo, msgRepo);
        var config = new LlmConfig
        {
            Endpoint = "http://test",
            Model = "test",
            MaxTokens = 512,
            ContextTokenBudget = 4096
        };
        var service = new LibrarianService(gatherer, taskRepo, _llmClient, config, NullLogger<LibrarianService>.Instance);

        _llmClient.CannedResponse = """
            {
              "relevant_items": [
                {
                  "type": "document",
                  "source_id": "knowledge/architecture",
                  "project_id": "knowledge",
                  "summary": "System architecture",
                  "why_relevant": "Matches the query",
                  "snippet": "The system uses a modular design with clear boundaries."
                }
              ],
              "recommendations": ["Review the architecture doc"],
              "confidence": "high"
            }
            """;

        var result = await service.QueryAsync("knowledge", "modular design");

        Assert.Equal(LibrarianConfidence.High, result.Confidence);
        Assert.Single(result.RelevantItems);
        Assert.Equal("knowledge/architecture", result.RelevantItems[0].SourceId);
        Assert.Contains("System Architecture", _llmClient.LastSystemPrompt!);
    }

    private sealed class StubLlmClient : ILlmClient
    {
        public string CannedResponse { get; set; } = """{"relevant_items": [], "recommendations": [], "confidence": "low"}""";
        public string? LastSystemPrompt { get; private set; }
        public string? LastUserMessage { get; private set; }

        public Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
        {
            LastSystemPrompt = systemPrompt;
            LastUserMessage = userMessage;
            return Task.FromResult(CannedResponse);
        }
    }
}
