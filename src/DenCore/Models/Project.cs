namespace DenCore.Models;

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

/// <summary>
/// Request body for updating an existing project/space. Only the fields
/// that are explicitly set (non-null, or non-empty for name) will be
/// applied; all other fields are left unchanged.
/// </summary>
public sealed class ProjectUpdateRequest
{
    /// <summary>New display name for the project (optional).</summary>
    public string? Name { get; set; }

    /// <summary>New absolute root path on disk (optional).</summary>
    public string? RootPath { get; set; }

    /// <summary>Updated description (optional).</summary>
    public string? Description { get; set; }

    /// <summary>Updated owner (optional).</summary>
    public string? Owner { get; set; }

    /// <summary>Updated settings JSON (optional).</summary>
    public string? SettingsJson { get; set; }
}
