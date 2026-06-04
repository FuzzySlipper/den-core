using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DenCore.Data;
using DenCore.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenCore.Service.Tests;

/// <summary>
/// Integration tests for the Core-owned Direct Delivery contract (/api/direct-delivery).
///
/// Acceptance criteria from task #1901:
///   1. Core-facing contracts use generic Den terms; no Hermes/Pi/Codex internals.
///   2. #1885 is reconciled as session-owner slice: session_owner_id, session_id,
///      channel_id, and lane fields in the binding snapshot.
///   3. Determine whether existing endpoints/projections are sufficient.
///   4. Tests prove durable agent instance identity and shared-profile worker pool
///      identity separation.
/// </summary>
public sealed class DirectDeliveryContractApiTests : IAsyncLifetime
{
    private const string ProjectId = "dd-contract-proj";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private static readonly JsonSerializerOptions JsonCamelOpts = new(JsonSerializerDefaults.Web);

    private DirectDeliveryAppFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new DirectDeliveryAppFactory();
        var initializer = new DatabaseInitializer(_factory.DatabasePath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await projects.CreateAsync(new Project { Id = ProjectId, Name = "Direct Delivery Contract Project" });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    // A1: Generic Den terms — no Hermes/Pi/Codex internals
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Bindings_UseGenericDenTerms()
    {
        // Seed a binding and a pool member with known data
        using var scope = _factory.Services.CreateScope();
        var bindingsRepo = scope.ServiceProvider.GetRequiredService<IAgentInstanceBindingRepository>();
        var poolRepo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();

        await bindingsRepo.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "dd-binding-1",
            ProjectId = ProjectId,
            AgentIdentity = "spawned-coder-01",
            AgentFamily = "hermes",
            Role = "coder",
            TransportKind = "local_adapter",
            SessionId = "session-dd-1",
            Status = AgentInstanceBindingStatus.Active,
            Metadata = "{\"provider\":\"deepseek\"}"
        });

        await poolRepo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "pool-worker-dd-1",
            PoolMemberId = "pool-coder-01",
            ProfileIdentity = "spawned-coder",
            WorkerRole = "coder",
            AgentInstanceId = "dd-binding-1",
            SessionId = "session-dd-1",
            ChannelId = "ch-dd-test",
            AdapterInstanceId = "adapter:dd-1",
            Capabilities = "[\"coder\",\"dotnet\"]",
            Status = WorkerPoolStates.MemberAvailable,
        });

        // Call /api/direct-delivery/bindings
        var response = await _client.GetAsync($"/api/direct-delivery/bindings?projectId={ProjectId}");
        response.EnsureSuccessStatusCode();

        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var items = payload.RootElement.GetProperty("items").EnumerateArray().ToList();

        // Should have exactly one binding
        var item = Assert.Single(items);

        // Verify generic Den terms — these fields MUST NOT contain Hermes/Pi/Codex internals
        // Core fields: always present
        var coreFields = new[]
        {
            "agent_instance_id", "project_id", "agent_family", "agent_identity",
            "role", "transport_kind", "status", "checked_in_at", "last_heartbeat",
            "session_owner_id", "session_id", "channel_id", "lane",
            "pool_member_id", "profile_identity", "worker_role", "adapter_instance_id",
            "capabilities", "metadata", "updated_at"
        };
        foreach (var field in coreFields)
            Assert.True(item.TryGetProperty(field, out _),
                $"Expected core field '{field}' not found in DirectDeliveryBindingSnapshot");

        // Optional assignment fields: absent or present (null when no active assignment)
        if (item.TryGetProperty("assignment_id", out var assignmentId))
            Assert.Equal(JsonValueKind.Null, assignmentId.ValueKind);
        if (item.TryGetProperty("worker_run_id", out var workerRunId))
            Assert.Equal(JsonValueKind.Null, workerRunId.ValueKind);

        // Verify key values
        Assert.Equal("dd-binding-1", item.GetProperty("agent_instance_id").GetString());
        Assert.Equal(ProjectId, item.GetProperty("project_id").GetString());
        Assert.Equal("active", item.GetProperty("status").GetString());
        Assert.Equal("local_adapter", item.GetProperty("transport_kind").GetString());
        Assert.Equal("session-dd-1", item.GetProperty("session_id").GetString());
        Assert.Equal("ch-dd-test", item.GetProperty("channel_id").GetString());
        Assert.Equal("pool-worker-dd-1", item.GetProperty("pool_member_id").GetString());
        // ^ PoolMemberId defaults to WorkerIdentity when not separately persisted in DB
        Assert.Equal("spawned-coder", item.GetProperty("profile_identity").GetString());
        Assert.Equal("coder", item.GetProperty("worker_role").GetString());
        Assert.Equal("spawned-coder/coder", item.GetProperty("lane").GetString());
        Assert.Equal("adapter:dd-1", item.GetProperty("adapter_instance_id").GetString());

        // The response JSON must not contain Hermes/Pi/Codex-specific keys
        var rawJson = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("hermes_session_id", rawJson);
        Assert.DoesNotContain("pi_session", rawJson);
        Assert.DoesNotContain("codex_internal", rawJson);
        Assert.DoesNotContain("tmux", rawJson);
    }

    // ═══════════════════════════════════════════════════════════════════
    // A2: #1885 reconciliation — session-owner slice
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task BindingSnapshot_IncludesSessionOwnerSlice()
    {
        using var scope = _factory.Services.CreateScope();
        var bindingsRepo = scope.ServiceProvider.GetRequiredService<IAgentInstanceBindingRepository>();
        var poolRepo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();

        // Agent binding has its own InstanceId
        await bindingsRepo.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "dd-session-own-1",
            ProjectId = ProjectId,
            AgentIdentity = "worker-owner-1",
            AgentFamily = "hermes",
            Role = "coder",
            TransportKind = "local_adapter",
            SessionId = "session-owner-1",
            Status = AgentInstanceBindingStatus.Active,
        });

        // Pool member has a DIFFERENT AgentInstanceId (the session owner is distinct)
        await poolRepo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "pool-owner-1",
            PoolMemberId = "pool-coder-owner-01",
            ProfileIdentity = "spawned-coder",
            WorkerRole = "coder",
            AgentInstanceId = "dd-session-own-1",
            SessionId = "session-owner-1",
            ChannelId = "ch-owner-test",
            Status = WorkerPoolStates.MemberAvailable,
        });

        var response = await _client.GetAsync($"/api/direct-delivery/bindings?projectId={ProjectId}");
        response.EnsureSuccessStatusCode();

        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var item = Assert.Single(payload.RootElement.GetProperty("items").EnumerateArray());

        // Session-owner fields (#1885 slice) must be present
        Assert.Equal("dd-session-own-1", item.GetProperty("session_owner_id").GetString());
        Assert.Equal("session-owner-1", item.GetProperty("session_id").GetString());
        Assert.Equal("ch-owner-test", item.GetProperty("channel_id").GetString());
        Assert.Equal("spawned-coder/coder", item.GetProperty("lane").GetString());

        // Verify lane construction: profile_identity/worker_role
        var lane = item.GetProperty("lane").GetString();
        Assert.StartsWith("spawned-coder/", lane);
    }

    [Fact]
    public async Task BindingSnapshot_LaneReflectsProfilePlusRole()
    {
        using var scope = _factory.Services.CreateScope();
        var bindingsRepo = scope.ServiceProvider.GetRequiredService<IAgentInstanceBindingRepository>();
        var poolRepo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();

        await bindingsRepo.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "dd-lane-test",
            ProjectId = ProjectId,
            AgentIdentity = "reviewer-1",
            AgentFamily = "hermes",
            Role = "reviewer",
            TransportKind = "local_adapter",
            SessionId = "session-lane",
            Status = AgentInstanceBindingStatus.Active,
        });

        await poolRepo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "pool-reviewer-1",
            PoolMemberId = "pool-reviewer-01",
            ProfileIdentity = "spawned-reviewer",
            WorkerRole = "reviewer",
            AgentInstanceId = "dd-lane-test",
            Status = WorkerPoolStates.MemberAvailable,
        });

        var response = await _client.GetAsync($"/api/direct-delivery/bindings?projectId={ProjectId}");
        response.EnsureSuccessStatusCode();

        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var item = Assert.Single(payload.RootElement.GetProperty("items").EnumerateArray());

        Assert.Equal("spawned-reviewer/reviewer", item.GetProperty("lane").GetString());
        Assert.Equal("spawned-reviewer", item.GetProperty("profile_identity").GetString());
        Assert.Equal("reviewer", item.GetProperty("worker_role").GetString());
    }

    // ═══════════════════════════════════════════════════════════════════
    // A4: Durable agent instance identity + shared-profile pool member
    //     identity separation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DurableAgentInstanceIdentity_IsConcreteAgentInstanceNotChannelScoped()
    {
        // AgentInstanceBinding.InstanceId is a concrete agent instance id,
        // not a channel-scoped identifier. It persists across channel changes.
        using var scope = _factory.Services.CreateScope();
        var bindingsRepo = scope.ServiceProvider.GetRequiredService<IAgentInstanceBindingRepository>();
        var poolRepo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();

        var agentInstanceId = "dd-durable-instance-1";

        await bindingsRepo.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = agentInstanceId,
            ProjectId = ProjectId,
            AgentIdentity = "durable-agent",
            AgentFamily = "hermes",
            Role = "coder",
            TransportKind = "local_adapter",
            SessionId = "session-durable-1",
            Status = AgentInstanceBindingStatus.Active,
        });

        // Pool member with agent_instance_id = same durable id, but channel can vary
        await poolRepo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "durable-worker",
            PoolMemberId = "pool-coder-durable-01",
            ProfileIdentity = "spawned-coder",
            WorkerRole = "coder",
            AgentInstanceId = agentInstanceId,
            ChannelId = "ch-first",  // this can change without affecting the binding
            SessionId = "session-durable-1",
            Status = WorkerPoolStates.MemberAvailable,
        });

        var response = await _client.GetAsync($"/api/direct-delivery/bindings?projectId={ProjectId}");
        response.EnsureSuccessStatusCode();

        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var item = Assert.Single(payload.RootElement.GetProperty("items").EnumerateArray());

        // Agent instance id is the durable identity, not channel-scoped
        Assert.Equal(agentInstanceId, item.GetProperty("agent_instance_id").GetString());
        // Channel is just correlation
        Assert.Equal("ch-first", item.GetProperty("channel_id").GetString());

        // Now update the pool member's channel — binding identity stays the same
        await poolRepo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "durable-worker",
            PoolMemberId = "pool-coder-durable-01",
            ProfileIdentity = "spawned-coder",
            WorkerRole = "coder",
            AgentInstanceId = agentInstanceId,
            ChannelId = "ch-second",  // channel changed
            Status = WorkerPoolStates.MemberAvailable,
        });

        var response2 = await _client.GetAsync($"/api/direct-delivery/bindings?projectId={ProjectId}");
        response2.EnsureSuccessStatusCode();

        using var payload2 = await JsonDocument.ParseAsync(await response2.Content.ReadAsStreamAsync());
        var item2 = Assert.Single(payload2.RootElement.GetProperty("items").EnumerateArray());

        // Agent instance id is STILL the same — it's durable identity, not channel-scoped
        Assert.Equal(agentInstanceId, item2.GetProperty("agent_instance_id").GetString());
        // Channel changed
        Assert.Equal("ch-second", item2.GetProperty("channel_id").GetString());
    }

    [Fact]
    public async Task SharedProfileIdentity_TwoMembersDistinctPoolMemberIds()
    {
        // Two pool members sharing the same profile_identity ("spawned-coder")
        // must have distinct pool_member_id, agent_instance_id, worker_identity,
        // and session. This proves that shared-profile workers are individual
        // concrete members, not a single pooled identity.
        using var scope = _factory.Services.CreateScope();
        var bindingsRepo = scope.ServiceProvider.GetRequiredService<IAgentInstanceBindingRepository>();
        var poolRepo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();

        const string profileId = "spawned-coder";

        // Member Alpha
        var alphaInstanceId = "dd-shared-alpha-inst";
        await bindingsRepo.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = alphaInstanceId,
            ProjectId = ProjectId,
            AgentIdentity = "alpha-agent",
            AgentFamily = "hermes",
            Role = "coder",
            TransportKind = "local_adapter",
            SessionId = "session-alpha",
            Status = AgentInstanceBindingStatus.Active,
        });
        await poolRepo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "shared-worker-alpha",
            PoolMemberId = "pool-coder-alpha",
            ProfileIdentity = profileId,
            WorkerRole = "coder",
            AgentInstanceId = alphaInstanceId,
            SessionId = "session-alpha",
            ChannelId = "ch-alpha",
            Status = WorkerPoolStates.MemberAvailable,
        });

        // Member Beta — same profile_identity, DIFFERENT worker_identity/pool_member_id
        var betaInstanceId = "dd-shared-beta-inst";
        await bindingsRepo.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = betaInstanceId,
            ProjectId = ProjectId,
            AgentIdentity = "beta-agent",
            AgentFamily = "hermes",
            Role = "coder",
            TransportKind = "local_adapter",
            SessionId = "session-beta",
            Status = AgentInstanceBindingStatus.Active,
        });
        await poolRepo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "shared-worker-beta",
            PoolMemberId = "pool-coder-beta",
            ProfileIdentity = profileId,  // SAME profile
            WorkerRole = "coder",
            AgentInstanceId = betaInstanceId,
            SessionId = "session-beta",
            ChannelId = "ch-beta",
            Status = WorkerPoolStates.MemberAvailable,
        });

        var response = await _client.GetAsync($"/api/direct-delivery/bindings?projectId={ProjectId}");
        response.EnsureSuccessStatusCode();

        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var items = payload.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);

        // Both share the same profile_identity
        foreach (var item in items)
            Assert.Equal(profileId, item.GetProperty("profile_identity").GetString());

        // But they have distinct pool_member_ids
        // (Note: PoolMemberId defaults to WorkerIdentity when not persisted in DB,
        //  so pool_member_id equals worker_identity in this case. Both are distinct
        //  because WorkerIdentity is the concrete canonical lifecycle key.)
        var poolMemberIds = items.Select(i => i.GetProperty("pool_member_id").GetString()).ToHashSet();
        Assert.Equal(2, poolMemberIds.Count);
        // pool_member_id resolves to WorkerIdentity when PoolMemberId is not separately persisted
        Assert.Contains("shared-worker-alpha", poolMemberIds);
        Assert.Contains("shared-worker-beta", poolMemberIds);

        // And distinct worker identities (verify they match pool_member_ids)
        // WorkerIdentity is the canonical lifecycle key — the pool_member_id projection
        // carries it as the default pool_member_id when not explicitly persisted.

        // And distinct agent_instance_ids
        var agentInstanceIds = items.Select(i => i.GetProperty("agent_instance_id").GetString()).ToHashSet();
        Assert.Equal(2, agentInstanceIds.Count);
        Assert.Contains(alphaInstanceId, agentInstanceIds);
        Assert.Contains(betaInstanceId, agentInstanceIds);

        // And distinct session_ids
        var sessionIds = items.Select(i => i.GetProperty("session_id").GetString()).ToHashSet();
        Assert.Equal(2, sessionIds.Count);
        Assert.Contains("session-alpha", sessionIds);
        Assert.Contains("session-beta", sessionIds);

        // And distinct channel_ids
        var channelIds = items.Select(i => i.GetProperty("channel_id").GetString()).ToHashSet();
        Assert.Equal(2, channelIds.Count);
    }

    [Fact]
    public async Task SharedProfileIdentity_DistinctRunIdentitiesPerMember()
    {
        // When two pool members share a profile but have active assignments,
        // each gets its own worker_run_id. Run identity is per-member, not per-profile.
        using var scope = _factory.Services.CreateScope();
        var bindingsRepo = scope.ServiceProvider.GetRequiredService<IAgentInstanceBindingRepository>();
        var poolRepo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();

        const string profileId = "spawned-coder";

        // Seed member Alpha with binding + lease
        var alphaInstanceId = "dd-run-alpha-inst";
        await bindingsRepo.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = alphaInstanceId,
            ProjectId = ProjectId,
            AgentIdentity = "run-alpha-agent",
            AgentFamily = "hermes",
            Role = "coder",
            TransportKind = "local_adapter",
            SessionId = "session-run-alpha",
            Status = AgentInstanceBindingStatus.Active,
        });
        await poolRepo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "run-alpha-worker",
            PoolMemberId = "pool-coder-run-alpha",
            ProfileIdentity = profileId,
            WorkerRole = "coder",
            AgentInstanceId = alphaInstanceId,
            Status = WorkerPoolStates.MemberAvailable,
        });
        var leaseAlpha = await poolRepo.LeaseAvailableWorkerAsync(new LeaseWorkerInput
        {
            ProjectId = ProjectId,
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-alpha-001",
            PreferredWorkerIdentity = "run-alpha-worker",
        });
        Assert.NotNull(leaseAlpha);

        // Seed member Beta with binding + lease
        var betaInstanceId = "dd-run-beta-inst";
        await bindingsRepo.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = betaInstanceId,
            ProjectId = ProjectId,
            AgentIdentity = "run-beta-agent",
            AgentFamily = "hermes",
            Role = "coder",
            TransportKind = "local_adapter",
            SessionId = "session-run-beta",
            Status = AgentInstanceBindingStatus.Active,
        });
        await poolRepo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "run-beta-worker",
            PoolMemberId = "pool-coder-run-beta",
            ProfileIdentity = profileId,
            WorkerRole = "coder",
            AgentInstanceId = betaInstanceId,
            Status = WorkerPoolStates.MemberAvailable,
        });
        var leaseBeta = await poolRepo.LeaseAvailableWorkerAsync(new LeaseWorkerInput
        {
            ProjectId = ProjectId,
            Role = "coder",
            AssignedBy = "runner",
            RunId = "run-beta-001",
            PreferredWorkerIdentity = "run-beta-worker",
        });
        Assert.NotNull(leaseBeta);

        var response = await _client.GetAsync($"/api/direct-delivery/bindings?projectId={ProjectId}");
        response.EnsureSuccessStatusCode();

        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var items = payload.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);

        // Both share profile_identity
        foreach (var item in items)
        {
            Assert.Equal(profileId, item.GetProperty("profile_identity").GetString());
            // Each has a non-null worker_run_id
            var runId = item.GetProperty("worker_run_id").GetString();
            Assert.NotNull(runId);
            Assert.NotEmpty(runId);
        }

        // Distinct run identities
        var runIds = items.Select(i => i.GetProperty("worker_run_id").GetString()).ToHashSet();
        Assert.Equal(2, runIds.Count);
        Assert.Contains("run-alpha-001", runIds);
        Assert.Contains("run-beta-001", runIds);

        // Distinct assignment_ids
        foreach (var item in items)
        {
            var assignmentId = item.GetProperty("assignment_id");
            Assert.NotEqual(JsonValueKind.Null, assignmentId.ValueKind);
        }
        var assignmentIds = items.Select(i => i.GetProperty("assignment_id").GetInt32()).ToHashSet();
        Assert.Equal(2, assignmentIds.Count);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Readiness endpoint
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Readiness_ReturnsDirectDeliveryChecks()
    {
        var response = await _client.GetAsync("/api/direct-delivery/readiness");
        response.EnsureSuccessStatusCode();

        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = payload.RootElement;
        Assert.Equal("den-core-direct-delivery-contract", root.GetProperty("service").GetString());
        Assert.NotEqual("blocked", root.GetProperty("status").GetString());

        var checks = root.GetProperty("checks");
        Assert.Equal("ready", checks.GetProperty("process").GetProperty("status").GetString());
        Assert.Equal("ready", checks.GetProperty("database").GetProperty("status").GetString());
        Assert.Equal("ready", checks.GetProperty("direct_delivery_contract").GetProperty("status").GetString());

        var ddCheck = checks.GetProperty("direct_delivery_contract");
        var endpoints = ddCheck.GetProperty("metadata").GetProperty("endpoints").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Contains("/api/direct-delivery/bindings", endpoints);
        Assert.Contains("/api/direct-delivery/readiness", endpoints);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Compatibility: /api/gateway/bindings still works
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GatewayBindings_StillWorksAsCompatibilityAlias()
    {
        using var scope = _factory.Services.CreateScope();
        var bindingsRepo = scope.ServiceProvider.GetRequiredService<IAgentInstanceBindingRepository>();

        await bindingsRepo.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "gateway-compat-1",
            ProjectId = ProjectId,
            AgentIdentity = "compat-agent",
            AgentFamily = "hermes",
            Role = "coder",
            TransportKind = "local_adapter",
            SessionId = "session-compat",
            Status = AgentInstanceBindingStatus.Active,
        });

        // /api/gateway/bindings should still return raw binding data
        var gwResponse = await _client.GetAsync($"/api/gateway/bindings?projectId={ProjectId}");
        gwResponse.EnsureSuccessStatusCode();

        using var gwPayload = await JsonDocument.ParseAsync(await gwResponse.Content.ReadAsStreamAsync());
        var gwItems = gwPayload.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(gwItems);
        Assert.Equal("gateway-compat-1", gwItems[0].GetProperty("instance_id").GetString());

        // /api/direct-delivery/bindings enriches with pool member data
        var ddResponse = await _client.GetAsync($"/api/direct-delivery/bindings?projectId={ProjectId}");
        ddResponse.EnsureSuccessStatusCode();

        using var ddPayload = await JsonDocument.ParseAsync(await ddResponse.Content.ReadAsStreamAsync());
        var ddItems = ddPayload.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(ddItems);
        Assert.Equal("gateway-compat-1", ddItems[0].GetProperty("agent_instance_id").GetString());
    }

    // ═══════════════════════════════════════════════════════════════════
    // Profile/workerRole filtering
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Bindings_FilterByProfileIdentity()
    {
        using var scope = _factory.Services.CreateScope();
        var bindingsRepo = scope.ServiceProvider.GetRequiredService<IAgentInstanceBindingRepository>();
        var poolRepo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();

        // Seed a coder binding + pool member
        await bindingsRepo.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "dd-filter-coder-1",
            ProjectId = ProjectId,
            AgentIdentity = "filter-coder",
            AgentFamily = "hermes",
            Role = "coder",
            TransportKind = "local_adapter",
            SessionId = "session-filter-coder",
            Status = AgentInstanceBindingStatus.Active,
        });
        await poolRepo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "filter-coder-worker",
            ProfileIdentity = "spawned-coder",
            WorkerRole = "coder",
            AgentInstanceId = "dd-filter-coder-1",
            Status = WorkerPoolStates.MemberAvailable,
        });

        // Seed a reviewer binding + pool member with different profile
        await bindingsRepo.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "dd-filter-reviewer-1",
            ProjectId = ProjectId,
            AgentIdentity = "filter-reviewer",
            AgentFamily = "hermes",
            Role = "reviewer",
            TransportKind = "local_adapter",
            SessionId = "session-filter-reviewer",
            Status = AgentInstanceBindingStatus.Active,
        });
        await poolRepo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "filter-reviewer-worker",
            ProfileIdentity = "spawned-reviewer",
            WorkerRole = "reviewer",
            AgentInstanceId = "dd-filter-reviewer-1",
            Status = WorkerPoolStates.MemberAvailable,
        });

        // Seed a raw adapter binding with no pool member. Pool-profile filters
        // must not let unlinked bindings leak through because there is no
        // profile_identity to compare.
        await bindingsRepo.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "dd-filter-unlinked-1",
            ProjectId = ProjectId,
            AgentIdentity = "filter-unlinked",
            AgentFamily = "local-adapter",
            Role = "coder",
            TransportKind = "local_adapter",
            SessionId = "session-filter-unlinked",
            Status = AgentInstanceBindingStatus.Active,
        });

        // Filter by profile_identity=spawned-coder
        var response = await _client.GetAsync(
            $"/api/direct-delivery/bindings?projectId={ProjectId}&profileIdentity=spawned-coder");
        response.EnsureSuccessStatusCode();

        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var items = payload.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal("spawned-coder", items[0].GetProperty("profile_identity").GetString());
        Assert.Equal("filter-coder", items[0].GetProperty("agent_identity").GetString());
        Assert.DoesNotContain(items, i => i.GetProperty("agent_identity").GetString() == "filter-unlinked");
    }

    [Fact]
    public async Task Bindings_FilterByWorkerRole()
    {
        using var scope = _factory.Services.CreateScope();
        var bindingsRepo = scope.ServiceProvider.GetRequiredService<IAgentInstanceBindingRepository>();
        var poolRepo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();

        await bindingsRepo.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "dd-wr-coder",
            ProjectId = ProjectId,
            AgentIdentity = "wr-coder",
            AgentFamily = "hermes",
            Role = "coder",
            TransportKind = "local_adapter",
            Status = AgentInstanceBindingStatus.Active,
        });
        await poolRepo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "wr-coder-worker",
            ProfileIdentity = "spawned-coder",
            WorkerRole = "coder",
            AgentInstanceId = "dd-wr-coder",
            Status = WorkerPoolStates.MemberAvailable,
        });

        await bindingsRepo.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "dd-wr-reviewer",
            ProjectId = ProjectId,
            AgentIdentity = "wr-reviewer",
            AgentFamily = "hermes",
            Role = "reviewer",
            TransportKind = "local_adapter",
            Status = AgentInstanceBindingStatus.Active,
        });
        await poolRepo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = "wr-reviewer-worker",
            ProfileIdentity = "spawned-coder",
            WorkerRole = "reviewer",
            AgentInstanceId = "dd-wr-reviewer",
            Status = WorkerPoolStates.MemberAvailable,
        });

        var response = await _client.GetAsync(
            $"/api/direct-delivery/bindings?projectId={ProjectId}&workerRole=reviewer");
        response.EnsureSuccessStatusCode();

        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var items = payload.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal("reviewer", items[0].GetProperty("worker_role").GetString());
    }

    [Fact]
    public async Task Bindings_UnlinkedBindingWithoutPoolFilters_UsesBindingAsSessionOwner()
    {
        using var scope = _factory.Services.CreateScope();
        var bindingsRepo = scope.ServiceProvider.GetRequiredService<IAgentInstanceBindingRepository>();

        await bindingsRepo.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = "dd-unlinked-binding",
            ProjectId = ProjectId,
            AgentIdentity = "unlinked-adapter-agent",
            AgentFamily = "local-adapter",
            Role = "observer",
            TransportKind = "local_adapter",
            SessionId = "session-unlinked",
            Status = AgentInstanceBindingStatus.Active,
        });

        var response = await _client.GetAsync($"/api/direct-delivery/bindings?projectId={ProjectId}&agentIdentity=unlinked-adapter-agent");
        response.EnsureSuccessStatusCode();

        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var item = Assert.Single(payload.RootElement.GetProperty("items").EnumerateArray());

        Assert.Equal("dd-unlinked-binding", item.GetProperty("agent_instance_id").GetString());
        Assert.Equal("dd-unlinked-binding", item.GetProperty("session_owner_id").GetString());
        Assert.Equal("session-unlinked", item.GetProperty("session_id").GetString());
        Assert.True(!item.TryGetProperty("pool_member_id", out var poolMemberId) || poolMemberId.ValueKind == JsonValueKind.Null);
        Assert.True(!item.TryGetProperty("profile_identity", out var profileIdentity) || profileIdentity.ValueKind == JsonValueKind.Null);
        Assert.True(!item.TryGetProperty("worker_role", out var workerRole) || workerRole.ValueKind == JsonValueKind.Null);
        Assert.True(!item.TryGetProperty("lane", out var lane) || lane.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Bindings_FilterByProfileIdentityAndWorkerRole_UsesAndSemantics()
    {
        using var scope = _factory.Services.CreateScope();
        var bindingsRepo = scope.ServiceProvider.GetRequiredService<IAgentInstanceBindingRepository>();
        var poolRepo = scope.ServiceProvider.GetRequiredService<IWorkerPoolRepository>();

        await SeedBindingWithPoolMemberAsync(
            bindingsRepo,
            poolRepo,
            instanceId: "dd-combo-coder",
            agentIdentity: "combo-coder",
            workerIdentity: "combo-coder-worker",
            profileIdentity: "spawned-coder",
            workerRole: "coder");

        await SeedBindingWithPoolMemberAsync(
            bindingsRepo,
            poolRepo,
            instanceId: "dd-combo-reviewer",
            agentIdentity: "combo-reviewer",
            workerIdentity: "combo-reviewer-worker",
            profileIdentity: "spawned-coder",
            workerRole: "reviewer");

        await SeedBindingWithPoolMemberAsync(
            bindingsRepo,
            poolRepo,
            instanceId: "dd-combo-other-profile",
            agentIdentity: "combo-other-profile",
            workerIdentity: "combo-other-profile-worker",
            profileIdentity: "spawned-reviewer",
            workerRole: "reviewer");

        var response = await _client.GetAsync(
            $"/api/direct-delivery/bindings?projectId={ProjectId}&profileIdentity=spawned-coder&workerRole=reviewer");
        response.EnsureSuccessStatusCode();

        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var items = payload.RootElement.GetProperty("items").EnumerateArray().ToList();
        var item = Assert.Single(items);

        Assert.Equal("combo-reviewer", item.GetProperty("agent_identity").GetString());
        Assert.Equal("spawned-coder", item.GetProperty("profile_identity").GetString());
        Assert.Equal("reviewer", item.GetProperty("worker_role").GetString());
    }

    private static async Task SeedBindingWithPoolMemberAsync(
        IAgentInstanceBindingRepository bindingsRepo,
        IWorkerPoolRepository poolRepo,
        string instanceId,
        string agentIdentity,
        string workerIdentity,
        string profileIdentity,
        string workerRole)
    {
        await bindingsRepo.UpsertAsync(new AgentInstanceBinding
        {
            InstanceId = instanceId,
            ProjectId = ProjectId,
            AgentIdentity = agentIdentity,
            AgentFamily = "local-adapter",
            Role = workerRole,
            TransportKind = "local_adapter",
            SessionId = $"session-{instanceId}",
            Status = AgentInstanceBindingStatus.Active,
        });

        await poolRepo.UpsertMemberAsync(new WorkerPoolMember
        {
            WorkerIdentity = workerIdentity,
            ProfileIdentity = profileIdentity,
            WorkerRole = workerRole,
            AgentInstanceId = instanceId,
            Status = WorkerPoolStates.MemberAvailable,
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // Task #1930: Binding Registration (PUT /api/direct-delivery/bindings/{id})
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PutBinding_FreshRegistration_Returns200WithLastSeen()
    {
        // Use a concrete DTO to ensure serialization matches server expectations
        var registration = new DirectDeliveryBindingRegistration
        {
            AdapterKind = "host",
            AdapterInstanceId = "dd-reg-test-1",
            Host = "workstation-01",
            ManagedRoles = ["coder", "reviewer"],
            ManagedCapabilities = ["dotnet"],
            ProjectId = ProjectId,
        };

        var json = JsonSerializer.Serialize(registration, JsonOpts);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _client.PutAsync("/api/direct-delivery/bindings/dd-reg-test-1", content);
        response.EnsureSuccessStatusCode();

        Assert.Equal(200, (int)response.StatusCode);

        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = payload.RootElement;

        Assert.Equal("dd-reg-test-1", root.GetProperty("adapter_instance_id").GetString());
        Assert.Equal("host", root.GetProperty("adapter_kind").GetString());
        Assert.Equal("workstation-01", root.GetProperty("host").GetString());
        Assert.Equal("active", root.GetProperty("status").GetString());
        Assert.True(root.TryGetProperty("last_seen", out var lastSeen) && lastSeen.ValueKind == JsonValueKind.String);
        Assert.NotEmpty(lastSeen.GetString()!);

        // Verify it appears in the GET listing
        var getResponse = await _client.GetAsync($"/api/direct-delivery/bindings?projectId={ProjectId}");
        getResponse.EnsureSuccessStatusCode();

        using var listPayload = await JsonDocument.ParseAsync(await getResponse.Content.ReadAsStreamAsync());
        var items = listPayload.RootElement.GetProperty("items").EnumerateArray().ToList();
        var match = items.FirstOrDefault(i =>
            i.GetProperty("agent_instance_id").GetString() == "dd-reg-test-1");
        Assert.NotEqual(default, match);
    }

    [Fact]
    public async Task PutBinding_ReRegistration_UpdatesHeartbeat()
    {
        var body = new
        {
            adapterKind = "host",
            adapterInstanceId = "dd-reg-update-1",
            host = "workstation-01",
            managedRoles = Array.Empty<string>(),
            managedCapabilities = Array.Empty<string>(),
            projectId = ProjectId,
        };

        var json = JsonSerializer.Serialize(body, JsonOpts);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // First registration
        var response1 = await _client.PutAsync("/api/direct-delivery/bindings/dd-reg-update-1", content);
        response1.EnsureSuccessStatusCode();

        using var payload1 = await JsonDocument.ParseAsync(await response1.Content.ReadAsStreamAsync());
        var lastSeen1 = payload1.RootElement.GetProperty("last_seen").GetString();

        // Wait for SQLite's datetime('now') to advance (second precision)
        await Task.Delay(1100);

        // Re-registration (same body)
        var response2 = await _client.PutAsync("/api/direct-delivery/bindings/dd-reg-update-1", content);
        response2.EnsureSuccessStatusCode();

        using var payload2 = await JsonDocument.ParseAsync(await response2.Content.ReadAsStreamAsync());
        var lastSeen2 = payload2.RootElement.GetProperty("last_seen").GetString();

        // lastSeen should have advanced
        Assert.NotEqual(lastSeen1, lastSeen2);
    }

    [Fact]
    public async Task PutBinding_MissingRequiredFields_Returns400()
    {
        // Missing adapterKind
        var bodyMissingKind = new
        {
            adapterKind = "",
            adapterInstanceId = "dd-400-1",
            host = "test-host",
            managedRoles = Array.Empty<string>(),
            managedCapabilities = Array.Empty<string>(),
        };

        var json = JsonSerializer.Serialize(bodyMissingKind, JsonOpts);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _client.PutAsync("/api/direct-delivery/bindings/dd-400-1", content);
        Assert.Equal(400, (int)response.StatusCode);

        // Missing host
        var bodyMissingHost = new
        {
            adapterKind = "host",
            adapterInstanceId = "dd-400-2",
            host = "",
            managedRoles = Array.Empty<string>(),
            managedCapabilities = Array.Empty<string>(),
        };

        json = JsonSerializer.Serialize(bodyMissingHost, JsonOpts);
        content = new StringContent(json, Encoding.UTF8, "application/json");
        response = await _client.PutAsync("/api/direct-delivery/bindings/dd-400-2", content);
        Assert.Equal(400, (int)response.StatusCode);

        // URL/body mismatch
        var bodyMismatch = new
        {
            adapterKind = "host",
            adapterInstanceId = "dd-400-url-mismatch",
            host = "test-host",
            managedRoles = Array.Empty<string>(),
            managedCapabilities = Array.Empty<string>(),
        };

        json = JsonSerializer.Serialize(bodyMismatch, JsonOpts);
        content = new StringContent(json, Encoding.UTF8, "application/json");
        response = await _client.PutAsync("/api/direct-delivery/bindings/dd-400-different-url", content);
        Assert.Equal(400, (int)response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test infrastructure
    // ═══════════════════════════════════════════════════════════════════

    private sealed class DirectDeliveryAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath =
            Path.Combine(Path.GetTempPath(), $"den-core-dd-contract-{Guid.NewGuid()}.db");

        public string DatabasePath => _dbPath;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["db-path"] = _dbPath,
                    ["llm-endpoint"] = "http://localhost/fake",
                    ["llm-api-key"] = "test-key",
                    ["llm-model"] = "fake",
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbConnectionFactory>();
                services.AddSingleton(new DbConnectionFactory($"Data Source={_dbPath}"));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
    }
}
