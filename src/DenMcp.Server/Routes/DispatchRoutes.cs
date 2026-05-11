using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;

namespace DenMcp.Server.Routes;

public static class DispatchRoutes
{
    public static void MapDispatchRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/dispatch");

        group.MapGet("/", async (IDispatchRepository repo,
            string? projectId, string? targetAgent, string? status) =>
        {
            DispatchStatus[]? statuses;
            try
            {
                statuses = status?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(EnumExtensions.ParseDispatchStatus).ToArray();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            var entries = await repo.ListAsync(projectId, targetAgent, statuses);
            return Results.Ok(entries);
        });

        group.MapGet("/{id:int}", async (IDispatchRepository repo, int id) =>
        {
            var entry = await repo.GetByIdAsync(id);
            return entry is not null
                ? Results.Ok(entry)
                : Results.NotFound(new { error = $"Dispatch {id} not found" });
        });

        group.MapGet("/{id:int}/context", async (IDispatchContextService contexts, int id) =>
        {
            var context = await contexts.GetContextAsync(id);
            return context is not null
                ? Results.Ok(context)
                : Results.NotFound(new { error = $"Dispatch {id} not found" });
        });

        group.MapPost("/{id:int}/approve", async (IDispatchRepository repo, int id, ApproveRequest req) =>
        {
            var existing = await repo.GetByIdAsync(id);
            if (existing is null)
                return Results.NotFound(new { error = $"Dispatch {id} not found" });
            try
            {
                var entry = await repo.ApproveAsync(id, req.DecidedBy);
                return Results.Ok(entry);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/{id:int}/reject", async (IDispatchRepository repo, int id, RejectRequest req) =>
        {
            var existing = await repo.GetByIdAsync(id);
            if (existing is null)
                return Results.NotFound(new { error = $"Dispatch {id} not found" });
            try
            {
                var entry = await repo.RejectAsync(id, req.DecidedBy);
                return Results.Ok(entry);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/{id:int}/complete", async (IDispatchRepository repo, int id, CompleteRequest? req) =>
        {
            var existing = await repo.GetByIdAsync(id);
            if (existing is null)
                return Results.NotFound(new { error = $"Dispatch {id} not found" });
            try
            {
                var entry = await repo.CompleteAsync(id, req?.CompletedBy);
                return Results.Ok(entry);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapGet("/pending/count", async (IDispatchRepository repo, string? projectId) =>
        {
            var count = await repo.GetPendingCountAsync(projectId);
            return Results.Ok(new { count });
        });
    }
}

public record ApproveRequest(string DecidedBy);
public record RejectRequest(string DecidedBy);
public record CompleteRequest(string? CompletedBy = null);
