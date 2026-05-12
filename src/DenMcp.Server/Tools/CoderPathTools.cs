using System.ComponentModel;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Services;
using ModelContextProtocol.Server;

namespace DenMcp.Server.Tools;

[McpServerToolType]
public sealed class CoderPathTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    [McpServerTool(Name = "start_coder_worker_path"), Description("Hermes-facing coder path helper: prepare/reference a coder packet and launch a coder Pi worker through Den state only.")]
    public static async Task<string> StartCoderWorkerPath(
        ITaskRepository tasks,
        IMessageRepository messages,
        IPiSessionService service,
        [Description("Project ID.")] string project_id,
        [Description("Task ID.")] int task_id,
        [Description("Agent/user starting the coder path.")] string requested_by,
        [Description("Optional existing coder packet message id. If omitted, a packet is prepared first.")] int? prompt_packet_message_id = null,
        [Description("Optional branch/worktree guidance.")] string? branch = null,
        [Description("Optional base branch.")] string? base_branch = null,
        [Description("Optional base commit.")] string? base_commit = null,
        [Description("Optional allowed scope guidance.")] string? allowed_scope = null,
        [Description("Optional explicit run id.")] string? run_id = null,
        [Description("Optional explicit session id.")] string? session_id = null,
        [Description("Optional callback ports JSON array.")] string? callback_ports = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var launchJson = await RoleWorkerTools.LaunchCoderWorker(
            tasks,
            messages,
            service,
            project_id,
            task_id,
            requested_by,
            prompt_packet_message_id,
            branch,
            base_branch,
            base_commit,
            allowed_scope,
            notes: "Hermes coder path: worker must post an implementation_packet with branch/head/tests before orchestration proceeds.",
            run_id,
            session_id,
            workspace_id: null,
            model_hint: null,
            provider_hint: null,
            timeout_seconds: null,
            dedupe_key: null,
            callback_ports,
            verbose: true).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(launchJson);
        if (doc.RootElement.TryGetProperty("error", out var error))
            return JsonSerializer.Serialize(new { path_state = "failed_to_launch", error = error.GetString() }, JsonOptions);
        var result = new
        {
            path_state = "launched",
            summary = "coder worker path launched; wait for implementation_packet before review",
            packet_ref = doc.RootElement.GetProperty("packet_ref"),
            worker_run = doc.RootElement.GetProperty("worker_run"),
            next_step = "Poll get_worker_run and get_latest_worker_completion; process exit without implementation_packet is incomplete.",
        };
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool(Name = "verify_coder_worker_completion"), Description("Hermes-facing coder path verifier: decide whether a coder worker completion is sufficient to request review.")]
    public static async Task<string> VerifyCoderWorkerCompletion(
        IMessageRepository messages,
        [Description("Project ID.")] string project_id,
        [Description("Worker run id.")] string run_id,
        [Description("Task ID.")] int task_id,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var completionJson = await CompletionTools.GetLatestWorkerCompletion(
            messages,
            project_id,
            run_id,
            task_id,
            role: "coder",
            verbose: true).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(completionJson);
        var completionState = doc.RootElement.GetProperty("completion_state").GetString() ?? "unknown";
        if (completionState == "missing_packet")
        {
            return JsonSerializer.Serialize(new
            {
                verdict = "incomplete",
                completion_state = completionState,
                checks = new
                {
                    implementation_packet_exists = false,
                    status_completed = false,
                    branch_reported = false,
                    head_commit_reported = false,
                    tests_reported = false,
                },
                recovery_guidance = "Do not request review. Wait for or require a structured implementation_packet; process exit alone is not success."
            }, JsonOptions);
        }

        var completion = doc.RootElement.GetProperty("completion");
        var metadata = completion.GetProperty("metadata");
        var packetType = completion.GetProperty("packet_type").GetString();
        var status = completion.GetProperty("status").GetString();
        var branch = completion.GetProperty("final_repo").GetProperty("branch").GetString();
        var headCommit = completion.GetProperty("final_repo").GetProperty("head_commit").GetString();
        var testsReported = metadata.TryGetProperty("tests_run", out var tests) && tests.ValueKind is JsonValueKind.Array or JsonValueKind.String;
        var implementationPacketExists = packetType == "implementation_packet";
        var statusCompleted = status == "completed";
        var branchReported = !string.IsNullOrWhiteSpace(branch);
        var headReported = !string.IsNullOrWhiteSpace(headCommit);
        var ready = completionState == "present" && implementationPacketExists && statusCompleted && branchReported && headReported && testsReported;

        return JsonSerializer.Serialize(new
        {
            verdict = ready ? "ready_for_review" : "incomplete",
            completion_state = completionState,
            completion,
            checks = new
            {
                implementation_packet_exists = implementationPacketExists,
                status_completed = statusCompleted,
                branch_reported = branchReported,
                head_commit_reported = headReported,
                tests_reported = testsReported,
            },
            recovery_guidance = ready
                ? "Implementation packet has branch/head/test metadata; orchestrator may verify the diff and request review."
                : "Do not request review yet. Require completed implementation_packet with branch, head commit, and tests run or skipped-with-explanation."
        }, JsonOptions);
    }
}
