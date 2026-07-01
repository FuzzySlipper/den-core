using DenCore.Data;
using DenCore.Models;
using DenCore.Services;
using DenCore.Service.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenCore.Service.Tests;

public class TaskToolsTests
{
    private sealed class FakeTaskRepo : ITaskRepository
    {
        public int CreateCalls { get; private set; }
        public int GetByIdCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public int AddDependencyCalls { get; private set; }
        public int RemoveDependencyCalls { get; private set; }

        public Task<ProjectTask> CreateAsync(ProjectTask task, int[]? dependsOn = null)
        {
            CreateCalls++;
            return Task.FromResult(task);
        }

        public Task<ProjectTask?> GetByIdAsync(int id)
        {
            GetByIdCalls++;
            return Task.FromResult<ProjectTask?>(new ProjectTask
            {
                Id = id,
                ProjectId = "den-services",
                Title = "Existing task",
                Status = DenCore.Models.TaskStatus.Planned,
                Priority = 3,
            });
        }

        public Task<TaskDetail> GetDetailAsync(int id) => throw new NotSupportedException();
        public Task<TaskWorkflowSummary> GetWorkflowSummaryAsync(int id) => throw new NotSupportedException();
        public Task<List<TaskSummary>> ListAsync(
            string projectId,
            DenCore.Models.TaskStatus[]? statuses = null,
            string? assignedTo = null,
            string[]? tags = null,
            int? maxPriority = null,
            int? parentId = null,
            bool includeAll = false) => throw new NotSupportedException();

        public Task<ProjectTask> UpdateAsync(int id, Dictionary<string, object?> changes, string agent)
        {
            UpdateCalls++;
            return Task.FromResult(new ProjectTask { Id = id, ProjectId = "den-services", Title = "Updated" });
        }

        public Task AddDependencyAsync(int taskId, int dependsOn)
        {
            AddDependencyCalls++;
            return Task.CompletedTask;
        }

        public Task RemoveDependencyAsync(int taskId, int dependsOn)
        {
            RemoveDependencyCalls++;
            return Task.CompletedTask;
        }

        public Task<ProjectTask?> GetNextTaskAsync(string projectId, string? assignedTo = null) =>
            throw new NotSupportedException();
    }

    [Theory]
    [InlineData("create_task")]
    [InlineData("update_task")]
    [InlineData("add_dependency")]
    [InlineData("remove_dependency")]
    public async Task TaskWriteTools_AreTombstonedAfterTasksCutover(string toolName)
    {
        var repo = new FakeTaskRepo();

        var ex = toolName switch
        {
            "create_task" => await Assert.ThrowsAsync<InvalidOperationException>(() =>
                TaskTools.CreateTask(repo, project_id: "den-services", title: "Do the thing")),
            "update_task" => await Assert.ThrowsAsync<InvalidOperationException>(() =>
                TaskTools.UpdateTask(
                    repo,
                    detection: null!,
                    escalationService: null!,
                    logger: NullLogger<TaskTools>.Instance,
                    task_id: 3855,
                    agent: "codex",
                    status: "review")),
            "add_dependency" => await Assert.ThrowsAsync<InvalidOperationException>(() =>
                TaskTools.AddDependency(repo, task_id: 3855, depends_on: 3726)),
            "remove_dependency" => await Assert.ThrowsAsync<InvalidOperationException>(() =>
                TaskTools.RemoveDependency(repo, task_id: 3855, depends_on: 3726)),
            _ => throw new ArgumentOutOfRangeException(nameof(toolName), toolName, null),
        };

        Assert.Contains(toolName, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("moved from den-core to den-services/tasks", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, repo.CreateCalls);
        Assert.Equal(0, repo.GetByIdCalls);
        Assert.Equal(0, repo.UpdateCalls);
        Assert.Equal(0, repo.AddDependencyCalls);
        Assert.Equal(0, repo.RemoveDependencyCalls);
    }
}
