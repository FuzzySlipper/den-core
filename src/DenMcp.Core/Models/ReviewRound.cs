namespace DenMcp.Core.Models;

public sealed class ReviewRound
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public int RoundNumber { get; set; }
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
    public ReviewVerdict? Verdict { get; set; }
    public string? VerdictBy { get; set; }
    public string? VerdictNotes { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? VerdictAt { get; set; }

    public ReviewDiffRange PreferredDiff => new()
    {
        BaseRef = PreferredDiffBaseRef ?? BaseBranch,
        BaseCommit = PreferredDiffBaseCommit ?? BaseCommit,
        HeadRef = PreferredDiffHeadRef ?? Branch,
        HeadCommit = PreferredDiffHeadCommit ?? HeadCommit
    };

    public ReviewDiffRange? AlternateDiff => AlternateDiffBaseRef is null
        ? null
        : new ReviewDiffRange
        {
            BaseRef = AlternateDiffBaseRef,
            BaseCommit = AlternateDiffBaseCommit,
            HeadRef = AlternateDiffHeadRef ?? Branch,
            HeadCommit = AlternateDiffHeadCommit ?? HeadCommit
        };

    public ReviewDiffRange? DeltaDiff
    {
        get
        {
            var deltaBase = DeltaBaseCommit ?? LastReviewedHeadCommit;
            return deltaBase is null
                ? null
                : new ReviewDiffRange
                {
                    BaseRef = PreferredDiff.HeadRef,
                    BaseCommit = deltaBase,
                    HeadRef = PreferredDiff.HeadRef,
                    HeadCommit = PreferredDiff.HeadCommit
                };
        }
    }

    public ReviewBranchComposition BranchComposition => new()
    {
        InheritedCommitCount = InheritedCommitCount,
        TaskLocalCommitCount = TaskLocalCommitCount
    };

    public bool IsStackedBranchReview =>
        AlternateDiffBaseRef is not null &&
        !string.Equals(PreferredDiff.BaseRef, AlternateDiffBaseRef, StringComparison.Ordinal);
}

public sealed class CreateReviewRoundInput
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
}

public sealed class ReviewDiffRange
{
    public required string BaseRef { get; set; }
    public string? BaseCommit { get; set; }
    public required string HeadRef { get; set; }
    public required string HeadCommit { get; set; }
}

public sealed class ReviewBranchComposition
{
    public int? InheritedCommitCount { get; set; }
    public int? TaskLocalCommitCount { get; set; }
    public bool? HasInheritedChanges => InheritedCommitCount is null ? null : InheritedCommitCount > 0;
    public bool? HasTaskLocalChanges => TaskLocalCommitCount is null ? null : TaskLocalCommitCount > 0;
}
