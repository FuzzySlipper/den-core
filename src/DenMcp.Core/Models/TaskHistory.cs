namespace DenMcp.Core.Models;

public sealed class TaskHistoryEntry
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public required string Field { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? ChangedBy { get; set; }
    public DateTime ChangedAt { get; set; }
}
