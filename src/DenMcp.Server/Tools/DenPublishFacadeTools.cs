using System.ComponentModel;
using DenMcp.Core.Mcp;
using System.Text.Json;
using DenMcp.Core.Services;
using ModelContextProtocol.Server;

namespace DenMcp.Server.Tools;

[McpServerToolType]
public sealed class DenPublishFacadeTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    [McpToolProfile("legacy-full")]
    [McpToolBundle("legacy")]
    [McpServerTool(Name = "legacy_request_den_publish_dry_run"), Description("LEGACY / ADMIN ONLY: Den-native facade for den-publish validate-only promotion dry-run. Builds the camelCase den-publish payload from explicit Den/code-gate fields, verifies matching looks_good review state, calls den-publish, and records the result in the task thread. Publisher-path-only.")]
    public static async Task<string> RequestDenPublishDryRun(
        IDenPublishFacadeService facade,
        [Description("Project ID, e.g. den-channels.")] string project_id,
        [Description("Den task ID.")] int task_id,
        [Description("Exact Den/code-gate submission id.")] string submission_id,
        [Description("Worker run id that produced the submission.")] string worker_run_id,
        [Description("Agent/orchestrator requesting the dry-run.")] string requested_by,
        [Description("Coder/agent that submitted the code.")] string submitted_by,
        [Description("Code-gate repo path, e.g. den-channels/den-channels.")] string code_gate_repo,
        [Description("Code-gate remote URL.")] string code_gate_remote_url,
        [Description("Immutable ingress ref under refs/heads/submissions/...")] string ingress_ref,
        [Description("Base branch, normally main.")] string base_branch,
        [Description("Expected full 40-character base commit.")] string base_commit,
        [Description("Expected full 40-character head commit.")] string head_commit,
        [Description("Canonical remote URL expected by policy.")] string canonical_remote_url,
        [Description("Target canonical branch, e.g. task/123-short-slug.")] string target_branch,
        [Description("Den review round id that must have looks_good verdict for the same head.")] int review_round_id,
        [Description("Optional code-gate instance label. Defaults to den-code-gate.")] string code_gate_instance = "den-code-gate",
        [Description("Optional submission role. Defaults to coder.")] string role = "coder",
        [Description("Attempt ordinal. Defaults to 1.")] int attempt_ordinal = 1,
        [Description("Optional parent submission id for rework chains.")] string? parent_submission_id = null,
        [Description("Optional mutable convenience ref; authority remains ingress_ref.")] string? convenience_ref = null,
        [Description("Optional JSON array or comma-separated changed-file claim.")] string? changed_files_claim = null,
        [Description("Optional JSON array or comma-separated allowed changed-path prefixes.")] string? allowed_path_prefixes = null,
        [Description("Optional JSON array or newline-separated tests run.")] string? tests_run = null,
        [Description("Optional JSON array of structured overrides: [{override_id, finding_ids, reason, approved_by}].")] string? scope_overrides = null,
        [Description("Optional JSON object for trusted-orchestrator soft-failure policy, e.g. {unclassified_failure_policy, reason, expected_risk_categories}.")] string? orchestrator_override = null,
        [Description("Optional decision id. Defaults to pub_<task_id>_<submission_id>.")] string? decision_id = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var result = await facade.RequestDryRunAsync(new DenPublishDryRunRequest
        {
            ProjectId = project_id,
            TaskId = task_id,
            SubmissionId = submission_id,
            WorkerRunId = worker_run_id,
            RequestedBy = requested_by,
            SubmittedBy = submitted_by,
            Role = role,
            AttemptOrdinal = attempt_ordinal,
            ParentSubmissionId = parent_submission_id,
            CodeGateInstance = code_gate_instance,
            CodeGateRepo = code_gate_repo,
            CodeGateRemoteUrl = code_gate_remote_url,
            IngressRef = ingress_ref,
            ConvenienceRef = convenience_ref,
            BaseBranch = base_branch,
            BaseCommit = base_commit,
            HeadCommit = head_commit,
            CanonicalRemoteUrl = canonical_remote_url,
            TargetBranch = target_branch,
            ReviewRoundId = review_round_id,
            ChangedFilesClaim = ParseStringList(changed_files_claim),
            AllowedPathPrefixes = ParseStringList(allowed_path_prefixes),
            TestsRun = ParseStringList(tests_run, splitNewlines: true),
            ScopeOverrides = ParseScopeOverrides(scope_overrides),
            OrchestratorOverride = ParseOrchestratorOverride(orchestrator_override),
            DecisionId = decision_id,
        }).ConfigureAwait(false);

        return Serialize(result, verbose);
    }

    private static string Serialize(DenPublishFacadeResult result, bool verbose)
    {
        if (verbose)
            return JsonSerializer.Serialize(result, JsonOptions);
        return JsonSerializer.Serialize(new
        {
            result.Status,
            result.Summary,
            result.Succeeded,
            result.DecisionId,
            result.SubmissionId,
            result.PublishStatus,
            result.ValidationStatus,
            result.IsPublishable,
            result.AuditMessageId,
            result.CallerTrust,
            result.EffectivePolicyMode,
            Warnings = result.Warnings.Count == 0 ? null : result.Warnings,
            HardeningHints = result.HardeningHints.Count == 0 ? null : result.HardeningHints,
            Diagnostics = result.Diagnostics.Count == 0 ? null : result.Diagnostics,
        }, JsonOptions);
    }

    private static IReadOnlyList<string> ParseStringList(string? value, bool splitNewlines = false)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];
        var trimmed = value.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
            return JsonSerializer.Deserialize<List<string>>(trimmed, JsonOptions) ?? [];
        var separators = splitNewlines ? ['\n', '\r'] : new[] { ',' };
        return trimmed.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }

    private static IReadOnlyList<DenPublishScopeOverride> ParseScopeOverrides(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];
        using var doc = JsonDocument.Parse(value);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("scope_overrides must be a JSON array.", nameof(value));

        var overrides = new List<DenPublishScopeOverride>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var overrideId = GetString(item, "override_id") ?? GetString(item, "overrideId");
            var reason = GetString(item, "reason");
            var approvedBy = GetString(item, "approved_by") ?? GetString(item, "approvedBy");
            var findingIds = GetIntArray(item, "finding_ids") ?? GetIntArray(item, "findingIds") ?? [];
            if (string.IsNullOrWhiteSpace(overrideId) || string.IsNullOrWhiteSpace(reason) || string.IsNullOrWhiteSpace(approvedBy))
                throw new ArgumentException("Each scope override must include override_id, reason, and approved_by.", nameof(value));
            overrides.Add(new DenPublishScopeOverride
            {
                OverrideId = overrideId,
                Reason = reason,
                ApprovedBy = approvedBy,
                FindingIds = findingIds,
            });
        }
        return overrides;
    }


    private static DenPublishOrchestratorOverride? ParseOrchestratorOverride(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        using var doc = JsonDocument.Parse(value);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("orchestrator_override must be a JSON object.", nameof(value));

        var policy = GetString(doc.RootElement, "unclassified_failure_policy") ?? GetString(doc.RootElement, "unclassifiedFailurePolicy");
        var reason = GetString(doc.RootElement, "reason");
        var risks = GetStringArray(doc.RootElement, "expected_risk_categories")
            ?? GetStringArray(doc.RootElement, "expectedRiskCategories")
            ?? [];
        if (string.IsNullOrWhiteSpace(policy) || string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("orchestrator_override must include unclassified_failure_policy and reason.", nameof(value));

        return new DenPublishOrchestratorOverride
        {
            UnclassifiedFailurePolicy = policy,
            Reason = reason,
            ExpectedRiskCategories = risks,
        };
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;


    private static IReadOnlyList<string>? GetStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return null;
        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new ArgumentException($"{name} must contain string values.", name);
            var parsed = item.GetString();
            if (!string.IsNullOrWhiteSpace(parsed))
                items.Add(parsed);
        }
        return items;
    }

    private static IReadOnlyList<int>? GetIntArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return null;
        var ids = new List<int>();
        foreach (var id in value.EnumerateArray())
        {
            if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var parsed))
                throw new ArgumentException($"{name} must contain integer ids.", name);
            ids.Add(parsed);
        }
        return ids;
    }
}
