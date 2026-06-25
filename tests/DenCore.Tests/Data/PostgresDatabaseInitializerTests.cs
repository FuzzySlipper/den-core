using DenCore.Data;
using DenCore.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenCore.Tests.Data;

public class PostgresDatabaseInitializerTests
{
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
        }
        finally
        {
            await testDb.DisposeAsync();
        }
    }
}
