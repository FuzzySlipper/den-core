using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Tests;

namespace DenMcp.Core.Tests.Data;

public class CapabilityRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private ICapabilityRepository _repo = null!;
    private IProjectRepository _projects = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _repo = new CapabilityRepository(_testDb.Db);
        _projects = new ProjectRepository(_testDb.Db);

        await _projects.CreateAsync(new Project { Id = "test-proj", Name = "Test Project" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    private async Task SeedDefinition(string id, string status = CapabilityStatuses.Enabled)
    {
        await _repo.UpsertDefinitionAsync(new CapabilityDefinition
        {
            CapabilityId = id,
            DisplayName = $"Cap {id}",
            Status = status,
        });
    }

    // ─────────────────────────────────────────────────────────────────
    // Schema
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Schema_CreatesCapabilityTables()
    {
        await using var conn = await _testDb.Db.CreateConnectionAsync();

        foreach (var table in new[] { "capability_definitions", "capability_invocations" })
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT count(*) FROM sqlite_master WHERE type='table' AND name='{table}'";
            Assert.Equal(1L, (await cmd.ExecuteScalarAsync())!);
        }

        foreach (var idx in new[] {
            "idx_capability_definitions_status",
            "idx_capability_definitions_side_effect",
            "idx_capability_definitions_owner",
            "idx_cap_invocations_capability",
            "idx_cap_invocations_caller_project",
            "idx_cap_invocations_status",
        })
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT count(*) FROM sqlite_master WHERE type='index' AND name='{idx}'";
            Assert.Equal(1L, (await cmd.ExecuteScalarAsync())!);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Definition CRUD
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertDefinition_CreatesAndReturns()
    {
        var result = await _repo.UpsertDefinitionAsync(new CapabilityDefinition
        {
            CapabilityId = "vision.test.v1",
            DisplayName = "Test Vision",
            Description = "A test capability",
            Status = CapabilityStatuses.Enabled,
            ExecutorKind = "http_endpoint",
            SideEffectLevel = SideEffectLevels.None,
            HttpEndpoint = "http://localhost:9999/analyze",
        });

        Assert.Equal("vision.test.v1", result.CapabilityId);
        Assert.Equal("Test Vision", result.DisplayName);
        Assert.Equal(CapabilityStatuses.Enabled, result.Status);
        Assert.Equal("http_endpoint", result.ExecutorKind);
        Assert.Equal("http://localhost:9999/analyze", result.HttpEndpoint);
    }

    [Fact]
    public async Task UpsertDefinition_UpdatesExisting()
    {
        await SeedDefinition("upd-cap");
        var updated = await _repo.UpsertDefinitionAsync(new CapabilityDefinition
        {
            CapabilityId = "upd-cap",
            DisplayName = "Updated Cap",
            Status = CapabilityStatuses.Disabled,
            ExecutorKind = "external_service",
            SideEffectLevel = SideEffectLevels.Auditable,
        });

        Assert.Equal("Updated Cap", updated.DisplayName);
        Assert.Equal(CapabilityStatuses.Disabled, updated.Status);
        Assert.Equal("external_service", updated.ExecutorKind);
        Assert.Equal(SideEffectLevels.Auditable, updated.SideEffectLevel);
    }

    [Fact]
    public async Task GetDefinition_ReturnsNullForMissing()
    {
        var result = await _repo.GetDefinitionAsync("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetDefinition_ReturnsDefinition()
    {
        await SeedDefinition("get-me");
        var result = await _repo.GetDefinitionAsync("get-me");
        Assert.NotNull(result);
        Assert.Equal("get-me", result.CapabilityId);
    }

    [Fact]
    public async Task ListDefinitions_FiltersByStatus()
    {
        await SeedDefinition("enabled-1", CapabilityStatuses.Enabled);
        await SeedDefinition("enabled-2", CapabilityStatuses.Enabled);
        await SeedDefinition("disabled-1", CapabilityStatuses.Disabled);

        var enabled = await _repo.ListDefinitionsAsync(new CapabilityListOptions
        {
            Status = CapabilityStatuses.Enabled,
        });
        Assert.Equal(2, enabled.Count);

        var disabled = await _repo.ListDefinitionsAsync(new CapabilityListOptions
        {
            Status = CapabilityStatuses.Disabled,
        });
        Assert.Single(disabled);
    }

    [Fact]
    public async Task ListDefinitions_FiltersBySideEffectLevel()
    {
        await _repo.UpsertDefinitionAsync(new CapabilityDefinition
        {
            CapabilityId = "none-1",
            DisplayName = "None1",
            Status = CapabilityStatuses.Enabled,
            SideEffectLevel = SideEffectLevels.None,
        });
        await _repo.UpsertDefinitionAsync(new CapabilityDefinition
        {
            CapabilityId = "destructive-1",
            DisplayName = "Destructive1",
            Status = CapabilityStatuses.Enabled,
            SideEffectLevel = SideEffectLevels.Destructive,
        });

        var none = await _repo.ListDefinitionsAsync(new CapabilityListOptions
        {
            SideEffectLevel = SideEffectLevels.None,
        });
        Assert.Single(none);
    }

    [Fact]
    public async Task ListDefinitions_FiltersByOwnerProject()
    {
        await _repo.UpsertDefinitionAsync(new CapabilityDefinition
        {
            CapabilityId = "proj-cap-1",
            DisplayName = "Project Cap",
            Status = CapabilityStatuses.Enabled,
            OwnerProjectId = "test-proj",
        });

        var results = await _repo.ListDefinitionsAsync(new CapabilityListOptions
        {
            OwnerProjectId = "test-proj",
        });
        Assert.Single(results);
        Assert.Equal("proj-cap-1", results[0].CapabilityId);
    }

    [Fact]
    public async Task ListDefinitions_RespectsLimit()
    {
        for (var i = 0; i < 10; i++)
            await SeedDefinition($"limit-cap-{i}");

        var limited = await _repo.ListDefinitionsAsync(new CapabilityListOptions { Limit = 3 });
        Assert.Equal(3, limited.Count);
    }

    // ─────────────────────────────────────────────────────────────────
    // Invocation CRUD
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateInvocation_CreatesAndReturns()
    {
        await SeedDefinition("inv-cap-1");
        var inv = await _repo.CreateInvocationAsync(new CapabilityInvocation
        {
            CapabilityId = "inv-cap-1",
            CallerProjectId = "test-proj",
            CallerIdentity = "test-agent",
            Status = InvocationStatuses.Success,
            RequestPayload = "{\"input\":\"test\"}",
        });

        Assert.True(inv.Id > 0);
        Assert.Equal("inv-cap-1", inv.CapabilityId);
        Assert.Equal("test-proj", inv.CallerProjectId);
    }

    [Fact]
    public async Task UpdateInvocationStatus_Terminalizes()
    {
        await SeedDefinition("inv-cap-2");
        var inv = await _repo.CreateInvocationAsync(new CapabilityInvocation
        {
            CapabilityId = "inv-cap-2",
            CallerProjectId = "test-proj",
            CallerIdentity = "test-agent",
            Status = "pending",
        });

        var updated = await _repo.UpdateInvocationStatusAsync(inv.Id,
            InvocationStatuses.Timeout,
            errorMessage: "Timed out after 30000ms",
            durationMs: 30000);

        Assert.NotNull(updated);
        Assert.Equal(InvocationStatuses.Timeout, updated.Status);
        Assert.Equal("Timed out after 30000ms", updated.ErrorMessage);
        Assert.Equal(30000, updated.DurationMs);
    }

    [Fact]
    public async Task GetInvocation_ReturnsNullForMissing()
    {
        var result = await _repo.GetInvocationAsync(99999);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetInvocation_ReturnsInvocation()
    {
        await SeedDefinition("inv-cap-3");
        var inv = await _repo.CreateInvocationAsync(new CapabilityInvocation
        {
            CapabilityId = "inv-cap-3",
            CallerProjectId = "test-proj",
            CallerIdentity = "test-agent",
            Status = InvocationStatuses.Success,
        });

        var fetched = await _repo.GetInvocationAsync(inv.Id);
        Assert.NotNull(fetched);
        Assert.Equal(inv.Id, fetched.Id);
    }

    [Fact]
    public async Task ListInvocations_FiltersByCapability()
    {
        await SeedDefinition("list-inv-a");
        await SeedDefinition("list-inv-b");
        await _repo.CreateInvocationAsync(new CapabilityInvocation
        {
            CapabilityId = "list-inv-a",
            CallerProjectId = "test-proj",
            CallerIdentity = "agent",
            Status = InvocationStatuses.Success,
        });
        await _repo.CreateInvocationAsync(new CapabilityInvocation
        {
            CapabilityId = "list-inv-b",
            CallerProjectId = "test-proj",
            CallerIdentity = "agent",
            Status = InvocationStatuses.Success,
        });

        var results = await _repo.ListInvocationsAsync(new InvocationListOptions
        {
            CapabilityId = "list-inv-a",
        });
        Assert.Single(results);
        Assert.Equal("list-inv-a", results[0].CapabilityId);
    }

    [Fact]
    public async Task ListInvocations_FiltersByStatus()
    {
        await SeedDefinition("inv-filter");
        await _repo.CreateInvocationAsync(new CapabilityInvocation
        {
            CapabilityId = "inv-filter",
            CallerProjectId = "test-proj",
            CallerIdentity = "agent",
            Status = InvocationStatuses.Success,
        });
        await _repo.CreateInvocationAsync(new CapabilityInvocation
        {
            CapabilityId = "inv-filter",
            CallerProjectId = "test-proj",
            CallerIdentity = "agent",
            Status = InvocationStatuses.ExecutorFailure,
        });

        var successes = await _repo.ListInvocationsAsync(new InvocationListOptions
        {
            CapabilityId = "inv-filter",
            Status = InvocationStatuses.Success,
        });
        Assert.Single(successes);
        Assert.Equal(InvocationStatuses.Success, successes[0].Status);
    }

    [Fact]
    public async Task ListInvocations_FiltersByCallerProject()
    {
        await SeedDefinition("inv-caller");
        await _repo.CreateInvocationAsync(new CapabilityInvocation
        {
            CapabilityId = "inv-caller",
            CallerProjectId = "test-proj",
            CallerIdentity = "agent",
            Status = InvocationStatuses.Success,
        });
        await _repo.CreateInvocationAsync(new CapabilityInvocation
        {
            CapabilityId = "inv-caller",
            CallerProjectId = "other-proj",
            CallerIdentity = "agent",
            Status = InvocationStatuses.Success,
        });

        var results = await _repo.ListInvocationsAsync(new InvocationListOptions
        {
            CallerProjectId = "test-proj",
        });
        Assert.Single(results);
        Assert.Equal("test-proj", results[0].CallerProjectId);
    }

    [Fact]
    public async Task ListInvocations_FiltersByCallerTaskId()
    {
        await SeedDefinition("inv-task");
        await _repo.CreateInvocationAsync(new CapabilityInvocation
        {
            CapabilityId = "inv-task",
            CallerProjectId = "test-proj",
            CallerTaskId = "task-42",
            CallerIdentity = "agent",
            Status = InvocationStatuses.Success,
        });
        await _repo.CreateInvocationAsync(new CapabilityInvocation
        {
            CapabilityId = "inv-task",
            CallerProjectId = "test-proj",
            CallerIdentity = "agent",
            Status = InvocationStatuses.Success,
        });

        var results = await _repo.ListInvocationsAsync(new InvocationListOptions
        {
            CallerTaskId = "task-42",
        });
        Assert.Single(results);
        Assert.Equal("task-42", results[0].CallerTaskId);
    }
}
