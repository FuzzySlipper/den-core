namespace DenMcp.Core.Models;

public sealed class ReviewWorkflowSummary
{
    public ReviewRound? CurrentRound { get; set; }
    public ReviewVerdict? CurrentVerdict { get; set; }
    public int ReviewRoundCount { get; set; }
    public int UnresolvedFindingCount { get; set; }
    public int ResolvedFindingCount { get; set; }
    public int AddressedFindingCount { get; set; }
    public required List<ReviewTimelineEntry> Timeline { get; set; }
}

public sealed class ReviewTimelineEntry
{
    public int ReviewRoundId { get; set; }
    public int ReviewRoundNumber { get; set; }
    public required string Branch { get; set; }
    public required string RequestedBy { get; set; }
    public DateTime RequestedAt { get; set; }
    public string? HeadCommit { get; set; }
    public string? LastReviewedHeadCommit { get; set; }
    public int? CommitsSinceLastReview { get; set; }
    public ReviewVerdict? Verdict { get; set; }
    public string? VerdictBy { get; set; }
    public DateTime? VerdictAt { get; set; }
    public int TotalFindingCount { get; set; }
    public int OpenFindingCount { get; set; }
    public int AddressedFindingCount { get; set; }
    public int ClaimedFixedFindingCount { get; set; }
    public int ResolvedFindingCount { get; set; }
}

public enum ReviewPacketKind
{
    ReviewRequest,
    RereviewRequest,
    ReviewFindings
}

public sealed class ReviewPacket
{
    public ReviewPacketKind Kind { get; set; }
    public required string Title { get; set; }
    public required string Content { get; set; }
}

public sealed class ReviewPacketResult
{
    public ReviewRound? ReviewRound { get; set; }
    public required Message Message { get; set; }
    public required ReviewPacket Packet { get; set; }
    public required List<string> FindingsAddressed { get; set; }
    public required List<string> OpenFindings { get; set; }
    public required List<string> TestCommands { get; set; }
}

public sealed class RequestReviewInput
{
    public int TaskId { get; set; }
    public required string RequestedBy { get; set; }
    public required string Branch { get; set; }
    public required string BaseBranch { get; set; }
    public required string BaseCommit { get; set; }
    public required string HeadCommit { get; set; }
    public string? LastReviewedHeadCommit { get; set; }
    public int? CommitsSinceLastReview { get; set; }
    public List<string>? TestsRun { get; set; }
    public string? Notes { get; set; }
    public string? PreferredDiffBaseRef { get; set; }
    public string? PreferredDiffBaseCommit { get; set; }
    public string? PreferredDiffHeadRef { get; set; }
    public string? PreferredDiffHeadCommit { get; set; }
    public string? AlternateDiffBaseRef { get; set; }
    public string? AlternateDiffBaseCommit { get; set; }
    public string? AlternateDiffHeadRef { get; set; }
    public string? AlternateDiffHeadCommit { get; set; }
    public string? DeltaBaseCommit { get; set; }
    public int? InheritedCommitCount { get; set; }
    public int? TaskLocalCommitCount { get; set; }
    public int? ThreadId { get; set; }
    public string? RunId { get; set; }
}

public sealed class PostReviewFindingsInput
{
    public int TaskId { get; set; }
    public int ReviewRoundId { get; set; }
    public required string Sender { get; set; }
    public int? ThreadId { get; set; }
    public string? Notes { get; set; }
    public string? RunId { get; set; }
    public string? SubagentRole { get; set; }
}

public sealed class SetReviewVerdictInput
{
    public string? ProjectId { get; set; }
    public int? TaskId { get; set; }
    public int ReviewRoundId { get; set; }
    public ReviewVerdict Verdict { get; set; }
    public required string DecidedBy { get; set; }
    public string? Notes { get; set; }
    public string? RunId { get; set; }
    public string? SubagentRole { get; set; }
}

public sealed class ReviewVerdictResult
{
    public required ReviewRound ReviewRound { get; set; }
    public Message? HandoffMessage { get; set; }
    public required List<DispatchEntry> CompletedDispatches { get; set; }
}

public static class ReviewWorkflowSummaryBuilder
{
    public static ReviewWorkflowSummary Build(
        IReadOnlyList<ReviewRound> reviewRounds,
        IReadOnlyList<ReviewFinding> openFindings,
        IReadOnlyList<ReviewFinding> resolvedFindings)
    {
        var allFindings = openFindings.Concat(resolvedFindings).ToList();
        var findingsByRound = allFindings
            .GroupBy(finding => finding.ReviewRoundId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var timeline = reviewRounds
            .OrderByDescending(round => round.RoundNumber)
            .Select(round =>
            {
                var roundFindings = findingsByRound.GetValueOrDefault(round.Id, []);
                return new ReviewTimelineEntry
                {
                    ReviewRoundId = round.Id,
                    ReviewRoundNumber = round.RoundNumber,
                    Branch = round.Branch,
                    RequestedBy = round.RequestedBy,
                    RequestedAt = round.RequestedAt,
                    HeadCommit = round.HeadCommit,
                    LastReviewedHeadCommit = round.LastReviewedHeadCommit,
                    CommitsSinceLastReview = round.CommitsSinceLastReview,
                    Verdict = round.Verdict,
                    VerdictBy = round.VerdictBy,
                    VerdictAt = round.VerdictAt,
                    TotalFindingCount = roundFindings.Count,
                    OpenFindingCount = roundFindings.Count(finding => !finding.Status.IsResolved()),
                    AddressedFindingCount = roundFindings.Count(finding =>
                        finding.ResponseAt is not null || finding.Status != ReviewFindingStatus.Open),
                    ClaimedFixedFindingCount = roundFindings.Count(finding =>
                        finding.Status == ReviewFindingStatus.ClaimedFixed),
                    ResolvedFindingCount = roundFindings.Count(finding => finding.Status.IsResolved())
                };
            })
            .ToList();

        var currentRound = reviewRounds
            .OrderByDescending(round => round.RoundNumber)
            .FirstOrDefault();

        return new ReviewWorkflowSummary
        {
            CurrentRound = currentRound,
            CurrentVerdict = currentRound?.Verdict,
            ReviewRoundCount = reviewRounds.Count,
            UnresolvedFindingCount = openFindings.Count,
            ResolvedFindingCount = resolvedFindings.Count,
            AddressedFindingCount = allFindings.Count(finding =>
                finding.ResponseAt is not null || finding.Status != ReviewFindingStatus.Open),
            Timeline = timeline
        };
    }
}
