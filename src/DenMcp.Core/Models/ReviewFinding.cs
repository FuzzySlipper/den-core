namespace DenMcp.Core.Models;

public sealed class ReviewFinding
{
    public int Id { get; set; }
    public required string FindingKey { get; set; }
    public int TaskId { get; set; }
    public int ReviewRoundId { get; set; }
    public int ReviewRoundNumber { get; set; }
    public int FindingNumber { get; set; }
    public required string CreatedBy { get; set; }
    public ReviewFindingCategory Category { get; set; }
    public required string Summary { get; set; }
    public string? Notes { get; set; }
    public List<string>? FileReferences { get; set; }
    public List<string>? TestCommands { get; set; }
    public ReviewFindingStatus Status { get; set; }
    public string? StatusUpdatedBy { get; set; }
    public string? StatusNotes { get; set; }
    public DateTime? StatusUpdatedAt { get; set; }
    public string? ResponseBy { get; set; }
    public string? ResponseNotes { get; set; }
    public DateTime? ResponseAt { get; set; }
    public int? FollowUpTaskId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class CreateReviewFindingInput
{
    public int ReviewRoundId { get; set; }
    public required string CreatedBy { get; set; }
    public ReviewFindingCategory Category { get; set; }
    public required string Summary { get; set; }
    public string? Notes { get; set; }
    public List<string>? FileReferences { get; set; }
    public List<string>? TestCommands { get; set; }
    public string? RunId { get; set; }
    public string? SubagentRole { get; set; }
}

public sealed class RespondToReviewFindingInput
{
    public required string RespondedBy { get; set; }
    public string? ResponseNotes { get; set; }
    public ReviewFindingStatus? Status { get; set; }
    public string? StatusNotes { get; set; }
    public int? FollowUpTaskId { get; set; }
    public string? RunId { get; set; }
    public string? SubagentRole { get; set; }
}

public sealed class UpdateReviewFindingStatusInput
{
    public ReviewFindingStatus Status { get; set; }
    public required string UpdatedBy { get; set; }
    public string? Notes { get; set; }
    public int? FollowUpTaskId { get; set; }
    public string? RunId { get; set; }
    public string? SubagentRole { get; set; }
}
