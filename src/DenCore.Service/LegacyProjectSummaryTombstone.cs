using DenCore.Models;

namespace DenCore.Service;

public static class LegacyProjectSummaryTombstone
{
    public const string ErrorCode = "legacy_project_summary_retired";

    public static object Create(Project project, string operation) => new
    {
        error = ErrorCode,
        message = "Core project/space summary stats are retired after the den-services MCP cutover. Use the den-services MCP get_project/get_space composed response, or read metadata/tasks/messages from their successor services.",
        operation,
        project,
        successor_sources = new
        {
            metadata = "den-services/projects",
            task_counts_by_status = "den-services/tasks",
            unread_message_count = "den-services/messages"
        }
    };
}
