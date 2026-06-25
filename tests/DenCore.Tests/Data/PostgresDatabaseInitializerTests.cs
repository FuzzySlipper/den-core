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
        Assert.Contains("CREATE UNIQUE INDEX IF NOT EXISTS idx_agent_stream_dedup", schema);
        Assert.Contains("CREATE UNIQUE INDEX IF NOT EXISTS idx_dispatch_dedup", schema);
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
}
