using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DenMcp.Core.Data;
using DenMcp.Core.Llm;
using DenMcp.Core.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenMcp.Server.Tests;

public sealed class CollaborationApiTests : IAsyncLifetime
{
    private const string ProjectId = "collaboration-api-test";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private CollaborationAppFactory _factory = null!;
    private HttpClient _client = null!;
    private ProjectTask _task = null!;

    public async Task InitializeAsync()
    {
        _factory = new CollaborationAppFactory();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await projects.CreateAsync(new Project { Id = ProjectId, Name = "Collaboration API Test" });

        var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        _task = await tasks.CreateAsync(new ProjectTask { ProjectId = ProjectId, Title = "Collaboration task" });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CollaborationSessions_CreateListGetAnnotateAndDraft()
    {
        var createResponse = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/collaboration/sessions", new
        {
            task_id = _task.Id,
            title = "Annotate response",
            pi_run_id = "run-api",
            pi_session_id = "pi-api",
            desktop_operator_session_id = "desktop-api",
            created_by = "operator",
            initial_turn = new
            {
                role = "assistant",
                source_kind = "den_message",
                source_ref = "2614",
                source_uri = "den://messages/2614",
                source_context = new { task_id = _task.Id, thread_id = 2614 },
                raw_markdown = "# Heading\n\nParagraph.\n\n> quote"
            }
        }, JsonOpts);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<CollaborationSession>(JsonOpts);
        Assert.NotNull(created);
        Assert.Equal("run-api", created!.PiRunId);
        Assert.Equal("desktop-api", created.DesktopOperatorSessionId);
        var turn = Assert.Single(created.Turns);
        Assert.Equal(3, turn.Segments.Count);
        Assert.Equal("den-block-v1", turn.SegmenterVersion);
        Assert.Equal("den_message", turn.SourceKind);
        Assert.Equal(_task.Id, turn.SourceContext!.Value.GetProperty("task_id").GetInt32());
        var paragraph = turn.Segments.Single(s => s.SegmentType == CollaborationSegmentType.Paragraph);

        var listResponse = await _client.GetAsync($"/api/projects/{ProjectId}/collaboration/sessions?taskId={_task.Id}&status=active");
        listResponse.EnsureSuccessStatusCode();
        var listed = await listResponse.Content.ReadFromJsonAsync<List<CollaborationSession>>(JsonOpts);
        Assert.Equal(created.Id, Assert.Single(listed!).Id);

        var annotationResponse = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/collaboration/sessions/{created.Id}/turns/{turn.Id}/annotations", new
        {
            segment_id = paragraph.Id,
            annotation_type = "note",
            body = "tighten this paragraph",
            created_by = "operator"
        }, JsonOpts);
        annotationResponse.EnsureSuccessStatusCode();
        var annotation = await annotationResponse.Content.ReadFromJsonAsync<CollaborationAnnotation>(JsonOpts);
        Assert.NotNull(annotation);
        Assert.Equal(1, annotation!.Revision);
        Assert.Equal(paragraph.SegmentHash, annotation.SegmentHash);

        var updateResponse = await _client.PutAsJsonAsync($"/api/projects/{ProjectId}/collaboration/sessions/{created.Id}/annotations/{annotation.Id}", new
        {
            expected_revision = 1,
            annotation_type = "flag",
            body = "needs discussion",
            updated_by = "operator"
        }, JsonOpts);
        updateResponse.EnsureSuccessStatusCode();
        var updatedAnnotation = await updateResponse.Content.ReadFromJsonAsync<CollaborationAnnotation>(JsonOpts);
        Assert.Equal(2, updatedAnnotation!.Revision);
        Assert.Equal(CollaborationAnnotationType.Flag, updatedAnnotation.AnnotationType);

        var draftResponse = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/collaboration/sessions/{created.Id}/drafts", new
        {
            turn_id = turn.Id,
            content = "> Paragraph.\n  [FLAG]: needs discussion\n\n---\n[2 section(s) not annotated — treat as acknowledged]",
            created_by = "operator"
        }, JsonOpts);
        draftResponse.EnsureSuccessStatusCode();
        var draft = await draftResponse.Content.ReadFromJsonAsync<CollaborationResponseDraft>(JsonOpts);
        Assert.NotNull(draft);
        Assert.Equal(1, draft!.Revision);

        var updateDraftResponse = await _client.PutAsJsonAsync($"/api/projects/{ProjectId}/collaboration/sessions/{created.Id}/drafts/{draft.Id}", new
        {
            expected_revision = 1,
            content = "compiled response v2",
            updated_by = "operator"
        }, JsonOpts);
        updateDraftResponse.EnsureSuccessStatusCode();

        var getResponse = await _client.GetAsync($"/api/projects/{ProjectId}/collaboration/sessions/{created.Id}");
        getResponse.EnsureSuccessStatusCode();
        var loaded = await getResponse.Content.ReadFromJsonAsync<CollaborationSession>(JsonOpts);
        Assert.Equal("# Heading\n\nParagraph.\n\n> quote", Assert.Single(loaded!.Turns).RawMarkdown);
        Assert.Single(loaded.Annotations);
        Assert.Equal("compiled response v2", Assert.Single(loaded.Drafts).Content);
    }

    [Fact]
    public async Task CollaborationMutationRoutes_ReturnConflictForStaleRevision()
    {
        var session = await CreateSessionAsync();
        var turn = Assert.Single(session.Turns);
        var segment = Assert.Single(turn.Segments);

        var annotationResponse = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/collaboration/sessions/{session.Id}/turns/{turn.Id}/annotations", new
        {
            segment_id = segment.Id,
            annotation_type = "note",
            body = "first",
            created_by = "operator"
        }, JsonOpts);
        annotationResponse.EnsureSuccessStatusCode();
        var annotation = await annotationResponse.Content.ReadFromJsonAsync<CollaborationAnnotation>(JsonOpts);

        var firstUpdate = await _client.PutAsJsonAsync($"/api/projects/{ProjectId}/collaboration/sessions/{session.Id}/annotations/{annotation!.Id}", new
        {
            expected_revision = 1,
            annotation_type = "done",
            body = "handled",
            updated_by = "operator"
        }, JsonOpts);
        firstUpdate.EnsureSuccessStatusCode();

        var staleUpdate = await _client.PutAsJsonAsync($"/api/projects/{ProjectId}/collaboration/sessions/{session.Id}/annotations/{annotation.Id}", new
        {
            expected_revision = 1,
            annotation_type = "flag",
            body = "stale",
            updated_by = "pi"
        }, JsonOpts);
        Assert.Equal(HttpStatusCode.Conflict, staleUpdate.StatusCode);

        var draftResponse = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/collaboration/sessions/{session.Id}/drafts", new
        {
            content = "draft one",
            created_by = "operator"
        }, JsonOpts);
        draftResponse.EnsureSuccessStatusCode();
        var draft = await draftResponse.Content.ReadFromJsonAsync<CollaborationResponseDraft>(JsonOpts);

        var draftFirstUpdate = await _client.PutAsJsonAsync($"/api/projects/{ProjectId}/collaboration/sessions/{session.Id}/drafts/{draft!.Id}", new
        {
            expected_revision = 1,
            content = "draft two",
            updated_by = "operator"
        }, JsonOpts);
        draftFirstUpdate.EnsureSuccessStatusCode();

        var staleDraftUpdate = await _client.PutAsJsonAsync($"/api/projects/{ProjectId}/collaboration/sessions/{session.Id}/drafts/{draft.Id}", new
        {
            expected_revision = 1,
            content = "stale draft",
            updated_by = "pi"
        }, JsonOpts);
        Assert.Equal(HttpStatusCode.Conflict, staleDraftUpdate.StatusCode);
    }

    [Fact]
    public async Task SessionStatusUpdate_TransitionsAndConflicts()
    {
        var session = await CreateSessionAsync();

        // Resolve via PATCH
        var resolveResponse = await _client.PatchAsJsonAsync($"/api/projects/{ProjectId}/collaboration/sessions/{session.Id}/status", new
        {
            expected_status = "active",
            status = "resolved"
        }, JsonOpts);
        resolveResponse.EnsureSuccessStatusCode();
        var resolved = await resolveResponse.Content.ReadFromJsonAsync<CollaborationSession>(JsonOpts);
        Assert.Equal(CollaborationSessionStatus.Resolved, resolved!.Status);

        // Stale expected_status -> 409
        var staleResponse = await _client.PatchAsJsonAsync($"/api/projects/{ProjectId}/collaboration/sessions/{session.Id}/status", new
        {
            expected_status = "active",
            status = "archived"
        }, JsonOpts);
        Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);

        // Archive with correct expected_status
        var archiveResponse = await _client.PatchAsJsonAsync($"/api/projects/{ProjectId}/collaboration/sessions/{session.Id}/status", new
        {
            expected_status = "resolved",
            status = "archived"
        }, JsonOpts);
        archiveResponse.EnsureSuccessStatusCode();
        var archived = await archiveResponse.Content.ReadFromJsonAsync<CollaborationSession>(JsonOpts);
        Assert.Equal(CollaborationSessionStatus.Archived, archived!.Status);

        // Invalid status string -> 400
        var badStatusResponse = await _client.PatchAsJsonAsync($"/api/projects/{ProjectId}/collaboration/sessions/{session.Id}/status", new
        {
            expected_status = "active",
            status = "bogus"
        }, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, badStatusResponse.StatusCode);
    }

    [Fact]
    public async Task SessionStatusUpdate_ForMissingSession_Returns404()
    {
        var response = await _client.PatchAsJsonAsync($"/api/projects/{ProjectId}/collaboration/sessions/99999/status", new
        {
            expected_status = "active",
            status = "resolved"
        }, JsonOpts);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnnotationListAndDelete_Routes()
    {
        var session = await CreateSessionAsync();
        var turn = Assert.Single(session.Turns);
        var segment = Assert.Single(turn.Segments);

        // Create two annotations
        var create1 = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/collaboration/sessions/{session.Id}/turns/{turn.Id}/annotations", new
        {
            segment_id = segment.Id,
            annotation_type = "note",
            body = "first annotation",
            created_by = "operator"
        }, JsonOpts);
        create1.EnsureSuccessStatusCode();
        var ann1 = await create1.Content.ReadFromJsonAsync<CollaborationAnnotation>(JsonOpts);

        var create2 = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/collaboration/sessions/{session.Id}/turns/{turn.Id}/annotations", new
        {
            segment_id = segment.Id,
            annotation_type = "flag",
            body = "second annotation",
            created_by = "operator"
        }, JsonOpts);
        create2.EnsureSuccessStatusCode();
        var ann2 = await create2.Content.ReadFromJsonAsync<CollaborationAnnotation>(JsonOpts);

        // List all annotations for session
        var listResponse = await _client.GetAsync($"/api/projects/{ProjectId}/collaboration/sessions/{session.Id}/annotations");
        listResponse.EnsureSuccessStatusCode();
        var all = await listResponse.Content.ReadFromJsonAsync<List<CollaborationAnnotation>>(JsonOpts);
        Assert.Equal(2, all!.Count);

        // List filtered by turnId
        var filteredResponse = await _client.GetAsync($"/api/projects/{ProjectId}/collaboration/sessions/{session.Id}/annotations?turnId={turn.Id}");
        filteredResponse.EnsureSuccessStatusCode();
        var filtered = await filteredResponse.Content.ReadFromJsonAsync<List<CollaborationAnnotation>>(JsonOpts);
        Assert.Equal(2, filtered!.Count);

        // List filtered by segmentId
        var segFiltered = await _client.GetAsync($"/api/projects/{ProjectId}/collaboration/sessions/{session.Id}/annotations?segmentId={segment.Id}");
        segFiltered.EnsureSuccessStatusCode();
        var segAnnotations = await segFiltered.Content.ReadFromJsonAsync<List<CollaborationAnnotation>>(JsonOpts);
        Assert.Equal(2, segAnnotations!.Count);

        // Delete annotation 1 with correct revision
        var deleteResponse = await _client.DeleteAsync($"/api/projects/{ProjectId}/collaboration/sessions/{session.Id}/annotations/{ann1!.Id}?expectedRevision={ann1.Revision}");
        deleteResponse.EnsureSuccessStatusCode();
        var deleted = await deleteResponse.Content.ReadFromJsonAsync<CollaborationAnnotation>(JsonOpts);
        Assert.Equal(ann1.Id, deleted!.Id);

        // Verify only 1 remains
        var afterDelete = await _client.GetAsync($"/api/projects/{ProjectId}/collaboration/sessions/{session.Id}/annotations");
        afterDelete.EnsureSuccessStatusCode();
        var remaining = await afterDelete.Content.ReadFromJsonAsync<List<CollaborationAnnotation>>(JsonOpts);
        var single = Assert.Single(remaining!);
        Assert.Equal(ann2!.Id, single.Id);

        // Delete already-deleted -> 404
        var reDelete = await _client.DeleteAsync($"/api/projects/{ProjectId}/collaboration/sessions/{session.Id}/annotations/{ann1.Id}?expectedRevision={ann1.Revision}");
        Assert.Equal(HttpStatusCode.NotFound, reDelete.StatusCode);

        // Delete with stale revision -> 409
        // First update ann2 to bump revision
        var updateResponse = await _client.PutAsJsonAsync($"/api/projects/{ProjectId}/collaboration/sessions/{session.Id}/annotations/{ann2.Id}", new
        {
            expected_revision = 1,
            annotation_type = "done",
            body = "updated",
            updated_by = "operator"
        }, JsonOpts);
        updateResponse.EnsureSuccessStatusCode();

        var staleDelete = await _client.DeleteAsync($"/api/projects/{ProjectId}/collaboration/sessions/{session.Id}/annotations/{ann2.Id}?expectedRevision=1");
        Assert.Equal(HttpStatusCode.Conflict, staleDelete.StatusCode);

        // Delete with current revision works
        var goodDelete = await _client.DeleteAsync($"/api/projects/{ProjectId}/collaboration/sessions/{session.Id}/annotations/{ann2.Id}?expectedRevision=2");
        goodDelete.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ZeroSegmentMarkdown_Returns400()
    {
        // Whitespace-only markdown should be rejected with 400
        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/collaboration/sessions", new
        {
            task_id = _task.Id,
            title = "Zero segment test",
            initial_turn = new
            {
                raw_markdown = "   \n\n  \n "
            }
        }, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<CollaborationSession> CreateSessionAsync()
    {
        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/collaboration/sessions", new
        {
            task_id = _task.Id,
            title = "Stale conflict test",
            initial_turn = new
            {
                raw_markdown = "Paragraph only."
            }
        }, JsonOpts);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CollaborationSession>(JsonOpts))!;
    }

    private sealed class CollaborationAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-collaboration-api-{Guid.NewGuid()}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DenMcp:DatabasePath"] = _dbPath,
                    ["DenMcp:Llm:Endpoint"] = "",
                    ["DenMcp:Llm:Model"] = "test-model"
                });
            });

            builder.ConfigureServices(services =>
            {
                var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
                initializer.InitializeAsync().GetAwaiter().GetResult();

                services.RemoveAll<DbConnectionFactory>();
                services.AddSingleton(new DbConnectionFactory(initializer.ConnectionString));

                services.RemoveAll<ILlmClient>();
                services.AddSingleton<ILlmClient>(new NoOpLlmClient());
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        private sealed class NoOpLlmClient : ILlmClient
        {
            public Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
                => Task.FromResult("{}");
        }
    }
}
