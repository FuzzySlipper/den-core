using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DenCore.Data;
using DenCore.Models;

namespace DenCore.Services;

/// <summary>
/// Pluggable HTTP executor client for capability invocation.
/// Core uses this to proxy synchronous HTTP calls to registered read-only
/// http_endpoint capabilities.
/// </summary>
public interface IHttpExecutorClient
{
    /// <summary>
    /// Execute a synchronous HTTP request to the given endpoint.
    /// Returns (statusCode, responseBody, errorMessage).
    /// </summary>
    Task<(int statusCode, string? responseBody, string? error)> ExecuteAsync(
        string endpoint, string? payload, int timeoutMs);
}

/// <summary>
/// Default HTTP executor using HttpClient.
/// </summary>
public sealed class DefaultHttpExecutorClient : IHttpExecutorClient
{
    private readonly HttpClient _client;

    public DefaultHttpExecutorClient(HttpClient client)
    {
        _client = client;
    }

    public async Task<(int statusCode, string? responseBody, string? error)> ExecuteAsync(
        string endpoint, string? payload, int timeoutMs)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
            var content = payload is not null
                ? new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
                : null;

            var response = await _client.PostAsync(endpoint, content, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            return ((int)response.StatusCode, body, null);
        }
        catch (TaskCanceledException)
        {
            return (0, null, "timeout");
        }
        catch (HttpRequestException ex)
        {
            return (0, null, $"http_request_failed: {ex.Message}");
        }
    }
}

/// <summary>
/// Core-owned service for capability invocation lifecycle.
/// Records invocations, validates preconditions, executes through the
/// pluggable HTTP executor, and always terminalizes the audit record.
/// </summary>
public interface ICapabilityInvocationService
{
    /// <summary>
    /// Invoke a capability by its ID. Always produces a terminal audit record.
    /// Only read-only http_endpoint capabilities are executed by Core.
    /// </summary>
    Task<CapabilityInvocation> InvokeAsync(
        string capabilityId,
        string? requestJson,
        string? callerProjectId = null,
        int? callerTaskId = null,
        string? callerAgent = null,
        string? callerMessageId = null,
        string? callerSurface = null,
        int timeoutMs = DefaultTimeouts.InvocationTimeoutMs);

    /// <summary>
    /// Convenience wrapper for vision.analyze_image.v1.
    /// Validates image input before delegating to the invocation pipeline.
    /// Returns structured AnalyzeImageResult (not raw exceptions).
    /// </summary>
    Task<AnalyzeImageResult> AnalyzeImageAsync(
        string imageRef,
        string? question,
        string mode = "general",
        string? callerProjectId = null,
        int? callerTaskId = null,
        string? callerAgent = null,
        bool includeOcr = true,
        bool includeRegions = false,
        int timeoutMs = DefaultTimeouts.AnalyzeImageTimeoutMs);
}

public sealed class CapabilityInvocationService : ICapabilityInvocationService
{
    private readonly ICapabilityRepository _repo;
    private readonly IHttpExecutorClient _executor;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // Max request/response payload size to store inline (100KB)
    private const int MaxInlinePayloadSize = 100 * 1024;

    // Max total request size for analyze_image (10MB)
    private const int MaxImageRequestSize = 10 * 1024 * 1024;

    public CapabilityInvocationService(ICapabilityRepository repo, IHttpExecutorClient executor)
    {
        _repo = repo;
        _executor = executor;
    }

    public async Task<CapabilityInvocation> InvokeAsync(
        string capabilityId,
        string? requestJson,
        string? callerProjectId = null,
        int? callerTaskId = null,
        string? callerAgent = null,
        string? callerMessageId = null,
        string? callerSurface = null,
        int timeoutMs = DefaultTimeouts.InvocationTimeoutMs)
    {
        // Clamp timeout
        var clampedTimeout = Math.Clamp(timeoutMs, 1000, 300000);
        var sw = Stopwatch.StartNew();
        var startedAt = DateTime.UtcNow;

        // Compute request hash for dedup
        var requestHash = requestJson is not null
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestJson)))
            : null;

        // 1. Look up capability definition first
        var definition = await _repo.GetDefinitionAsync(capabilityId);
        if (definition is null)
        {
            sw.Stop();
            // Create a terminal invocation record (no FK dependency issues since invocation table
            // doesn't FK-reference capability_definitions)
            return await CreateAndReturnTerminal(
                capabilityId, callerProjectId ?? "unknown", callerTaskId,
                callerAgent, callerMessageId, callerSurface,
                InvocationStatuses.InvalidRequest, requestJson, requestHash, startedAt,
                errorType: "capability_not_found",
                errorMessage: $"Capability '{capabilityId}' not found",
                durationMs: (int)sw.ElapsedMilliseconds);
        }

        // 2. Check status — only active is executable
        if (definition.Status != CapabilityStatuses.Active)
        {
            sw.Stop();
            return await CreateAndReturnTerminal(
                capabilityId, callerProjectId ?? "unknown", callerTaskId,
                callerAgent, callerMessageId, callerSurface,
                InvocationStatuses.Disabled, requestJson, requestHash, startedAt,
                errorType: "capability_not_active",
                errorMessage: $"Capability '{capabilityId}' is {definition.Status}",
                durationMs: (int)sw.ElapsedMilliseconds);
        }

        // 3. Check request size
        var requestSize = requestJson?.Length ?? 0;
        if (requestSize > definition.MaxRequestBytes)
        {
            sw.Stop();
            return await CreateAndReturnTerminal(
                capabilityId, callerProjectId ?? "unknown", callerTaskId,
                callerAgent, callerMessageId, callerSurface,
                InvocationStatuses.InvalidRequest, TruncatePayload(requestJson), requestHash, startedAt,
                errorType: "request_too_large",
                errorMessage: $"Request payload ({requestSize} bytes) exceeds max_request_bytes ({definition.MaxRequestBytes} bytes)",
                durationMs: (int)sw.ElapsedMilliseconds);
        }

        // 4. Check that only read-only http_endpoint capabilities are proxied
        if (definition.ImplementationKind != ImplementationKinds.HttpEndpoint)
        {
            sw.Stop();
            return await CreateAndReturnTerminal(
                capabilityId, callerProjectId ?? "unknown", callerTaskId,
                callerAgent, callerMessageId, callerSurface,
                InvocationStatuses.Failed, TruncatePayload(requestJson), requestHash, startedAt,
                errorType: "not_http_endpoint",
                errorMessage: $"Capability '{capabilityId}' has ImplementationKind={definition.ImplementationKind}. Core only proxies http_endpoint.",
                durationMs: (int)sw.ElapsedMilliseconds);
        }

        if (definition.SideEffectLevel != SideEffectLevels.ReadOnly)
        {
            sw.Stop();
            return await CreateAndReturnTerminal(
                capabilityId, callerProjectId ?? "unknown", callerTaskId,
                callerAgent, callerMessageId, callerSurface,
                InvocationStatuses.Failed, TruncatePayload(requestJson), requestHash, startedAt,
                errorType: "non_read_only_rejected",
                errorMessage: $"Capability '{capabilityId}' has side_effect_level={definition.SideEffectLevel}. Core only proxies read_only.",
                durationMs: (int)sw.ElapsedMilliseconds);
        }

        // 5. Create initial queued audit record
        var invocation = await _repo.CreateInvocationAsync(new CapabilityInvocation
        {
            InvocationId = CapabilityRepository.GenerateInvocationId(),
            CapabilityId = capabilityId,
            CapabilityVersion = null,
            CallerAgent = callerAgent,
            CallerProjectId = callerProjectId ?? "unknown",
            CallerTaskId = callerTaskId,
            CallerMessageId = callerMessageId,
            CallerSurface = callerSurface,
            RequestJson = TruncatePayload(requestJson),
            RequestHash = requestHash,
            Status = InvocationStatuses.Queued,
            StartedAt = startedAt,
            DurationMs = (int)sw.ElapsedMilliseconds,
        });

        try
        {
            // 6. Check endpoint is configured
            var endpoint = definition.ServiceEndpoint;
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return await TerminalizeInvocation(invocation.Id,
                    InvocationStatuses.Failed, sw, startedAt,
                    errorType: "no_endpoint",
                    errorMessage: $"Capability '{capabilityId}' has no service_endpoint configured");
            }

            // 7. Build executor envelope
            var deadlineUtc = startedAt.AddMilliseconds(clampedTimeout);
            object? requestObj = null;
            if (requestJson is not null)
            {
                try { requestObj = JsonSerializer.Deserialize<JsonElement>(requestJson); }
                catch { requestObj = requestJson; }
            }

            var envelope = new ExecutorRequestEnvelope
            {
                InvocationId = invocation.InvocationId ?? "",
                CapabilityId = capabilityId,
                CapabilityVersion = null,
                Caller = new ExecutorCaller
                {
                    Agent = callerAgent,
                    ProjectId = callerProjectId,
                    TaskId = callerTaskId,
                },
                SideEffectLevel = definition.SideEffectLevel,
                DeadlineUtc = deadlineUtc.ToString("o"),
                Request = requestObj,
                Safety = new { },
            };

            var envelopeJson = JsonSerializer.Serialize(envelope, JsonOpts);

            // 8. Update to running
            var remainingTimeout = Math.Max(1000, clampedTimeout - (int)sw.ElapsedMilliseconds);

            // 9. Execute via HTTP executor
            var (statusCode, responseBody, execError) = await _executor.ExecuteAsync(endpoint, envelopeJson, remainingTimeout);

            if (execError == "timeout")
            {
                return await TerminalizeInvocation(invocation.Id,
                    InvocationStatuses.TimedOut, sw, startedAt,
                    errorType: "timeout",
                    errorMessage: $"Executor timeout after {clampedTimeout}ms");
            }

            if (execError is not null)
            {
                return await TerminalizeInvocation(invocation.Id,
                    InvocationStatuses.Failed, sw, startedAt,
                    errorType: "executor_error",
                    errorMessage: execError);
            }

            // 10. Parse executor response envelope
            var (execStatus, outputSummary, outputJson, outputArtifactRefs, modelInfo, timingsMs, cost, metadata)
                = ParseExecutorResponse(responseBody);

            if (statusCode < 200 || statusCode >= 300 || execStatus == "failed")
            {
                var errorMsg = $"Executor returned HTTP {statusCode}";
                if (execStatus == "failed")
                    errorMsg = outputSummary ?? errorMsg;

                return await TerminalizeInvocation(invocation.Id,
                    InvocationStatuses.Failed, sw, startedAt,
                    errorType: "executor_error",
                    errorMessage: errorMsg,
                    outputSummary: outputSummary,
                    outputJson: outputJson,
                    outputArtifactRefsJson: outputArtifactRefs,
                    modelProvider: modelInfo?.Provider,
                    modelName: modelInfo?.Name,
                    modelVersion: modelInfo?.Version,
                    timingsMsJson: timingsMs,
                    costJson: cost,
                    metadataJson: metadata);
            }

            // 11. Validate output (basic check — executor should return valid JSON)
            if (outputJson is not null)
            {
                try { JsonDocument.Parse(outputJson); }
                catch
                {
                    return await TerminalizeInvocation(invocation.Id,
                        InvocationStatuses.InvalidOutput, sw, startedAt,
                        errorType: "invalid_output",
                        errorMessage: "Executor returned invalid JSON output",
                        outputSummary: outputSummary,
                        outputJson: outputJson,
                        outputArtifactRefsJson: outputArtifactRefs,
                        modelProvider: modelInfo?.Provider,
                        modelName: modelInfo?.Name,
                        modelVersion: modelInfo?.Version,
                        timingsMsJson: timingsMs,
                        costJson: cost,
                        metadataJson: metadata);
                }
            }

            // 12. Success
            return await TerminalizeInvocation(invocation.Id,
                InvocationStatuses.Completed, sw, startedAt,
                outputSummary: outputSummary,
                outputJson: outputJson,
                outputArtifactRefsJson: outputArtifactRefs,
                modelProvider: modelInfo?.Provider,
                modelName: modelInfo?.Name,
                modelVersion: modelInfo?.Version,
                timingsMsJson: timingsMs,
                costJson: cost,
                metadataJson: metadata);
        }
        catch (Exception ex)
        {
            return await TerminalizeInvocation(invocation.Id,
                InvocationStatuses.Failed, sw, startedAt,
                errorType: "unhandled_error",
                errorMessage: $"Unhandled invocation error: {ex.Message}");
        }
    }

    public async Task<AnalyzeImageResult> AnalyzeImageAsync(
        string imageRef,
        string? question,
        string mode = "general",
        string? callerProjectId = null,
        int? callerTaskId = null,
        string? callerAgent = null,
        bool includeOcr = true,
        bool includeRegions = false,
        int timeoutMs = DefaultTimeouts.AnalyzeImageTimeoutMs)
    {
        // Reject data: URLs and raw base64-like input
        if (IsRawImageInput(imageRef))
        {
            return new AnalyzeImageResult
            {
                Success = false,
                Status = InvocationStatuses.InvalidRequest,
                ErrorType = "invalid_image_input",
                Error = "Image input rejected: data: URLs and raw base64-like strings are not allowed. " +
                        "Use artifact references (file path, artifact URL, or Den artifact ref) instead.",
            };
        }

        // Check request size
        var requestSizeEstimate = (imageRef?.Length ?? 0) + (question?.Length ?? 0);
        if (requestSizeEstimate > MaxImageRequestSize)
        {
            return new AnalyzeImageResult
            {
                Success = false,
                Status = InvocationStatuses.InvalidRequest,
                ErrorType = "request_too_large",
                Error = $"Image request too large ({requestSizeEstimate} bytes). Maximum is {MaxImageRequestSize / (1024 * 1024)}MB.",
            };
        }

        // Build Vision Analyzer V1 request shape
        var analyzeRequest = new AnalyzeImageRequest
        {
            ImageRef = imageRef!,
            Mode = mode,
            Question = question,
            IncludeOcr = includeOcr,
            IncludeRegions = includeRegions,
        };
        var payload = JsonSerializer.Serialize(analyzeRequest, JsonOpts);

        // Delegate to the standard invocation pipeline
        var invocation = await InvokeAsync(
            CapabilityIds.VisionAnalyzeImageV1,
            payload,
            callerProjectId: callerProjectId ?? "unknown",
            callerTaskId: callerTaskId,
            callerAgent: callerAgent,
            timeoutMs: timeoutMs);

        return MapInvocationToAnalyzeResult(invocation);
    }

    // ── Private helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Create a terminal invocation record inline (for cases where the capability
    /// doesn't exist or can't be executed).
    /// </summary>
    private async Task<CapabilityInvocation> CreateAndReturnTerminal(
        string capabilityId, string callerProjectId, int? callerTaskId,
        string? callerAgent, string? callerMessageId, string? callerSurface,
        string status, string? requestJson, string? requestHash,
        DateTime startedAt,
        string? errorType = null, string? errorMessage = null, int? durationMs = null)
    {
        // Create the invocation record directly with a terminal status
        return await _repo.CreateInvocationAsync(new CapabilityInvocation
        {
            InvocationId = CapabilityRepository.GenerateInvocationId(),
            CapabilityId = capabilityId,
            CallerAgent = callerAgent,
            CallerProjectId = callerProjectId,
            CallerTaskId = callerTaskId,
            CallerMessageId = callerMessageId,
            CallerSurface = callerSurface,
            RequestJson = TruncatePayload(requestJson),
            RequestHash = requestHash,
            Status = status,
            StartedAt = startedAt,
            CompletedAt = DateTime.UtcNow,
            DurationMs = durationMs,
            ErrorType = errorType,
            ErrorMessage = errorMessage,
        });
    }

    private async Task<CapabilityInvocation> TerminalizeInvocation(
        int invocationId, string status, Stopwatch sw, DateTime startedAt,
        string? errorType = null, string? errorMessage = null,
        string? outputSummary = null, string? outputJson = null,
        string? outputArtifactRefsJson = null,
        string? modelProvider = null, string? modelName = null, string? modelVersion = null,
        string? timingsMsJson = null, string? costJson = null,
        string? metadataJson = null)
    {
        sw.Stop();
        var completedAt = DateTime.UtcNow;
        var result = await _repo.UpdateInvocationStatusAsync(
            invocationId, status,
            outputSummary: outputSummary,
            errorType: errorType,
            errorMessage: errorMessage,
            durationMs: (int)sw.ElapsedMilliseconds,
            completedAt: completedAt,
            outputJson: outputJson,
            modelProvider: modelProvider,
            modelName: modelName,
            modelVersion: modelVersion,
            timingsMsJson: timingsMsJson,
            costJson: costJson,
            outputArtifactRefsJson: outputArtifactRefsJson,
            metadataJson: metadataJson);

        return result ?? new CapabilityInvocation
        {
            Id = invocationId,
            Status = status,
            ErrorType = errorType,
            ErrorMessage = errorMessage ?? "invocation record not found after terminalize",
            DurationMs = (int)sw.ElapsedMilliseconds,
            CapabilityId = "unknown",
            CallerProjectId = "unknown",
        };
    }

    /// <summary>
    /// Parse the executor response envelope from the response body.
    /// Returns parsed values or defaults.
    /// </summary>
    internal static (string? status, string? outputSummary, string? outputJson, string? outputArtifactRefs,
        ExecutorModelInfo? modelInfo, string? timingsMsJson, string? costJson, string? metadataJson)
        ParseExecutorResponse(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return (null, null, null, null, null, null, null, null);

        try
        {
            var envelope = JsonSerializer.Deserialize<ExecutorResponseEnvelope>(responseBody, JsonOpts);
            if (envelope is null)
                return (null, null, null, null, null, null, null, null);

            var outputArtifactRefs = envelope.OutputArtifactRefs is { Length: > 0 }
                ? JsonSerializer.Serialize(envelope.OutputArtifactRefs)
                : null;
            var timingsMs = envelope.TimingsMs is { Count: > 0 }
                ? JsonSerializer.Serialize(envelope.TimingsMs, JsonOpts)
                : null;
            var cost = envelope.Cost is { Count: > 0 }
                ? JsonSerializer.Serialize(envelope.Cost, JsonOpts)
                : null;
            var metadata = envelope.Metadata is { Count: > 0 }
                ? JsonSerializer.Serialize(envelope.Metadata, JsonOpts)
                : null;

            return (envelope.Status, envelope.OutputSummary, envelope.Output,
                outputArtifactRefs, envelope.Model, timingsMs, cost, metadata);
        }
        catch
        {
            // If we can't parse as envelope, treat the whole response as output
            return ("completed", null, responseBody, null, null, null, null, null);
        }
    }

    private static AnalyzeImageResult MapInvocationToAnalyzeResult(CapabilityInvocation invocation)
    {
        var result = new AnalyzeImageResult
        {
            Success = invocation.Status == InvocationStatuses.Completed,
            Status = invocation.Status,
            InvocationId = invocation.InvocationId,
            DurationMs = invocation.DurationMs,
            ErrorType = invocation.ErrorType,
        };

        if (invocation.Status == InvocationStatuses.Completed)
        {
            // Try to parse the output JSON for description field
            if (invocation.OutputJson is not null)
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<JsonElement>(invocation.OutputJson);
                    if (parsed.TryGetProperty("description", out var desc))
                        result.Description = desc.GetString();
                    if (parsed.TryGetProperty("summary", out var summary))
                        result.Description ??= summary.GetString();
                    result.RawOutput = invocation.OutputJson;
                }
                catch
                {
                    result.RawOutput = invocation.OutputJson;
                }
            }
        }
        else
        {
            result.Error = invocation.ErrorMessage;
        }

        return result;
    }

    /// <summary>
    /// Check if an image reference is a raw base64-like or data: URL that should be rejected.
    /// </summary>
    internal static bool IsRawImageInput(string imageRef)
    {
        if (string.IsNullOrWhiteSpace(imageRef))
            return false;

        // Reject data: URLs
        if (imageRef.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return true;

        // Reject raw base64-like strings (starts with a long base64 prefix)
        if (imageRef.StartsWith("/9j/", StringComparison.Ordinal) ||  // JPEG
            imageRef.StartsWith("iVBOR", StringComparison.Ordinal) || // PNG
            imageRef.StartsWith("R0lG", StringComparison.Ordinal))    // GIF
            return true;

        return false;
    }

    /// <summary>
    /// Truncate payloads that exceed the inline storage limit.
    /// </summary>
    internal static string? TruncatePayload(string? payload)
    {
        if (payload is null) return null;
        return payload.Length <= MaxInlinePayloadSize
            ? payload
            : payload[..MaxInlinePayloadSize] + "... [truncated]";
    }
}
