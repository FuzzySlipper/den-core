using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using Microsoft.Extensions.Logging;

namespace DenMcp.Core.Services;

public sealed class DenPublishFacadeOptions
{
    public string Endpoint { get; set; } = "http://127.0.0.1:5090";
    public List<string> TrustedOrchestrators { get; set; } = [];
    public List<string> TrustedOrchestratorRoles { get; set; } = ["orchestrator"];
    public string TrustedOrchestratorPolicyMode { get; set; } = "audit_warn";
}

public sealed class DenPublishDryRunRequest
{
    public required string ProjectId { get; init; }
    public required int TaskId { get; init; }
    public required string SubmissionId { get; init; }
    public required string WorkerRunId { get; init; }
    public required string RequestedBy { get; init; }
    public required string SubmittedBy { get; init; }
    public string Role { get; init; } = "coder";
    public int AttemptOrdinal { get; init; } = 1;
    public string? ParentSubmissionId { get; init; }
    public required string CodeGateInstance { get; init; }
    public required string CodeGateRepo { get; init; }
    public required string CodeGateRemoteUrl { get; init; }
    public required string IngressRef { get; init; }
    public string? ConvenienceRef { get; init; }
    public required string BaseBranch { get; init; }
    public required string BaseCommit { get; init; }
    public required string HeadCommit { get; init; }
    public required string CanonicalRemoteUrl { get; init; }
    public required string TargetBranch { get; init; }
    public required int ReviewRoundId { get; init; }
    public IReadOnlyList<string> ChangedFilesClaim { get; init; } = [];
    public IReadOnlyList<string> AllowedPathPrefixes { get; init; } = [];
    public IReadOnlyList<string> TestsRun { get; init; } = [];
    public IReadOnlyList<DenPublishScopeOverride> ScopeOverrides { get; init; } = [];
    public DenPublishOrchestratorOverride? OrchestratorOverride { get; init; }
    public string Operation { get; init; } = "push_branch";
    public string TargetRemote { get; init; } = "canonical";
    public string? DecisionId { get; init; }
    public string? WorkspacePath { get; init; }
}

public sealed class DenPublishScopeOverride
{
    public required string OverrideId { get; init; }
    public IReadOnlyList<int> FindingIds { get; init; } = [];
    public required string Reason { get; init; }
    public required string ApprovedBy { get; init; }
}

public sealed class DenPublishOrchestratorOverride
{
    public required string UnclassifiedFailurePolicy { get; init; }
    public required string Reason { get; init; }
    public IReadOnlyList<string> ExpectedRiskCategories { get; init; } = [];
}

public sealed record DenPublishValidationWarning(
    string Code,
    string Message,
    string Reason,
    string Severity = "warning",
    string StrictAction = "reject",
    string PermissiveAction = "allow_with_warning",
    IReadOnlyDictionary<string, string>? ObservedValues = null)
{
    public IReadOnlyDictionary<string, string> ObservedValues { get; init; } = ObservedValues ?? new Dictionary<string, string>();
}

public sealed class DenPublishFacadeResult
{
    public required string Status { get; init; }
    public required string Summary { get; init; }
    public bool Succeeded { get; init; }
    public string? DecisionId { get; init; }
    public string? SubmissionId { get; init; }
    public string? PublishStatus { get; init; }
    public string? ValidationStatus { get; init; }
    public bool? IsPublishable { get; init; }
    public string? FetchedHeadCommit { get; init; }
    public string? LocalRef { get; init; }
    public int? AuditMessageId { get; init; }
    public string CallerTrust { get; init; } = "worker";
    public string EffectivePolicyMode { get; init; } = "strict";
    public List<DenPublishValidationWarning> Warnings { get; init; } = [];
    public List<string> HardeningHints { get; init; } = [];
    public List<string> Diagnostics { get; init; } = [];
}

public interface IDenPublishFacadeService
{
    Task<DenPublishFacadeResult> RequestDryRunAsync(DenPublishDryRunRequest request, CancellationToken cancellationToken = default);
}

public sealed class DenPublishFacadeService : IDenPublishFacadeService
{
    private static readonly Regex SafeSha = new("^[0-9a-f]{40}$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex SafeTaskBranch = new("^task/[0-9]+[A-Za-z0-9._/-]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IProjectRepository _projects;
    private readonly IReviewRoundRepository _reviewRounds;
    private readonly IReviewFindingRepository _reviewFindings;
    private readonly IMessageRepository _messages;
    private readonly IAgentInstanceBindingRepository _agentBindings;
    private readonly HttpClient _http;
    private readonly DenPublishFacadeOptions _options;
    private readonly ILogger<DenPublishFacadeService> _logger;

    public DenPublishFacadeService(
        IProjectRepository projects,
        IReviewRoundRepository reviewRounds,
        IReviewFindingRepository reviewFindings,
        IMessageRepository messages,
        IAgentInstanceBindingRepository agentBindings,
        HttpClient http,
        DenPublishFacadeOptions options,
        ILogger<DenPublishFacadeService> logger)
    {
        _projects = projects;
        _reviewRounds = reviewRounds;
        _reviewFindings = reviewFindings;
        _messages = messages;
        _agentBindings = agentBindings;
        _http = http;
        _options = options;
        _logger = logger;
    }

    public async Task<DenPublishFacadeResult> RequestDryRunAsync(DenPublishDryRunRequest request, CancellationToken cancellationToken = default)
    {
        var diagnostics = ValidateRequestShape(request);
        var decisionId = string.IsNullOrWhiteSpace(request.DecisionId)
            ? $"pub_{request.TaskId}_{request.SubmissionId}"
            : request.DecisionId;
        var callerContext = await ResolveCallerContextAsync(request).ConfigureAwait(false);

        if (request.OrchestratorOverride is not null && !callerContext.IsTrustedOrchestrator)
        {
            diagnostics.Add("Orchestrator override requires a trusted orchestrator resolved from Den Core configuration or an active allowlisted agent binding; requestedBy alone is not trusted.");
        }

        var project = await _projects.GetByIdAsync(request.ProjectId).ConfigureAwait(false);
        if (project is null)
            diagnostics.Add($"Project '{request.ProjectId}' was not found in Den state.");

        var review = await _reviewRounds.GetByIdAsync(request.ReviewRoundId).ConfigureAwait(false);
        if (review is null)
        {
            diagnostics.Add($"Review round {request.ReviewRoundId} was not found.");
        }
        else
        {
            if (review.TaskId != request.TaskId)
                diagnostics.Add($"Review round task mismatch: expected task {request.TaskId}, found task {review.TaskId}.");
            if (review.Verdict != ReviewVerdict.LooksGood)
                diagnostics.Add($"Review round {request.ReviewRoundId} must have verdict looks_good before den-publish dry-run.");
            if (!ShaEquals(review.HeadCommit, request.HeadCommit))
                diagnostics.Add($"Review head mismatch: review round {request.ReviewRoundId} has {review.HeadCommit}, request has {request.HeadCommit}.");
            if (!string.Equals(review.BaseCommit, request.BaseCommit, StringComparison.OrdinalIgnoreCase))
                diagnostics.Add($"Review base mismatch: review round {request.ReviewRoundId} has {review.BaseCommit}, request has {request.BaseCommit}.");
            if (!IsAllowedTargetBranchForReview(request.TargetBranch, request.TaskId, review))
                diagnostics.Add($"TargetBranch '{request.TargetBranch}' must either be a safe task-scoped branch for task {request.TaskId} or match the reviewed base branch for review round {request.ReviewRoundId}.");
        }

        var findings = review is null
            ? []
            : await _reviewFindings.ListByReviewRoundAsync(request.ReviewRoundId).ConfigureAwait(false);
        var unresolvedBlocking = findings.Where(IsUnresolvedBlocking).ToList();
        foreach (var finding in unresolvedBlocking)
        {
            if (!HasStructuredOverride(request.ScopeOverrides, finding.Id))
                diagnostics.Add($"Unresolved blocking finding {finding.Id} requires a structured scope override with reason and approver.");
        }

        if (diagnostics.Count > 0)
        {
            return new DenPublishFacadeResult
            {
                Status = "rejected",
                Summary = "den-publish dry-run request rejected before calling den-publish",
                Succeeded = false,
                DecisionId = decisionId,
                SubmissionId = request.SubmissionId,
                CallerTrust = callerContext.CallerTrust,
                EffectivePolicyMode = callerContext.PolicyMode,
                Diagnostics = diagnostics,
            };
        }

        var apiPayload = BuildApiPayload(request, review!, findings, decisionId, callerContext);
        HttpResponseMessage response;
        string responseText;
        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/promotion/dry-run")
            {
                Content = JsonContent.Create(apiPayload, options: ApiJsonOptions)
            };
            httpRequest.Headers.TryAddWithoutValidation("X-Den-Requested-By", request.RequestedBy);
            httpRequest.Headers.TryAddWithoutValidation("X-Den-Caller-Trust", callerContext.CallerTrust);
            httpRequest.Headers.TryAddWithoutValidation("X-Den-Promotion-Policy-Mode", callerContext.PolicyMode);

            response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "den-publish dry-run request failed");
            return new DenPublishFacadeResult
            {
                Status = "failed",
                Summary = "den-publish dry-run request failed before receiving a response",
                Succeeded = false,
                DecisionId = decisionId,
                SubmissionId = request.SubmissionId,
                CallerTrust = callerContext.CallerTrust,
                EffectivePolicyMode = callerContext.PolicyMode,
                Diagnostics = [$"den-publish request failed: {ex.GetType().Name}: {ex.Message}"],
            };
        }

        var parsed = ParseResponse(response, responseText, decisionId, request.SubmissionId);
        var auditMessageId = await AuditAsync(request, parsed, response.StatusCode, callerContext, cancellationToken).ConfigureAwait(false);

        return new DenPublishFacadeResult
        {
            Status = parsed.Status,
            Summary = parsed.Summary,
            Succeeded = parsed.Succeeded,
            DecisionId = parsed.DecisionId,
            SubmissionId = request.SubmissionId,
            PublishStatus = parsed.PublishStatus,
            ValidationStatus = parsed.ValidationStatus,
            IsPublishable = parsed.IsPublishable,
            FetchedHeadCommit = parsed.FetchedHeadCommit,
            LocalRef = parsed.LocalRef,
            AuditMessageId = auditMessageId,
            CallerTrust = callerContext.CallerTrust,
            EffectivePolicyMode = callerContext.PolicyMode,
            Warnings = parsed.Warnings,
            HardeningHints = parsed.Warnings.Count > 0 ? WarningHardeningHints.ToList() : [],
            Diagnostics = parsed.Diagnostics,
        };
    }

    private static List<string> ValidateRequestShape(DenPublishDryRunRequest request)
    {
        var diagnostics = new List<string>();
        Require(nameof(request.ProjectId), request.ProjectId, diagnostics);
        Require(nameof(request.SubmissionId), request.SubmissionId, diagnostics);
        Require(nameof(request.WorkerRunId), request.WorkerRunId, diagnostics);
        Require(nameof(request.RequestedBy), request.RequestedBy, diagnostics);
        Require(nameof(request.SubmittedBy), request.SubmittedBy, diagnostics);
        Require(nameof(request.CodeGateInstance), request.CodeGateInstance, diagnostics);
        Require(nameof(request.CodeGateRepo), request.CodeGateRepo, diagnostics);
        Require(nameof(request.CodeGateRemoteUrl), request.CodeGateRemoteUrl, diagnostics);
        Require(nameof(request.IngressRef), request.IngressRef, diagnostics);
        Require(nameof(request.BaseBranch), request.BaseBranch, diagnostics);
        Require(nameof(request.CanonicalRemoteUrl), request.CanonicalRemoteUrl, diagnostics);
        if (!SafeSha.IsMatch(request.BaseCommit))
            diagnostics.Add("BaseCommit must be a full 40-character SHA.");
        if (!SafeSha.IsMatch(request.HeadCommit))
            diagnostics.Add("HeadCommit must be a full 40-character SHA.");
        if (!SafeTaskBranch.IsMatch(request.TargetBranch))
            diagnostics.Add($"TargetBranch '{request.TargetBranch}' must be a safe task-scoped branch.");
        if (request.AttemptOrdinal < 1)
            diagnostics.Add("AttemptOrdinal must be >= 1.");
        return diagnostics;
    }

    private static bool IsAllowedTargetBranchForReview(string targetBranch, int taskId, ReviewRound review)
    {
        if (targetBranch.StartsWith($"task/{taskId}", StringComparison.Ordinal))
            return true;

        if (string.Equals(targetBranch, review.BaseBranch, StringComparison.Ordinal))
            return true;

        return !string.IsNullOrWhiteSpace(review.PreferredDiffBaseRef)
            && string.Equals(targetBranch, review.PreferredDiffBaseRef, StringComparison.Ordinal);
    }

    private static void Require(string name, string? value, List<string> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value))
            diagnostics.Add($"{name} is required.");
    }

    private static object BuildApiPayload(
        DenPublishDryRunRequest request,
        ReviewRound review,
        IReadOnlyList<ReviewFinding> findings,
        string decisionId,
        DenPublishCallerContext callerContext)
    {
        var usedScopeOverrides = request.ScopeOverrides.Select(o => new
        {
            o.OverrideId,
            o.FindingIds,
            o.Reason,
            ApprovedBy = o.ApprovedBy
        }).ToArray();

        return new
        {
            WorkspacePath = request.WorkspacePath ?? "/tmp/den-publish-facade-managed-workspace",
            AllowedPathPrefixes = request.AllowedPathPrefixes,
            Decision = new
            {
                DecisionId = decisionId,
                ProjectId = request.ProjectId,
                TaskId = request.TaskId,
                SubmissionId = request.SubmissionId,
                RequestedBy = request.RequestedBy,
                Operation = request.Operation,
                TargetRemote = request.TargetRemote,
                TargetBranch = request.TargetBranch,
                ExpectedHeadCommit = request.HeadCommit,
                ExpectedBaseBranch = request.BaseBranch,
                ReviewRoundId = request.ReviewRoundId,
                ScopeOverrideIds = request.ScopeOverrides.Select(o => o.OverrideId).ToArray(),
                ValidateOnly = true,
                CreatedAt = DateTimeOffset.UtcNow,
                ScopeOverrides = usedScopeOverrides,
                OrchestratorOverride = callerContext.IsTrustedOrchestrator && request.OrchestratorOverride is not null
                    ? new
                    {
                        request.OrchestratorOverride.UnclassifiedFailurePolicy,
                        request.OrchestratorOverride.Reason,
                        request.OrchestratorOverride.ExpectedRiskCategories,
                    }
                    : null,
            },
            Submission = new
            {
                SubmissionId = request.SubmissionId,
                ProjectId = request.ProjectId,
                TaskId = request.TaskId,
                WorkerRunId = request.WorkerRunId,
                SubmittedBy = request.SubmittedBy,
                Role = request.Role,
                AttemptOrdinal = request.AttemptOrdinal,
                ParentSubmissionId = request.ParentSubmissionId,
                CodeGateInstance = request.CodeGateInstance,
                CodeGateRepo = request.CodeGateRepo,
                CodeGateRemoteUrl = request.CodeGateRemoteUrl,
                IngressRef = request.IngressRef,
                ConvenienceRef = request.ConvenienceRef,
                BaseBranch = request.BaseBranch,
                BaseCommit = request.BaseCommit,
                HeadCommit = request.HeadCommit,
                CanonicalRemoteUrl = request.CanonicalRemoteUrl,
                TargetBranch = request.TargetBranch,
                ChangedFilesClaim = request.ChangedFilesClaim,
                TestsRun = request.TestsRun,
                Status = "approved",
                CreatedAt = DateTimeOffset.UtcNow,
                Review = new
                {
                    ReviewRoundId = review.Id,
                    Verdict = review.Verdict?.ToDbValue(),
                    Findings = findings.Select(f => new
                    {
                        FindingId = f.Id.ToString(CultureInfo.InvariantCulture),
                        Blocking = IsBlockingFinding(f),
                        Resolved = IsResolvedFinding(f),
                        OverrideId = request.ScopeOverrides
                            .FirstOrDefault(o => o.FindingIds.Contains(f.Id))
                            ?.OverrideId,
                    }).ToArray(),
                }
            }
        };
    }

    private async Task<int?> AuditAsync(
        DenPublishDryRunRequest request,
        ParsedDenPublishResponse parsed,
        System.Net.HttpStatusCode statusCode,
        DenPublishCallerContext callerContext,
        CancellationToken cancellationToken)
    {
        var metadata = JsonSerializer.SerializeToElement(new
        {
            type = "den_publish_dry_run_result",
            decision_id = parsed.DecisionId,
            submission_id = request.SubmissionId,
            publish_status = parsed.PublishStatus,
            validation_status = parsed.ValidationStatus,
            is_publishable = parsed.IsPublishable,
            fetched_head_commit = parsed.FetchedHeadCommit,
            local_ref = parsed.LocalRef,
            http_status = (int)statusCode,
            succeeded = parsed.Succeeded,
            caller_trust = callerContext.CallerTrust,
            effective_policy_mode = callerContext.PolicyMode,
            warning_count = parsed.Warnings.Count,
            warnings = parsed.Warnings.Select(warning => new
            {
                code = warning.Code,
                message = warning.Message,
                reason = warning.Reason,
                severity = warning.Severity,
                strict_action = warning.StrictAction,
                permissive_action = warning.PermissiveAction,
                observed_values = warning.ObservedValues,
            }).ToArray(),
            hardening_hints = parsed.Warnings.Count > 0 ? WarningHardeningHints : [],
        }, ApiJsonOptions);

        var content = parsed.Succeeded switch
        {
            true when parsed.Warnings.Count > 0 =>
                $"den-publish dry-run allowed submission `{request.SubmissionId}` for `{request.TargetBranch}` at `{request.HeadCommit}` with {parsed.Warnings.Count} warning(s): {SummarizeWarnings(parsed.Warnings)}\n\nHardening hints: {string.Join(" ", WarningHardeningHints)}",
            true =>
                $"den-publish dry-run validated submission `{request.SubmissionId}` for `{request.TargetBranch}` at `{request.HeadCommit}`.",
            _ =>
                $"den-publish dry-run failed for submission `{request.SubmissionId}`: {parsed.Summary}"
        };

        var message = await _messages.CreateAsync(new Message
        {
            ProjectId = request.ProjectId,
            TaskId = request.TaskId,
            Sender = request.RequestedBy,
            Intent = MessageIntent.StatusUpdate,
            Content = content,
            Metadata = metadata,
        }).ConfigureAwait(false);
        return message.Id;
    }

    private static ParsedDenPublishResponse ParseResponse(HttpResponseMessage response, string responseText, string decisionId, string submissionId)
    {
        var diagnostics = new List<string>();
        if (!response.IsSuccessStatusCode)
            diagnostics.Add($"den-publish returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim());

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(responseText) ? "{}" : responseText);
            var root = doc.RootElement;
            var succeeded = response.IsSuccessStatusCode && GetBoolean(root, "succeeded") == true;
            var publishStatus = GetString(root, "publishStatus");
            var responseDecisionId = GetNestedString(root, "audit", "decisionId") ?? decisionId;
            var validation = root.TryGetProperty("validation", out var validationElement) ? validationElement : default;
            var validationStatus = validation.ValueKind == JsonValueKind.Object ? GetString(validation, "status") : null;
            var validationSummary = validation.ValueKind == JsonValueKind.Object ? GetString(validation, "summary") : null;
            var isPublishable = validation.ValueKind == JsonValueKind.Object ? GetBoolean(validation, "isPublishable") : null;
            var fetchedHead = validation.ValueKind == JsonValueKind.Object ? GetString(validation, "fetchedHeadCommit") : null;
            var localRef = validation.ValueKind == JsonValueKind.Object ? GetString(validation, "localRef") : null;
            var warnings = validation.ValueKind == JsonValueKind.Object ? GetWarnings(validation) : [];

            if (response.IsSuccessStatusCode && publishStatus is null)
                diagnostics.Add("den-publish response was missing publishStatus.");
            if (response.IsSuccessStatusCode && validationStatus is null)
                diagnostics.Add("den-publish response was missing validation.status.");

            var parsedStatus = ParseFacadeStatus(succeeded, diagnostics.Count == 0, validationStatus, warnings.Count);
            var summary = parsedStatus switch
            {
                "allowed_with_warnings" => $"den-publish dry-run allowed submission {submissionId} with warning(s)",
                "validated" => $"den-publish dry-run validated submission {submissionId}",
                "rejected" => validationSummary ?? "den-publish dry-run response was rejected",
                _ => validationSummary ?? "den-publish dry-run response was not publishable",
            };

            return new ParsedDenPublishResponse(
                Status: parsedStatus,
                Summary: summary,
                Succeeded: succeeded && diagnostics.Count == 0,
                DecisionId: responseDecisionId,
                PublishStatus: publishStatus,
                ValidationStatus: validationStatus,
                IsPublishable: isPublishable,
                FetchedHeadCommit: fetchedHead,
                LocalRef: localRef,
                Warnings: warnings,
                Diagnostics: diagnostics);
        }
        catch (JsonException ex)
        {
            diagnostics.Add($"den-publish returned malformed JSON: {ex.Message}");
            return new ParsedDenPublishResponse(
                Status: "failed",
                Summary: "den-publish dry-run returned malformed JSON",
                Succeeded: false,
                DecisionId: decisionId,
                PublishStatus: null,
                ValidationStatus: null,
                IsPublishable: null,
                FetchedHeadCommit: null,
                LocalRef: null,
                Warnings: [],
                Diagnostics: diagnostics);
        }
    }

    private async Task<DenPublishCallerContext> ResolveCallerContextAsync(DenPublishDryRunRequest request)
    {
        if (IsConfiguredTrustedOrchestrator(request.RequestedBy))
        {
            return DenPublishCallerContext.Trusted(_options.TrustedOrchestratorPolicyMode, "configured_trusted_orchestrator");
        }

        try
        {
            var bindings = await _agentBindings.ListAsync(new AgentInstanceBindingListOptions
            {
                ProjectId = request.ProjectId,
                AgentIdentity = request.RequestedBy,
                Statuses = [AgentInstanceBindingStatus.Active, AgentInstanceBindingStatus.Degraded]
            }).ConfigureAwait(false);

            if (bindings.Any(binding => IsTrustedOrchestratorRole(binding.Role)))
            {
                return DenPublishCallerContext.Trusted(_options.TrustedOrchestratorPolicyMode, "active_agent_binding");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve Den publish caller binding for {RequestedBy}", request.RequestedBy);
        }

        return DenPublishCallerContext.Worker;
    }

    private bool IsConfiguredTrustedOrchestrator(string requestedBy) =>
        _options.TrustedOrchestrators.Any(identity => string.Equals(identity, requestedBy, StringComparison.Ordinal));

    private bool IsTrustedOrchestratorRole(string? role) =>
        !string.IsNullOrWhiteSpace(role)
        && _options.TrustedOrchestratorRoles.Any(trustedRole => string.Equals(trustedRole, role, StringComparison.OrdinalIgnoreCase));

    private static bool IsUnresolvedBlocking(ReviewFinding finding)
    {
        if (!IsBlockingFinding(finding))
            return false;
        return !IsResolvedFinding(finding);
    }

    private static bool IsBlockingFinding(ReviewFinding finding) =>
        finding.Category is ReviewFindingCategory.BlockingBug or ReviewFindingCategory.AcceptanceGap;

    private static bool IsResolvedFinding(ReviewFinding finding) =>
        finding.Status is ReviewFindingStatus.VerifiedFixed or ReviewFindingStatus.Superseded or ReviewFindingStatus.SplitToFollowUp;

    private static bool HasStructuredOverride(IEnumerable<DenPublishScopeOverride> overrides, int findingId) =>
        overrides.Any(o =>
            o.FindingIds.Contains(findingId) &&
            !string.IsNullOrWhiteSpace(o.OverrideId) &&
            !string.IsNullOrWhiteSpace(o.Reason) &&
            !string.IsNullOrWhiteSpace(o.ApprovedBy));

    private static bool ShaEquals(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static readonly string[] WarningHardeningHints =
    [
        "Resolve warning(s) before canonical publish when practical.",
        "If warning(s) are unexpected, switch to a stricter policy and re-run validation.",
        "Preserve and inspect warning audit metadata before live promotion."
    ];

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? GetNestedString(JsonElement element, string objectProperty, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(objectProperty, out var nested)
            ? GetString(nested, property)
            : null;

    private static bool? GetBoolean(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static IReadOnlyDictionary<string, string> GetStringDictionary(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>();
        }

        return value.EnumerateObject()
            .Where(item => item.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(item => item.Name, item => item.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
    }

    private static List<DenPublishValidationWarning> GetWarnings(JsonElement validation)
    {
        if (!validation.TryGetProperty("warnings", out var warningsElement) || warningsElement.ValueKind != JsonValueKind.Array)
            return [];

        var warnings = new List<DenPublishValidationWarning>();
        foreach (var warning in warningsElement.EnumerateArray())
        {
            warnings.Add(new DenPublishValidationWarning(
                GetString(warning, "code") ?? "unknown_warning",
                GetString(warning, "message") ?? string.Empty,
                GetString(warning, "reason") ?? string.Empty,
                GetString(warning, "severity") ?? "warning",
                GetString(warning, "strictAction") ?? "reject",
                GetString(warning, "permissiveAction") ?? "allow_with_warning",
                GetStringDictionary(warning, "observedValues")));
        }
        return warnings;
    }

    private static string ParseFacadeStatus(bool succeeded, bool structurallyValid, string? validationStatus, int warningCount)
    {
        if (succeeded && structurallyValid)
            return warningCount > 0 ? "allowed_with_warnings" : "validated";
        if (validationStatus is "rejected")
            return "rejected";
        return "failed";
    }

    private static string SummarizeWarnings(IEnumerable<DenPublishValidationWarning> warnings) =>
        string.Join("; ", warnings.Select(warning => $"{warning.Code}: {warning.Message}"));

    private sealed record DenPublishCallerContext(
        bool IsTrustedOrchestrator,
        string CallerTrust,
        string PolicyMode,
        string ResolvedFrom)
    {
        public static DenPublishCallerContext Worker { get; } = new(false, "worker", "strict", "default_strict_worker");

        public static DenPublishCallerContext Trusted(string policyMode, string resolvedFrom) =>
            new(true, "trusted_orchestrator", string.IsNullOrWhiteSpace(policyMode) ? "audit_warn" : policyMode, resolvedFrom);
    }

    private sealed record ParsedDenPublishResponse(
        string Status,
        string Summary,
        bool Succeeded,
        string? DecisionId,
        string? PublishStatus,
        string? ValidationStatus,
        bool? IsPublishable,
        string? FetchedHeadCommit,
        string? LocalRef,
        List<DenPublishValidationWarning> Warnings,
        List<string> Diagnostics);
}
