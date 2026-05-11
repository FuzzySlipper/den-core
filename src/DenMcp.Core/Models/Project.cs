namespace DenMcp.Core.Models;

public sealed class Project
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public string Kind { get; set; } = "project";
    public string Visibility { get; set; } = "normal";
    public string? Owner { get; set; }
    public string? RootPath { get; set; }
    public string? Description { get; set; }
    public string? SettingsJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class ProjectWithStats
{
    public required Project Project { get; set; }
    public required Dictionary<TaskStatus, int> TaskCountsByStatus { get; set; }
    public int UnreadMessageCount { get; set; }
}
