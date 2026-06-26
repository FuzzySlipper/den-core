using DenCore.Data;
using DenCore.Models;
using Microsoft.Extensions.Logging.Abstractions;
using TaskStatus = DenCore.Models.TaskStatus;

namespace DenCore.Tests.Data;

public sealed class TaskRepositoryPostgresTests
{
    [Fact]
    [Trait("Category", "PostgresProvider")]
    public async Task CreateAsync_WritesJsonbTagsAndSupportsTagFilter()
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

            var project = await projects.CreateAsync(new Project
            {
                Id = "pg-task-tags",
                Name = "Postgres Task Tags"
            });

            var created = await tasks.CreateAsync(new ProjectTask
            {
                ProjectId = project.Id,
                Title = "Task with JSONB tags",
                Tags = ["postgres", "jsonb"]
            });

            Assert.Equal(["postgres", "jsonb"], created.Tags);

            var fetched = await tasks.GetByIdAsync(created.Id);
            Assert.NotNull(fetched);
            Assert.Equal(["postgres", "jsonb"], fetched!.Tags);

            var listed = await tasks.ListAsync(project.Id, tags: ["jsonb"]);
            var listedTask = Assert.Single(listed);
            Assert.Equal(created.Id, listedTask.Id);
            Assert.Equal(["postgres", "jsonb"], listedTask.Tags);

            var updated = await tasks.UpdateAsync(
                created.Id,
                new Dictionary<string, object?>
                {
                    ["tags"] = new List<string> { "postgres", "updated" },
                    ["status"] = TaskStatus.InProgress
                },
                "postgres-test");
            Assert.Equal(["postgres", "updated"], updated.Tags);

            var updatedList = await tasks.ListAsync(project.Id, tags: ["updated"]);
            Assert.Single(updatedList);
        }
        finally
        {
            await testDb.DisposeAsync();
        }
    }
}
