using DenCore.Data;
using DenCore.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenCore.Tests.Data;

public sealed class DocumentRepositoryPostgresTests
{
    [Fact]
    [Trait("Category", "PostgresProvider")]
    public async Task DocumentLifecycle_WritesJsonbTagsAndSupportsReadListSearchDelete()
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

            var project = await projects.CreateAsync(new Project
            {
                Id = "pg-doc-lifecycle",
                Name = "Postgres Document Lifecycle"
            });

            var stored = await documents.UpsertAsync(new Document
            {
                ProjectId = project.Id,
                Slug = "jsonb-tags-doc",
                Title = "JSONB Tags Doc",
                Content = "A lifecycle searchable phrase for Postgres document smoke.",
                DocType = DocType.Reference,
                Tags = ["postgres", "jsonb"],
                Summary = "Postgres document lifecycle summary"
            });
            Assert.True(stored.Id > 0);
            Assert.Equal(["postgres", "jsonb"], stored.Tags);

            var fetched = await documents.GetAsync(project.Id, "jsonb-tags-doc");
            Assert.NotNull(fetched);
            Assert.Equal("JSONB Tags Doc", fetched!.Title);
            Assert.Equal(["postgres", "jsonb"], fetched.Tags);
            Assert.Equal("Postgres document lifecycle summary", fetched.Summary);

            var listed = await documents.ListAsync(project.Id, tags: ["jsonb"]);
            var listedDoc = Assert.Single(listed);
            Assert.Equal("jsonb-tags-doc", listedDoc.Slug);
            Assert.Equal(["postgres", "jsonb"], listedDoc.Tags);
            Assert.Equal("Postgres document lifecycle summary", listedDoc.Summary);

            var search = await documents.SearchAsync("\"lifecycle searchable phrase\"", project.Id);
            var searchDoc = Assert.Single(search);
            Assert.Equal("jsonb-tags-doc", searchDoc.Slug);
            Assert.Equal(DocType.Reference, searchDoc.DocType);
            Assert.Equal("Postgres document lifecycle summary", searchDoc.Summary);

            Assert.True(await documents.DeleteAsync(project.Id, "jsonb-tags-doc"));
            Assert.Null(await documents.GetAsync(project.Id, "jsonb-tags-doc"));
            Assert.Empty(await documents.SearchAsync("\"lifecycle searchable phrase\"", project.Id));
        }
        finally
        {
            await testDb.DisposeAsync();
        }
    }
}
