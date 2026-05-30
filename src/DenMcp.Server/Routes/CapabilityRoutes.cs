using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;

namespace DenMcp.Server.Routes;

public static class CapabilityRoutes
{
    public static void MapCapabilityRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/capabilities");

        // ── Definitions ─────────────────────────────────────────────

        // GET /api/capabilities?status=&sideEffectLevel=&ownerProjectId=&limit=
        group.MapGet("/", async (ICapabilityRepository repo,
            string? status, string? sideEffectLevel, string? ownerProjectId, int limit = 50) =>
        {
            var capabilities = await repo.ListDefinitionsAsync(new CapabilityListOptions
            {
                Status = status,
                SideEffectLevel = sideEffectLevel,
                OwnerProjectId = ownerProjectId,
                Limit = Math.Clamp(limit, 1, 200),
            });
            return Results.Ok(new { capabilities, count = capabilities.Count });
        });

        // GET /api/capabilities/{capabilityId}
        group.MapGet("/{capabilityId}", async (ICapabilityRepository repo, string capabilityId) =>
        {
            var cap = await repo.GetDefinitionAsync(capabilityId);
            if (cap is null)
                return Results.NotFound(new { error = $"Capability '{capabilityId}' not found" });
            return Results.Ok(cap);
        });

        // POST /api/capabilities (upsert by capability_id in body)
        group.MapPost("/", async (ICapabilityRepository repo, UpsertCapabilityRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.CapabilityId))
                return Results.BadRequest(new { error = "capability_id is required" });

            var definition = new CapabilityDefinition
            {
                CapabilityId = req.CapabilityId,
                DisplayName = req.DisplayName,
                Description = req.Description,
                Status = req.Status,
                HttpEndpoint = req.HttpEndpoint,
                ExecutorKind = req.ExecutorKind,
                SideEffectLevel = req.SideEffectLevel,
                OwnerProjectId = req.OwnerProjectId,
                RequestSchemaJson = req.RequestSchemaJson,
                ResponseSchemaJson = req.ResponseSchemaJson,
                Metadata = req.Metadata,
            };

            var result = await repo.UpsertDefinitionAsync(definition);
            return Results.Ok(result);
        });

        // PUT /api/capabilities/{capabilityId} (upsert by path)
        group.MapPut("/{capabilityId}", async (ICapabilityRepository repo, string capabilityId, UpsertCapabilityRequest req) =>
        {
            var definition = new CapabilityDefinition
            {
                CapabilityId = capabilityId,
                DisplayName = req.DisplayName,
                Description = req.Description,
                Status = req.Status,
                HttpEndpoint = req.HttpEndpoint,
                ExecutorKind = req.ExecutorKind,
                SideEffectLevel = req.SideEffectLevel,
                OwnerProjectId = req.OwnerProjectId,
                RequestSchemaJson = req.RequestSchemaJson,
                ResponseSchemaJson = req.ResponseSchemaJson,
                Metadata = req.Metadata,
            };

            var result = await repo.UpsertDefinitionAsync(definition);
            return Results.Ok(result);
        });

        // ── Invocation ────────────────────────────────────────────────

        // POST /api/capabilities/{capabilityId}/invoke
        group.MapPost("/{capabilityId}/invoke", async (
            ICapabilityInvocationService service,
            string capabilityId, InvokeCapabilityRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.CallerProjectId))
                return Results.BadRequest(new { error = "caller_project_id is required" });
            if (string.IsNullOrWhiteSpace(req.CallerIdentity))
                return Results.BadRequest(new { error = "caller_identity is required" });

            var invocation = await service.InvokeAsync(
                capabilityId,
                req.CallerProjectId,
                req.CallerTaskId,
                req.CallerIdentity,
                req.Payload,
                req.TimeoutMs);

            return Results.Ok(invocation);
        });

        // ── Invocation Audit ─────────────────────────────────────────

        // GET /api/capabilities/invocations?capabilityId=&callerProjectId=&callerTaskId=&status=&limit=
        group.MapGet("/invocations", async (ICapabilityRepository repo,
            string? capabilityId, string? callerProjectId, string? callerTaskId,
            string? status, int limit = 50) =>
        {
            var invocations = await repo.ListInvocationsAsync(new InvocationListOptions
            {
                CapabilityId = capabilityId,
                CallerProjectId = callerProjectId,
                CallerTaskId = callerTaskId,
                Status = status,
                Limit = Math.Clamp(limit, 1, 200),
            });
            return Results.Ok(new { invocations, count = invocations.Count });
        });

        // GET /api/capabilities/invocations/{invocationId}
        group.MapGet("/invocations/{invocationId:int}", async (ICapabilityRepository repo, int invocationId) =>
        {
            var invocation = await repo.GetInvocationAsync(invocationId);
            if (invocation is null)
                return Results.NotFound(new { error = $"Invocation {invocationId} not found" });
            return Results.Ok(invocation);
        });
    }
}
