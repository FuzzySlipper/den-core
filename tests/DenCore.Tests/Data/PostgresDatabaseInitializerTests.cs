using DenCore.Data;
using DenCore.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace DenCore.Tests.Data;

public class PostgresDatabaseInitializerTests
{
    [Fact]
    public void InitialSchema_IncludesRouteFacingRepositoryColumns()
    {
        var schema = PostgresDatabaseInitializer.InitialSchema;

        foreach (var column in new[]
        {
            "instance_id",
            "agent_family",
            "transport_kind",
            "session_id",
            "thread_id",
            "delivery_mode",
            "body",
            "dedup_key",
            "target_agent",
            "trigger_type",
            "trigger_id",
            "summary",
            "context_prompt",
            "context_json",
            "expires_at",
            "decided_at",
            "completed_at",
            "decided_by",
            "completed_by"
        })
        {
            Assert.Contains(column, schema);
        }

        Assert.DoesNotContain("content               TEXT", schema);
        Assert.Contains("tags       JSONB", schema);
        Assert.Contains("CREATE UNIQUE INDEX IF NOT EXISTS idx_agent_stream_dedup", schema);
        Assert.Contains("CREATE UNIQUE INDEX IF NOT EXISTS idx_dispatch_dedup", schema);
    }

    [Fact]
    public void FullTextSearchSchema_ReplacesFtsShadowTablesWithGinIndexes()
    {
        var schema = PostgresDatabaseInitializer.FullTextSearchSchema;

        Assert.Contains("DROP TABLE IF EXISTS documents_fts", schema);
        Assert.Contains("DROP TABLE IF EXISTS knowledge_entries_fts", schema);
        Assert.Contains("idx_documents_search_gin", schema);
        Assert.Contains("idx_knowledge_entries_search_gin", schema);
        Assert.Contains("coalesce(tags::text, '')", schema);
        Assert.Contains("CREATE TABLE IF NOT EXISTS knowledge_entries", schema);
        Assert.DoesNotContain("CREATE VIRTUAL TABLE", schema);
        Assert.DoesNotContain("CREATE TABLE IF NOT EXISTS documents_fts", schema);
        Assert.DoesNotContain("CREATE TABLE IF NOT EXISTS knowledge_entries_fts", schema);
    }

    [Fact]
    [Trait("Category", "PostgresProvider")]
    public async Task Initialize_WhenConfigured_SupportsRepresentativeNonFtsRepositories()
    {
        if (!PostgresTestDb.IsConfigured)
            return;

        var testDb = new PostgresTestDb();
        await testDb.InitializeAsync();
        try
        {
            var initializer = new PostgresDatabaseInitializer(
                testDb.Db,
                NullLogger<PostgresDatabaseInitializer>.Instance);
            await initializer.InitializeAsync();

            var projects = new ProjectRepository(testDb.Db);
            var tasks = new TaskRepository(testDb.Db);
            var messages = new MessageRepository(testDb.Db);
            var documents = new DocumentRepository(testDb.Db);
            var usage = new UsageCostRepository(testDb.Db);
            var workerPool = new WorkerPoolRepository(testDb.Db);
            var bindings = new AgentInstanceBindingRepository(testDb.Db);
            var dispatch = new DispatchRepository(testDb.Db);
            var stream = new AgentStreamRepository(testDb.Db);

            var project = await projects.CreateAsync(new Project
            {
                Id = "pg-proj",
                Name = "Postgres Project"
            });
            Assert.Equal("pg-proj", project.Id);

            var task = await tasks.CreateAsync(new ProjectTask
            {
                ProjectId = project.Id,
                Title = "Postgres task",
                Status = DenCore.Models.TaskStatus.Planned,
                Tags = ["postgres", "phase-0c"]
            });
            task = await tasks.UpdateAsync(
                task.Id,
                new Dictionary<string, object?> { ["status"] = DenCore.Models.TaskStatus.InProgress },
                "postgres-test");
            Assert.Equal(DenCore.Models.TaskStatus.InProgress, task.Status);

            var message = await messages.CreateAsync(new Message
            {
                ProjectId = project.Id,
                TaskId = task.Id,
                Sender = "postgres-test",
                Content = "hello from postgres",
                Intent = MessageIntent.StatusUpdate
            });
            Assert.True(message.Id > 0);

            await messages.MarkReadAsync("postgres-reader", [message.Id]);
            var unread = await messages.GetMessagesAsync(project.Id, unreadFor: "postgres-reader");
            Assert.Empty(unread);

            var doc = await documents.UpsertAsync(new Document
            {
                ProjectId = project.Id,
                Slug = "pg-doc",
                Title = "Postgres Doc",
                Content = "Body",
                DocType = DocType.Spec,
                Tags = ["postgres"],
                Summary = "Document metadata path"
            });
            Assert.True(doc.Id > 0);

            var docs = await documents.ListAsync(project.Id, tags: ["postgres"]);
            Assert.Single(docs);

            var snapshot = await usage.EnsureDefaultPricingSnapshotAsync();
            var usageEvent = await usage.RecordUsageEventAsync(new ModelUsageEvent
            {
                OccurredAt = DateTime.UtcNow.ToString("O"),
                ProjectId = project.Id,
                TaskId = task.Id,
                OperationKind = "test",
                Provider = "local",
                Model = "llama-3-70b",
                RequestCount = 1
            });
            Assert.True(snapshot.Id > 0);
            Assert.True(usageEvent.Id > 0);

            var member = await workerPool.UpsertMemberAsync(new WorkerPoolMember
            {
                WorkerIdentity = "pg-worker-1",
                ProfileIdentity = "postgres-profile",
                WorkerRole = "coder",
                Status = WorkerPoolStates.MemberAvailable,
                LastHeartbeat = DateTime.UtcNow.ToString("O")
            });
            Assert.Equal("pg-worker-1", member.WorkerIdentity);

            var members = await workerPool.ListMembersAsync(new WorkerPoolMemberListOptions
            {
                ProfileIdentity = "postgres-profile"
            });
            Assert.Single(members);

            var binding = await bindings.UpsertAsync(new AgentInstanceBinding
            {
                InstanceId = "pg-instance-1",
                ProjectId = project.Id,
                AgentIdentity = "pg-agent",
                AgentFamily = "codex",
                Role = "coder",
                TransportKind = "direct",
                SessionId = "pg-session-1",
                Status = AgentInstanceBindingStatus.Active,
                Metadata = "{\"provider\":\"postgres-test\"}"
            });
            Assert.Equal("pg-instance-1", binding.InstanceId);
            Assert.NotNull(await bindings.GetActiveByInstanceIdAsync(binding.InstanceId));

            var dispatchEntry = new DispatchEntry
            {
                ProjectId = project.Id,
                TargetAgent = "pg-agent",
                Status = DispatchStatus.Pending,
                TriggerType = DispatchTriggerType.Message,
                TriggerId = message.Id,
                TaskId = task.Id,
                Summary = "Postgres dispatch",
                ContextPrompt = "context",
                ContextJson = "{\"ok\":true}",
                DedupKey = DispatchEntry.BuildDedupKey(DispatchTriggerType.Message, message.Id, "pg-agent"),
                ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            };
            var (createdDispatch, created) = await dispatch.CreateIfAbsentAsync(dispatchEntry);
            Assert.True(created);
            Assert.Equal("pg-agent", createdDispatch.TargetAgent);

            var streamEntry = await stream.AppendAsync(new AgentStreamEntry
            {
                StreamKind = AgentStreamKind.Message,
                EventType = "review_requested",
                ProjectId = project.Id,
                TaskId = task.Id,
                ThreadId = message.Id,
                DispatchId = createdDispatch.Id,
                Sender = "pg-agent",
                SenderInstanceId = binding.InstanceId,
                RecipientAgent = "pg-reviewer",
                RecipientRole = "reviewer",
                DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
                Body = "please review",
                Metadata = JsonSerializer.Deserialize<JsonElement>("{\"run_id\":\"pg-run-1\"}"),
                DedupKey = "pg-stream-dedup"
            });
            Assert.True(streamEntry.Id > 0);

            var streamResults = await stream.ListAsync(new AgentStreamListOptions
            {
                ProjectId = project.Id,
                MetadataRunId = "pg-run-1",
                IncludeDebug = true
            });
            Assert.Single(streamResults);
        }
        finally
        {
            await testDb.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresProvider")]
    public async Task Initialize_WhenConfigured_SupportsDocumentAndKnowledgeFullTextSearch()
    {
        if (!PostgresTestDb.IsConfigured)
            return;

        var testDb = new PostgresTestDb();
        await testDb.InitializeAsync();
        try
        {
            var initializer = new PostgresDatabaseInitializer(
                testDb.Db,
                NullLogger<PostgresDatabaseInitializer>.Instance);
            await initializer.InitializeAsync();

            var projects = new ProjectRepository(testDb.Db);
            var documents = new DocumentRepository(testDb.Db);
            var knowledge = new KnowledgeRepository(testDb.Db);

            var project = await projects.CreateAsync(new Project
            {
                Id = "pg-search-proj",
                Name = "Postgres Search Project"
            });
            await projects.CreateAsync(new Project
            {
                Id = "pg-search-other",
                Name = "Postgres Search Other"
            });

            await documents.UpsertAsync(new Document
            {
                ProjectId = project.Id,
                Slug = "pg-search-normal",
                Title = "Postgres Search Normal",
                Content = "The searchable phrase appears in this normal document.",
                DocType = DocType.Spec
            });
            await documents.UpsertAsync(new Document
            {
                ProjectId = "pg-search-other",
                Slug = "pg-search-other",
                Title = "Postgres Search Other",
                Content = "The searchable phrase appears in another project.",
                DocType = DocType.Spec
            });
            await documents.UpsertAsync(new Document
            {
                ProjectId = project.Id,
                Slug = "pg-search-archived",
                Title = "Postgres Search Archived",
                Content = "A retired search phrase lives in an archived document.",
                DocType = DocType.Reference,
                Visibility = DocumentVisibility.Archived
            });

            var scopedDocs = await documents.SearchAsync("searchable phrase?!", project.Id);
            Assert.Single(scopedDocs);
            Assert.Equal("pg-search-normal", scopedDocs[0].Slug);
            Assert.Contains("<b>", scopedDocs[0].Snippet);

            var archivedDocs = await documents.SearchArchivedAsync("\"retired search phrase\"", project.Id);
            Assert.Single(archivedDocs);
            Assert.Equal("pg-search-archived", archivedDocs[0].Slug);
            Assert.Equal(DocumentVisibility.Archived, archivedDocs[0].Visibility);

            var stopwordDocs = await documents.SearchAsync("the is to of", project.Id);
            Assert.Empty(stopwordDocs);

            await knowledge.UpsertAsync(new KnowledgeEntry
            {
                Slug = "pg-fts-reviewed",
                Title = "Postgres FTS Reviewed",
                Summary = "Reviewed search summary",
                BodyMarkdown = "Postgres tsvector query planner details for full-text search.",
                Kind = KnowledgeEntryKinds.Reference,
                Status = KnowledgeEntryStatuses.Reviewed,
                CurationState = KnowledgeCurationStates.AgentCurated,
                Tags = ["fts", "postgres"],
                Audience = ["runner"]
            });
            await knowledge.UpsertAsync(new KnowledgeEntry
            {
                Slug = "pg-fts-draft",
                Title = "Postgres FTS Draft",
                BodyMarkdown = "Postgres tsvector query planner draft details for full-text search.",
                Kind = KnowledgeEntryKinds.Reference,
                Status = KnowledgeEntryStatuses.Draft,
                CurationState = KnowledgeCurationStates.UnreviewedImport,
                Tags = ["fts", "postgres"]
            });

            var reviewedKnowledge = await knowledge.SearchAsync(new KnowledgeSearchQuery
            {
                Query = "tsvector query! planner?",
                RequiredTags = ["fts"],
                Audience = ["runner"]
            });
            Assert.Single(reviewedKnowledge);
            Assert.Equal("pg-fts-reviewed", reviewedKnowledge[0].Slug);
            Assert.Contains("<b>", reviewedKnowledge[0].Snippet);

            var allKnowledge = await knowledge.SearchAsync(new KnowledgeSearchQuery
            {
                Query = "tsvector query planner",
                RequiredTags = ["fts"],
                IncludeUnreviewed = true
            });
            Assert.Equal(2, allKnowledge.Count);

            var stopwordKnowledge = await knowledge.SearchAsync(new KnowledgeSearchQuery
            {
                Query = "the is to of"
            });
            Assert.Empty(stopwordKnowledge);
        }
        finally
        {
            await testDb.DisposeAsync();
        }
    }
}
