namespace DenMcp.Core.Mcp;

/// <summary>
/// Well-known MCP tool profile names used for request-scoped filtering.
/// A profile is a curated set of tools appropriate for a specific agent role.
/// </summary>
public static class McpToolProfiles
{
    public const string Planner = "planner";
    public const string Runner = "runner";
    public const string AdminCurrent = "admin-current";
    public const string LegacyFull = "legacy-full";
    public const string WorkerCoder = "worker-coder";
    public const string WorkerReviewer = "worker-reviewer";
    public const string WorkerValidator = "worker-validator";
    public const string WorkerDriftChecker = "worker-drift-checker";
    public const string WorkerPacketAuditor = "worker-packet-auditor";
    public const string Curator = "curator";
    public const string Diagnostics = "diagnostics";
}

/// <summary>
/// Well-known MCP tool bundle names. A bundle is a functional grouping of tools.
/// Profiles are composed of bundles plus explicit inclusions/exclusions.
/// </summary>
public static class McpToolBundles
{
    public const string Core = "core";
    public const string Task = "task";
    public const string Review = "review";
    public const string Messaging = "messaging";
    public const string Document = "document";
    public const string Blackboard = "blackboard";
    public const string Agent = "agent";
    public const string AgentStream = "agent-stream";
    public const string Worker = "worker";
    public const string Packet = "packet";
    public const string Orchestrator = "orchestrator";
    public const string Topics = "topics";
    public const string Legacy = "legacy";
    public const string Diagnostics = "diagnostics";
    public const string Discussion = "discussion";
    public const string WorkerPool = "worker-pool";
    public const string Capability = "capability";

    // Public bundle selector names from the MCP profile/bundle model spec.
    // These are virtual bundles composed from the finer-grained implementation
    // bundles above, and are accepted by tool_bundles request selectors.
    public const string CoreRead = "core-read";
    public const string CoreWrite = "core-write";
    public const string Planning = "planning";
    public const string WorkerWorkflow = "worker-workflow";
    public const string Curation = "curation";
    public const string GovernanceAdmin = "governance-admin";
    public const string AllCurrent = "all-current";
    public const string AllWithLegacy = "all-with-legacy";
}
