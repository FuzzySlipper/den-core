using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DenMcp.Core.Mcp;

/// <summary>
/// Authoritative runtime registry for MCP tool profile/bundle classification.
/// Core owns this model. It maps every known tool name to the bundles and
/// profiles that include it, and provides filtering helpers for scoped
/// tools/list and tools/call enforcement.
/// </summary>
public sealed class McpToolProfileRegistry
{
    // tool name -> bundles it belongs to
    private readonly Dictionary<string, HashSet<string>> _toolBundles = new(StringComparer.Ordinal);

    // profile name -> computed set of tool names
    private readonly Dictionary<string, HashSet<string>> _profileTools = new(StringComparer.Ordinal);

    // bundle name -> computed set of tool names
    private readonly Dictionary<string, HashSet<string>> _bundleTools = new(StringComparer.Ordinal);

    private readonly HashSet<string> _allToolNames = new(StringComparer.Ordinal);

    public IReadOnlySet<string> AllToolNames => _allToolNames;

    public IReadOnlyCollection<string> KnownProfiles => _profileTools.Keys;
    public IReadOnlyCollection<string> KnownBundles => _bundleTools.Keys;

    public IReadOnlySet<string> GetProfileTools(string profile)
    {
        if (_profileTools.TryGetValue(profile, out var tools))
            return tools;
        return new HashSet<string>(StringComparer.Ordinal);
    }

    public IReadOnlySet<string> GetBundleTools(string bundle)
    {
        if (_bundleTools.TryGetValue(bundle, out var tools))
            return tools;
        return new HashSet<string>(StringComparer.Ordinal);
    }

    public bool IsKnownProfile(string profile) => _profileTools.ContainsKey(profile);
    public bool IsKnownBundle(string bundle) => _bundleTools.ContainsKey(bundle);

    public IReadOnlySet<string> GetToolBundles(string toolName) =>
        _toolBundles.TryGetValue(toolName, out var bundles) ? bundles : new HashSet<string>(StringComparer.Ordinal);

    public bool ToolInBundle(string toolName, string bundle) =>
        _toolBundles.TryGetValue(toolName, out var bundles) && bundles.Contains(bundle);

    public bool ToolInProfile(string toolName, string profile) =>
        _profileTools.TryGetValue(profile, out var tools) && tools.Contains(toolName);

    /// <summary>
    /// Build a filtered tool set from request-scoped selectors.
    /// </summary>
    /// <param name="profile">Optional profile name. When null/empty, all tools are returned unless bundles are specified.</param>
    /// <param name="bundles">Optional bundle names. When provided, the union of these bundles is used.</param>
    /// <param name="error">Set when selectors are invalid.</param>
    /// <returns>The allowed tool names, or null if an error occurred.</returns>
    public HashSet<string>? ComputeAllowedTools(string? profile, string[]? bundles, out string? error)
    {
        error = null;
        var allowed = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(profile))
        {
            var p = profile!.Trim().ToLowerInvariant();
            if (!_profileTools.ContainsKey(p))
            {
                error = $"Unknown tool profile: '{p}'. Known profiles: {string.Join(", ", _profileTools.Keys.OrderBy(k => k))}.";
                return null;
            }
            allowed.UnionWith(_profileTools[p]);
        }

        if (bundles is { Length: > 0 })
        {
            foreach (var b in bundles)
            {
                var bn = b.Trim().ToLowerInvariant();
                if (!_bundleTools.ContainsKey(bn))
                {
                    error = $"Unknown tool bundle: '{bn}'. Known bundles: {string.Join(", ", _bundleTools.Keys.OrderBy(k => k))}.";
                    return null;
                }
                allowed.UnionWith(_bundleTools[bn]);
            }
        }

        // If neither profile nor bundles specified, allow everything (full-compatible behavior)
        if (allowed.Count == 0 && string.IsNullOrWhiteSpace(profile) && (bundles is null || bundles.Length == 0))
        {
            allowed.UnionWith(_allToolNames);
        }

        return allowed;
    }

    /// <summary>
    /// Create the canonical registry from an explicit manifest.
    /// </summary>
    public static McpToolProfileRegistry CreateDefault()
    {
        var registry = new McpToolProfileRegistry();
        registry.BuildFromManifest();
        return registry;
    }

    /// <summary>
    /// Scan the given assembly for methods annotated with both
    /// <see cref="McpServerToolAttribute"/> and our profile/bundle attributes,
    /// then validate that the runtime manifest matches the annotations.
    /// Returns a list of mismatch descriptions (empty when consistent).
    /// </summary>
    public List<string> ValidateAgainstAssembly(Assembly assembly)
    {
        var mismatches = new List<string>();
        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            {
                var toolAttr = method.GetCustomAttributesData()
                    .FirstOrDefault(a => a.AttributeType.FullName == "ModelContextProtocol.Server.McpServerToolAttribute");
                if (toolAttr is null)
                    continue;

                var nameArg = toolAttr.ConstructorArguments.FirstOrDefault();
                var nameProp = toolAttr.NamedArguments.FirstOrDefault(na => na.MemberName == "Name");
                string? toolName = nameProp.TypedValue.Value as string ?? nameArg.Value as string;
                if (string.IsNullOrEmpty(toolName))
                    continue;

                var profileAttr = method.GetCustomAttribute<McpToolProfileAttribute>();
                var bundleAttr = method.GetCustomAttribute<McpToolBundleAttribute>();

                if (!_allToolNames.Contains(toolName))
                {
                    mismatches.Add($"Tool '{toolName}' annotated in assembly but missing from registry manifest.");
                    continue;
                }

                // Validate bundles
                if (bundleAttr is not null)
                {
                    var expectedBundles = new HashSet<string>(bundleAttr.Bundles, StringComparer.Ordinal);
                    var actualBundles = _toolBundles.GetValueOrDefault(toolName) ?? new HashSet<string>(StringComparer.Ordinal);
                    var missing = expectedBundles.Except(actualBundles).ToList();
                    var extra = actualBundles.Except(expectedBundles).ToList();
                    if (missing.Count > 0 || extra.Count > 0)
                    {
                        mismatches.Add($"Tool '{toolName}' bundle mismatch: annotated=[{string.Join(",", bundleAttr.Bundles)}], registry=[{string.Join(",", actualBundles)}].");
                    }
                }
                else if (_toolBundles.TryGetValue(toolName, out var registryBundles) && registryBundles.Count > 0)
                {
                    mismatches.Add($"Tool '{toolName}' has bundles in registry but no [McpToolBundle] attribute.");
                }

                // Validate profiles
                if (profileAttr is not null)
                {
                    var expectedProfiles = new HashSet<string>(profileAttr.Profiles, StringComparer.Ordinal);
                    var actualProfiles = _profileTools.Keys.Where(p => _profileTools[p].Contains(toolName)).ToHashSet(StringComparer.Ordinal);
                    var missing = expectedProfiles.Except(actualProfiles).ToList();
                    var extra = actualProfiles.Except(expectedProfiles).ToList();
                    if (missing.Count > 0 || extra.Count > 0)
                    {
                        mismatches.Add($"Tool '{toolName}' profile mismatch: annotated=[{string.Join(",", profileAttr.Profiles)}], registry=[{string.Join(",", actualProfiles)}].");
                    }
                }
            }
        }
        return mismatches;
    }

    private void BuildFromManifest()
    {
        // Register every tool with its bundles, then define profiles as unions of bundles + overrides.
        var m = new ManifestBuilder(this);

        // ---- core bundle ----
        m.Add(McpToolBundles.Core,
            "create_project", "get_project", "list_projects",
            "create_space", "get_space", "list_spaces",
            "update_space_visibility", "archive_space",
            "list_active_agents", "list_agent_instance_bindings");

        // ---- task bundle ----
        m.Add(McpToolBundles.Task,
            "create_task", "update_task", "get_task", "get_task_workflow_summary",
            "list_tasks", "next_task", "add_dependency", "remove_dependency");

        // ---- review bundle ----
        m.Add(McpToolBundles.Review,
            "create_review_round", "list_review_rounds", "set_review_verdict",
            "create_review_finding", "list_review_findings",
            "respond_to_review_finding", "set_review_finding_status",
            "request_review", "post_review_findings",
            "split_review_findings_to_follow_up");

        // ---- messaging bundle ----
        m.Add(McpToolBundles.Messaging,
            "send_message", "get_messages", "get_thread", "mark_read", "send_user_notification");

        // ---- document bundle ----
        m.Add(McpToolBundles.Document,
            "store_document", "get_document", "list_documents", "search_documents", "delete_document",
            "query_librarian");

        // ---- blackboard bundle ----
        m.Add(McpToolBundles.Blackboard,
            "store_blackboard_entry", "get_blackboard_entry", "list_blackboard_entries",
            "delete_blackboard_entry", "cleanup_blackboard_entries");

        // ---- agent guidance bundle ----
        m.Add(McpToolBundles.Agent,
            "add_agent_guidance_entry", "delete_agent_guidance_entry",
            "get_agent_guidance", "list_agent_guidance_entries");

        // ---- agent-stream bundle ----
        m.Add(McpToolBundles.AgentStream,
            "get_agent_stream_entry", "list_agent_stream");

        // ---- worker bundle ----
        m.Add(McpToolBundles.Worker,
            "register_worker_run", "get_worker_run", "get_worker_run_status",
            "list_worker_runs", "cleanup_worker_run", "abort_worker_run", "rerun_worker_run",
            "get_latest_worker_completion", "post_worker_completion_packet");

        // ---- packet bundle ----
        m.Add(McpToolBundles.Packet,
            "get_latest_task_packet",
            "prepare_coder_context_packet", "prepare_reviewer_context_packet",
            "prepare_validator_context_packet", "prepare_drift_checker_context_packet",
            "prepare_packet_auditor_context_packet",
            "render_worker_prompt");

        // ---- orchestrator bundle ----
        m.Add(McpToolBundles.Orchestrator,
            "determine_orchestrator_next_action");

        // ---- topics bundle ----
        m.Add(McpToolBundles.Topics,
            "create_topic", "delete_topic", "get_topic", "list_topics", "update_topic", "validate_topic_tags",
            "append_topic_clip", "claim_topic_clip_batch", "cleanup_topic_clip_raw_content",
            "complete_topic_clips", "discard_topic_clips", "escalate_topic_clips",
            "list_curation_decisions", "list_topic_clips");

        // ---- legacy bundle ----
        m.Add(McpToolBundles.Legacy,
            "legacy_start_coder_worker_path", "legacy_verify_coder_worker_completion",
            "legacy_start_reviewer_worker_path", "legacy_verify_reviewer_worker_completion",
            "legacy_launch_coder_worker", "legacy_launch_reviewer_worker",
            "legacy_launch_validator_worker", "legacy_launch_drift_checker_worker",
            "legacy_launch_packet_auditor_worker",
            "legacy_launch_pi_worker",
            "legacy_approve_dispatch", "legacy_complete_dispatch", "legacy_get_dispatch",
            "legacy_list_dispatches", "legacy_reject_dispatch",
            "legacy_request_den_publish_dry_run",
            "legacy_publish_reviewed_branch", "legacy_publish_worker_branch");

        // ---- diagnostics bundle ----
        m.Add(McpToolBundles.Diagnostics,
            "send_agent_stream_message");

        // ---- discussion bundle ----
        m.Add(McpToolBundles.Discussion,
            "get_document_discussion", "comment_on_document",
            "list_discussion_threads", "get_discussion_thread",
            "create_discussion_comment");

        AddPublicSpecBundles();

        // ---- profile: planner ----
        // core + task + messaging + document + blackboard + agent + agent-stream + orchestrator + worker(read) + packet(read) + discussion
        m.Profile(McpToolProfiles.Planner,
            McpToolBundles.Core,
            McpToolBundles.Task,
            McpToolBundles.Messaging,
            McpToolBundles.Document,
            McpToolBundles.Blackboard,
            McpToolBundles.Agent,
            McpToolBundles.AgentStream,
            McpToolBundles.Orchestrator,
            McpToolBundles.Discussion);
        // worker read-only subset for planner
        m.ProfileAdd(McpToolProfiles.Planner,
            "get_worker_run", "get_worker_run_status", "list_worker_runs",
            "get_latest_worker_completion");
        // packet read-only subset for planner
        m.ProfileAdd(McpToolProfiles.Planner,
            "get_latest_task_packet", "render_worker_prompt");

        // ---- profile: runner ----
        // core + task + review + messaging + document + blackboard + agent + agent-stream + worker + packet(coder+validator) + orchestrator + discussion
        m.Profile(McpToolProfiles.Runner,
            McpToolBundles.Core,
            McpToolBundles.Task,
            McpToolBundles.Review,
            McpToolBundles.Messaging,
            McpToolBundles.Document,
            McpToolBundles.Blackboard,
            McpToolBundles.Agent,
            McpToolBundles.AgentStream,
            McpToolBundles.Worker,
            McpToolBundles.Orchestrator,
            McpToolBundles.Discussion);
        // runner-specific packet subset
        m.ProfileAdd(McpToolProfiles.Runner,
            "get_latest_task_packet", "render_worker_prompt",
            "prepare_coder_context_packet", "prepare_validator_context_packet");

        // ---- profile: admin-current ----
        // everything except legacy and diagnostics
        m.Profile(McpToolProfiles.AdminCurrent,
            McpToolBundles.Core,
            McpToolBundles.Task,
            McpToolBundles.Review,
            McpToolBundles.Messaging,
            McpToolBundles.Document,
            McpToolBundles.Blackboard,
            McpToolBundles.Agent,
            McpToolBundles.AgentStream,
            McpToolBundles.Worker,
            McpToolBundles.Packet,
            McpToolBundles.Orchestrator,
            McpToolBundles.Topics,
            McpToolBundles.Discussion);

        // ---- profile: legacy-full ----
        m.Profile(McpToolProfiles.LegacyFull,
            McpToolBundles.Legacy);
        m.ProfileAdd(McpToolProfiles.LegacyFull,
            "send_agent_stream_message");

        // ---- profile: worker-coder ----
        // Coder workers should receive any relevant discussion context in their
        // orchestrator packet rather than fetching discussion threads directly.
        m.Profile(McpToolProfiles.WorkerCoder,
            McpToolBundles.Core,
            McpToolBundles.Task,
            McpToolBundles.Messaging,
            McpToolBundles.Document,
            McpToolBundles.Agent,
            McpToolBundles.Worker);
        m.ProfileAdd(McpToolProfiles.WorkerCoder,
            "get_task_workflow_summary", "list_tasks", "get_task",
            "list_review_rounds", "list_review_findings",
            "get_latest_task_packet",
            "prepare_coder_context_packet", "render_worker_prompt");

        // ---- profile: worker-reviewer ----
        m.Profile(McpToolProfiles.WorkerReviewer,
            McpToolBundles.Core,
            McpToolBundles.Task,
            McpToolBundles.Review,
            McpToolBundles.Messaging,
            McpToolBundles.Document,
            McpToolBundles.Agent,
            McpToolBundles.Worker);
        m.ProfileAdd(McpToolProfiles.WorkerReviewer,
            "get_task_workflow_summary", "list_tasks", "get_task",
            "get_latest_task_packet",
            "prepare_reviewer_context_packet", "render_worker_prompt",
            "get_document_discussion", "list_discussion_threads", "get_discussion_thread");

        // ---- profile: worker-validator ----
        // Narrow profile: packet retrieval, status, completion, and role-specific context packet helpers only.
        // No legacy tools, no governance/admin tools, no messaging/agent-stream/topics/orchestrator.
        m.ProfileAdd(McpToolProfiles.WorkerValidator,
            "get_project", "list_projects",
            "get_space", "list_spaces",
            "get_task", "list_tasks", "get_task_workflow_summary",
            "get_document", "list_documents", "search_documents", "query_librarian",
            "get_worker_run", "get_worker_run_status", "list_worker_runs",
            "get_latest_worker_completion", "post_worker_completion_packet",
            "get_latest_task_packet",
            "prepare_validator_context_packet", "render_worker_prompt");

        // ---- profile: worker-drift-checker ----
        // Narrow profile: same tool scope as worker-validator with drift-checker-specific packet.
        m.ProfileAdd(McpToolProfiles.WorkerDriftChecker,
            "get_project", "list_projects",
            "get_space", "list_spaces",
            "get_task", "list_tasks", "get_task_workflow_summary",
            "get_document", "list_documents", "search_documents", "query_librarian",
            "get_worker_run", "get_worker_run_status", "list_worker_runs",
            "get_latest_worker_completion", "post_worker_completion_packet",
            "get_latest_task_packet",
            "prepare_drift_checker_context_packet", "render_worker_prompt");

        // ---- profile: worker-packet-auditor ----
        // Narrow profile: same tool scope as worker-validator with packet-auditor-specific packet.
        m.ProfileAdd(McpToolProfiles.WorkerPacketAuditor,
            "get_project", "list_projects",
            "get_space", "list_spaces",
            "get_task", "list_tasks", "get_task_workflow_summary",
            "get_document", "list_documents", "search_documents", "query_librarian",
            "get_worker_run", "get_worker_run_status", "list_worker_runs",
            "get_latest_worker_completion", "post_worker_completion_packet",
            "get_latest_task_packet",
            "prepare_packet_auditor_context_packet", "render_worker_prompt");

        // ---- profile: curator ----
        m.Profile(McpToolProfiles.Curator,
            McpToolBundles.Topics);
        m.ProfileAdd(McpToolProfiles.Curator,
            "list_projects", "get_project",
            "list_spaces", "get_space",
            "update_space_visibility", "archive_space");

        // ---- profile: diagnostics ----
        m.Profile(McpToolProfiles.Diagnostics,
            McpToolBundles.AgentStream,
            McpToolBundles.Diagnostics);
        m.ProfileAdd(McpToolProfiles.Diagnostics,
            "list_active_agents", "list_agent_instance_bindings",
            "get_agent_guidance", "list_agent_guidance_entries");

        // ---- delete_space: registered as core bundle member but restricted to admin-current only ----
        // The annotation [McpToolBundle("core")] requires core in the tool's bundle map,
        // but [McpToolProfile("admin-current")] restricts access. We register the bundle
        // mapping directly without adding to the core bundle's tool set, then add it
        // explicitly to the admin-current profile.
        if (!_toolBundles.ContainsKey("delete_space"))
            _toolBundles["delete_space"] = new HashSet<string>(StringComparer.Ordinal) { "core" };
        if (!_profileTools.TryGetValue("admin-current", out var adminProfile))
        {
            adminProfile = new HashSet<string>(StringComparer.Ordinal);
            _profileTools["admin-current"] = adminProfile;
        }
        adminProfile.Add("delete_space");

        // ---- update_discussion_thread: registered as discussion bundle member but restricted to admin-current only ----
        // The annotation [McpToolBundle("discussion")] requires discussion in the tool's bundle map,
        // but [McpToolProfile("admin-current")] restricts access. We register the bundle
        // mapping directly without adding to the discussion bundle's tool set, then add it
        // explicitly to the admin-current profile.
        if (!_toolBundles.ContainsKey("update_discussion_thread"))
            _toolBundles["update_discussion_thread"] = new HashSet<string>(StringComparer.Ordinal) { "discussion" };
        if (!_profileTools.TryGetValue("admin-current", out var adminProfile2))
        {
            adminProfile2 = new HashSet<string>(StringComparer.Ordinal);
            _profileTools["admin-current"] = adminProfile2;
        }
        adminProfile2.Add("update_discussion_thread");

        // Freeze all computed sets
        foreach (var k in _toolBundles.Keys.ToList())
            _toolBundles[k] = new HashSet<string>(_toolBundles[k], StringComparer.Ordinal);
        foreach (var k in _bundleTools.Keys.ToList())
            _bundleTools[k] = new HashSet<string>(_bundleTools[k], StringComparer.Ordinal);
        foreach (var k in _profileTools.Keys.ToList())
            _profileTools[k] = new HashSet<string>(_profileTools[k], StringComparer.Ordinal);

        _allToolNames.UnionWith(_toolBundles.Keys);
    }

    private void EnsureBundle(string bundle)
    {
        if (!_bundleTools.ContainsKey(bundle))
            _bundleTools[bundle] = new HashSet<string>(StringComparer.Ordinal);
    }

    private void AddPublicSpecBundles()
    {
        AddVirtualBundle(McpToolBundles.CoreRead,
            "list_projects", "get_project",
            "list_spaces", "get_space",
            "list_tasks", "get_task", "get_task_workflow_summary", "next_task",
            "list_documents", "get_document", "search_documents", "query_librarian",
            "get_messages", "get_thread",
            "list_review_rounds", "list_review_findings",
            "get_agent_guidance", "list_agent_guidance_entries",
            "list_blackboard_entries", "get_blackboard_entry");

        AddVirtualBundle(McpToolBundles.CoreWrite,
            "create_task", "update_task", "add_dependency", "remove_dependency",
            "store_document", "delete_document",
            "send_message", "send_user_notification", "mark_read",
            "store_blackboard_entry", "delete_blackboard_entry");

        AddVirtualBundle(McpToolBundles.Planning,
            "list_active_agents", "get_task_workflow_summary",
            "list_agent_stream", "get_agent_stream_entry",
            "list_agent_instance_bindings",
            "list_blackboard_entries", "get_blackboard_entry", "store_blackboard_entry");

        AddVirtualBundle(McpToolBundles.WorkerWorkflow,
            "register_worker_run", "post_worker_completion_packet",
            "get_latest_worker_completion", "get_latest_task_packet",
            "prepare_coder_context_packet", "prepare_reviewer_context_packet",
            "prepare_validator_context_packet", "prepare_drift_checker_context_packet", "prepare_packet_auditor_context_packet",
            "render_worker_prompt", "determine_orchestrator_next_action",
            "list_worker_runs", "get_worker_run", "get_worker_run_status",
            "cleanup_worker_run", "abort_worker_run", "rerun_worker_run");

        AddVirtualBundle(McpToolBundles.Curation,
            "append_topic_clip", "claim_topic_clip_batch", "complete_topic_clips", "discard_topic_clips", "escalate_topic_clips",
            "list_topic_clips", "list_curation_decisions",
            "create_topic", "update_topic", "delete_topic", "get_topic", "list_topics", "validate_topic_tags",
            "cleanup_topic_clip_raw_content");

        AddVirtualBundle(McpToolBundles.GovernanceAdmin,
            "create_project", "create_space",
            "add_agent_guidance_entry", "delete_agent_guidance_entry",
            "delete_document", "delete_blackboard_entry", "delete_topic",
            "cleanup_blackboard_entries", "list_agent_instance_bindings");

        var current = _toolBundles.Keys
            .Where(tool => !(_bundleTools.TryGetValue(McpToolBundles.Legacy, out var legacy) && legacy.Contains(tool)))
            .Where(tool => tool != "send_agent_stream_message")
            .ToArray();
        AddVirtualBundle(McpToolBundles.AllCurrent, current);
        AddVirtualBundle(McpToolBundles.AllWithLegacy, _toolBundles.Keys.ToArray());
    }

    private void AddVirtualBundle(string bundle, params string[] toolNames)
    {
        EnsureBundle(bundle);
        foreach (var toolName in toolNames)
        {
            if (!_toolBundles.ContainsKey(toolName))
                throw new InvalidOperationException($"Virtual MCP tool bundle '{bundle}' references unknown tool '{toolName}'.");
            _bundleTools[bundle].Add(toolName);
        }
    }

    private void AddToolToBundle(string toolName, string bundle)
    {
        EnsureBundle(bundle);
        _bundleTools[bundle].Add(toolName);
        if (!_toolBundles.TryGetValue(toolName, out var bundles))
        {
            bundles = new HashSet<string>(StringComparer.Ordinal);
            _toolBundles[toolName] = bundles;
        }
        bundles.Add(bundle);
    }

    private sealed class ManifestBuilder
    {
        private readonly McpToolProfileRegistry _r;
        public ManifestBuilder(McpToolProfileRegistry r) => _r = r;

        public void Add(string bundle, params string[] toolNames)
        {
            foreach (var t in toolNames)
                _r.AddToolToBundle(t, bundle);
        }

        public void Profile(string profile, params string[] bundles)
        {
            if (!_r._profileTools.TryGetValue(profile, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                _r._profileTools[profile] = set;
            }
            foreach (var b in bundles)
            {
                if (_r._bundleTools.TryGetValue(b, out var tools))
                    set.UnionWith(tools);
            }
        }

        public void ProfileAdd(string profile, params string[] toolNames)
        {
            if (!_r._profileTools.TryGetValue(profile, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                _r._profileTools[profile] = set;
            }
            foreach (var t in toolNames)
                set.Add(t);
        }
    }
}
