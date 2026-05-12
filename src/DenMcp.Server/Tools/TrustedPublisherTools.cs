using System.ComponentModel;
using System.Text.Json;
using DenMcp.Core.Services;
using ModelContextProtocol.Server;

namespace DenMcp.Server.Tools;

[McpServerToolType]
public sealed class TrustedPublisherTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    [McpServerTool(Name = "publish_worker_branch"), Description("Trusted publisher Mode A: publish a verified Pi worker branch without exposing Git credentials to the worker sandbox.")]
    public static async Task<string> PublishWorkerBranch(
        ITrustedPublisherService publisher,
        [Description("Project ID.")] string project_id,
        [Description("Task ID.")] int task_id,
        [Description("Worker run id, or session id as fallback.")] string run_id,
        [Description("Actor requesting publish.")] string requested_by,
        [Description("Expected safe task-scoped branch, e.g. task/1285-trusted-publisher.")] string expected_branch,
        [Description("Expected full 40-character worker HEAD commit.")] string expected_head_commit,
        [Description("Expected worker role. Defaults to coder.")] string role = "coder",
        [Description("Optional expected base commit/ref for changed-file scope validation. Defaults to completion packet base_commit or origin/main.")] string? expected_base_commit = null,
        [Description("Optional comma-separated allowed changed-path prefixes.")] string? allowed_path_prefixes = null,
        [Description("Optional remote name. Defaults to origin.")] string? remote_name = null,
        [Description("Optional expected canonical remote URL. If omitted, the project root origin URL is used.")] string? expected_remote_url = null,
        [Description("If true, perform all validation and audit but do not push.")] bool validate_only = false,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var result = await publisher.PublishWorkerBranchAsync(new PublishWorkerBranchRequest
        {
            ProjectId = project_id,
            TaskId = task_id,
            RunId = run_id,
            RequestedBy = requested_by,
            ExpectedBranch = expected_branch,
            ExpectedHeadCommit = expected_head_commit,
            Role = role,
            ExpectedBaseCommit = expected_base_commit,
            AllowedPathPrefixes = allowed_path_prefixes,
            RemoteName = remote_name,
            ExpectedRemoteUrl = expected_remote_url,
            ValidateOnly = validate_only,
        }).ConfigureAwait(false);
        return Serialize(result, verbose);
    }

    [McpServerTool(Name = "publish_reviewed_branch"), Description("Trusted publisher Mode B: let an allowed Hermes orchestrator publish or fast-forward reviewed work after Den review checks pass.")]
    public static async Task<string> PublishReviewedBranch(
        ITrustedPublisherService publisher,
        [Description("Project ID.")] string project_id,
        [Description("Task ID.")] int task_id,
        [Description("Allowed trusted orchestrator identity requesting publish/merge.")] string requested_by,
        [Description("Reviewed safe task-scoped branch.")] string branch,
        [Description("Expected full 40-character reviewed HEAD commit.")] string expected_head_commit,
        [Description("Expected target/base branch. Defaults to main.")] string expected_base_branch,
        [Description("Den review round id with looks_good verdict.")] int review_round_id,
        [Description("Requested operation: push_branch or fast_forward_main.")] string operation = "push_branch",
        [Description("Optional remote name. Defaults to origin.")] string? remote_name = null,
        [Description("Optional expected canonical remote URL. If omitted, the project root origin URL is used.")] string? expected_remote_url = null,
        [Description("If true, perform all validation and audit but do not push.")] bool validate_only = false,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var result = await publisher.PublishReviewedBranchAsync(new PublishReviewedBranchRequest
        {
            ProjectId = project_id,
            TaskId = task_id,
            RequestedBy = requested_by,
            Branch = branch,
            ExpectedHeadCommit = expected_head_commit,
            ExpectedBaseBranch = expected_base_branch,
            ReviewRoundId = review_round_id,
            Operation = operation,
            RemoteName = remote_name,
            ExpectedRemoteUrl = expected_remote_url,
            ValidateOnly = validate_only,
        }).ConfigureAwait(false);
        return Serialize(result, verbose);
    }

    private static string Serialize(TrustedPublisherResult result, bool verbose)
    {
        if (verbose)
            return JsonSerializer.Serialize(result, JsonOptions);
        return JsonSerializer.Serialize(new
        {
            result.Status,
            result.Mode,
            result.Summary,
            result.AuditMessageId,
            Diagnostics = result.Diagnostics.Count == 0 ? null : result.Diagnostics,
        }, JsonOptions);
    }
}
