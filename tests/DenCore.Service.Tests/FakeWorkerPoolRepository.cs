using DenCore.Data;
using DenCore.Models;

namespace DenCore.Service.Tests;

internal sealed class FakeWorkerPoolRepository : IWorkerPoolRepository
{
    private readonly Dictionary<string, WorkerPoolMember> _members = new(StringComparer.Ordinal);
    private readonly Dictionary<int, WorkerAssignment> _assignmentsById = [];
    private readonly Dictionary<string, WorkerAssignment> _assignmentsByRunId = new(StringComparer.Ordinal);
    private int _nextAssignmentId = 1;

    public int LeaseCalls { get; private set; }
    public int UpsertMemberCalls { get; private set; }
    public int ReleaseCalls { get; private set; }
    public IReadOnlyList<WorkerAssignment> Assignments => _assignmentsById.Values.ToList();

    public FakeWorkerPoolRepository AddAssignment(WorkerAssignment assignment)
    {
        if (assignment.Id == 0)
            assignment.Id = _nextAssignmentId++;
        else
            _nextAssignmentId = Math.Max(_nextAssignmentId, assignment.Id + 1);
        assignment.CreatedAt = assignment.CreatedAt == default ? DateTime.UtcNow : assignment.CreatedAt;
        assignment.UpdatedAt = assignment.UpdatedAt == default ? assignment.CreatedAt : assignment.UpdatedAt;
        _assignmentsById[assignment.Id] = assignment;
        _assignmentsByRunId[assignment.RunId] = assignment;
        return this;
    }

    public Task<WorkerPoolMember> UpsertMemberAsync(WorkerPoolMember member)
    {
        UpsertMemberCalls++;
        member.PoolMemberId ??= member.WorkerIdentity;
        member.CreatedAt = member.CreatedAt == default ? DateTime.UtcNow : member.CreatedAt;
        member.UpdatedAt = DateTime.UtcNow;
        _members[member.WorkerIdentity] = member;
        return Task.FromResult(member);
    }

    public Task<WorkerPoolMember?> GetMemberAsync(string workerIdentity)
        => Task.FromResult(_members.GetValueOrDefault(workerIdentity));

    public Task<List<WorkerPoolMember>> ListMembersAsync(WorkerPoolMemberListOptions options)
        => Task.FromResult(_members.Values
            .Where(member => string.IsNullOrWhiteSpace(options.Status) || member.Status == options.Status)
            .Where(member => string.IsNullOrWhiteSpace(options.WorkerIdentity) || member.WorkerIdentity == options.WorkerIdentity)
            .Where(member => string.IsNullOrWhiteSpace(options.ProfileIdentity) || member.ProfileIdentity == options.ProfileIdentity)
            .Where(member => string.IsNullOrWhiteSpace(options.WorkerRole) || member.WorkerRole == options.WorkerRole)
            .Take(Math.Clamp(options.Limit, 1, 200))
            .ToList());

    public Task<int> SetMemberStatusAsync(string workerIdentity, string status, string? metadata = null)
    {
        if (!_members.TryGetValue(workerIdentity, out var member))
            return Task.FromResult(0);
        member.Status = status;
        member.Metadata = metadata ?? member.Metadata;
        member.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult(1);
    }

    public Task<WorkerAssignment?> LeaseAvailableWorkerAsync(LeaseWorkerInput input)
    {
        LeaseCalls++;
        var workerIdentity = input.PreferredWorkerIdentity ?? _members.Values.FirstOrDefault(m => m.Status == WorkerPoolStates.MemberAvailable)?.WorkerIdentity;
        if (workerIdentity is null || !_members.TryGetValue(workerIdentity, out var member) || member.Status != WorkerPoolStates.MemberAvailable)
            return Task.FromResult<WorkerAssignment?>(null);

        member.Status = WorkerPoolStates.MemberBusy;
        var now = DateTime.UtcNow;
        var assignment = new WorkerAssignment
        {
            Id = _nextAssignmentId++,
            WorkerIdentity = workerIdentity,
            PoolMemberId = member.PoolMemberId ?? member.WorkerIdentity,
            WorkerRole = member.WorkerRole,
            AgentInstanceId = member.AgentInstanceId,
            ChannelId = member.ChannelId,
            RunId = input.RunId,
            LeaseId = $"{workerIdentity}:{input.RunId}",
            ProfileIdentity = member.ProfileIdentity,
            ProjectId = input.ProjectId,
            TaskId = input.TaskId,
            Role = input.Role,
            AssignedBy = input.AssignedBy,
            State = WorkerPoolStates.Ack,
            AcquiredAt = now.ToString("o"),
            CreatedAt = now,
            UpdatedAt = now,
        };
        _assignmentsById[assignment.Id] = assignment;
        _assignmentsByRunId[assignment.RunId] = assignment;
        return Task.FromResult<WorkerAssignment?>(assignment);
    }

    public Task<WorkerAssignment?> GetAssignmentAsync(int assignmentId)
        => Task.FromResult(_assignmentsById.GetValueOrDefault(assignmentId));

    public Task<WorkerAssignment?> GetAssignmentByRunIdAsync(string runId)
        => Task.FromResult(_assignmentsByRunId.GetValueOrDefault(runId));

    public Task<List<WorkerAssignment>> ListAssignmentsAsync(WorkerAssignmentListOptions options)
        => Task.FromResult(_assignmentsById.Values
            .Where(assignment => string.IsNullOrWhiteSpace(options.ProjectId) || assignment.ProjectId == options.ProjectId)
            .Where(assignment => options.TaskId is null || assignment.TaskId == options.TaskId)
            .Where(assignment => string.IsNullOrWhiteSpace(options.WorkerIdentity) || assignment.WorkerIdentity == options.WorkerIdentity)
            .Where(assignment => string.IsNullOrWhiteSpace(options.State) || assignment.State == options.State)
            .Where(assignment => string.IsNullOrWhiteSpace(options.Role) || assignment.Role == options.Role)
            .Take(Math.Clamp(options.Limit, 1, 200))
            .ToList());

    public Task<WorkerAssignment?> TransitionAssignmentStateAsync(int assignmentId, string newState, string? metadata = null)
    {
        if (!_assignmentsById.TryGetValue(assignmentId, out var assignment))
            return Task.FromResult<WorkerAssignment?>(null);
        assignment.State = newState;
        assignment.UpdatedAt = DateTime.UtcNow;
        if (WorkerPoolStates.IsTerminal(newState) && _members.TryGetValue(assignment.WorkerIdentity, out var member))
            member.Status = WorkerPoolStates.MemberAvailable;
        return Task.FromResult<WorkerAssignment?>(assignment);
    }

    public Task<WorkerCheckpoint> AppendCheckpointAsync(int assignmentId, string runId, string checkpointType, string payload)
        => throw new NotSupportedException();

    public Task<List<WorkerCheckpoint>> ListCheckpointsAsync(WorkerCheckpointListOptions options)
        => Task.FromResult(new List<WorkerCheckpoint>());

    public Task<CheckpointResponse> AppendCheckpointResponseAsync(int checkpointId, int? assignmentId, string runId, string responseType, string payload)
        => throw new NotSupportedException();

    public Task<List<CheckpointResponse>> ListResponsesAsync(int checkpointId)
        => Task.FromResult(new List<CheckpointResponse>());

    public Task<List<CheckpointResponse>> ListResponsesByRunIdAsync(string runId, int limit = 50)
        => Task.FromResult(new List<CheckpointResponse>());

    public Task<WorkerAssignment?> RecordCleanupEvidenceAsync(int assignmentId, string evidenceJson)
    {
        if (!_assignmentsById.TryGetValue(assignmentId, out var assignment))
            return Task.FromResult<WorkerAssignment?>(null);
        assignment.CleanupEvidence = evidenceJson;
        assignment.CleanupRecordedAt = DateTime.UtcNow.ToString("o");
        return Task.FromResult<WorkerAssignment?>(assignment);
    }

    public Task<WorkerAssignment?> ReleaseAssignmentAsync(int assignmentId)
    {
        ReleaseCalls++;
        if (!_assignmentsById.TryGetValue(assignmentId, out var assignment))
            return Task.FromResult<WorkerAssignment?>(null);
        if (string.IsNullOrWhiteSpace(assignment.CleanupEvidence))
            return Task.FromResult<WorkerAssignment?>(null);
        assignment.ReleasedAt = DateTime.UtcNow.ToString("o");
        assignment.UpdatedAt = DateTime.UtcNow;
        if (_members.TryGetValue(assignment.WorkerIdentity, out var member))
            member.Status = WorkerPoolStates.MemberAvailable;
        return Task.FromResult<WorkerAssignment?>(assignment);
    }

    public Task<bool> QuarantineWorkerAsync(string workerIdentity, string quarantinedBy, string? reason = null)
        => Task.FromResult(false);

    public Task<WorkerPoolSummary> GetSummaryAsync()
        => Task.FromResult(new WorkerPoolSummary
        {
            TotalMembers = _members.Count,
            AvailableMembers = _members.Values.Count(m => m.Status == WorkerPoolStates.MemberAvailable),
            BusyMembers = _members.Values.Count(m => m.Status == WorkerPoolStates.MemberBusy),
            ActiveAssignments = _assignmentsById.Values.Count(a => !WorkerPoolStates.IsTerminal(a.State)),
            CompletedAssignments = _assignmentsById.Values.Count(a => a.State == WorkerPoolStates.Completed),
            FailedAssignments = _assignmentsById.Values.Count(a => a.State == WorkerPoolStates.Failed),
            ExpiredAssignments = _assignmentsById.Values.Count(a => a.State == WorkerPoolStates.Expired),
        });

    public Task<LeaseWorkerResult> LeaseWorkerWithDiagnosticsAsync(LeaseWorkerInput input)
        => throw new NotSupportedException();

    public Task<List<WorkerNoCapacityRequest>> ListNoCapacityRequestsAsync(NoCapacityRequestListOptions options)
        => Task.FromResult(new List<WorkerNoCapacityRequest>());

    public Task<WorkerNoCapacityRequest?> GetNoCapacityRequestAsync(int id)
        => Task.FromResult<WorkerNoCapacityRequest?>(null);

    public Task<WorkerPoolLane> UpsertLaneAsync(WorkerPoolLane lane) => throw new NotSupportedException();
    public Task<WorkerPoolLane?> GetLaneAsync(string profileIdentity, string workerRole) => Task.FromResult<WorkerPoolLane?>(null);
    public Task<List<WorkerPoolLane>> ListLanesAsync(string? profileIdentity = null, string? status = null, int limit = 50) => Task.FromResult(new List<WorkerPoolLane>());
    public Task<int> SetLaneStatusAsync(string profileIdentity, string workerRole, string status) => Task.FromResult(0);
    public Task<ProfileCapacitySummary> GetProfileCapacitySummaryAsync(string profileIdentity) => throw new NotSupportedException();
    public Task<int> ReleaseStaleLeasesAsync() => Task.FromResult(0);
    public Task<StaleWorkerSweepResult> SweepStaleWorkersAsync(StaleSweepOptions options) =>
        Task.FromResult(new StaleWorkerSweepResult { SweptAt = DateTime.UtcNow.ToString("o") });
    public Task<OrchestratorLease> CreateOrchestratorLeaseAsync(CreateOrchestratorLeaseInput input) => throw new NotSupportedException();
    public Task<OrchestratorLease?> GetOrchestratorLeaseAsync(int id) => Task.FromResult<OrchestratorLease?>(null);
    public Task<OrchestratorLease?> GetOrchestratorLeaseByLeaseIdAsync(string leaseId) => Task.FromResult<OrchestratorLease?>(null);
    public Task<List<OrchestratorLease>> ListOrchestratorLeasesAsync(OrchestratorLeaseListOptions options) => Task.FromResult(new List<OrchestratorLease>());
    public Task<OrchestratorLease?> TransitionOrchestratorLeaseAsync(TransitionOrchestratorLeaseInput input) => Task.FromResult<OrchestratorLease?>(null);
    public Task<OrchestratorLease?> RecordOrchestratorLeaseCleanupAsync(int leaseId, string evidenceJson) => Task.FromResult<OrchestratorLease?>(null);
    public Task<int> ReconcileStaleOrchestratorLeasesAsync() => Task.FromResult(0);
    public Task<List<PoolResidencyProjection>> GetPoolResidencyProjectionAsync(string projectId) => Task.FromResult(new List<PoolResidencyProjection>());
}
