using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;

namespace DenMcp.Core.Tests.Services;

public sealed class SubagentRunServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"den-subagent-runs-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public async Task SessionFallback_DoesNotTreatDisabledProviderSummaryAsRawPreview()
    {
        const string runId = "run-summary-fallback";
        const string summary = "Reviewed the safe provider-visible summary.";
        var artifactDir = Path.Combine(_tempRoot, "den-subagent-runs", runId);
        var sessionDir = Path.Combine(artifactDir, "sessions");
        Directory.CreateDirectory(sessionDir);

        await File.WriteAllTextAsync(Path.Combine(artifactDir, "status.json"), JsonSerializer.Serialize(new
        {
            reasoning_capture = new
            {
                capture_provider_summaries = false,
                capture_raw_local_previews = true,
                preview_chars = 240
            }
        }));
        await File.WriteAllTextAsync(Path.Combine(sessionDir, "session.jsonl"), JsonSerializer.Serialize(new
        {
            type = "message",
            timestamp = "2026-04-27T02:00:00Z",
            message = new
            {
                role = "assistant",
                provider = "openai",
                model = "gpt-test",
                content = new object[]
                {
                    new
                    {
                        type = "thinking",
                        thinking = summary,
                        thinkingSignature = JsonSerializer.Serialize(new { summary })
                    }
                }
            }
        }) + "\n");

        var stream = new FakeAgentStreamRepository([
            StreamEntry(runId, artifactDir)
        ]);
        var service = new SubagentRunService(stream, new FakeAgentRunRepository());

        var detail = await service.GetAsync(runId, new SubagentRunListOptions { ProjectId = "den-mcp", TaskId = 854 });

        Assert.NotNull(detail);
        var reasoning = Assert.Single(detail.WorkEvents, EventTypeIs("subagent.work_reasoning_end"));
        Assert.True(reasoning.TryGetProperty("reasoning_redacted", out var redacted));
        Assert.True(redacted.GetBoolean());
        Assert.False(reasoning.TryGetProperty("text_preview", out _));
        Assert.False(reasoning.TryGetProperty("reasoning_summary_preview", out _));
    }

    [Fact]
    public async Task ListAsync_UsesPrecomputedCountersWithoutPerRunEventLoads()
    {
        // Simulate 10 runs, each with a started + completed entry in the stream.
        // The FakeAgentStreamRepository tracks query counts to verify batched loading.
        var entries = new List<AgentStreamEntry>();
        var records = new List<AgentRunRecord>();
        var utcNow = DateTime.UtcNow;

        for (var i = 0; i < 10; i++)
        {
            var runId = $"run-list-{i:D3}";
            var startedId = i * 2 + 1;
            var completedId = i * 2 + 2;

            entries.Add(new AgentStreamEntry
            {
                Id = startedId,
                StreamKind = AgentStreamKind.Ops,
                EventType = "subagent_started",
                ProjectId = "proj",
                TaskId = 100,
                Sender = "pi",
                DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
                CreatedAt = utcNow.AddSeconds(-10 + i),
                Metadata = JsonSerializer.SerializeToElement(new { run_id = runId, role = "coder", backend = "pi-cli" })
            });
            entries.Add(new AgentStreamEntry
            {
                Id = completedId,
                StreamKind = AgentStreamKind.Ops,
                EventType = "subagent_completed",
                ProjectId = "proj",
                TaskId = 100,
                Sender = "pi",
                DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
                CreatedAt = utcNow.AddSeconds(i),
                Metadata = JsonSerializer.SerializeToElement(new
                {
                    run_id = runId,
                    role = "coder",
                    exit_code = 0,
                    output_status = "assistant_final"
                })
            });

            records.Add(new AgentRunRecord
            {
                RunId = runId,
                ProjectId = "proj",
                TaskId = 100,
                Role = "coder",
                State = "complete",
                Backend = "pi-cli",
                StartedAt = utcNow.AddSeconds(-10 + i),
                EndedAt = utcNow.AddSeconds(i),
                LatestStreamEntryId = completedId,
                StartedStreamEntryId = startedId,
                EventCount = 2,
                RawWorkEventCount = 0,
                OperatorEventsJson = JsonSerializer.Serialize(new List<SubagentRunOperatorEvent>
                {
                    new() { EventName = "coder_started", Source = "agent_stream", SourceEventType = "subagent_started", StreamEntryId = startedId, OccurredAt = utcNow.AddSeconds(-10 + i), Visibility = "summary" },
                    new() { EventName = "coder_completed", Source = "agent_stream", SourceEventType = "subagent_completed", StreamEntryId = completedId, OccurredAt = utcNow.AddSeconds(i), Visibility = "summary" }
                }),
                HeartbeatCount = 0,
                AssistantOutputCount = 0,
                CreatedAt = utcNow.AddSeconds(-10 + i),
                UpdatedAt = utcNow
            });
        }

        var stream = new FakeAgentStreamRepository(entries);
        var runRepo = new FakeAgentRunRepository(records);
        var service = new SubagentRunService(stream, runRepo);

        var summaries = await service.ListAsync(new SubagentRunListOptions
        {
            ProjectId = "proj",
            TaskId = 100,
            Limit = 10,
            SourceLimit = 0  // Suppress stream summary discovery to test record-only path
        });

        Assert.Equal(10, summaries.Count);

        // Verify each summary has correct pre-computed counters
        foreach (var summary in summaries)
        {
            Assert.Equal("complete", summary.State);
            Assert.Equal(2, summary.EventCounts.Total);
            Assert.Equal(2, summary.EventCounts.Lifecycle); // started + completed (no raw work)
            Assert.Equal(0, summary.EventCounts.RawWork);
            Assert.Equal(2, summary.OperatorEvents.Count);
            Assert.Equal("coder_started", summary.OperatorEvents[0].EventName);
            Assert.Equal("coder_completed", summary.OperatorEvents[1].EventName);
        }

        // Verify that ListAsync used batched GetByIdsAsync (1 call) instead of
        // individual GetByIdAsync calls (would have been 20 for 10 runs × 2 entries).
        Assert.True(stream.GetByIdsAsyncCallCount <= 2, $"Expected ≤2 GetByIdsAsync calls, got {stream.GetByIdsAsyncCallCount}");
        Assert.Equal(0, stream.GetByIdAsyncCallCount);
    }

    [Fact]
    public async Task ListAsync_WithManyRunsAndEvents_PerformanceRegression()
    {
        // Create 50 runs, each with 3 materialized work events.
        // This tests that the list path avoids O(N) event loading.
        const int runCount = 50;
        const int workEventsPerRun = 3;
        var entries = new List<AgentStreamEntry>();
        var records = new List<AgentRunRecord>();
        var utcNow = DateTime.UtcNow;

        for (var i = 0; i < runCount; i++)
        {
            var runId = $"run-perf-{i:D3}";
            var startedId = i * 2 + 1;
            var completedId = i * 2 + 2;

            entries.Add(new AgentStreamEntry
            {
                Id = startedId,
                StreamKind = AgentStreamKind.Ops,
                EventType = "subagent_started",
                ProjectId = "proj",
                TaskId = 200,
                Sender = "pi",
                DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
                CreatedAt = utcNow.AddSeconds(-runCount + i),
                Metadata = JsonSerializer.SerializeToElement(new { run_id = runId, role = "coder", backend = "pi-cli" })
            });
            entries.Add(new AgentStreamEntry
            {
                Id = completedId,
                StreamKind = AgentStreamKind.Ops,
                EventType = "subagent_completed",
                ProjectId = "proj",
                TaskId = 200,
                Sender = "pi",
                DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
                CreatedAt = utcNow.AddSeconds(i),
                Metadata = JsonSerializer.SerializeToElement(new
                {
                    run_id = runId,
                    role = "coder",
                    exit_code = 0,
                    output_status = "assistant_final"
                })
            });

            var totalEvents = 2 + workEventsPerRun; // started + work events + completed
            records.Add(new AgentRunRecord
            {
                RunId = runId,
                ProjectId = "proj",
                TaskId = 200,
                Role = "coder",
                State = "complete",
                Backend = "pi-cli",
                StartedAt = utcNow.AddSeconds(-runCount + i),
                EndedAt = utcNow.AddSeconds(i),
                LatestStreamEntryId = completedId,
                StartedStreamEntryId = startedId,
                EventCount = totalEvents,
                RawWorkEventCount = workEventsPerRun,
                OperatorEventsJson = JsonSerializer.Serialize(new List<SubagentRunOperatorEvent>
                {
                    new() { EventName = "coder_started", Source = "agent_stream", SourceEventType = "subagent_started", StreamEntryId = startedId, OccurredAt = utcNow.AddSeconds(-runCount + i), Visibility = "summary" },
                    new() { EventName = "coder_completed", Source = "agent_stream", SourceEventType = "subagent_completed", StreamEntryId = completedId, OccurredAt = utcNow.AddSeconds(i), Visibility = "summary" }
                }),
                HeartbeatCount = 0,
                AssistantOutputCount = 0,
                CreatedAt = utcNow.AddSeconds(-runCount + i),
                UpdatedAt = utcNow
            });
        }

        var stream = new FakeAgentStreamRepository(entries);
        var runRepo = new FakeAgentRunRepository(records);
        var service = new SubagentRunService(stream, runRepo);

        var summaries = await service.ListAsync(new SubagentRunListOptions
        {
            ProjectId = "proj",
            TaskId = 200,
            Limit = 50,
            SourceLimit = 0
        });

        Assert.Equal(runCount, summaries.Count);

        // Verify event counts reflect materialized data, not event loading
        foreach (var summary in summaries)
        {
            Assert.Equal(2 + workEventsPerRun, summary.EventCounts.Total);
            Assert.Equal(workEventsPerRun, summary.EventCounts.RawWork);
            Assert.Equal(workEventsPerRun, summary.EventCounts.Debug);
            Assert.Equal(2, summary.EventCounts.Lifecycle); // started + completed only
            Assert.Equal(2, summary.OperatorEvents.Count);
        }

        // Performance guard: the deterministic signal is the query path, not wall-clock
        // timing (which can flake on slow CI runners). This would have been 100+
        // single-entry lookups before the materialized/batched list projection.
        Assert.Equal(0, stream.GetByIdAsyncCallCount);
        Assert.True(stream.GetByIdsAsyncCallCount <= 2, $"Expected ≤2 GetByIdsAsync calls, got {stream.GetByIdsAsyncCallCount}");
    }

    [Fact]
    public async Task GetAsync_DetailPath_StillLoadsFullEvents()
    {
        // Verify that the detail (GetAsync) path still loads full stream events
        // for raw event/artifact behavior, not using pre-computed shortcuts.
        const string runId = "run-detail";
        var utcNow = DateTime.UtcNow;

        var entries = new List<AgentStreamEntry>
        {
            new()
            {
                Id = 1,
                StreamKind = AgentStreamKind.Ops,
                EventType = "subagent_started",
                ProjectId = "proj",
                TaskId = 300,
                Sender = "pi",
                DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
                CreatedAt = utcNow,
                Metadata = JsonSerializer.SerializeToElement(new { run_id = runId, role = "coder", backend = "pi-cli" })
            },
            new()
            {
                Id = 2,
                StreamKind = AgentStreamKind.Ops,
                EventType = "subagent_completed",
                ProjectId = "proj",
                TaskId = 300,
                Sender = "pi",
                DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
                CreatedAt = utcNow.AddSeconds(5),
                Metadata = JsonSerializer.SerializeToElement(new { run_id = runId, role = "coder", exit_code = 0 })
            }
        };

        var record = new AgentRunRecord
        {
            RunId = runId,
            ProjectId = "proj",
            TaskId = 300,
            Role = "coder",
            State = "complete",
            Backend = "pi-cli",
            StartedAt = utcNow,
            EndedAt = utcNow.AddSeconds(5),
            LatestStreamEntryId = 2,
            StartedStreamEntryId = 1,
            EventCount = 2,
            RawWorkEventCount = 0,
            HeartbeatCount = 0,
            AssistantOutputCount = 0,
            CreatedAt = utcNow,
            UpdatedAt = utcNow
        };

        var stream = new FakeAgentStreamRepository(entries);
        var runRepo = new FakeAgentRunRepository([record]);
        var service = new SubagentRunService(stream, runRepo);

        var detail = await service.GetAsync(runId, new SubagentRunListOptions
        {
            ProjectId = "proj",
            TaskId = 300
        });

        Assert.NotNull(detail);
        Assert.Equal(2, detail.Events.Count);
        Assert.Equal("subagent_started", detail.Events[0].EventType);
        Assert.Equal("subagent_completed", detail.Events[1].EventType);
        Assert.Equal(2, detail.Summary.EventCounts.Total);
        Assert.Equal(2, detail.Summary.EventCounts.Lifecycle);
    }

    private static Predicate<JsonElement> EventTypeIs(string eventType) => element =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty("type", out var type) &&
        type.ValueKind == JsonValueKind.String &&
        type.GetString() == eventType;

    private static AgentStreamEntry StreamEntry(string runId, string artifactDir) => new()
    {
        Id = 1,
        StreamKind = AgentStreamKind.Ops,
        EventType = "subagent_completed",
        ProjectId = "den-mcp",
        TaskId = 854,
        Sender = "pi",
        DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
        CreatedAt = DateTime.UtcNow,
        Metadata = JsonSerializer.SerializeToElement(new
        {
            run_id = runId,
            role = "coder",
            backend = "pi-cli",
            model = "gpt-test",
            artifacts = new { dir = artifactDir }
        })
    };

    private sealed class FakeAgentStreamRepository(IReadOnlyList<AgentStreamEntry> entries) : IAgentStreamRepository
    {
        public int GetByIdAsyncCallCount { get; private set; }
        public int GetByIdsAsyncCallCount { get; private set; }

        public Task<AgentStreamEntry> AppendAsync(AgentStreamEntry entry) => Task.FromResult(entry);

        public Task<AgentStreamEntry?> GetByIdAsync(int id)
        {
            GetByIdAsyncCallCount++;
            return Task.FromResult(entries.FirstOrDefault(entry => entry.Id == id));
        }

        public Task<Dictionary<int, AgentStreamEntry>> GetByIdsAsync(IReadOnlyList<int> ids)
        {
            GetByIdsAsyncCallCount++;
            var result = entries.Where(e => ids.Contains(e.Id)).ToDictionary(e => e.Id);
            return Task.FromResult(result);
        }

        public Task<List<AgentStreamEntry>> ListAsync(AgentStreamListOptions? options = null) =>
            Task.FromResult(entries
                .Where(entry => options?.ProjectId is null || entry.ProjectId == options.ProjectId)
                .Where(entry => options?.TaskId is null || entry.TaskId == options.TaskId)
                .Where(entry => options?.StreamKind is null || entry.StreamKind == options.StreamKind)
                .Where(entry => options?.MetadataRunId is null || entry.Metadata?.TryGetProperty("run_id", out var runId) == true && runId.GetString() == options.MetadataRunId)
                .Take(Math.Clamp(options?.Limit ?? 50, 1, 200))
                .ToList());
    }

    private sealed class FakeAgentRunRepository : IAgentRunRepository
    {
        private readonly List<AgentRunRecord> _records;

        public FakeAgentRunRepository() : this([]) { }
        public FakeAgentRunRepository(List<AgentRunRecord> records) => _records = records;

        public Task<bool> UpsertFromStreamEntryAsync(AgentStreamEntry entry) => Task.FromResult(false);
        public Task<AgentRunRecord?> RebuildFromStreamAsync(string runId) => Task.FromResult<AgentRunRecord?>(null);
        public Task<AgentRunRecord?> GetAsync(string runId, SubagentRunListOptions options) => Task.FromResult(_records.FirstOrDefault(r => r.RunId == runId));
        public Task<List<AgentRunRecord>> ListAsync(SubagentRunListOptions options) => Task.FromResult(_records.ToList());
    }
}
