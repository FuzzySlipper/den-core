using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenMcp.Core.Tests.Data;

public class AgentRunRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private AgentStreamRepository _stream = null!;
    private AgentRunRepository _runs = null!;
    private ProjectTask _task = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _stream = new AgentStreamRepository(_testDb.Db);
        _runs = new AgentRunRepository(_testDb.Db);

        var projects = new ProjectRepository(_testDb.Db);
        await projects.CreateAsync(new Project { Id = "proj", Name = "Project" });
        await projects.CreateAsync(new Project { Id = "other", Name = "Other" });

        var tasks = new TaskRepository(_testDb.Db);
        _task = await tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "proj",
            Title = "Sub-agent host"
        });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    [Fact]
    public async Task UpsertFromStreamEntry_ProjectsLifecycleIntoDurableRunRecord()
    {
        var started = await AppendAndProjectAsync("subagent_started", """
            {
              "schema":"den_subagent_run",
              "schema_version":1,
              "run_id":"run-complete",
              "role":"coder",
              "backend":"pi-cli",
              "model":"gpt-test",
              "sender_instance_id":"pi-proj-coder-run-complete",
              "started_at":"2026-04-26T08:00:00Z",
              "artifacts":{
                "dir":"/tmp/den-subagent-runs/run-complete",
                "stdout_jsonl_path":"/tmp/den-subagent-runs/run-complete/stdout.jsonl",
                "stderr_log_path":"/tmp/den-subagent-runs/run-complete/stderr.log",
                "status_json_path":"/tmp/den-subagent-runs/run-complete/status.json",
                "events_jsonl_path":"/tmp/den-subagent-runs/run-complete/events.jsonl"
              }
            }
            """);
        var processStarted = await AppendAndProjectAsync("subagent_process_started", """
            {"run_id":"run-complete","event":{"type":"subagent.process_started","pid":4242}}
            """);
        await AppendAndProjectAsync("subagent_heartbeat", """
            {"run_id":"run-complete","event":{"type":"subagent.heartbeat","duration_ms":5000}}
            """);
        await AppendAndProjectAsync("subagent_assistant_output", """
            {"run_id":"run-complete","event":{"type":"subagent.assistant_output","chars":32}}
            """);
        var completed = await AppendAndProjectAsync("subagent_completed", """
            {
              "run_id":"run-complete",
              "role":"coder",
              "backend":"pi-cli",
              "model":"gpt-test-final",
              "ended_at":"2026-04-26T08:00:09Z",
              "duration_ms":9000,
              "exit_code":0,
              "output_status":"assistant_final",
              "infrastructure_warning_reason":"extension_runtime"
            }
            """);

        var record = await _runs.GetAsync("run-complete", new SubagentRunListOptions { ProjectId = "proj", TaskId = _task.Id });

        Assert.NotNull(record);
        Assert.Equal("run-complete", record!.RunId);
        Assert.Equal("complete", record.State);
        Assert.Equal("proj", record.ProjectId);
        Assert.Equal(_task.Id, record.TaskId);
        Assert.Equal("coder", record.Role);
        Assert.Equal("pi-cli", record.Backend);
        Assert.Equal("gpt-test-final", record.Model);
        Assert.Equal("pi-proj-coder-run-complete", record.SenderInstanceId);
        Assert.Equal(DateTime.Parse("2026-04-26T08:00:00Z"), record.StartedAt);
        Assert.Equal(DateTime.Parse("2026-04-26T08:00:09Z"), record.EndedAt);
        Assert.Equal(9000, record.DurationMs);
        Assert.Equal(4242, record.Pid);
        Assert.Equal(0, record.ExitCode);
        Assert.Equal("assistant_final", record.OutputStatus);
        Assert.Equal("extension_runtime", record.InfrastructureWarningReason);
        Assert.Equal("/tmp/den-subagent-runs/run-complete", record.ArtifactDir);
        Assert.Equal("/tmp/den-subagent-runs/run-complete/stdout.jsonl", record.StdoutJsonlPath);
        Assert.Equal(started.Id, record.StartedStreamEntryId);
        Assert.Equal(completed.Id, record.LatestStreamEntryId);
        Assert.Equal(1, record.HeartbeatCount);
        Assert.Equal(1, record.AssistantOutputCount);
        Assert.Equal(5, record.EventCount);
        Assert.NotNull(record.LastHeartbeatAt);
        Assert.NotNull(record.LastAssistantOutputAt);

        // Re-projecting an existing stream row is idempotent: counts are rebuilt
        // from agent_stream_entries instead of incremented in-place.
        Assert.True(await _runs.UpsertFromStreamEntryAsync(completed));
        var idempotent = await _runs.GetAsync("run-complete", new SubagentRunListOptions());
        Assert.Equal(5, idempotent!.EventCount);
        Assert.Equal(1, idempotent.HeartbeatCount);
        Assert.Equal(completed.Id, idempotent.LatestStreamEntryId);
        Assert.NotEqual(processStarted.Id, idempotent.LatestStreamEntryId);
    }

    [Fact]
    public async Task AgentStreamOpsService_AppendsAuditEntryAndProjectsAgentRun()
    {
        var ops = new AgentStreamOpsService(_stream, NullLogger<AgentStreamOpsService>.Instance, _runs);

        var entry = await ops.AppendOpsAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "subagent_started",
            ProjectId = "proj",
            TaskId = _task.Id,
            Sender = "pi",
            SenderInstanceId = "pi-proj-coder-run-ops",
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Metadata = Metadata("""{"run_id":"run-ops","role":"coder","backend":"pi-cli"}""")
        });

        var record = await _runs.GetAsync("run-ops", new SubagentRunListOptions { ProjectId = "proj" });

        Assert.NotNull(record);
        Assert.Equal(entry.Id, record!.LatestStreamEntryId);
        Assert.Equal(entry.Id, record.StartedStreamEntryId);
        Assert.Equal("running", record.State);
        Assert.Equal("coder", record.Role);
    }

    [Fact]
    public async Task RebuildFromStreamAsync_BackfillsAndListFiltersDurableRecords()
    {
        await AppendAsync("subagent_started", "run-timeout", "planner", """{"run_id":"run-timeout","role":"planner","backend":"pi-cli"}""");
        await AppendAsync("subagent_timeout", "run-timeout", "planner", """{"run_id":"run-timeout","role":"planner","timeout_kind":"startup","exit_code":143,"output_status":"no_assistant_final","infrastructure_failure_reason":"timeout"}""");

        await AppendAsync("subagent_started", "run-abort", "reviewer", """{"run_id":"run-abort","role":"reviewer","backend":"pi-cli"}""");
        await AppendAsync("subagent_abort_requested", "run-abort", "reviewer", """{"run_id":"run-abort","role":"reviewer","action":"abort"}""");
        await AppendAsync("subagent_aborted", "run-abort", "reviewer", """{"run_id":"run-abort","role":"reviewer","aborted":true,"exit_code":143}""");

        var timeout = await _runs.RebuildFromStreamAsync("run-timeout");
        var abort = await _runs.RebuildFromStreamAsync("run-abort");

        Assert.Equal("timeout", timeout!.State);
        Assert.Equal("startup", timeout.TimeoutKind);
        Assert.Equal("timeout", timeout.InfrastructureFailureReason);
        Assert.Equal("aborted", abort!.State);
        Assert.Equal(3, abort.EventCount);

        var problemRuns = await _runs.ListAsync(new SubagentRunListOptions
        {
            ProjectId = "proj",
            State = "problem",
            Limit = 10
        });

        Assert.Equal(new[] { "run-abort", "run-timeout" }, problemRuns.Select(run => run.RunId).Order().ToArray());
        Assert.Empty(await _runs.ListAsync(new SubagentRunListOptions { ProjectId = "other", Limit = 10 }));
        Assert.Empty(await _runs.ListAsync(new SubagentRunListOptions { ProjectId = "proj", State = "complete", Limit = 10 }));
    }

    [Fact]
    public async Task RebuildFromStreamAsync_MaterializesRawWorkCountAndOperatorEvents()
    {
        // Start a coder run
        await AppendAsync("subagent_started", "run-mat", "coder", """{"run_id":"run-mat","role":"coder","backend":"pi-cli"}""");
        // Add raw work events
        for (var i = 0; i < 5; i++)
        {
            var workMeta = $"{{\"run_id\":\"run-mat\",\"event\":{{\"type\":\"subagent.work_message_end\",\"text_preview\":\"work {i}\"}}}}";
            await AppendAsync("subagent_work_message_end", "run-mat", "coder", workMeta);
        }
        // Complete the run
        await AppendAsync("subagent_completed", "run-mat", "coder", """{"run_id":"run-mat","role":"coder","exit_code":0}""");

        var record = await _runs.RebuildFromStreamAsync("run-mat");

        Assert.NotNull(record);
        Assert.Equal(7, record!.EventCount);         // started + 5 work + completed
        Assert.Equal(5, record.RawWorkEventCount);     // 5 raw work events

        // Operator events JSON should contain coder_started and coder_completed
        Assert.NotNull(record.OperatorEventsJson);
        var ops = JsonSerializer.Deserialize<List<SubagentRunOperatorEvent>>(record.OperatorEventsJson!);
        Assert.NotNull(ops);
        Assert.Equal(2, ops!.Count); // coder_started + coder_completed
        Assert.Equal("coder_started", ops[0].EventName);
        Assert.Equal("coder_completed", ops[1].EventName);
        Assert.Equal("agent_stream", ops[0].Source);
        Assert.Equal("summary", ops[0].Visibility);
    }

    [Fact]
    public async Task RebuildFromStreamAsync_MaterializesZeroOperatorEventsForUnknownRoles()
    {
        await AppendAsync("subagent_started", "run-norole", "planner", """{"run_id":"run-norole","role":"planner","backend":"pi-cli"}""");
        await AppendAsync("subagent_completed", "run-norole", "planner", """{"run_id":"run-norole","role":"planner","exit_code":0}""");

        var record = await _runs.RebuildFromStreamAsync("run-norole");

        Assert.NotNull(record);
        Assert.Equal(2, record!.EventCount);
        Assert.Equal(0, record.RawWorkEventCount);
        // Planner role has no operator event mapping, so JSON is null
        Assert.Null(record.OperatorEventsJson);
    }

    [Fact]
    public async Task RebuildFromStreamAsync_RecordsExplicitOperatorEventMetadata()
    {
        await AppendAsync("subagent_started", "run-explicit", "coder", """{"run_id":"run-explicit","role":"coder","backend":"pi-cli"}""");
        await AppendAsync("subagent_heartbeat", "run-explicit", "coder", """{"run_id":"run-explicit","operator_event":"coder_heartbeat","event_visibility":"debug"}""");
        await AppendAsync("subagent_completed", "run-explicit", "coder", """{"run_id":"run-explicit","role":"coder","exit_code":0}""");

        var record = await _runs.RebuildFromStreamAsync("run-explicit");

        Assert.NotNull(record);
        var ops = JsonSerializer.Deserialize<List<SubagentRunOperatorEvent>>(record!.OperatorEventsJson!);
        Assert.NotNull(ops);
        Assert.Equal(3, ops!.Count); // coder_started + explicit coder_heartbeat + coder_completed
        Assert.Equal("coder_heartbeat", ops[1].EventName);
        Assert.Equal("debug", ops[1].Visibility);
    }

    private async Task<AgentStreamEntry> AppendAndProjectAsync(string eventType, string metadataJson)
    {
        var entry = await AppendAsync(eventType, "run-complete", "coder", metadataJson);
        Assert.True(await _runs.UpsertFromStreamEntryAsync(entry));
        return entry;
    }

    private Task<AgentStreamEntry> AppendAsync(string eventType, string runId, string role, string metadataJson) =>
        _stream.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = eventType,
            ProjectId = "proj",
            TaskId = _task.Id,
            Sender = "pi",
            SenderInstanceId = $"pi-proj-{role}-{runId}",
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Body = $"{role} {eventType}",
            Metadata = Metadata(metadataJson)
        });

    private static JsonElement Metadata(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}
