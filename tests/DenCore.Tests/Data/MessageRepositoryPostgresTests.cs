using System.Globalization;
using System.Text.Json;
using DenCore.Data;
using DenCore.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace DenCore.Tests.Data;

public sealed class MessageRepositoryPostgresTests
{
    [Fact]
    [Trait("Category", "PostgresProvider")]
    public async Task CreateAsync_WritesJsonbMetadataAndUtcTimestampThatSortsAfterImportedRows()
    {
        if (!PostgresTestDb.IsConfigured)
            return;

        var testDb = new PostgresTestDb();
        await testDb.InitializeAsync();
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(testDb.ConnectionString);
            builder["Options"] = "-c TimeZone=America/Los_Angeles";
            var db = new DbConnectionFactory(builder.ConnectionString, DatabaseProviderKind.Postgres);

            var initializer = new PostgresDatabaseInitializer(
                db,
                NullLogger<PostgresDatabaseInitializer>.Instance);
            await initializer.InitializeAsync();

            var projects = new ProjectRepository(db);
            var messages = new MessageRepository(db);

            var project = await projects.CreateAsync(new Project
            {
                Id = "pg-message-cutover",
                Name = "Postgres Message Cutover"
            });

            var importedCreatedAt = DateTime.UtcNow
                .AddMinutes(-5)
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            await SeedImportedMessageAsync(db, project.Id, importedCreatedAt);

            var metadata = JsonSerializer.Deserialize<JsonElement>(
                """{"type":"status_update","provider":"Postgres","task_id":3326}""");
            var created = await messages.CreateAsync(new Message
            {
                ProjectId = project.Id,
                Sender = "cutover-smoke",
                Content = "Live Postgres provider metadata write/read ordering check.",
                Metadata = metadata
            });

            var rawCreatedAt = await ReadRawCreatedAtAsync(db, created.Id);
            Assert.Matches(
                @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$",
                rawCreatedAt);

            var fetched = await messages.GetByIdAsync(created.Id);
            Assert.NotNull(fetched);
            Assert.Equal(MessageIntent.StatusUpdate, fetched!.Intent);
            Assert.Equal("Postgres", fetched.Metadata!.Value.GetProperty("provider").GetString());
            Assert.Equal(3326, fetched.Metadata.Value.GetProperty("task_id").GetInt32());

            var recent = await messages.GetMessagesAsync(project.Id, limit: 2);
            Assert.Equal(created.Id, recent[0].Id);
            Assert.Equal("imported-before-cutover", recent[1].Sender);
        }
        finally
        {
            await testDb.DisposeAsync();
        }
    }

    private static async Task SeedImportedMessageAsync(
        DbConnectionFactory db,
        string projectId,
        string createdAt)
    {
        await using var conn = await db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO messages (project_id, sender, content, intent, metadata, created_at)
            VALUES (@projectId, 'imported-before-cutover', 'Imported SQLite message', 'status_update', NULL, @createdAt)
            """;
        cmd.AddParameterWithValue("@projectId", projectId);
        cmd.AddParameterWithValue("@createdAt", createdAt);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadRawCreatedAtAsync(DbConnectionFactory db, int messageId)
    {
        await using var conn = await db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT created_at FROM messages WHERE id = @id";
        cmd.AddParameterWithValue("@id", messageId);
        var result = await cmd.ExecuteScalarAsync();
        return Assert.IsType<string>(result);
    }
}
