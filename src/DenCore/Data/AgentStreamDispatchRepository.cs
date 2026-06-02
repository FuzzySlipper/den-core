using DenCore.Models;
using DenCore.Services;

namespace DenCore.Data;

public sealed class AgentStreamDispatchRepository : IDispatchRepository
{
    private readonly IDispatchRepository _inner;
    private readonly IAgentStreamOpsService _ops;

    public AgentStreamDispatchRepository(IDispatchRepository inner, IAgentStreamOpsService ops)
    {
        _inner = inner;
        _ops = ops;
    }

    public async Task<(DispatchEntry Entry, bool Created)> CreateIfAbsentAsync(DispatchEntry entry)
    {
        var result = await _inner.CreateIfAbsentAsync(entry);
        if (result.Created)
            await _ops.RecordDispatchCreatedAsync(result.Entry);
        return result;
    }

    public Task<DispatchEntry?> GetByIdAsync(int id) => _inner.GetByIdAsync(id);

    public Task<List<DispatchEntry>> ListAsync(
        string? projectId = null,
        string? targetAgent = null,
        DispatchStatus[]? statuses = null) => _inner.ListAsync(projectId, targetAgent, statuses);

    public Task<DispatchEntry> ExpireAsync(int id) => _inner.ExpireAsync(id);

    public Task<int> ExpireOpenForTaskAsync(string projectId, int taskId, int? excludeId = null) =>
        _inner.ExpireOpenForTaskAsync(projectId, taskId, excludeId);

    public Task<int> ExpireSupersededForTaskTargetAsync(string projectId, int taskId, string targetAgent, int keepId) =>
        _inner.ExpireSupersededForTaskTargetAsync(projectId, taskId, targetAgent, keepId);

    public Task<int> ExpireStaleAsync(DateTime now) => _inner.ExpireStaleAsync(now);

    public Task<int> GetPendingCountAsync(string? projectId = null) => _inner.GetPendingCountAsync(projectId);
}
