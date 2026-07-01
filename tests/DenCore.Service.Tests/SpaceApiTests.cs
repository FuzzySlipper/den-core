using System.Net.Http.Json;
using System.Text.Json;
using DenCore.Data;
using DenCore.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DenCore.Service.Tests;

public sealed class SpaceApiTests : IAsyncLifetime
{
    private SpaceAppFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new SpaceAppFactory();
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ─── GET /api/projects defaults ──────────────────────────────────────

    [Fact]
    public async Task GetProjects_Defaults_ToProjectKindOnly()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();

        await repo.CreateAsync(new Project { Id = $"proj-visible-{suffix}", Name = "Visible Project" });
        await repo.CreateAsync(new Project { Id = $"proj-hidden-{suffix}", Name = "Hidden Project", Visibility = "hidden" });
        await repo.CreateAsync(new Project { Id = $"assistant-{suffix}", Name = "Assistant Space", Kind = "assistant" });
        await repo.CreateAsync(new Project { Id = $"personal-{suffix}", Name = "Personal Space", Kind = "personal" });
        await repo.CreateAsync(new Project { Id = $"kb-{suffix}", Name = "Knowledge Base", Kind = "knowledge_base" });
        await repo.CreateAsync(new Project { Id = $"system-{suffix}", Name = "System Space", Kind = "system" });

        var response = await _client.GetAsync("/api/projects");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToHashSet();

        Assert.Contains($"proj-visible-{suffix}", ids);
        Assert.DoesNotContain($"proj-hidden-{suffix}", ids);
        Assert.DoesNotContain($"assistant-{suffix}", ids);
        Assert.DoesNotContain($"personal-{suffix}", ids);
        Assert.DoesNotContain($"kb-{suffix}", ids);
        Assert.DoesNotContain($"system-{suffix}", ids);
    }

    // ─── GET /api/spaces ─────────────────────────────────────────────────

    [Fact]
    public async Task GetSpaces_Default_ExcludesHiddenAndArchived()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();

        await repo.CreateAsync(new Project { Id = $"proj-visible-{suffix}", Name = "Visible Project" });
        await repo.CreateAsync(new Project { Id = $"proj-hidden-{suffix}", Name = "Hidden Project", Visibility = "hidden" });
        await repo.CreateAsync(new Project { Id = $"assistant-{suffix}", Name = "Assistant Space", Kind = "assistant" });

        var response = await _client.GetAsync("/api/spaces");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToHashSet();

        Assert.Contains($"proj-visible-{suffix}", ids);
        Assert.Contains($"assistant-{suffix}", ids);
        Assert.DoesNotContain($"proj-hidden-{suffix}", ids);
    }

    [Fact]
    public async Task GetSpaces_WithKindFilter()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();

        await repo.CreateAsync(new Project { Id = $"proj-visible-{suffix}", Name = "Visible Project" });
        await repo.CreateAsync(new Project { Id = $"assistant-{suffix}", Name = "Assistant Space", Kind = "assistant" });

        var response = await _client.GetAsync($"/api/spaces?kind=assistant");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToHashSet();

        Assert.Contains($"assistant-{suffix}", ids);
        Assert.DoesNotContain($"proj-visible-{suffix}", ids);
    }

    [Fact]
    public async Task GetSpaces_IncludeHidden()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();

        await repo.CreateAsync(new Project { Id = $"proj-visible-{suffix}", Name = "Visible Project" });
        await repo.CreateAsync(new Project { Id = $"proj-hidden-{suffix}", Name = "Hidden Project", Visibility = "hidden" });

        var response = await _client.GetAsync("/api/spaces?includeHidden=true");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToHashSet();

        Assert.Contains($"proj-visible-{suffix}", ids);
        Assert.Contains($"proj-hidden-{suffix}", ids);
    }

    [Fact]
    public async Task GetSpaces_IncludeArchived()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();

        await repo.CreateAsync(new Project { Id = $"proj-visible-{suffix}", Name = "Visible Project" });
        await repo.CreateAsync(new Project { Id = $"proj-archived-{suffix}", Name = "Archived Project", Visibility = "archived" });

        var response = await _client.GetAsync("/api/spaces?includeArchived=true");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToHashSet();

        Assert.Contains($"proj-visible-{suffix}", ids);
        Assert.Contains($"proj-archived-{suffix}", ids);
    }

    [Fact]
    public async Task GetSpaces_Default_IncludesAllVisibleKinds()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();

        await repo.CreateAsync(new Project { Id = $"proj-visible-{suffix}", Name = "Visible Project" });
        await repo.CreateAsync(new Project { Id = $"system-{suffix}", Name = "System Space", Kind = "system" });
        await repo.CreateAsync(new Project { Id = $"personal-{suffix}", Name = "Personal Space", Kind = "personal" });

        var response = await _client.GetAsync("/api/spaces");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToHashSet();

        Assert.Contains($"proj-visible-{suffix}", ids);
        Assert.Contains($"system-{suffix}", ids);
        Assert.Contains($"personal-{suffix}", ids);
    }

    [Fact]
    public async Task GetSpaces_ExcludesHiddenSpacesByDefault()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();

        await repo.CreateAsync(new Project { Id = $"proj-visible-{suffix}", Name = "Visible Project" });
        await repo.CreateAsync(new Project { Id = $"system-hidden-{suffix}", Name = "Hidden System Space", Kind = "system", Visibility = "hidden" });
        await repo.CreateAsync(new Project { Id = $"personal-hidden-{suffix}", Name = "Hidden Personal Space", Kind = "personal", Visibility = "hidden" });

        var response = await _client.GetAsync("/api/spaces");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToHashSet();

        Assert.Contains($"proj-visible-{suffix}", ids);
        Assert.DoesNotContain($"system-hidden-{suffix}", ids);
        Assert.DoesNotContain($"personal-hidden-{suffix}", ids);
    }

    // ─── POST /api/spaces ────────────────────────────────────────────────

    [Fact]
    public async Task PostSpace_CreatesNonProjectSpace()
    {
        var id = $"new-assistant-{Guid.NewGuid():N}";
        var request = new { id, name = "New Assistant", kind = "assistant", visibility = "normal" };
        var response = await _client.PostAsJsonAsync("/api/spaces", request);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(id, doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("assistant", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("normal", doc.RootElement.GetProperty("visibility").GetString());
    }

    [Fact]
    public async Task PostSpace_DefaultsToProjectKind()
    {
        var id = $"new-project-{Guid.NewGuid():N}";
        var request = new { id, name = "New Project" };
        var response = await _client.PostAsJsonAsync("/api/spaces", request);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(id, doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("project", doc.RootElement.GetProperty("kind").GetString());
    }

    // ─── GET /api/spaces/{id} ────────────────────────────────────────────

    [Fact]
    public async Task GetProject_ReturnsRetiredSummaryTombstone()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await repo.CreateAsync(new Project { Id = $"project-summary-{suffix}", Name = "Project Summary" });

        var response = await _client.GetAsync($"/api/projects/project-summary-{suffix}?agent=codex");

        Assert.Equal(System.Net.HttpStatusCode.Gone, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("legacy_project_summary_retired", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal($"project-summary-{suffix}", doc.RootElement.GetProperty("project").GetProperty("id").GetString());
        Assert.True(doc.RootElement.GetProperty("successor_sources").TryGetProperty("task_counts_by_status", out _));
        Assert.False(doc.RootElement.TryGetProperty("task_counts_by_status", out _));
        Assert.False(doc.RootElement.TryGetProperty("unread_message_count", out _));
    }

    [Fact]
    public async Task GetSpace_ReturnsRetiredSummaryTombstone()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await repo.CreateAsync(new Project { Id = $"space-stats-{suffix}", Name = "Space Stats" });

        var response = await _client.GetAsync($"/api/spaces/space-stats-{suffix}");

        Assert.Equal(System.Net.HttpStatusCode.Gone, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("legacy_project_summary_retired", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal($"space-stats-{suffix}", doc.RootElement.GetProperty("project").GetProperty("id").GetString());
        Assert.True(doc.RootElement.GetProperty("successor_sources").TryGetProperty("unread_message_count", out _));
        Assert.False(doc.RootElement.TryGetProperty("task_counts_by_status", out _));
        Assert.False(doc.RootElement.TryGetProperty("unread_message_count", out _));
    }

    [Fact]
    public async Task GetSpace_NotFound_Returns404()
    {
        var response = await _client.GetAsync("/api/spaces/nonexistent");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    // ─── PATCH /api/spaces/{id}/visibility ───────────────────────────────

    [Fact]
    public async Task PatchVisibility_UpdatesToHidden()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await repo.CreateAsync(new Project { Id = $"vis-test-{suffix}", Name = "Visibility Test" });

        var patchReq = new { visibility = "hidden" };
        var response = await _client.PatchAsJsonAsync($"/api/spaces/vis-test-{suffix}/visibility", patchReq);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal($"vis-test-{suffix}", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("hidden", doc.RootElement.GetProperty("visibility").GetString());

        // Verify it's excluded from default listing
        var listResponse = await _client.GetAsync("/api/spaces");
        using var listDoc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var ids = listDoc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToHashSet();
        Assert.DoesNotContain($"vis-test-{suffix}", ids);
    }

    [Fact]
    public async Task PatchVisibility_UpdatesToArchived()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await repo.CreateAsync(new Project { Id = $"arch-test-{suffix}", Name = "Archive Test" });

        var patchReq = new { visibility = "archived" };
        var response = await _client.PatchAsJsonAsync($"/api/spaces/arch-test-{suffix}/visibility", patchReq);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("archived", doc.RootElement.GetProperty("visibility").GetString());
    }

    [Fact]
    public async Task PatchVisibility_CanRestoreFromHidden()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await repo.CreateAsync(new Project { Id = $"restore-test-{suffix}", Name = "Restore Test", Visibility = "hidden" });

        // Restore to normal
        var patchReq = new { visibility = "normal" };
        var response = await _client.PatchAsJsonAsync($"/api/spaces/restore-test-{suffix}/visibility", patchReq);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("normal", doc.RootElement.GetProperty("visibility").GetString());

        // Verify it shows up in default listing
        var listResponse = await _client.GetAsync("/api/spaces");
        using var listDoc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var ids = listDoc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToHashSet();
        Assert.Contains($"restore-test-{suffix}", ids);
    }

    [Fact]
    public async Task PatchVisibility_NotFound_Returns404()
    {
        var patchReq = new { visibility = "hidden" };
        var response = await _client.PatchAsJsonAsync("/api/spaces/nonexistent/visibility", patchReq);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PatchVisibility_RejectsInvalidValue()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await repo.CreateAsync(new Project { Id = $"inv-vis-{suffix}", Name = "Invalid Visibility" });

        // Invalid visibility values should be rejected with 400
        foreach (var badVal in new[] { "visible", "deleted", "public", "private", "" })
        {
            var patchReq = new { visibility = badVal };
            var response = await _client.PatchAsJsonAsync($"/api/spaces/inv-vis-{suffix}/visibility", patchReq);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Contains("invalid visibility", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        }

        // Verify space was not modified
        var project = await repo.GetByIdAsync($"inv-vis-{suffix}");
        Assert.NotNull(project);
        Assert.Equal("normal", project!.Visibility);
    }

    // ─── POST /api/spaces/{id}/archive ───────────────────────────────────

    [Fact]
    public async Task PostArchive_ArchivesSpace()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await repo.CreateAsync(new Project { Id = $"arc-{suffix}", Name = "To Archive" });

        var response = await _client.PostAsync($"/api/spaces/arc-{suffix}/archive", null);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("archived", doc.RootElement.GetProperty("visibility").GetString());

        // Excluded from default listing
        var listResponse = await _client.GetAsync("/api/spaces");
        using var listDoc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var ids = listDoc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToHashSet();
        Assert.DoesNotContain($"arc-{suffix}", ids);

        // Included with includeArchived=true
        var archivedResponse = await _client.GetAsync("/api/spaces?includeArchived=true");
        using var archivedDoc = JsonDocument.Parse(await archivedResponse.Content.ReadAsStringAsync());
        var archivedIds = archivedDoc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToHashSet();
        Assert.Contains($"arc-{suffix}", archivedIds);
    }

    [Fact]
    public async Task PostArchive_NotFound_Returns404()
    {
        var response = await _client.PostAsync("/api/spaces/nonexistent/archive", null);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    // ─── DELETE /api/spaces/{id} ─────────────────────────────────────────

    [Fact]
    public async Task DeleteSpace_RefusesSystemKind()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await repo.CreateAsync(new Project { Id = $"sys-del-{suffix}", Name = "System Space", Kind = "system" });

        var response = await _client.DeleteAsync($"/api/spaces/sys-del-{suffix}");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("protected", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);

        // Space still exists
        Assert.NotNull(await repo.GetByIdAsync($"sys-del-{suffix}"));
    }

    [Fact]
    public async Task DeleteSpace_RefusesPersonalKind()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await repo.CreateAsync(new Project { Id = $"pers-del-{suffix}", Name = "Personal Space", Kind = "personal" });

        var response = await _client.DeleteAsync($"/api/spaces/pers-del-{suffix}");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("protected", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteSpace_RefusesCoreProjectIds()
    {
        foreach (var coreId in new[] { "den", "den-core", "core" })
        {
            using var scope = _factory.Services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();

            // Use the exact core ID (no suffix) so the guard catches it
            var existing = await repo.GetByIdAsync(coreId);
            if (existing is null)
            {
                await repo.CreateAsync(new Project { Id = coreId, Name = $"Core {coreId}" });
            }

            var response = await _client.DeleteAsync($"/api/spaces/{coreId}");
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Contains("protected", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task DeleteSpace_ReportsDependentsAndRefuses()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await repo.CreateAsync(new Project { Id = $"dep-test-{suffix}", Name = "Dep Test" });

        // Create a dependent task
        var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        await taskRepo.CreateAsync(new ProjectTask
        {
            ProjectId = $"dep-test-{suffix}",
            Title = "Dependent Task"
        });

        var response = await _client.DeleteAsync($"/api/spaces/dep-test-{suffix}");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("dependent", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);

        // Verify dependent_counts are reported
        var dependentCounts = doc.RootElement.GetProperty("dependent_counts");
        Assert.True(dependentCounts.TryGetProperty("tasks", out var taskCount));
        Assert.True(taskCount.GetInt32() >= 1);
    }

    [Fact]
    public async Task DeleteSpace_WithForce_DeletesEmptySpace()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await repo.CreateAsync(new Project { Id = $"force-del-{suffix}", Name = "Force Delete Me", Kind = "assistant" });

        var response = await _client.DeleteAsync($"/api/spaces/force-del-{suffix}?force=true");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("deleted").GetBoolean());
        Assert.Equal($"force-del-{suffix}", doc.RootElement.GetProperty("id").GetString());

        // Verify gone
        Assert.Null(await repo.GetByIdAsync($"force-del-{suffix}"));
    }

    [Fact]
    public async Task DeleteSpace_NotFound_Returns404()
    {
        var response = await _client.DeleteAsync("/api/spaces/nonexistent");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteSpace_WithForce_DeletesWithDependents()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await repo.CreateAsync(new Project { Id = $"force-dep-{suffix}", Name = "Force Delete Deps", Kind = "assistant" });

        // Create dependent task
        var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        await taskRepo.CreateAsync(new ProjectTask
        {
            ProjectId = $"force-dep-{suffix}",
            Title = "Dependent Task"
        });

        // force=true should bypass dependency check
        var response = await _client.DeleteAsync($"/api/spaces/force-dep-{suffix}?force=true");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("deleted").GetBoolean());

        // Verify gone
        Assert.Null(await repo.GetByIdAsync($"force-dep-{suffix}"));
    }

    [Fact]
    public async Task DeleteSpace_WithForce_BypassesProtectedKindGuard()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await repo.CreateAsync(new Project { Id = $"force-kind-{suffix}", Name = "Force Delete System", Kind = "system" });

        // force=true bypasses the protected kind check for system kind
        var response = await _client.DeleteAsync($"/api/spaces/force-kind-{suffix}?force=true");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("deleted").GetBoolean());

        // Verify gone
        Assert.Null(await repo.GetByIdAsync($"force-kind-{suffix}"));
    }

    [Fact]
    public async Task DeleteSpace_WithForce_BypassesProtectedPersonalKindGuard()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await repo.CreateAsync(new Project { Id = $"force-pers-{suffix}", Name = "Force Delete Personal", Kind = "personal" });

        // force=true bypasses the protected kind check for personal kind
        var response = await _client.DeleteAsync($"/api/spaces/force-pers-{suffix}?force=true");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("deleted").GetBoolean());

        // Verify gone
        Assert.Null(await repo.GetByIdAsync($"force-pers-{suffix}"));
    }

    [Fact]
    public async Task DeleteSpace_WithForce_BypassesProtectedCoreIdGuard()
    {
        // Create a space using core IDs that normally would be protected
        var testIds = new[] { "den-force-test", "den-core-force-test", "core-force-test" };
        foreach (var coreId in testIds)
        {
            using var scope = _factory.Services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
            var existing = await repo.GetByIdAsync(coreId);
            if (existing is null)
            {
                await repo.CreateAsync(new Project { Id = coreId, Name = $"Force Core {coreId}" });
            }

            // force=true bypasses the protected core-id check
            var response = await _client.DeleteAsync($"/api/spaces/{coreId}?force=true");
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("deleted").GetBoolean());

            // Verify gone
            Assert.Null(await repo.GetByIdAsync(coreId));
        }
    }

    // ─── MCP tool tests ────────────────────────────────────────────────────
    // MCP tools are tested via the HTTP REST API endpoints above.
    // Streamable HTTP transport tests belong in McpEndpointTests.cs.

    private sealed class SpaceAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-space-{Guid.NewGuid()}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("DenCore:Provider", "Postgres");
            builder.UseSetting("DenCore:ConnectionString", DatabaseInitializer.GetConnectionString(_dbPath));
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["db-path"] = _dbPath,
                    ["DenCore:ConnectionString"] = DatabaseInitializer.GetConnectionString(_dbPath),
                    ["llm-endpoint"] = "http://localhost/fake",
                    ["llm-api-key"] = "test-key",
                    ["llm-model"] = "fake"
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            DatabaseInitializer.DisposeLeaseAsync(_dbPath).AsTask().GetAwaiter().GetResult();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
    }
}
