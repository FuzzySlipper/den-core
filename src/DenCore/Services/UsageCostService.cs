using System.Text.Json;
using DenCore.Data;
using DenCore.Models;

namespace DenCore.Services;

/// <summary>
/// Service interface for recording worker model usage events and
/// querying aggregated usage/cost reports.
/// </summary>
public interface IUsageCostService
{
    /// <summary>Record a single usage event with automatic pricing resolution.</summary>
    Task<ModelUsageEvent> RecordUsageAsync(UsageEventIngestInput input);

    /// <summary>Batch-record usage events.</summary>
    Task<List<ModelUsageEvent>> RecordUsageBatchAsync(List<UsageEventIngestInput> inputs);

    /// <summary>Run an aggregated usage/cost report.</summary>
    Task<UsageCostReport> GetReportAsync(UsageCostQueryOptions options);

    /// <summary>Ensure a default pricing snapshot exists. Idempotent.</summary>
    Task<PricingSnapshot> EnsureDefaultPricingAsync();

    /// <summary>Get the latest pricing snapshot, or null if none exists.</summary>
    Task<PricingSnapshot?> GetLatestPricingAsync();
}

public sealed class UsageCostService : IUsageCostService
{
    private readonly IUsageCostRepository _repo;

    public UsageCostService(IUsageCostRepository repo)
    {
        _repo = repo;
    }

    public async Task<ModelUsageEvent> RecordUsageAsync(UsageEventIngestInput input)
    {
        var snapshot = await _repo.GetLatestPricingSnapshotAsync();

        var e = new ModelUsageEvent
        {
            OccurredAt = input.OccurredAt ?? DateTime.UtcNow.ToString("o"),
            ProjectId = input.ProjectId,
            TaskId = input.TaskId,
            AssignmentId = input.AssignmentId,
            RunId = input.RunId,
            SessionId = input.SessionId,
            AgentIdentity = input.AgentIdentity,
            ProfileIdentity = input.ProfileIdentity,
            WorkerRole = input.WorkerRole,
            WorkerIdentity = input.WorkerIdentity,
            OperationKind = input.OperationKind,
            Provider = input.Provider,
            Model = input.Model,
            ModelAlias = input.ModelAlias,
            ResolvedModel = input.ResolvedModel ?? input.Model,
            EndpointKind = input.EndpointKind,
            InputTokens = input.InputTokens,
            OutputTokens = input.OutputTokens,
            CacheReadTokens = input.CacheReadTokens,
            CacheWriteTokens = input.CacheWriteTokens,
            ReasoningTokens = input.ReasoningTokens,
            ToolResultTokens = input.ToolResultTokens,
            RequestCount = input.RequestCount,
            RetryCount = input.RetryCount,
            Streaming = input.Streaming,
            ErrorKind = input.ErrorKind,
            PricingSnapshotId = snapshot?.Id,
            Provenance = input.Provenance,
            AdapterVersion = input.AdapterVersion,
            RawUsageSource = input.RawUsageSource,
            RequestIdHint = input.RequestIdHint,
        };

        return await _repo.RecordUsageEventAsync(e);
    }

    public async Task<List<ModelUsageEvent>> RecordUsageBatchAsync(List<UsageEventIngestInput> inputs)
    {
        var snapshot = await _repo.GetLatestPricingSnapshotAsync();

        var events = inputs.Select(input => new ModelUsageEvent
        {
            OccurredAt = input.OccurredAt ?? DateTime.UtcNow.ToString("o"),
            ProjectId = input.ProjectId,
            TaskId = input.TaskId,
            AssignmentId = input.AssignmentId,
            RunId = input.RunId,
            SessionId = input.SessionId,
            AgentIdentity = input.AgentIdentity,
            ProfileIdentity = input.ProfileIdentity,
            WorkerRole = input.WorkerRole,
            WorkerIdentity = input.WorkerIdentity,
            OperationKind = input.OperationKind,
            Provider = input.Provider,
            Model = input.Model,
            ModelAlias = input.ModelAlias,
            ResolvedModel = input.ResolvedModel ?? input.Model,
            EndpointKind = input.EndpointKind,
            InputTokens = input.InputTokens,
            OutputTokens = input.OutputTokens,
            CacheReadTokens = input.CacheReadTokens,
            CacheWriteTokens = input.CacheWriteTokens,
            ReasoningTokens = input.ReasoningTokens,
            ToolResultTokens = input.ToolResultTokens,
            RequestCount = input.RequestCount,
            RetryCount = input.RetryCount,
            Streaming = input.Streaming,
            ErrorKind = input.ErrorKind,
            PricingSnapshotId = snapshot?.Id,
            Provenance = input.Provenance,
            AdapterVersion = input.AdapterVersion,
            RawUsageSource = input.RawUsageSource,
            RequestIdHint = input.RequestIdHint,
        }).ToList();

        return await _repo.RecordUsageEventsAsync(events);
    }

    public Task<UsageCostReport> GetReportAsync(UsageCostQueryOptions options)
        => _repo.RunReportAsync(options);

    public Task<PricingSnapshot> EnsureDefaultPricingAsync()
        => _repo.EnsureDefaultPricingSnapshotAsync();

    public Task<PricingSnapshot?> GetLatestPricingAsync()
        => _repo.GetLatestPricingSnapshotAsync();
}
