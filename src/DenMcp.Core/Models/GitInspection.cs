namespace DenMcp.Core.Models;

public sealed class GitStatusResponse
{
    public required string ProjectId { get; set; }
    public string? WorkspaceId { get; set; }
    public int? TaskId { get; set; }
    public string? WorkspaceBranch { get; set; }
    public string? WorkspaceBaseBranch { get; set; }
    public string? WorkspaceBaseCommit { get; set; }
    public string? WorkspaceHeadCommit { get; set; }
    public required string RootPath { get; set; }
    public bool IsGitRepository { get; set; }
    public string? Branch { get; set; }
    public bool IsDetached { get; set; }
    public string? HeadSha { get; set; }
    public string? Upstream { get; set; }
    public int? Ahead { get; set; }
    public int? Behind { get; set; }
    public GitDirtyCounts DirtyCounts { get; set; } = new();
    public List<GitFileStatus> Files { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public bool Truncated { get; set; }
}

public sealed class GitFilesResponse
{
    public required string ProjectId { get; set; }
    public string? WorkspaceId { get; set; }
    public int? TaskId { get; set; }
    public string? WorkspaceBranch { get; set; }
    public string? WorkspaceBaseBranch { get; set; }
    public string? WorkspaceBaseCommit { get; set; }
    public string? WorkspaceHeadCommit { get; set; }
    public required string RootPath { get; set; }
    public string? BaseRef { get; set; }
    public string? HeadRef { get; set; }
    public List<GitFileStatus> Files { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public bool Truncated { get; set; }
}

public sealed class GitDiffResponse
{
    public required string ProjectId { get; set; }
    public string? WorkspaceId { get; set; }
    public int? TaskId { get; set; }
    public string? WorkspaceBranch { get; set; }
    public string? WorkspaceBaseBranch { get; set; }
    public string? WorkspaceBaseCommit { get; set; }
    public string? WorkspaceHeadCommit { get; set; }
    public required string RootPath { get; set; }
    public string? Path { get; set; }
    public string? BaseRef { get; set; }
    public string? HeadRef { get; set; }
    public int MaxBytes { get; set; }
    public bool Staged { get; set; }
    public required string Diff { get; set; }
    public bool Truncated { get; set; }
    public bool Binary { get; set; }
    public List<string> Warnings { get; set; } = [];
    public List<string> Errors { get; set; } = [];
}

public sealed class GitDirtyCounts
{
    public int Total { get; set; }
    public int Staged { get; set; }
    public int Unstaged { get; set; }
    public int Untracked { get; set; }
    public int Modified { get; set; }
    public int Added { get; set; }
    public int Deleted { get; set; }
    public int Renamed { get; set; }
}

public sealed class GitFileStatus
{
    public required string Path { get; set; }
    public string? OldPath { get; set; }
    public string? IndexStatus { get; set; }
    public string? WorktreeStatus { get; set; }
    public required string Category { get; set; }
    public bool IsUntracked { get; set; }
}
