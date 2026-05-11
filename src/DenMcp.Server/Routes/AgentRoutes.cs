using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using Microsoft.Extensions.Logging;

namespace DenMcp.Server.Routes;

public static class AgentRoutes
{
    public static void MapAgentRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/agents");

        group.MapPost("/checkin", async (IAgentSessionRepository repo, IAgentInstanceBindingRepository bindings, INotificationChannel notifications,
            ILoggerFactory loggers, CheckInRequest req) =>
        {
            if (!TryBuildBinding(req, out var binding, out var error))
                return Results.BadRequest(new { error });

            var session = await repo.CheckInAsync(req.Agent, req.ProjectId, req.SessionId, req.Metadata);
            if (binding is not null)
                await bindings.UpsertAsync(binding);
            try
            {
                await notifications.SendAgentStatusAsync(req.ProjectId, req.Agent, "checked_in");
            }
            catch (Exception ex)
            {
                loggers.CreateLogger("Notifications")
                    .LogError(ex, "Agent check-in notification failed for {Agent} on {ProjectId}", req.Agent, req.ProjectId);
            }
            return Results.Ok(session);
        });

        group.MapPost("/heartbeat", async (IAgentSessionRepository repo, IAgentInstanceBindingRepository bindings, HeartbeatRequest req) =>
        {
            var sessionOk = await repo.HeartbeatAsync(req.Agent, req.ProjectId);
            var bindingOk = req.InstanceId is null || await bindings.HeartbeatAsync(req.InstanceId);
            var ok = sessionOk || bindingOk;
            return ok
                ? Results.Ok(new { status = "ok" })
                : Results.NotFound(new { error = "No active session or binding found. Call checkin first." });
        });

        group.MapPost("/checkout", async (IAgentSessionRepository repo, IAgentInstanceBindingRepository bindings, INotificationChannel notifications,
            ILoggerFactory loggers, CheckOutRequest req) =>
        {
            bool sessionOk;
            if (req.SessionId is not null)
                sessionOk = await repo.CheckOutBySessionAsync(req.SessionId);
            else
                sessionOk = await repo.CheckOutAsync(req.Agent, req.ProjectId);

            var bindingOk = false;
            if (req.InstanceId is not null)
                bindingOk = await bindings.CheckOutAsync(req.InstanceId);
            else if (req.SessionId is not null)
                bindingOk = await bindings.CheckOutBySessionAsync(req.SessionId) > 0;

            var ok = sessionOk || bindingOk;

            if (ok)
            {
                try
                {
                    await notifications.SendAgentStatusAsync(req.ProjectId, req.Agent, "checked_out");
                }
                catch (Exception ex)
                {
                    loggers.CreateLogger("Notifications")
                        .LogError(ex, "Agent checkout notification failed for {Agent} on {ProjectId}", req.Agent, req.ProjectId);
                }
            }

            return ok
                ? Results.Ok(new { status = "checked_out" })
                : Results.NotFound(new { error = "No active session found." });
        });

        group.MapGet("/active", async (IAgentSessionRepository repo, string? projectId) =>
        {
            var sessions = await repo.ListActiveAsync(projectId);
            return Results.Ok(sessions);
        });

        group.MapGet("/bindings", async (IAgentInstanceBindingRepository repo, string? projectId, string? status, string? role, string? agentIdentity, string? transportKind) =>
        {
            AgentInstanceBindingStatus[]? statuses = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                try
                {
                    statuses = status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(EnumExtensions.ParseAgentInstanceBindingStatus)
                        .ToArray();
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }

            var bindings = await repo.ListAsync(new AgentInstanceBindingListOptions
            {
                ProjectId = projectId,
                AgentIdentity = agentIdentity,
                Role = role,
                TransportKind = transportKind,
                Statuses = statuses ??
                [
                    AgentInstanceBindingStatus.Active,
                    AgentInstanceBindingStatus.Degraded
                ]
            });

            return Results.Ok(bindings);
        });
    }

    private static bool TryBuildBinding(CheckInRequest req, out AgentInstanceBinding? binding, out string? error)
    {
        binding = null;
        error = null;

        var requested =
            !string.IsNullOrWhiteSpace(req.InstanceId) ||
            !string.IsNullOrWhiteSpace(req.AgentFamily) ||
            !string.IsNullOrWhiteSpace(req.Role) ||
            !string.IsNullOrWhiteSpace(req.TransportKind) ||
            !string.IsNullOrWhiteSpace(req.BindingStatus);

        if (!requested)
            return true;

        var instanceId = req.InstanceId ?? req.SessionId;
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            error = "instance_id or session_id is required when registering an agent binding.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(req.AgentFamily))
        {
            error = "agent_family is required when registering an agent binding.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(req.TransportKind))
        {
            error = "transport_kind is required when registering an agent binding.";
            return false;
        }

        AgentInstanceBindingStatus status = AgentInstanceBindingStatus.Active;
        if (!string.IsNullOrWhiteSpace(req.BindingStatus))
        {
            try
            {
                status = EnumExtensions.ParseAgentInstanceBindingStatus(req.BindingStatus);
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                return false;
            }
        }

        binding = new AgentInstanceBinding
        {
            InstanceId = instanceId,
            ProjectId = req.ProjectId,
            AgentIdentity = req.Agent,
            AgentFamily = req.AgentFamily,
            Role = req.Role,
            TransportKind = req.TransportKind,
            SessionId = req.SessionId,
            Status = status,
            Metadata = req.Metadata
        };

        return true;
    }
}

public record CheckInRequest(
    string Agent,
    string ProjectId,
    string? SessionId = null,
    string? Metadata = null,
    string? InstanceId = null,
    string? AgentFamily = null,
    string? Role = null,
    string? TransportKind = null,
    string? BindingStatus = null);

public record HeartbeatRequest(string Agent, string ProjectId, string? InstanceId = null);
public record CheckOutRequest(string Agent, string ProjectId, string? SessionId = null, string? InstanceId = null);
