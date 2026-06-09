using DenCore.Data;
using DenCore.Models;
using DenCore.Tests;
using System.Text.Json;

namespace DenCore.Tests.Data;

public class UsageCostRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private IUsageCostRepository _repo = null!;
    private IProjectRepository _projects = null!;
    private ITaskRepository _tasks = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _repo = new UsageCostRepository(_testDb.Db);
        _projects = new ProjectRepository(_testDb.Db);
        _tasks = new TaskRepository(_testDb.Db);

        await _projects.CreateAsync(new Project { Id = "test-proj", Name = "Test Project" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    private async Task<int> SeedTaskAsync(string title = "Test Task")
    {
        var task = await _tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "test-proj",
            Title = title,
            Status = DenCore.Models.TaskStatus.Planned,
        }, null);
        return task.Id;
    }

    // ─────────────────────────────────────────────────────────────────
    // Schema
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Schema_CreatesUsageEventsAndPricingSnapshotsTables()
    {
        await using var conn = await _testDb.Db.CreateConnectionAsync();

        foreach (var table in new[] { "usage_events", "pricing_snapshots" })
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT count(*) FROM sqlite_master WHERE type='table' AND name='{table}'";
            Assert.Equal(1L, (await cmd.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task Schema_CreatesExpectedIndexes()
    {
        await using var conn = await _testDb.Db.CreateConnectionAsync();

        foreach (var idx in new[]
        {
            "idx_usage_events_project_occurred",
            "idx_usage_events_task_occurred",
            "idx_usage_events_role_occurred",
            "idx_usage_events_provider_model",
            "idx_usage_events_run",
            "idx_usage_events_pricing_snapshot",
            "idx_pricing_snapshots_version"
        })
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT count(*) FROM sqlite_master WHERE type='index' AND name='{idx}'";
            Assert.Equal(1L, (await cmd.ExecuteScalarAsync())!);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Default pricing catalog
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureDefaultPricingSnapshot_CreatesOnce()
    {
        var first = await _repo.EnsureDefaultPricingSnapshotAsync();
        Assert.Equal("initial-seed", first.SnapshotLabel);
        Assert.Equal("1.0.0", first.SnapshotVersion);

        var second = await _repo.EnsureDefaultPricingSnapshotAsync();
        Assert.Equal(first.Id, second.Id); // idempotent

        var entries = JsonSerializer.Deserialize<List<PricingEntry>>(first.EntriesJson);
        Assert.NotNull(entries);
        Assert.NotEmpty(entries);
    }

    [Fact]
    public async Task ResolvePricing_ExactMatch()
    {
        var snap = await _repo.EnsureDefaultPricingSnapshotAsync();
        var pricing = await _repo.ResolvePricingAsync(snap.Id, "deepseek", "deepseek-v4-flash");
        Assert.NotNull(pricing);
        Assert.Equal(27_000, pricing.InputPriceMicroCentsPerMillion);
        Assert.Equal(1_100_000, pricing.OutputPriceMicroCentsPerMillion);
        Assert.Equal("api", pricing.PricingKind);
    }

    [Fact]
    public async Task ResolvePricing_WildcardProviderMatch()
    {
        var snap = await _repo.EnsureDefaultPricingSnapshotAsync();
        // Unknown provider with known model — should fall to wildcard
        var pricing = await _repo.ResolvePricingAsync(snap.Id, "unknown-provider", "some-model");
        Assert.NotNull(pricing);
        Assert.Equal(UsageCostConstants.PricingKindUnknown, pricing.PricingKind);
    }

    [Fact]
    public async Task ResolvePricing_LocalModelReturnsFree()
    {
        var snap = await _repo.EnsureDefaultPricingSnapshotAsync();
        var pricing = await _repo.ResolvePricingAsync(snap.Id, "local", "llama-3-70b");
        Assert.NotNull(pricing);
        Assert.Equal(UsageCostConstants.PricingKindLocal, pricing.PricingKind);
    }

    // ─────────────────────────────────────────────────────────────────
    // Use case: Full token/cost data
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordUsage_FullTokenData_ComputesCostCorrectly()
    {
        var snap = await _repo.EnsureDefaultPricingSnapshotAsync();
        var taskId = await SeedTaskAsync();

        var e = new ModelUsageEvent
        {
            OccurredAt = DateTime.UtcNow.ToString("o"),
            ProjectId = "test-proj",
            TaskId = taskId,
            RunId = "run-1",
            AssignmentId = 42,
            WorkerRole = "coder",
            OperationKind = UsageCostConstants.OperationWorkerTurn,
            Provider = "deepseek",
            Model = "deepseek-v4-flash",
            EndpointKind = UsageCostConstants.EndpointApi,
            InputTokens = 1_000_000,
            OutputTokens = 500_000,
            PricingSnapshotId = snap.Id,
            Provenance = "hermes-bridge",
        };

        var result = await _repo.RecordUsageEventAsync(e);

        Assert.NotEqual(0, result.Id);
        // DeepSeek V4 Flash: $0.27/M input $1.10/M output
        // 1M input * 0.27 = 27,000 micro-cents
        // 500K output = 0.5M * 1,100,000 / 1,000,000 = 550,000 micro-cents
        // Total ~ 577,000 micro-cents = $0.00577
        Assert.NotNull(result.ApproximateCostMicroCents);
        Assert.True(result.ApproximateCostMicroCents > 0);
    }

    [Fact]
    public async Task RecordUsage_CostIsInMicroCents_NotFloatingPoint()
    {
        var snap = await _repo.EnsureDefaultPricingSnapshotAsync();
        var taskId = await SeedTaskAsync();

        var e = new ModelUsageEvent
        {
            OccurredAt = DateTime.UtcNow.ToString("o"),
            ProjectId = "test-proj",
            TaskId = taskId,
            WorkerRole = "coder",
            OperationKind = UsageCostConstants.OperationWorkerTurn,
            Provider = "deepseek",
            Model = "deepseek-v4-flash",
            EndpointKind = UsageCostConstants.EndpointApi,
            InputTokens = 100_000,
            OutputTokens = 50_000,
            PricingSnapshotId = snap.Id,
        };

        var result = await _repo.RecordUsageEventAsync(e);

        Assert.NotNull(result.ApproximateCostMicroCents);
        Assert.IsType<long>(result.ApproximateCostMicroCents!.Value);
        // Verify it's an integer — not a floating-point value in storage
        var fetched = await _repo.GetUsageEventAsync(result.Id);
        Assert.NotNull(fetched!.ApproximateCostMicroCents);
        Assert.True(fetched.ApproximateCostMicroCents.Value > 0);
    }

    // ─────────────────────────────────────────────────────────────────
    // Use case: Missing token data
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordUsage_MissingTokenData_ReturnsNullCost()
    {
        var snap = await _repo.EnsureDefaultPricingSnapshotAsync();
        var taskId = await SeedTaskAsync();

        var e = new ModelUsageEvent
        {
            OccurredAt = DateTime.UtcNow.ToString("o"),
            ProjectId = "test-proj",
            TaskId = taskId,
            WorkerRole = "reviewer",
            OperationKind = UsageCostConstants.OperationReview,
            Provider = "openai",
            Model = "gpt-4o",
            EndpointKind = UsageCostConstants.EndpointApi,
            // No token data
            InputTokens = null,
            OutputTokens = null,
            PricingSnapshotId = snap.Id,
        };

        var result = await _repo.RecordUsageEventAsync(e);

        // Should be null because no token data to compute cost from
        Assert.Null(result.ApproximateCostMicroCents);
    }

    [Fact]
    public async Task RecordUsage_PartialTokenData_HonestRepresentation()
    {
        var snap = await _repo.EnsureDefaultPricingSnapshotAsync();
        var taskId = await SeedTaskAsync();

        var e = new ModelUsageEvent
        {
            OccurredAt = DateTime.UtcNow.ToString("o"),
            ProjectId = "test-proj",
            TaskId = taskId,
            WorkerRole = "reviewer",
            OperationKind = UsageCostConstants.OperationReview,
            Provider = "openai",
            Model = "gpt-4o",
            EndpointKind = UsageCostConstants.EndpointApi,
            InputTokens = 100_000,
            OutputTokens = null, // Only input known
            PricingSnapshotId = snap.Id,
        };

        var result = await _repo.RecordUsageEventAsync(e);

        // Should have some cost from input tokens, not a fabricated full cost
        Assert.NotNull(result.ApproximateCostMicroCents);
        Assert.True(result.ApproximateCostMicroCents.Value > 0);
    }

    // ─────────────────────────────────────────────────────────────────
    // Use case: Unknown pricing (cost recorded as NULL)
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordUsage_UnknownPricing_CostIsNull()
    {
        // Create a snapshot with only unknown-catchall entry
        var snap = await _repo.CreatePricingSnapshotAsync(new PricingSnapshot
        {
            SnapshotLabel = "unknown-only",
            SnapshotVersion = "0.1.0",
            EntriesJson = JsonSerializer.Serialize(new List<PricingEntry>
            {
                new()
                {
                    Provider = "*",
                    Model = "*",
                    PricingKind = UsageCostConstants.PricingKindUnknown,
                }
            })
        });

        var taskId = await SeedTaskAsync();

        var e = new ModelUsageEvent
        {
            OccurredAt = DateTime.UtcNow.ToString("o"),
            ProjectId = "test-proj",
            TaskId = taskId,
            WorkerRole = "validator",
            OperationKind = UsageCostConstants.OperationValidation,
            Provider = "some-new-provider",
            Model = "some-new-model",
            EndpointKind = UsageCostConstants.EndpointApi,
            InputTokens = 1_000_000,
            OutputTokens = 500_000,
            PricingSnapshotId = snap.Id,
        };

        var result = await _repo.RecordUsageEventAsync(e);

        // Unknown pricing => cost is NULL, honest representation
        Assert.Null(result.ApproximateCostMicroCents);
    }

    // ─────────────────────────────────────────────────────────────────
    // Use case: Local/free model (cost recorded as 0)
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordUsage_LocalModel_ZeroCost()
    {
        var snap = await _repo.EnsureDefaultPricingSnapshotAsync();
        var taskId = await SeedTaskAsync();

        var e = new ModelUsageEvent
        {
            OccurredAt = DateTime.UtcNow.ToString("o"),
            ProjectId = "test-proj",
            TaskId = taskId,
            WorkerRole = "coder",
            OperationKind = UsageCostConstants.OperationWorkerTurn,
            Provider = "local",
            Model = "llama-3-70b",
            EndpointKind = UsageCostConstants.EndpointLocal,
            InputTokens = 10_000,
            OutputTokens = 5_000,
            PricingSnapshotId = snap.Id,
        };

        var result = await _repo.RecordUsageEventAsync(e);

        Assert.NotNull(result.ApproximateCostMicroCents);
        Assert.Equal(0L, result.ApproximateCostMicroCents!.Value);
    }

    [Fact]
    public async Task RecordUsage_OllamaModel_ZeroCost()
    {
        var snap = await _repo.EnsureDefaultPricingSnapshotAsync();
        var taskId = await SeedTaskAsync();

        var e = new ModelUsageEvent
        {
            OccurredAt = DateTime.UtcNow.ToString("o"),
            ProjectId = "test-proj",
            TaskId = taskId,
            WorkerRole = "coder",
            OperationKind = UsageCostConstants.OperationWorkerTurn,
            Provider = "ollama",
            Model = "qwen-2.5",
            EndpointKind = UsageCostConstants.EndpointLocal,
            InputTokens = 5_000,
            OutputTokens = 2_000,
            PricingSnapshotId = snap.Id,
        };

        var result = await _repo.RecordUsageEventAsync(e);

        Assert.NotNull(result.ApproximateCostMicroCents);
        Assert.Equal(0L, result.ApproximateCostMicroCents!.Value);
    }

    // ─────────────────────────────────────────────────────────────────
    // Use case: Alias-to-resolved-model attribution
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordUsage_ModelAlias_StoresBothAliasAndResolved()
    {
        var snap = await _repo.EnsureDefaultPricingSnapshotAsync();
        var taskId = await SeedTaskAsync();

        var e = new ModelUsageEvent
        {
            OccurredAt = DateTime.UtcNow.ToString("o"),
            ProjectId = "test-proj",
            TaskId = taskId,
            WorkerRole = "reviewer",
            OperationKind = UsageCostConstants.OperationReview,
            Provider = "deepseek",
            Model = "deepseek-v4-flash",
            ModelAlias = "cheap-fast-bounded",
            ResolvedModel = "deepseek-v4-flash",
            EndpointKind = UsageCostConstants.EndpointApi,
            InputTokens = 100_000,
            OutputTokens = 50_000,
            PricingSnapshotId = snap.Id,
        };

        var result = await _repo.RecordUsageEventAsync(e);

        Assert.Equal("cheap-fast-bounded", result.ModelAlias);
        Assert.Equal("deepseek-v4-flash", result.ResolvedModel);

        var fetched = await _repo.GetUsageEventAsync(result.Id);
        Assert.Equal("cheap-fast-bounded", fetched!.ModelAlias);
        Assert.Equal("deepseek-v4-flash", fetched.ResolvedModel);
    }

    // ─────────────────────────────────────────────────────────────────
    // Report: by task
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Report_ByTask_AggregatesCorrectly()
    {
        var snap = await _repo.EnsureDefaultPricingSnapshotAsync();
        var taskId1 = await SeedTaskAsync("Task 1");
        var taskId2 = await SeedTaskAsync("Task 2");

        // Task 1: two coder events
        await _repo.RecordUsageEventAsync(MakeEvent(snap.Id, taskId1, "coder", "deepseek", "deepseek-v4-flash",
            inputTokens: 100_000, outputTokens: 50_000));
        await _repo.RecordUsageEventAsync(MakeEvent(snap.Id, taskId1, "coder", "deepseek", "deepseek-v4-flash",
            inputTokens: 50_000, outputTokens: 25_000));

        // Task 2: one reviewer event
        await _repo.RecordUsageEventAsync(MakeEvent(snap.Id, taskId2, "reviewer", "anthropic", "claude-sonnet-4",
            inputTokens: 200_000, outputTokens: 100_000));

        var report = await _repo.RunReportAsync(new UsageCostQueryOptions
        {
            ProjectId = "test-proj",
            GroupBy = UsageCostConstants.GroupByTask,
            Limit = 10,
        });

        Assert.Equal(2, report.Rows.Count);
        Assert.Equal(3, report.TotalEvents);

        var task1Row = report.Rows.First(r => r.TaskId == taskId1);
        Assert.Equal(2, task1Row.EventCount);

        var task2Row = report.Rows.First(r => r.TaskId == taskId2);
        Assert.Equal(1, task2Row.EventCount);
    }

    // ─────────────────────────────────────────────────────────────────
    // Report: by role
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Report_ByRole_AggregatesCorrectly()
    {
        var snap = await _repo.EnsureDefaultPricingSnapshotAsync();
        var taskId = await SeedTaskAsync();

        await _repo.RecordUsageEventAsync(MakeEvent(snap.Id, taskId, "coder", "deepseek", "deepseek-v4-flash",
            inputTokens: 100_000, outputTokens: 50_000));
        await _repo.RecordUsageEventAsync(MakeEvent(snap.Id, taskId, "coder", "deepseek", "deepseek-v4-flash",
            inputTokens: 50_000, outputTokens: 25_000));
        await _repo.RecordUsageEventAsync(MakeEvent(snap.Id, taskId, "reviewer", "anthropic", "claude-sonnet-4",
            inputTokens: 200_000, outputTokens: 100_000));
        await _repo.RecordUsageEventAsync(MakeEvent(snap.Id, taskId, "validator", "deepseek", "deepseek-v4-flash",
            inputTokens: 75_000, outputTokens: 30_000));

        var report = await _repo.RunReportAsync(new UsageCostQueryOptions
        {
            ProjectId = "test-proj",
            GroupBy = UsageCostConstants.GroupByRole,
            Limit = 10,
        });

        Assert.Equal(3, report.Rows.Count); // coder, reviewer, validator
        Assert.Equal(4, report.TotalEvents);

        var coderRow = report.Rows.First(r => r.WorkerRole == "coder");
        Assert.Equal(2, coderRow.EventCount);
        Assert.True(coderRow.TotalInputTokens >= 150_000);

        var reviewerRow = report.Rows.First(r => r.WorkerRole == "reviewer");
        Assert.Equal(1, reviewerRow.EventCount);
    }

    // ─────────────────────────────────────────────────────────────────
    // Report: by model/provider
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Report_ByModel_AggregatesCorrectly()
    {
        var snap = await _repo.EnsureDefaultPricingSnapshotAsync();
        var taskId = await SeedTaskAsync();

        await _repo.RecordUsageEventAsync(MakeEvent(snap.Id, taskId, "coder", "deepseek", "deepseek-v4-flash",
            inputTokens: 100_000, outputTokens: 50_000));
        await _repo.RecordUsageEventAsync(MakeEvent(snap.Id, taskId, "reviewer", "deepseek", "deepseek-v4-flash",
            inputTokens: 50_000, outputTokens: 25_000));
        await _repo.RecordUsageEventAsync(MakeEvent(snap.Id, taskId, "reviewer", "anthropic", "claude-sonnet-4",
            inputTokens: 200_000, outputTokens: 100_000));
        await _repo.RecordUsageEventAsync(MakeEvent(snap.Id, taskId, "validator", "anthropic", "claude-sonnet-4",
            inputTokens: 75_000, outputTokens: 30_000));

        var report = await _repo.RunReportAsync(new UsageCostQueryOptions
        {
            ProjectId = "test-proj",
            GroupBy = UsageCostConstants.GroupByModel,
            Limit = 10,
        });

        Assert.Equal(2, report.Rows.Count); // 2 distinct provider+model combos
        Assert.Equal(4, report.TotalEvents);
    }

    [Fact]
    public async Task Report_ByProvider_AggregatesCorrectly()
    {
        var snap = await _repo.EnsureDefaultPricingSnapshotAsync();
        var taskId = await SeedTaskAsync();

        await _repo.RecordUsageEventAsync(MakeEvent(snap.Id, taskId, "coder", "deepseek", "deepseek-v4-flash",
            inputTokens: 100_000, outputTokens: 50_000));
        await _repo.RecordUsageEventAsync(MakeEvent(snap.Id, taskId, "reviewer", "deepseek", "deepseek-v4-pro",
            inputTokens: 200_000, outputTokens: 100_000));
        await _repo.RecordUsageEventAsync(MakeEvent(snap.Id, taskId, "reviewer", "anthropic", "claude-sonnet-4",
            inputTokens: 100_000, outputTokens: 50_000));

        var report = await _repo.RunReportAsync(new UsageCostQueryOptions
        {
            ProjectId = "test-proj",
            GroupBy = UsageCostConstants.GroupByProvider,
            Limit = 10,
        });

        Assert.Equal(2, report.Rows.Count); // deepseek + anthropic
        Assert.Equal(3, report.TotalEvents);
    }

    // ─────────────────────────────────────────────────────────────────
    // Report: project/time window
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Report_ByProject_AggregatesCorrectly()
    {
        var snap = await _repo.EnsureDefaultPricingSnapshotAsync();
        var taskId = await SeedTaskAsync();
        await _projects.CreateAsync(new Project { Id = "proj-2", Name = "Project 2" });

        await _repo.RecordUsageEventAsync(MakeEvent(snap.Id, taskId, "coder", "deepseek", "deepseek-v4-flash",
            inputTokens: 100_000, outputTokens: 50_000, projectId: "test-proj"));
        await _repo.RecordUsageEventAsync(MakeEvent(snap.Id, null, "coder", "deepseek", "deepseek-v4-flash",
            inputTokens: 50_000, outputTokens: 25_000, projectId: "proj-2"));

        var report = await _repo.RunReportAsync(new UsageCostQueryOptions
        {
            GroupBy = UsageCostConstants.GroupByProject,
            Limit = 10,
        });

        Assert.Equal(2, report.Rows.Count);
        Assert.Equal(2, report.TotalEvents);
    }

    [Fact]
    public async Task Report_TimeWindow_FiltersCorrectly()
    {
        var snap = await _repo.EnsureDefaultPricingSnapshotAsync();
        var taskId = await SeedTaskAsync();

        var now = DateTime.UtcNow;
        var oneHourAgo = now.AddHours(-1).ToString("o");
        var twoHoursAgo = now.AddHours(-2).ToString("o");
        var threeHoursAgo = now.AddHours(-3).ToString("o");

        await _repo.RecordUsageEventAsync(MakeEvent(snap.Id, taskId, "coder", "deepseek", "deepseek-v4-flash",
            inputTokens: 100_000, outputTokens: 50_000, occurredAt: oneHourAgo));
        await _repo.RecordUsageEventAsync(MakeEvent(snap.Id, taskId, "reviewer", "anthropic", "claude-sonnet-4",
            inputTokens: 200_000, outputTokens: 100_000, occurredAt: threeHoursAgo));

        var report = await _repo.RunReportAsync(new UsageCostQueryOptions
        {
            ProjectId = "test-proj",
            GroupBy = UsageCostConstants.GroupByTask,
            FromOccurredAt = twoHoursAgo,
            Limit = 10,
        });

        Assert.Single(report.Rows); // Only the 1-hour-ago event
        Assert.Equal(1, report.TotalEvents);

        var row = report.Rows[0];
        Assert.Single(report.Rows);
    }

    // ─────────────────────────────────────────────────────────────────
    // Report: unknown cost events tracked
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Report_TracksKnownVsUnknownCost()
    {
        var snap = await _repo.CreatePricingSnapshotAsync(new PricingSnapshot
        {
            SnapshotLabel = "mixed",
            SnapshotVersion = "0.1.0",
            EntriesJson = JsonSerializer.Serialize(new List<PricingEntry>
            {
                new() { Provider = "deepseek", Model = "deepseek-v4-flash",
                    InputPriceMicroCentsPerMillion = 27_000, OutputPriceMicroCentsPerMillion = 1_100_000,
                    PricingKind = UsageCostConstants.PricingKindApi },
                new() { Provider = "*", Model = "*", PricingKind = UsageCostConstants.PricingKindUnknown },
            })
        });
        var taskId = await SeedTaskAsync();

        // Known pricing
        await _repo.RecordUsageEventAsync(MakeEvent(snap.Id, taskId, "coder", "deepseek", "deepseek-v4-flash",
            inputTokens: 100_000, outputTokens: 50_000));

        // Unknown pricing
        await _repo.RecordUsageEventAsync(MakeEvent(snap.Id, taskId, "reviewer", "mystery", "model-x",
            inputTokens: 100_000, outputTokens: 50_000));

        var report = await _repo.RunReportAsync(new UsageCostQueryOptions
        {
            ProjectId = "test-proj",
            GroupBy = UsageCostConstants.GroupByTask,
            Limit = 10,
        });

        Assert.Single(report.Rows);
        var row = report.Rows[0];
        Assert.Equal(2, row.EventCount);
        Assert.True(row.EventsWithKnownCost >= 1);
        Assert.True(row.EventsWithUnknownCost >= 1);
    }

    // ─────────────────────────────────────────────────────────────────
    // Batch ingest
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordBatch_IngestsMultiple()
    {
        var snap = await _repo.EnsureDefaultPricingSnapshotAsync();
        var taskId = await SeedTaskAsync();

        var events = new List<ModelUsageEvent>
        {
            MakeEvent(snap.Id, taskId, "coder", "deepseek", "deepseek-v4-flash",
                inputTokens: 100_000, outputTokens: 50_000),
            MakeEvent(snap.Id, taskId, "reviewer", "anthropic", "claude-sonnet-4",
                inputTokens: 200_000, outputTokens: 100_000),
            MakeEvent(snap.Id, taskId, "validator", "openai", "gpt-4o-mini",
                inputTokens: 50_000, outputTokens: 25_000),
        };

        var results = await _repo.RecordUsageEventsAsync(events);
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.NotEqual(0, r.Id));

        // Verify we can fetch them all
        var list = await _repo.ListUsageEventsAsync(new UsageCostQueryOptions
        {
            ProjectId = "test-proj",
            TaskId = taskId,
            Limit = 10,
        });
        Assert.Equal(3, list.Count);
    }

    // ─────────────────────────────────────────────────────────────────
    // Pricing snapshot versioning
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PricingSnapshot_OlderEventsDontChangeWhenCatalogUpdates()
    {
        var snap1 = await _repo.EnsureDefaultPricingSnapshotAsync();

        var taskId = await SeedTaskAsync();
        var e = await _repo.RecordUsageEventAsync(MakeEvent(snap1.Id, taskId, "coder", "deepseek", "deepseek-v4-flash",
            inputTokens: 100_000, outputTokens: 50_000));

        var costAtSnap1 = e.ApproximateCostMicroCents;
        Assert.NotNull(costAtSnap1);

        // Create a new snapshot with drastically different pricing
        var newEntries = new List<PricingEntry>
        {
            new() { Provider = "deepseek", Model = "deepseek-v4-flash",
                InputPriceMicroCentsPerMillion = 1_000_000, OutputPriceMicroCentsPerMillion = 10_000_000,
                PricingKind = UsageCostConstants.PricingKindApi },
            new() { Provider = "*", Model = "*", PricingKind = UsageCostConstants.PricingKindUnknown },
        };
        var snap2 = await _repo.CreatePricingSnapshotAsync(new PricingSnapshot
        {
            SnapshotLabel = "price-hike",
            SnapshotVersion = "2.0.0",
            EntriesJson = JsonSerializer.Serialize(newEntries),
        });

        // Fetch the old event — it should still use snap1's pricing
        var fetched = await _repo.GetUsageEventAsync(e.Id);
        Assert.Equal(snap1.Id, fetched!.PricingSnapshotId);
        Assert.Equal(costAtSnap1, fetched.ApproximateCostMicroCents);
        Assert.NotEqual(snap2.Id, fetched.PricingSnapshotId);
    }

    // ─────────────────────────────────────────────────────────────────
    // Error kind tracking
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordUsage_ErrorKind_StoredCorrectly()
    {
        var snap = await _repo.EnsureDefaultPricingSnapshotAsync();
        var taskId = await SeedTaskAsync();

        var e = new ModelUsageEvent
        {
            OccurredAt = DateTime.UtcNow.ToString("o"),
            ProjectId = "test-proj",
            TaskId = taskId,
            WorkerRole = "coder",
            OperationKind = UsageCostConstants.OperationWorkerTurn,
            Provider = "deepseek",
            Model = "deepseek-v4-flash",
            EndpointKind = UsageCostConstants.EndpointApi,
            ErrorKind = "rate_limit",
            InputTokens = 0,
            OutputTokens = null,
            PricingSnapshotId = snap.Id,
        };

        var result = await _repo.RecordUsageEventAsync(e);
        Assert.Equal("rate_limit", result.ErrorKind);

        var fetched = await _repo.GetUsageEventAsync(result.Id);
        Assert.Equal("rate_limit", fetched!.ErrorKind);
    }

    [Fact]
    public async Task RecordUsage_RetryCountAndStreaming_StoredCorrectly()
    {
        var snap = await _repo.EnsureDefaultPricingSnapshotAsync();
        var taskId = await SeedTaskAsync();

        var e = new ModelUsageEvent
        {
            OccurredAt = DateTime.UtcNow.ToString("o"),
            ProjectId = "test-proj",
            TaskId = taskId,
            WorkerRole = "coder",
            OperationKind = UsageCostConstants.OperationWorkerTurn,
            Provider = "deepseek",
            Model = "deepseek-v4-flash",
            EndpointKind = UsageCostConstants.EndpointApi,
            RequestCount = 3,
            RetryCount = 2,
            Streaming = true,
            InputTokens = 100_000,
            OutputTokens = 50_000,
            PricingSnapshotId = snap.Id,
        };

        var result = await _repo.RecordUsageEventAsync(e);
        Assert.Equal(3, result.RequestCount);
        Assert.Equal(2, result.RetryCount);
        Assert.True(result.Streaming);

        var fetched = await _repo.GetUsageEventAsync(result.Id);
        Assert.Equal(3, fetched!.RequestCount);
        Assert.Equal(2, fetched.RetryCount);
        Assert.True(fetched.Streaming);
    }

    // ─────────────────────────────────────────────────────────────────
    // All workflow attribution fields
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordUsage_AllAttributionFields_StoredCorrectly()
    {
        var snap = await _repo.EnsureDefaultPricingSnapshotAsync();
        var taskId = await SeedTaskAsync();

        var e = new ModelUsageEvent
        {
            OccurredAt = "2026-06-09T12:00:00Z",
            ProjectId = "test-proj",
            TaskId = taskId,
            AssignmentId = 42,
            RunId = "run-abc-123",
            SessionId = "sess-def-456",
            AgentIdentity = "spawned-coder",
            ProfileIdentity = "spawned-coder",
            WorkerRole = "coder",
            WorkerIdentity = "spawned-coder-run-1",
            OperationKind = UsageCostConstants.OperationWorkerTurn,
            Provider = "deepseek",
            Model = "deepseek-v4-flash",
            ModelAlias = "cheap-fast",
            ResolvedModel = "deepseek-v4-flash",
            EndpointKind = UsageCostConstants.EndpointApi,
            InputTokens = 100_000,
            OutputTokens = 50_000,
            CacheReadTokens = 10_000,
            CacheWriteTokens = 5_000,
            ReasoningTokens = 20_000,
            ToolResultTokens = 15_000,
            RequestCount = 1,
            RetryCount = 0,
            Streaming = false,
            ErrorKind = null,
            PricingSnapshotId = snap.Id,
            Provenance = "hermes-bridge",
            AdapterVersion = "2.1.0",
            RawUsageSource = "openai_usage_block",
            RequestIdHint = "req-xxxx-redacted",
        };

        var result = await _repo.RecordUsageEventAsync(e);

        Assert.Equal("2026-06-09T12:00:00Z", result.OccurredAt);
        Assert.Equal("test-proj", result.ProjectId);
        Assert.Equal(taskId, result.TaskId);
        Assert.Equal(42, result.AssignmentId);
        Assert.Equal("run-abc-123", result.RunId);
        Assert.Equal("sess-def-456", result.SessionId);
        Assert.Equal("spawned-coder", result.AgentIdentity);
        Assert.Equal("coder", result.WorkerRole);
        Assert.Equal("cheap-fast", result.ModelAlias);
        Assert.Equal("deepseek-v4-flash", result.ResolvedModel);
        Assert.Equal(10_000, result.CacheReadTokens);
        Assert.Equal(5_000, result.CacheWriteTokens);
        Assert.Equal(20_000, result.ReasoningTokens);
        Assert.Equal(15_000, result.ToolResultTokens);
        Assert.Equal("hermes-bridge", result.Provenance);
        Assert.Equal("2.1.0", result.AdapterVersion);
        Assert.Equal("openai_usage_block", result.RawUsageSource);
        Assert.Equal("req-xxxx-redacted", result.RequestIdHint);
    }

    // ─────────────────────────────────────────────────────────────────
    // Exact integer cost arithmetic — no floating-point drift
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ComputeCost_ExactIntegerArithmetic_NoFloatingPointDrift()
    {
        var snap = await _repo.EnsureDefaultPricingSnapshotAsync();
        var taskId = await SeedTaskAsync();

        // Concrete scenario: openai gpt-4o-mini
        // input: 15000 micro-cents/M, output: 60000 micro-cents/M
        // 500K input → (500_000 * 15000 + 500_000) / 1_000_000 = (7_500_000_000 + 500_000) / 1_000_000 = 7500
        // 200K output → (200_000 * 60000 + 500_000) / 1_000_000 = (12_000_000_000 + 500_000) / 1_000_000 = 12000
        var e = await _repo.RecordUsageEventAsync(MakeEvent(snap.Id, taskId, "coder",
            "openai", "gpt-4o-mini", inputTokens: 500_000, outputTokens: 200_000));

        Assert.NotNull(e.ApproximateCostMicroCents);
        Assert.Equal(19500, e.ApproximateCostMicroCents.Value); // 7500 + 12000 = 19500 micro-cents
    }

    [Fact]
    public async Task ComputeCost_IncludesReasoningAndPerRequestCosts_ExactInteger()
    {
        var snap = await _repo.EnsureDefaultPricingSnapshotAsync();
        var taskId = await SeedTaskAsync();

        // xai grok-3: input 300000, output 1500000 per-M micro-cents
        // 500K input → (500_000 * 300_000 + 500_000) / 1_000_000 = 150_000
        // 100K output → (100_000 * 1_500_000 + 500_000) / 1_000_000 = 150_000
        // Reasoning tokens don't have specific pricing for grok-3, so cost comes only from input+output
        var e = await _repo.RecordUsageEventAsync(new ModelUsageEvent
        {
            OccurredAt = DateTime.UtcNow.ToString("o"),
            ProjectId = "test-proj",
            TaskId = taskId,
            WorkerRole = "coder",
            OperationKind = UsageCostConstants.OperationWorkerTurn,
            Provider = "xai",
            Model = "grok-3",
            EndpointKind = UsageCostConstants.EndpointApi,
            InputTokens = 500_000,
            OutputTokens = 100_000,
            ReasoningTokens = 200_000,
            RequestCount = 1,
            PricingSnapshotId = snap.Id,
        });

        Assert.NotNull(e.ApproximateCostMicroCents);
        Assert.Equal(300_000, e.ApproximateCostMicroCents.Value); // 150_000 + 150_000 = 300_000
    }

    [Fact]
    public async Task ComputeCost_FreeModel_ReturnsZeroMicroCents()
    {
        var snap = await _repo.EnsureDefaultPricingSnapshotAsync();
        var taskId = await SeedTaskAsync();

        var e = await _repo.RecordUsageEventAsync(new ModelUsageEvent
        {
            OccurredAt = DateTime.UtcNow.ToString("o"),
            ProjectId = "test-proj",
            TaskId = taskId,
            WorkerRole = "coder",
            OperationKind = UsageCostConstants.OperationWorkerTurn,
            Provider = "ollama",
            Model = "llama3.2:3b",
            EndpointKind = UsageCostConstants.EndpointLocal,
            InputTokens = 10_000,
            OutputTokens = 5_000,
            PricingSnapshotId = snap.Id,
        });

        Assert.NotNull(e.ApproximateCostMicroCents);
        Assert.Equal(0, e.ApproximateCostMicroCents.Value);
    }

    [Fact]
    public async Task ComputeCost_UnknownPricing_ReturnsNull()
    {
        var snap = await _repo.EnsureDefaultPricingSnapshotAsync();
        var taskId = await SeedTaskAsync();

        var e = await _repo.RecordUsageEventAsync(new ModelUsageEvent
        {
            OccurredAt = DateTime.UtcNow.ToString("o"),
            ProjectId = "test-proj",
            TaskId = taskId,
            WorkerRole = "coder",
            OperationKind = UsageCostConstants.OperationWorkerTurn,
            Provider = "unknown-provider",
            Model = "unknown-model",
            EndpointKind = UsageCostConstants.EndpointApi,
            InputTokens = 10_000,
            OutputTokens = 5_000,
            PricingSnapshotId = snap.Id,
        });

        Assert.Null(e.ApproximateCostMicroCents);
    }

    [Fact]
    public async Task ComputeCost_CacheTokens_ExactIntegerArithmetic()
    {
        var snap = await _repo.EnsureDefaultPricingSnapshotAsync();
        var taskId = await SeedTaskAsync();

        // anthropic claude-sonnet-4: input 300_000, output 1_500_000, cache_read 30_000, cache_write 375_000 per-M micro-cents
        // 100K input → (100_000 * 300_000 + 500_000) / 1_000_000 = 30_000
        // 50K output → (50_000 * 1_500_000 + 500_000) / 1_000_000 = 75_000
        // 200K cache_read → (200_000 * 30_000 + 500_000) / 1_000_000 = 6_000
        // 10K cache_write → (10_000 * 375_000 + 500_000) / 1_000_000 = 3_750
        // Total: 30_000 + 75_000 + 6_000 + 3_750 = 114_750
        var e = await _repo.RecordUsageEventAsync(new ModelUsageEvent
        {
            OccurredAt = DateTime.UtcNow.ToString("o"),
            ProjectId = "test-proj",
            TaskId = taskId,
            WorkerRole = "reviewer",
            OperationKind = UsageCostConstants.OperationReview,
            Provider = "anthropic",
            Model = "claude-sonnet-4",
            EndpointKind = UsageCostConstants.EndpointApi,
            InputTokens = 100_000,
            OutputTokens = 50_000,
            CacheReadTokens = 200_000,
            CacheWriteTokens = 10_000,
            PricingSnapshotId = snap.Id,
        });

        Assert.NotNull(e.ApproximateCostMicroCents);
        Assert.Equal(114_750, e.ApproximateCostMicroCents.Value);
    }

    // ─────────────────────────────────────────────────────────────────
    // Cache read/write cost computation (behavioural)
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ComputeCost_IncludesCacheTokens()
    {
        var snap = await _repo.EnsureDefaultPricingSnapshotAsync();
        var taskId = await SeedTaskAsync();

        // Use Anthropic pricing which has cache read/write prices
        var eWithoutCache = await _repo.RecordUsageEventAsync(MakeEvent(snap.Id, taskId, "reviewer",
            "anthropic", "claude-sonnet-4", inputTokens: 100_000, outputTokens: 50_000));

        var eWithCache = await _repo.RecordUsageEventAsync(new ModelUsageEvent
        {
            OccurredAt = DateTime.UtcNow.ToString("o"),
            ProjectId = "test-proj",
            TaskId = taskId,
            WorkerRole = "reviewer",
            OperationKind = UsageCostConstants.OperationReview,
            Provider = "anthropic",
            Model = "claude-sonnet-4",
            EndpointKind = UsageCostConstants.EndpointApi,
            InputTokens = 100_000,
            OutputTokens = 50_000,
            CacheReadTokens = 500_000, // Large cache read
            CacheWriteTokens = 100_000,
            PricingSnapshotId = snap.Id,
        });

        Assert.NotNull(eWithoutCache.ApproximateCostMicroCents);
        Assert.NotNull(eWithCache.ApproximateCostMicroCents);
        Assert.True(eWithCache.ApproximateCostMicroCents > eWithoutCache.ApproximateCostMicroCents,
            "Cache tokens should increase cost");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private ModelUsageEvent MakeEvent(
        int snapId, int? taskId, string workerRole, string provider, string model,
        int? inputTokens = null, int? outputTokens = null,
        string? projectId = "test-proj", string? occurredAt = null)
    {
        return new ModelUsageEvent
        {
            OccurredAt = occurredAt ?? DateTime.UtcNow.ToString("o"),
            ProjectId = projectId!,
            TaskId = taskId,
            WorkerRole = workerRole,
            OperationKind = workerRole switch
            {
                "coder" => UsageCostConstants.OperationWorkerTurn,
                "reviewer" => UsageCostConstants.OperationReview,
                "validator" => UsageCostConstants.OperationValidation,
                _ => UsageCostConstants.OperationWorkerTurn,
            },
            Provider = provider,
            Model = model,
            EndpointKind = UsageCostConstants.EndpointApi,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            PricingSnapshotId = snapId,
        };
    }
}
