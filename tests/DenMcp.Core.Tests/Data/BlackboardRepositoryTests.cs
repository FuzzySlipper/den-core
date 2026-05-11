using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Core.Tests.Data;

public sealed class BlackboardRepositoryTests : IClassFixture<TestDb>
{
    private readonly TestDb _db;
    private readonly BlackboardRepository _repo;

    public BlackboardRepositoryTests(TestDb db)
    {
        _db = db;
        _repo = new BlackboardRepository(db.Db);
    }

    [Fact]
    public async Task UpsertGetListAndDelete_WorkWithoutProjectScope()
    {
        var created = await _repo.UpsertAsync(new BlackboardEntry
        {
            Slug = $"handoff-{Guid.NewGuid():N}",
            Title = "Handoff Note",
            Content = "# Handoff\n\nRemember this.",
            Tags = ["handoff", "scratch"]
        });

        Assert.True(created.Id > 0);
        Assert.Equal("Handoff Note", created.Title);
        Assert.Null(created.IdleTtlSeconds);
        Assert.True(created.LastAccessedAt >= created.CreatedAt);

        var fetched = await _repo.GetAsync(created.Slug);
        Assert.NotNull(fetched);
        Assert.Equal(created.Slug, fetched!.Slug);
        Assert.Equal("# Handoff\n\nRemember this.", fetched.Content);
        Assert.Equal(new[] { "handoff", "scratch" }, fetched.Tags);

        var listed = await _repo.ListAsync(["handoff"]);
        Assert.Contains(listed, entry => entry.Slug == created.Slug && entry.Title == "Handoff Note");

        var updated = await _repo.UpsertAsync(new BlackboardEntry
        {
            Slug = created.Slug,
            Title = "Updated Handoff",
            Content = "Updated content",
            Tags = ["handoff"],
            IdleTtlSeconds = 3600
        });
        Assert.Equal(created.Id, updated.Id);
        Assert.Equal("Updated Handoff", updated.Title);
        Assert.Equal(3600, updated.IdleTtlSeconds);

        Assert.True(await _repo.DeleteAsync(created.Slug));
        Assert.Null(await _repo.GetAsync(created.Slug));
    }

    [Fact]
    public async Task Get_LazilyDeletesExpiredIdleTtlEntries()
    {
        var slug = $"expired-{Guid.NewGuid():N}";
        await _repo.UpsertAsync(new BlackboardEntry
        {
            Slug = slug,
            Title = "Expired",
            Content = "temporary",
            IdleTtlSeconds = 60
        });
        await SetLastAccessedAsync(slug, DateTime.UtcNow.AddMinutes(-5));

        var fetched = await _repo.GetAsync(slug);

        Assert.Null(fetched);
        Assert.DoesNotContain(await _repo.ListAsync(), entry => entry.Slug == slug);
    }

    [Fact]
    public async Task Get_RefreshesLastAccessedForIdleTtlEntries()
    {
        var slug = $"refresh-{Guid.NewGuid():N}";
        await _repo.UpsertAsync(new BlackboardEntry
        {
            Slug = slug,
            Title = "Refresh",
            Content = "temporary",
            IdleTtlSeconds = 3600
        });
        var oldAccess = DateTime.UtcNow.AddMinutes(-10);
        await SetLastAccessedAsync(slug, oldAccess);

        var fetched = await _repo.GetAsync(slug);

        Assert.NotNull(fetched);
        Assert.True(fetched!.LastAccessedAt > oldAccess.AddMinutes(5));
    }

    [Fact]
    public async Task Cleanup_RemovesExpiredEntriesAndKeepsDurableEntries()
    {
        var expiredSlug = $"cleanup-expired-{Guid.NewGuid():N}";
        var durableSlug = $"cleanup-durable-{Guid.NewGuid():N}";
        await _repo.UpsertAsync(new BlackboardEntry
        {
            Slug = expiredSlug,
            Title = "Expired",
            Content = "temporary",
            IdleTtlSeconds = 1
        });
        await _repo.UpsertAsync(new BlackboardEntry
        {
            Slug = durableSlug,
            Title = "Durable",
            Content = "kept"
        });
        await SetLastAccessedAsync(expiredSlug, DateTime.UtcNow.AddMinutes(-1));

        var deleted = await _repo.DeleteExpiredAsync();

        Assert.Equal(1, deleted);
        Assert.Null(await _repo.GetAsync(expiredSlug));
        Assert.NotNull(await _repo.GetAsync(durableSlug));
    }

    private async Task SetLastAccessedAsync(string slug, DateTime value)
    {
        await using var conn = await _db.Db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE blackboard_entries SET last_accessed_at = @lastAccessed WHERE slug = @slug";
        cmd.Parameters.AddWithValue("@slug", slug);
        cmd.Parameters.AddWithValue("@lastAccessed", value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        await cmd.ExecuteNonQueryAsync();
    }
}
