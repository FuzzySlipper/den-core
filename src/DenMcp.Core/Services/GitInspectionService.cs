using System.Diagnostics;
using System.Text;
using DenMcp.Core.Models;

namespace DenMcp.Core.Services;

public interface IGitInspectionService
{
    Task<GitStatusResponse> GetStatusAsync(string projectId, string? rootPath, CancellationToken cancellationToken = default);
    Task<GitFilesResponse> GetFilesAsync(string projectId, string? rootPath, string? baseRef = null, string? headRef = null, bool includeUntracked = true, CancellationToken cancellationToken = default);
    Task<GitDiffResponse> GetDiffAsync(string projectId, string? rootPath, string? path = null, string? baseRef = null, string? headRef = null, int? maxBytes = null, bool staged = false, CancellationToken cancellationToken = default);
}

public sealed class GitInspectionService : IGitInspectionService
{
    private const int DefaultMaxStatusBytes = 1024 * 1024;
    private const int DefaultMaxDiffBytes = 128 * 1024;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public async Task<GitStatusResponse> GetStatusAsync(string projectId, string? rootPath, CancellationToken cancellationToken = default)
    {
        var resolved = ResolveRoot(projectId, rootPath);
        var response = new GitStatusResponse
        {
            ProjectId = projectId,
            RootPath = resolved.RootPath ?? string.Empty
        };
        response.Errors.AddRange(resolved.Errors);
        if (response.Errors.Count > 0) return response;

        var result = await RunGitAsync(resolved.RootPath!, ["status", "--porcelain=v2", "--branch", "--untracked-files=all"], DefaultMaxStatusBytes, DefaultTimeout, cancellationToken);
        response.Truncated = result.Truncated;
        if (result.ExitCode != 0)
        {
            response.Errors.Add(GitError("git status", result));
            response.Warnings.AddRange(result.Warnings);
            return response;
        }

        var parsed = GitStatusParser.ParsePorcelainV2(projectId, resolved.RootPath!, result.Stdout);
        parsed.Truncated = result.Truncated;
        parsed.Warnings.AddRange(result.Warnings);
        return parsed;
    }

    public async Task<GitFilesResponse> GetFilesAsync(string projectId, string? rootPath, string? baseRef = null, string? headRef = null, bool includeUntracked = true, CancellationToken cancellationToken = default)
    {
        var resolved = ResolveRoot(projectId, rootPath);
        var response = new GitFilesResponse
        {
            ProjectId = projectId,
            RootPath = resolved.RootPath ?? string.Empty,
            BaseRef = baseRef,
            HeadRef = headRef
        };
        response.Errors.AddRange(resolved.Errors);
        if (response.Errors.Count > 0) return response;

        if (!string.IsNullOrWhiteSpace(baseRef) || !string.IsNullOrWhiteSpace(headRef))
        {
            if (string.IsNullOrWhiteSpace(baseRef) || string.IsNullOrWhiteSpace(headRef))
            {
                response.Errors.Add("Both baseRef and headRef are required when requesting a range.");
                return response;
            }

            var refError = ValidateRef(baseRef) ?? ValidateRef(headRef);
            if (refError is not null)
            {
                response.Errors.Add(refError);
                return response;
            }

            var result = await RunGitAsync(resolved.RootPath!, ["diff", "--name-status", "--find-renames", $"{baseRef}...{headRef}"], DefaultMaxStatusBytes, DefaultTimeout, cancellationToken);
            response.Truncated = result.Truncated;
            response.Warnings.AddRange(result.Warnings);
            if (result.ExitCode != 0)
            {
                response.Errors.Add(GitError("git diff --name-status", result));
                return response;
            }

            response.Files = GitStatusParser.ParseNameStatus(result.Stdout);
            if (includeUntracked)
            {
                var status = await GetStatusAsync(projectId, resolved.RootPath, cancellationToken);
                response.Warnings.AddRange(status.Warnings);
                response.Errors.AddRange(status.Errors);
                response.Files.AddRange(status.Files.Where(file => file.IsUntracked));
            }
            return response;
        }

        var snapshot = await GetStatusAsync(projectId, resolved.RootPath, cancellationToken);
        return new GitFilesResponse
        {
            ProjectId = projectId,
            RootPath = resolved.RootPath!,
            Files = includeUntracked ? snapshot.Files : snapshot.Files.Where(file => !file.IsUntracked).ToList(),
            Warnings = snapshot.Warnings,
            Errors = snapshot.Errors,
            Truncated = snapshot.Truncated
        };
    }

    public async Task<GitDiffResponse> GetDiffAsync(string projectId, string? rootPath, string? path = null, string? baseRef = null, string? headRef = null, int? maxBytes = null, bool staged = false, CancellationToken cancellationToken = default)
    {
        var resolved = ResolveRoot(projectId, rootPath);
        var max = Math.Clamp(maxBytes ?? DefaultMaxDiffBytes, 1024, 1024 * 1024);
        var response = new GitDiffResponse
        {
            ProjectId = projectId,
            RootPath = resolved.RootPath ?? string.Empty,
            Path = path,
            BaseRef = baseRef,
            HeadRef = headRef,
            MaxBytes = max,
            Staged = staged,
            Diff = string.Empty
        };
        response.Errors.AddRange(resolved.Errors);
        if (response.Errors.Count > 0) return response;

        var relativePath = path;
        if (!string.IsNullOrWhiteSpace(path))
        {
            var pathResult = ValidateRelativePath(resolved.RootPath!, path);
            if (pathResult.Error is not null)
            {
                response.Errors.Add(pathResult.Error);
                return response;
            }
            relativePath = pathResult.RelativePath;
        }

        var args = new List<string> { "diff" };
        if (staged && string.IsNullOrWhiteSpace(baseRef) && string.IsNullOrWhiteSpace(headRef))
            args.Add("--cached");
        else if (staged)
            response.Warnings.Add("staged=true is ignored for explicit base/head range diffs.");

        if (!string.IsNullOrWhiteSpace(baseRef) || !string.IsNullOrWhiteSpace(headRef))
        {
            if (string.IsNullOrWhiteSpace(baseRef) || string.IsNullOrWhiteSpace(headRef))
            {
                response.Errors.Add("Both baseRef and headRef are required when requesting a range diff.");
                return response;
            }
            var refError = ValidateRef(baseRef) ?? ValidateRef(headRef);
            if (refError is not null)
            {
                response.Errors.Add(refError);
                return response;
            }
            args.Add($"{baseRef}...{headRef}");
        }
        args.Add("--");
        if (!string.IsNullOrWhiteSpace(relativePath))
            args.Add(relativePath!);

        var result = await RunGitAsync(resolved.RootPath!, args, max, DefaultTimeout, cancellationToken);
        response.Truncated = result.Truncated;
        response.Warnings.AddRange(result.Warnings);
        response.Diff = result.Stdout;
        response.Binary = result.Stdout.Contains("Binary files ", StringComparison.Ordinal) || result.Stdout.Contains("GIT binary patch", StringComparison.Ordinal);
        if (result.ExitCode != 0)
            response.Errors.Add(GitError("git diff", result));

        return response;
    }

    private static RootResolution ResolveRoot(string projectId, string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            return new RootResolution(null, [$"Project '{projectId}' does not have a registered root_path."]);

        try
        {
            var full = Path.GetFullPath(rootPath);
            if (!Directory.Exists(full))
                return new RootResolution(full, [$"Registered root path does not exist: {full}"]);
            return new RootResolution(full, []);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new RootResolution(null, [$"Invalid registered root path: {ex.Message}"]);
        }
    }

    internal static PathValidationResult ValidateRelativePath(string rootPath, string path)
    {
        if (Path.IsPathRooted(path))
            return new PathValidationResult(null, "Path must be relative to the repository root.");

        try
        {
            var fullRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(Path.Combine(fullRoot, path));
            if (!fullPath.StartsWith(fullRoot, StringComparison.Ordinal))
                return new PathValidationResult(null, "Path escapes the repository root.");

            var relative = Path.GetRelativePath(fullRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
            return new PathValidationResult(relative, null);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new PathValidationResult(null, $"Invalid path: {ex.Message}");
        }
    }

    private static string? ValidateRef(string value)
    {
        if (value.Length > 200)
            return "Git ref is too long.";
        if (value.StartsWith('-') || value.Contains('\0') || value.Contains("..", StringComparison.Ordinal) || value.Contains(' ') || value.Contains('~') || value.Contains('^') || value.Contains(':'))
            return $"Unsupported git ref syntax: {value}";
        return null;
    }

    private static async Task<GitCommandResult> RunGitAsync(string rootPath, IReadOnlyList<string> args, int maxBytes, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = rootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(rootPath);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git process.");
            var killedForOutputCap = false;
            var stdoutTask = ReadCappedAsync(process.StandardOutput.BaseStream, maxBytes, timeoutCts.Token, () =>
            {
                killedForOutputCap = true;
                TryKill(process);
            });
            var stderrTask = ReadCappedAsync(process.StandardError.BaseStream, 8192, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                var warnings = new List<string>();
                if (stdout.Truncated)
                    warnings.Add($"Git output truncated to {maxBytes} bytes.");
                if (stderr.Truncated)
                    warnings.Add("Git stderr truncated to 8192 bytes.");

                return new GitCommandResult(killedForOutputCap ? 0 : process.ExitCode, stdout.Text, stderr.Text, stdout.Truncated, warnings);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return new GitCommandResult(-1, string.Empty, "git command timed out", false, [$"Git command timed out after {timeout.TotalSeconds:0.#} seconds."]);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new GitCommandResult(-1, string.Empty, ex.Message, false, [$"Failed to start git: {ex.Message}"]);
        }
    }

    private static async Task<CappedText> ReadCappedAsync(Stream stream, int maxBytes, CancellationToken cancellationToken, Action? onCapReached = null)
    {
        var buffer = new byte[8192];
        await using var output = new MemoryStream(capacity: Math.Min(maxBytes, buffer.Length));
        var truncated = false;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, Math.Max(1, maxBytes - (int)output.Length))), cancellationToken);
            if (read == 0) break;

            var remaining = maxBytes - (int)output.Length;
            if (read <= remaining)
            {
                output.Write(buffer, 0, read);
            }
            else
            {
                output.Write(buffer, 0, remaining);
                truncated = true;
                onCapReached?.Invoke();
                break;
            }

            if (output.Length >= maxBytes)
            {
                truncated = true;
                onCapReached?.Invoke();
                break;
            }
        }

        return new CappedText(Encoding.UTF8.GetString(output.ToArray()).TrimEnd('\uFFFD'), truncated);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static string GitError(string command, GitCommandResult result)
    {
        var stderr = string.IsNullOrWhiteSpace(result.Stderr) ? "no stderr" : result.Stderr.Trim();
        return $"{command} failed with exit code {result.ExitCode}: {stderr}";
    }

    private sealed record RootResolution(string? RootPath, List<string> Errors);
    internal sealed record PathValidationResult(string? RelativePath, string? Error);
    private sealed record CappedText(string Text, bool Truncated);
    private sealed record GitCommandResult(int ExitCode, string Stdout, string Stderr, bool Truncated, List<string> Warnings);
}

public static class GitStatusParser
{
    public static GitStatusResponse ParsePorcelainV2(string projectId, string rootPath, string output)
    {
        var response = new GitStatusResponse
        {
            ProjectId = projectId,
            RootPath = rootPath,
            IsGitRepository = true
        };

        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                ParseBranchHeader(response, line[2..]);
                continue;
            }

            var entry = ParsePorcelainFile(line);
            if (entry is not null)
                response.Files.Add(entry);
        }

        response.DirtyCounts = Count(response.Files);
        if (response.Upstream is null)
            response.Warnings.Add("No upstream branch reported by git status.");
        if (response.IsDetached)
            response.Warnings.Add("Repository is in detached HEAD state.");
        return response;
    }

    public static List<GitFileStatus> ParseNameStatus(string output)
    {
        var files = new List<GitFileStatus>();
        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = rawLine.TrimEnd('\r').Split('\t');
            if (parts.Length < 2) continue;
            var status = parts[0];
            if (status.StartsWith('R') && parts.Length >= 3)
            {
                files.Add(new GitFileStatus
                {
                    Path = parts[2],
                    OldPath = parts[1],
                    IndexStatus = "R",
                    WorktreeStatus = ".",
                    Category = "renamed"
                });
                continue;
            }

            var letter = status.Length > 0 ? status[0].ToString() : "M";
            files.Add(new GitFileStatus
            {
                Path = parts[1],
                IndexStatus = letter,
                WorktreeStatus = ".",
                Category = CategoryFromStatus(letter, ".", false)
            });
        }
        return files;
    }

    private static void ParseBranchHeader(GitStatusResponse response, string header)
    {
        if (header.StartsWith("branch.oid ", StringComparison.Ordinal))
        {
            var oid = header["branch.oid ".Length..].Trim();
            response.HeadSha = oid == "(initial)" ? null : oid;
        }
        else if (header.StartsWith("branch.head ", StringComparison.Ordinal))
        {
            var head = header["branch.head ".Length..].Trim();
            response.IsDetached = head == "(detached)";
            response.Branch = response.IsDetached ? null : head;
        }
        else if (header.StartsWith("branch.upstream ", StringComparison.Ordinal))
        {
            response.Upstream = header["branch.upstream ".Length..].Trim();
        }
        else if (header.StartsWith("branch.ab ", StringComparison.Ordinal))
        {
            foreach (var token in header["branch.ab ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith('+') && int.TryParse(token[1..], out var ahead)) response.Ahead = ahead;
                if (token.StartsWith('-') && int.TryParse(token[1..], out var behind)) response.Behind = behind;
            }
        }
    }

    private static GitFileStatus? ParsePorcelainFile(string line)
    {
        if (line.StartsWith("? ", StringComparison.Ordinal))
        {
            return new GitFileStatus
            {
                Path = line[2..],
                IndexStatus = "?",
                WorktreeStatus = "?",
                Category = "untracked",
                IsUntracked = true
            };
        }

        if (line.StartsWith("1 ", StringComparison.Ordinal))
        {
            var parts = line.Split(' ', 9, StringSplitOptions.None);
            if (parts.Length < 9) return null;
            var xy = parts[1];
            var index = xy.Length > 0 ? xy[0].ToString() : ".";
            var worktree = xy.Length > 1 ? xy[1].ToString() : ".";
            return new GitFileStatus
            {
                Path = parts[8],
                IndexStatus = index,
                WorktreeStatus = worktree,
                Category = CategoryFromStatus(index, worktree, false)
            };
        }

        if (line.StartsWith("2 ", StringComparison.Ordinal))
        {
            var parts = line.Split(' ', 10, StringSplitOptions.None);
            if (parts.Length < 10) return null;
            var xy = parts[1];
            var index = xy.Length > 0 ? xy[0].ToString() : "R";
            var worktree = xy.Length > 1 ? xy[1].ToString() : ".";
            var paths = parts[9].Split('\t', 2);
            return new GitFileStatus
            {
                Path = paths[0],
                OldPath = paths.Length > 1 ? paths[1] : null,
                IndexStatus = index,
                WorktreeStatus = worktree,
                Category = "renamed"
            };
        }

        return null;
    }

    private static GitDirtyCounts Count(List<GitFileStatus> files)
    {
        var counts = new GitDirtyCounts { Total = files.Count };
        foreach (var file in files)
        {
            if (file.IsUntracked) counts.Untracked++;
            if (file.IndexStatus is not null && file.IndexStatus is not "." and not "?" and not " ") counts.Staged++;
            if (file.WorktreeStatus is not null && file.WorktreeStatus is not "." and not "?" and not " ") counts.Unstaged++;
            switch (file.Category)
            {
                case "modified": counts.Modified++; break;
                case "added": counts.Added++; break;
                case "deleted": counts.Deleted++; break;
                case "renamed": counts.Renamed++; break;
            }
        }
        return counts;
    }

    private static string CategoryFromStatus(string index, string worktree, bool untracked)
    {
        if (untracked || index == "?" || worktree == "?") return "untracked";
        if (index == "R" || worktree == "R") return "renamed";
        if (index == "D" || worktree == "D") return "deleted";
        if (index == "A" || worktree == "A") return "added";
        if (index == "M" || worktree == "M") return "modified";
        return "changed";
    }
}
