using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;

namespace DenMcp.Core.Tests.Data;

public sealed class CollaborationRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private CollaborationRepository _repo = null!;
    private ProjectTask _task = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _repo = new CollaborationRepository(_testDb.Db);

        var projects = new ProjectRepository(_testDb.Db);
        await projects.CreateAsync(new Project { Id = "proj", Name = "Project" });

        var tasks = new TaskRepository(_testDb.Db);
        _task = await tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Collaboration task" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    [Fact]
    public void SegmenterVersion_DelegatesToMarkdownBlockSegmenterDefault()
    {
        Assert.Equal(MarkdownBlockSegmenter.DefaultSegmenterVersion, CollaborationRepository.SegmenterVersion);
    }

    [Fact]
    public async Task CreateSession_PersistsImmutableSourceSnapshotAndDeterministicSegments()
    {
        using var context = JsonDocument.Parse("""{"thread_id":2614,"source":"task_thread"}""");
        var rawMarkdown = """
            # Plan

            This is a paragraph.

            - first
            - second

            > quoted
            > reply

            ```csharp
            Console.WriteLine("hi");
            ```
            """;

        var session = await _repo.CreateSessionAsync(new CreateCollaborationSessionRequestModel
        {
            ProjectId = "proj",
            TaskId = _task.Id,
            PiRunId = "run-1",
            PiSessionId = "pi-session-1",
            DesktopOperatorSessionId = "operator-1",
            Title = "Annotate plan",
            CreatedBy = "tester",
            InitialTurn = new CreateCollaborationTurnRequestModel
            {
                Role = "assistant",
                SourceKind = "den_message",
                SourceRef = "2614",
                SourceLabel = "agent response",
                SourceUri = "den://messages/2614",
                SourceContext = context.RootElement.Clone(),
                RawMarkdown = rawMarkdown
            }
        });

        Assert.Equal("proj", session.ProjectId);
        Assert.Equal(_task.Id, session.TaskId);
        Assert.Equal("run-1", session.PiRunId);
        Assert.Equal("pi-session-1", session.PiSessionId);
        Assert.Equal("operator-1", session.DesktopOperatorSessionId);
        var turn = Assert.Single(session.Turns);
        Assert.Equal(rawMarkdown, turn.RawMarkdown);
        Assert.Equal(CollaborationRepository.SegmenterVersion, turn.SegmenterVersion);
        Assert.Equal(64, turn.SourceContentHash.Length);
        Assert.Equal("task_thread", turn.SourceContext!.Value.GetProperty("source").GetString());

        Assert.Collection(turn.Segments,
            heading =>
            {
                Assert.Equal(1, heading.SequenceNumber);
                Assert.Equal(CollaborationSegmentType.Heading, heading.SegmentType);
                Assert.Equal(1, heading.HeadingLevel);
                Assert.Equal("Plan", heading.Text);
            },
            paragraph => Assert.Equal(CollaborationSegmentType.Paragraph, paragraph.SegmentType),
            list => Assert.Equal(CollaborationSegmentType.List, list.SegmentType),
            quote => Assert.Equal(CollaborationSegmentType.BlockQuote, quote.SegmentType),
            code =>
            {
                Assert.Equal(CollaborationSegmentType.CodeBlock, code.SegmentType);
                Assert.Equal("csharp", code.CodeLanguage);
            });

        Assert.All(turn.Segments, segment => Assert.Equal(64, segment.SegmentHash.Length));
        Assert.Equal(turn.Segments.Count, turn.Segments.Select(s => s.SegmentHash).Distinct().Count());

        var same = await _repo.AddTurnAsync("proj", session.Id, new CreateCollaborationTurnRequestModel
        {
            Role = "assistant",
            RawMarkdown = rawMarkdown
        });
        Assert.Equal(2, same.TurnOrder);
        Assert.Equal(turn.Segments.Select(s => s.SegmentHash), same.Segments.Select(s => s.SegmentHash));
    }

    [Fact]
    public async Task AnnotationAndDraftUpdates_UseExpectedRevisionConflicts()
    {
        var session = await NewSessionAsync();
        var turn = Assert.Single(session.Turns);
        var segment = turn.Segments[0];

        var annotation = await _repo.CreateAnnotationAsync(
            "proj",
            session.Id,
            turn.Id,
            segment.Id,
            CollaborationAnnotationType.Note,
            "please clarify",
            "operator");

        Assert.Equal(1, annotation.Revision);
        Assert.Equal(segment.SegmentHash, annotation.SegmentHash);

        var updated = await _repo.UpdateAnnotationAsync(
            "proj",
            session.Id,
            annotation.Id,
            expectedRevision: 1,
            CollaborationAnnotationType.Flag,
            "discuss before proceeding",
            "operator");
        Assert.Equal(2, updated.Revision);
        Assert.Equal(CollaborationAnnotationType.Flag, updated.AnnotationType);

        await Assert.ThrowsAsync<CollaborationConflictException>(() => _repo.UpdateAnnotationAsync(
            "proj",
            session.Id,
            annotation.Id,
            expectedRevision: 1,
            CollaborationAnnotationType.Done,
            "stale update",
            "pi"));

        var draft = await _repo.CreateDraftAsync("proj", session.Id, turn.Id, "> segment\n  [note]: please clarify", "operator");
        Assert.Equal(1, draft.Revision);

        var draftUpdated = await _repo.UpdateDraftAsync("proj", session.Id, draft.Id, 1, "compiled response v2", "operator");
        Assert.Equal(2, draftUpdated.Revision);

        await Assert.ThrowsAsync<CollaborationConflictException>(() => _repo.UpdateDraftAsync(
            "proj",
            session.Id,
            draft.Id,
            expectedRevision: 1,
            content: "stale draft",
            updatedBy: "pi"));

        var reloaded = await _repo.GetSessionAsync("proj", session.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("## Reply\n\nParagraph to annotate.", Assert.Single(reloaded!.Turns).RawMarkdown);
        Assert.Equal(Assert.Single(reloaded.Turns).Segments[0].SegmentHash, Assert.Single(reloaded.Annotations).SegmentHash);
        Assert.Equal("compiled response v2", Assert.Single(reloaded.Drafts).Content);
    }

    [Fact]
    public async Task UpdateSessionStatus_WithExpectedStatus_UpdatesSuccessfully()
    {
        var session = await NewSessionAsync();
        Assert.Equal(CollaborationSessionStatus.Active, session.Status);

        var resolved = await _repo.UpdateSessionStatusAsync("proj", session.Id, CollaborationSessionStatus.Active, CollaborationSessionStatus.Resolved);
        Assert.Equal(CollaborationSessionStatus.Resolved, resolved.Status);
        Assert.True(resolved.UpdatedAt >= session.UpdatedAt);

        var archived = await _repo.UpdateSessionStatusAsync("proj", session.Id, CollaborationSessionStatus.Resolved, CollaborationSessionStatus.Archived);
        Assert.Equal(CollaborationSessionStatus.Archived, archived.Status);
    }

    [Fact]
    public async Task UpdateSessionStatus_WithStaleExpectedStatus_ThrowsConflict()
    {
        var session = await NewSessionAsync();
        Assert.Equal(CollaborationSessionStatus.Active, session.Status);

        // Resolve once
        await _repo.UpdateSessionStatusAsync("proj", session.Id, CollaborationSessionStatus.Active, CollaborationSessionStatus.Resolved);

        // Try to resolve again with Active as expected — stale
        var ex = await Assert.ThrowsAsync<CollaborationConflictException>(() =>
            _repo.UpdateSessionStatusAsync("proj", session.Id, CollaborationSessionStatus.Active, CollaborationSessionStatus.Archived));
        Assert.Contains("expected 'active'", ex.Message.ToLowerInvariant());
    }

    [Fact]
    public async Task UpdateSessionStatus_ForMissingSession_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _repo.UpdateSessionStatusAsync("proj", 99999, CollaborationSessionStatus.Active, CollaborationSessionStatus.Resolved));
    }

    [Fact]
    public async Task ListAnnotations_BySession_ReturnsAll()
    {
        var session = await NewSessionAsync();
        var turn = Assert.Single(session.Turns);
        var segment = turn.Segments[0];

        var a1 = await _repo.CreateAnnotationAsync("proj", session.Id, turn.Id, segment.Id, CollaborationAnnotationType.Note, "first", "tester");
        var a2 = await _repo.CreateAnnotationAsync("proj", session.Id, turn.Id, segment.Id, CollaborationAnnotationType.Flag, "second", "tester");

        var all = await _repo.ListAnnotationsAsync("proj", session.Id, new CollaborationAnnotationListOptions());
        Assert.Equal(2, all.Count);
        Assert.Contains(all, a => a.Id == a1.Id);
        Assert.Contains(all, a => a.Id == a2.Id);
    }

    [Fact]
    public async Task ListAnnotations_ByTurnId_FiltersCorrectly()
    {
        var session = await NewSessionAsync();
        var turn = Assert.Single(session.Turns);
        var segment = turn.Segments[0];

        var a1 = await _repo.CreateAnnotationAsync("proj", session.Id, turn.Id, segment.Id, CollaborationAnnotationType.Note, "on turn", "tester");

        var filtered = await _repo.ListAnnotationsAsync("proj", session.Id, new CollaborationAnnotationListOptions
        {
            TurnId = turn.Id
        });
        Assert.Single(filtered);
        Assert.Equal(a1.Id, filtered[0].Id);

        var noMatch = await _repo.ListAnnotationsAsync("proj", session.Id, new CollaborationAnnotationListOptions
        {
            TurnId = 99999
        });
        Assert.Empty(noMatch);
    }

    [Fact]
    public async Task ListAnnotations_BySegmentId_FiltersCorrectly()
    {
        var session = await NewSessionAsync();
        var turn = Assert.Single(session.Turns);
        var segment1 = turn.Segments[0];
        var segment2 = turn.Segments[1];

        var a1 = await _repo.CreateAnnotationAsync("proj", session.Id, turn.Id, segment1.Id, CollaborationAnnotationType.Note, "on seg1", "tester");
        var a2 = await _repo.CreateAnnotationAsync("proj", session.Id, turn.Id, segment2.Id, CollaborationAnnotationType.Done, "on seg2", "tester");

        var seg1Annotations = await _repo.ListAnnotationsAsync("proj", session.Id, new CollaborationAnnotationListOptions
        {
            SegmentId = segment1.Id
        });
        Assert.Single(seg1Annotations);
        Assert.Equal(a1.Id, seg1Annotations[0].Id);

        var seg2Annotations = await _repo.ListAnnotationsAsync("proj", session.Id, new CollaborationAnnotationListOptions
        {
            SegmentId = segment2.Id
        });
        Assert.Single(seg2Annotations);
        Assert.Equal(a2.Id, seg2Annotations[0].Id);
    }

    [Fact]
    public async Task ListAnnotations_WithTurnAndSegment_FiltersByBoth()
    {
        var session = await NewSessionAsync();
        var turn = Assert.Single(session.Turns);
        var segment = turn.Segments[0];

        var a1 = await _repo.CreateAnnotationAsync("proj", session.Id, turn.Id, segment.Id, CollaborationAnnotationType.Note, "combined", "tester");

        var result = await _repo.ListAnnotationsAsync("proj", session.Id, new CollaborationAnnotationListOptions
        {
            TurnId = turn.Id,
            SegmentId = segment.Id
        });
        Assert.Single(result);
        Assert.Equal(a1.Id, result[0].Id);

        var noMatch = await _repo.ListAnnotationsAsync("proj", session.Id, new CollaborationAnnotationListOptions
        {
            TurnId = turn.Id,
            SegmentId = 99999
        });
        Assert.Empty(noMatch);
    }

    [Fact]
    public async Task ListAnnotations_ForMissingSession_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _repo.ListAnnotationsAsync("proj", 99999, new CollaborationAnnotationListOptions()));
    }

    [Fact]
    public async Task DeleteAnnotation_WithCorrectRevision_Deletes()
    {
        var session = await NewSessionAsync();
        var turn = Assert.Single(session.Turns);
        var segment = turn.Segments[0];

        var annotation = await _repo.CreateAnnotationAsync("proj", session.Id, turn.Id, segment.Id, CollaborationAnnotationType.Note, "delete me", "tester");
        Assert.Equal(1, annotation.Revision);

        var deleted = await _repo.DeleteAnnotationAsync("proj", session.Id, annotation.Id, expectedRevision: 1);
        Assert.Equal(annotation.Id, deleted.Id);
        Assert.Equal(1, deleted.Revision);

        // Verify it's gone
        var all = await _repo.ListAnnotationsAsync("proj", session.Id, new CollaborationAnnotationListOptions());
        Assert.DoesNotContain(all, a => a.Id == annotation.Id);
    }

    [Fact]
    public async Task DeleteAnnotation_WithStaleRevision_ThrowsConflict()
    {
        var session = await NewSessionAsync();
        var turn = Assert.Single(session.Turns);
        var segment = turn.Segments[0];

        var annotation = await _repo.CreateAnnotationAsync("proj", session.Id, turn.Id, segment.Id, CollaborationAnnotationType.Note, "stale test", "tester");
        Assert.Equal(1, annotation.Revision);

        // Update to bump revision
        await _repo.UpdateAnnotationAsync("proj", session.Id, annotation.Id, 1, CollaborationAnnotationType.Flag, "updated", "tester");

        // Try to delete with stale revision 1
        var ex = await Assert.ThrowsAsync<CollaborationConflictException>(() =>
            _repo.DeleteAnnotationAsync("proj", session.Id, annotation.Id, expectedRevision: 1));
        Assert.Contains("changed since revision 1", ex.Message);

        // Delete with current revision 2 should work
        var deleted = await _repo.DeleteAnnotationAsync("proj", session.Id, annotation.Id, expectedRevision: 2);
        Assert.Equal(annotation.Id, deleted.Id);
    }

    [Fact]
    public async Task DeleteAnnotation_ForMissingAnnotation_ThrowsNotFound()
    {
        var session = await NewSessionAsync();
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _repo.DeleteAnnotationAsync("proj", session.Id, 99999, expectedRevision: 1));
    }

    [Fact]
    public async Task DeleteAnnotation_WithInvalidRevision_ThrowsArgument()
    {
        var session = await NewSessionAsync();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _repo.DeleteAnnotationAsync("proj", session.Id, 1, expectedRevision: 0));
    }

    [Fact]
    public async Task InsertTurn_WithWhitespaceOnly_ThrowsValidation()
    {
        var session = await NewSessionAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _repo.AddTurnAsync("proj", session.Id, new CreateCollaborationTurnRequestModel
            {
                Role = "assistant",
                RawMarkdown = "   \n\n  \n "
            }));
        Assert.Contains("raw markdown", ex.Message.ToLowerInvariant());
    }

    private Task<CollaborationSession> NewSessionAsync() => _repo.CreateSessionAsync(new CreateCollaborationSessionRequestModel
    {
        ProjectId = "proj",
        TaskId = _task.Id,
        Title = "Annotate reply",
        CreatedBy = "tester",
        InitialTurn = new CreateCollaborationTurnRequestModel
        {
            Role = "assistant",
            SourceKind = "markdown",
            RawMarkdown = "## Reply\n\nParagraph to annotate."
        }
    });
}
