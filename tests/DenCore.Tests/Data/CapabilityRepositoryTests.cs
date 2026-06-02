using DenCore.Data;
using DenCore.Models;
using DenCore.Tests;

namespace DenCore.Tests.Data;

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

    private async Task SeedDefinition(string id, string status = CapabilityStatuses.Active,
        string implKind = ImplementationKinds.HttpEndpoint,
        string seLevel = SideEffectLevels.ReadOnly)
    {
        await _repo.UpsertDefinitionAsync(new CapabilityDefinition
        {
            CapabilityId = id,
            DisplayName = $"Cap {id}",
            Status = status,
            ImplementationKind = implKind,
            SideEffectLevel = seLevel,
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

        // Verify new columns exist
        await using var colsCmd = conn.CreateCommand();
        colsCmd.CommandText = "SELECT count(*) FROM pragma_table_info('capability_definitions') WHERE name IN ('implementation_kind', 'service_endpoint', 'http_method', 'input_schema_ref', 'output_schema_ref', 'input_schema_json', 'output_schema_json', 'default_model_json', 'fallback_models_json', 'eval_refs_json', 'timeout_ms', 'max_request_bytes', 'metadata_json')";
        Assert.Equal(13L, (await colsCmd.ExecuteScalarAsync())!);

        // Verify invocation columns
        await using var invColsCmd = conn.CreateCommand();
        invColsCmd.CommandText = "SELECT count(*) FROM pragma_table_info('capability_invocations') WHERE name IN ('invocation_id', 'caller_agent', 'caller_task_id', 'caller_message_id', 'caller_surface', 'input_artifact_refs_json', 'request_json', 'request_hash', 'model_provider', 'model_name', 'model_version', 'timings_ms_json', 'cost_json', 'output_summary', 'output_json', 'output_artifact_refs_json', 'error_type', 'metadata_json')";
        Assert.Equal(18L, (await invColsCmd.ExecuteScalarAsync())!);

        foreach (var idx in new[] {
            "idx_capability_definitions_status",
            "idx_capability_definitions_side_effect",
            "idx_capability_definitions_owner",
            "idx_cap_invocations_invocation",
            "idx_cap_invocations_capability",
            "idx_cap_invocations_caller_project",
            "idx_cap_invocations_status",
            "idx_cap_invocations_caller_task",
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
            Status = CapabilityStatuses.Active,
            ImplementationKind = ImplementationKinds.HttpEndpoint,
            SideEffectLevel = SideEffectLevels.ReadOnly,
            ServiceEndpoint = "http://localhost:9999/analyze",
            HttpMethod = "POST",
        });

        Assert.Equal("vision.test.v1", result.CapabilityId);
        Assert.Equal("Test Vision", result.DisplayName);
        Assert.Equal(CapabilityStatuses.Active, result.Status);
        Assert.Equal(ImplementationKinds.HttpEndpoint, result.ImplementationKind);
        Assert.Equal("http://localhost:9999/analyze", result.ServiceEndpoint);
        Assert.Equal("POST", result.HttpMethod);
        Assert.Equal(30000, result.TimeoutMs);
        Assert.Equal(10485760, result.MaxRequestBytes);
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
            ImplementationKind = ImplementationKinds.RegistryOnly,
            SideEffectLevel = SideEffectLevels.NotificationOnly,
        });

        Assert.Equal("Updated Cap", updated.DisplayName);
        Assert.Equal(CapabilityStatuses.Disabled, updated.Status);
        Assert.Equal(ImplementationKinds.RegistryOnly, updated.ImplementationKind);
        Assert.Equal(SideEffectLevels.NotificationOnly, updated.SideEffectLevel);
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
        await SeedDefinition("active-1", CapabilityStatuses.Active);
        await SeedDefinition("active-2", CapabilityStatuses.Active);
        await SeedDefinition("disabled-1", CapabilityStatuses.Disabled);

        var active = await _repo.ListDefinitionsAsync(new CapabilityListOptions
        {
            Status = CapabilityStatuses.Active,
        });
        Assert.Equal(2, active.Count);

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
            CapabilityId = "readonly-1",
            DisplayName = "RO1",
            Status = CapabilityStatuses.Active,
            SideEffectLevel = SideEffectLevels.ReadOnly,
        });
        await _repo.UpsertDefinitionAsync(new CapabilityDefinition
        {
            CapabilityId = "bounded-1",
            DisplayName = "BW1",
            Status = CapabilityStatuses.Active,
            SideEffectLevel = SideEffectLevels.BoundedWrite,
        });

        var readOnly = await _repo.ListDefinitionsAsync(new CapabilityListOptions
        {
            SideEffectLevel = SideEffectLevels.ReadOnly,
        });
        Assert.Single(readOnly);
    }

    [Fact]
    public async Task ListDefinitions_FiltersByOwnerProject()
    {
        await _repo.UpsertDefinitionAsync(new CapabilityDefinition
        {
            CapabilityId = "proj-cap-1",
            DisplayName = "Project Cap",
            Status = CapabilityStatuses.Active,
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
            CallerAgent = "test-agent",
            Status = InvocationStatuses.Completed,
            RequestJson = "{\"input\":\"test\"}",
        });

        Assert.True(inv.Id > 0);
        Assert.NotNull(inv.InvocationId);
        Assert.StartsWith("capinv_", inv.InvocationId);
        Assert.Equal("inv-cap-1", inv.CapabilityId);
        Assert.Equal("test-proj", inv.CallerProjectId);
        Assert.Equal("test-agent", inv.CallerAgent);
    }

    [Fact]
    public async Task UpdateInvocationStatus_Terminalizes()
    {
        await SeedDefinition("inv-cap-2");
        var inv = await _repo.CreateInvocationAsync(new CapabilityInvocation
        {
            CapabilityId = "inv-cap-2",
            CallerProjectId = "test-proj",
            CallerAgent = "test-agent",
            Status = InvocationStatuses.Queued,
        });

        var updated = await _repo.UpdateInvocationStatusAsync(inv.Id,
            InvocationStatuses.TimedOut,
            errorType: "timeout",
            errorMessage: "Timed out after 30000ms",
            durationMs: 30000);

        Assert.NotNull(updated);
        Assert.Equal(InvocationStatuses.TimedOut, updated.Status);
        Assert.Equal("timeout", updated.ErrorType);
        Assert.Equal("Timed out after 30000ms", updated.ErrorMessage);
        Assert.Equal(30000, updated.DurationMs);
    }

    [Fact]
    public async Task GetInvocation_ReturnsNullForMissing()
    {
        var result = await _repo.GetInvocationByIdAsync(99999);
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
            CallerAgent = "test-agent",
            Status = InvocationStatuses.Completed,
        });

        var fetched = await _repo.GetInvocationByIdAsync(inv.Id);
        Assert.NotNull(fetched);
        Assert.Equal(inv.Id, fetched.Id);
        Assert.Equal(inv.InvocationId, fetched.InvocationId);
    }

    [Fact]
    public async Task GetInvocationByInvocationId_FindsByPublicId()
    {
        await SeedDefinition("inv-cap-pub");
        var inv = await _repo.CreateInvocationAsync(new CapabilityInvocation
        {
            CapabilityId = "inv-cap-pub",
            CallerProjectId = "test-proj",
            CallerAgent = "test-agent",
            Status = InvocationStatuses.Queued,
        });

        var fetched = await _repo.GetInvocationByInvocationIdAsync(inv.InvocationId!);
        Assert.NotNull(fetched);
        Assert.Equal(inv.Id, fetched.Id);
        Assert.Equal(inv.InvocationId, fetched.InvocationId);
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
            CallerAgent = "agent",
            Status = InvocationStatuses.Completed,
        });
        await _repo.CreateInvocationAsync(new CapabilityInvocation
        {
            CapabilityId = "list-inv-b",
            CallerProjectId = "test-proj",
            CallerAgent = "agent",
            Status = InvocationStatuses.Completed,
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
            CallerAgent = "agent",
            Status = InvocationStatuses.Completed,
        });
        await _repo.CreateInvocationAsync(new CapabilityInvocation
        {
            CapabilityId = "inv-filter",
            CallerProjectId = "test-proj",
            CallerAgent = "agent",
            Status = InvocationStatuses.Failed,
        });

        var successes = await _repo.ListInvocationsAsync(new InvocationListOptions
        {
            CapabilityId = "inv-filter",
            Status = InvocationStatuses.Completed,
        });
        Assert.Single(successes);
        Assert.Equal(InvocationStatuses.Completed, successes[0].Status);
    }

    [Fact]
    public async Task ListInvocations_FiltersByCallerProject()
    {
        await SeedDefinition("inv-caller");
        await _repo.CreateInvocationAsync(new CapabilityInvocation
        {
            CapabilityId = "inv-caller",
            CallerProjectId = "test-proj",
            CallerAgent = "agent",
            Status = InvocationStatuses.Completed,
        });
        await _repo.CreateInvocationAsync(new CapabilityInvocation
        {
            CapabilityId = "inv-caller",
            CallerProjectId = "other-proj",
            CallerAgent = "agent",
            Status = InvocationStatuses.Completed,
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
            CallerAgent = "agent",
            CallerTaskId = 42,
            Status = InvocationStatuses.Completed,
        });
        await _repo.CreateInvocationAsync(new CapabilityInvocation
        {
            CapabilityId = "inv-task",
            CallerProjectId = "test-proj",
            CallerAgent = "agent",
            Status = InvocationStatuses.Completed,
        });

        var results = await _repo.ListInvocationsAsync(new InvocationListOptions
        {
            CallerTaskId = 42,
        });
        Assert.Single(results);
        Assert.Equal(42, results[0].CallerTaskId);
    }

    [Fact]
    public async Task GenerateInvocationId_Format()
    {
        var ids = Enumerable.Range(0, 10).Select(_ => CapabilityRepository.GenerateInvocationId()).ToList();
        Assert.All(ids, id => Assert.Matches("^capinv_\\d+_\\d{6}$", id));
        Assert.Equal(10, ids.Distinct().Count());
    }
}
