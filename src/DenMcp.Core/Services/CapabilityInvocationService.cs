using System.Diagnostics;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Core.Services;

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
        string callerProjectId,
        string? callerTaskId,
        string callerIdentity,
        string? payload,
        int timeoutMs = 30000);

    /// <summary>
    /// Convenience wrapper for vision.analyze_image.v1.
    /// Validates image input before delegating to the invocation pipeline.
    /// Returns structured AnalyzeImageResult (not raw exceptions).
    /// </summary>
    Task<AnalyzeImageResult> AnalyzeImageAsync(
        string imageRef,
        string callerProjectId,
        string? callerTaskId,
        string callerIdentity,
        string? prompt = null,
        string? model = null,
        int? maxTokens = null,
        int timeoutMs = 60000);
}

public sealed class CapabilityInvocationService : ICapabilityInvocationService
{
    private readonly ICapabilityRepository _repo;
    private readonly IHttpExecutorClient _executor;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // Max request/response payload size to store directly in audit record (100KB)
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
        string callerProjectId,
        string? callerTaskId,
        string callerIdentity,
        string? payload,
        int timeoutMs = 30000)
    {
        // Clamp timeout
        var clampedTimeout = Math.Clamp(timeoutMs, 1000, 120000);
        var sw = Stopwatch.StartNew();

        // 1. Look up capability definition first (before creating invocation)
        var definition = await _repo.GetDefinitionAsync(capabilityId);
        if (definition is null)
        {
            // No definition found — return a terminal invocation record
            // created inline to avoid FK dependency issues on the audit table
            sw.Stop();
            return new CapabilityInvocation
            {
                Id = 0,
                CapabilityId = capabilityId,
                CallerProjectId = callerProjectId,
                CallerTaskId = callerTaskId,
                CallerIdentity = callerIdentity,
                Status = InvocationStatuses.InvalidRequest,
                RequestPayload = TruncatePayload(payload),
                ErrorMessage = $"Capability '{capabilityId}' not found",
                DurationMs = (int)sw.ElapsedMilliseconds,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
        }

        // Create initial invocation record (pending status for in-progress calls)
        var invocation = await _repo.CreateInvocationAsync(new CapabilityInvocation
        {
            CapabilityId = capabilityId,
            CallerProjectId = callerProjectId,
            CallerTaskId = callerTaskId,
            CallerIdentity = callerIdentity,
            Status = "pending",
            RequestPayload = TruncatePayload(payload),
        });

        try
        {
            // 2. Check status
            if (definition.Status != CapabilityStatuses.Enabled)
            {
                return await TerminalizeInvocation(invocation.Id,
                    InvocationStatuses.Disabled, null,
                    $"Capability '{capabilityId}' is {definition.Status}", sw);
            }

            // 3. Check read-only / proxiable
            // Core only proxies synchronous HTTP calls for:
            //   - executor_kind == "http_endpoint"
            //   - side_effect_level == "none"
            if (definition.ExecutorKind != "http_endpoint" || definition.SideEffectLevel != SideEffectLevels.None)
            {
                return await TerminalizeInvocation(invocation.Id,
                    InvocationStatuses.NonReadOnlyRejected, null,
                    $"Capability '{capabilityId}' is not a read-only http_endpoint (executor={definition.ExecutorKind}, side_effect={definition.SideEffectLevel}). Core defers non-read-only capabilities to external executors.",
                    sw);
            }

            // 4. Check endpoint is configured
            var endpoint = definition.HttpEndpoint;
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return await TerminalizeInvocation(invocation.Id,
                    InvocationStatuses.InvalidRequest, null,
                    $"Capability '{capabilityId}' has no http_endpoint configured", sw);
            }

            // 5. Execute via HTTP executor
            var remainingTimeout = Math.Max(1000, clampedTimeout - (int)sw.ElapsedMilliseconds);
            var (statusCode, responseBody, execError) = await _executor.ExecuteAsync(endpoint, payload, remainingTimeout);

            if (execError == "timeout")
            {
                return await TerminalizeInvocation(invocation.Id,
                    InvocationStatuses.Timeout, null, $"Executor timeout after {clampedTimeout}ms", sw);
            }

            if (execError is not null)
            {
                return await TerminalizeInvocation(invocation.Id,
                    InvocationStatuses.ExecutorFailure, null, execError, sw);
            }

            // 6. Validate response (2xx is success)
            if (statusCode < 200 || statusCode >= 300)
            {
                return await TerminalizeInvocation(invocation.Id,
                    InvocationStatuses.ExecutorFailure, TruncatePayload(responseBody),
                    $"Executor returned HTTP {statusCode}", sw);
            }

            // Success
            return await TerminalizeInvocation(invocation.Id,
                InvocationStatuses.Success, TruncatePayload(responseBody), null, sw);
        }
        catch (Exception ex)
        {
            return await TerminalizeInvocation(invocation.Id,
                InvocationStatuses.ExecutorFailure, null,
                $"Unhandled invocation error: {ex.Message}", sw);
        }
    }

    public async Task<AnalyzeImageResult> AnalyzeImageAsync(
        string imageRef,
        string callerProjectId,
        string? callerTaskId,
        string callerIdentity,
        string? prompt = null,
        string? model = null,
        int? maxTokens = null,
        int timeoutMs = 60000)
    {
        // Reject data: URLs and raw base64-like input
        if (IsRawImageInput(imageRef))
        {
            return new AnalyzeImageResult
            {
                Success = false,
                Status = InvocationStatuses.InvalidRequest,
                Error = "Image input rejected: data: URLs and raw base64-like strings are not allowed. " +
                        "Use artifact references (file path, artifact URL, or Den artifact ref) instead.",
            };
        }

        // Check request size
        var requestSizeEstimate = (imageRef?.Length ?? 0) + (prompt?.Length ?? 0);
        if (requestSizeEstimate > MaxImageRequestSize)
        {
            return new AnalyzeImageResult
            {
                Success = false,
                Status = InvocationStatuses.InvalidRequest,
                Error = $"Image request too large ({requestSizeEstimate} bytes). Maximum is {MaxImageRequestSize / (1024 * 1024)}MB.",
            };
        }

        // Build the capability request payload
        var analyzeRequest = new AnalyzeImageRequest
        {
            ImageRef = imageRef!,
            Prompt = prompt,
            Model = model,
            MaxTokens = maxTokens,
        };
        var payload = JsonSerializer.Serialize(analyzeRequest, JsonOpts);

        // Delegate to the standard invocation pipeline
        var invocation = await InvokeAsync(
            CapabilityIds.VisionAnalyzeImageV1,
            callerProjectId,
            callerTaskId,
            callerIdentity,
            payload,
            timeoutMs);

        return MapInvocationToAnalyzeResult(invocation);
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private async Task<CapabilityInvocation> TerminalizeInvocation(
        int invocationId, string status, string? responsePayload,
        string? errorMessage, Stopwatch sw)
    {
        sw.Stop();
        var result = await _repo.UpdateInvocationStatusAsync(
            invocationId, status, responsePayload, errorMessage, (int)sw.ElapsedMilliseconds);

        return result ?? new CapabilityInvocation
        {
            Id = invocationId,
            Status = status,
            ErrorMessage = errorMessage ?? "invocation record not found after terminalize",
            DurationMs = (int)sw.ElapsedMilliseconds,
            CapabilityId = "unknown",
            CallerProjectId = "unknown",
            CallerIdentity = "unknown",
        };
    }

    private static AnalyzeImageResult MapInvocationToAnalyzeResult(CapabilityInvocation invocation)
    {
        var result = new AnalyzeImageResult
        {
            Success = invocation.Status == InvocationStatuses.Success,
            Status = invocation.Status,
            DurationMs = invocation.DurationMs,
        };

        if (invocation.Status == InvocationStatuses.Success)
        {
            // Try to parse the response payload
            if (invocation.ResponsePayload is not null)
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<JsonElement>(invocation.ResponsePayload);
                    if (parsed.TryGetProperty("description", out var desc))
                        result.Description = desc.GetString();
                    result.RawOutput = invocation.ResponsePayload;
                }
                catch
                {
                    result.RawOutput = invocation.ResponsePayload;
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
        // Common base64 image prefixes
        if (imageRef.StartsWith("/9j/", StringComparison.Ordinal) ||  // JPEG
            imageRef.StartsWith("iVBOR", StringComparison.Ordinal) || // PNG
            imageRef.StartsWith("R0lG", StringComparison.Ordinal))    // GIF
            return true;

        return false;
    }

    /// <summary>
    /// Truncate payloads that exceed the inline storage limit.
    /// Large payloads should use artifact references instead.
    /// </summary>
    private static string? TruncatePayload(string? payload)
    {
        if (payload is null) return null;
        return payload.Length <= MaxInlinePayloadSize
            ? payload
            : payload[..MaxInlinePayloadSize] + "... [truncated]";
    }
}
