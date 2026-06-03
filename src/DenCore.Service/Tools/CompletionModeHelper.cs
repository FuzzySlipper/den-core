namespace DenCore.Service.Tools;

/// <summary>
/// Shared helper for completion reporting mode normalization logic.
/// Extracted from duplicate copies in PacketTools and WorkerTools.
/// </summary>
internal static class CompletionModeHelper
{
    /// <summary>
    /// Normalize a completion reporting mode string.
    /// Default is "worker_mcp_tool". Known values: "artifact_reconciled".
    /// </summary>
    internal static string NormalizeCompletionMode(string? mode)
    {
        var normalized = string.IsNullOrWhiteSpace(mode) ? "worker_mcp_tool" : mode.Trim().ToLowerInvariant().Replace('-', '_');
        return normalized switch
        {
            "artifact_reconciled" => "artifact_reconciled",
            _ => "worker_mcp_tool",
        };
    }
}
