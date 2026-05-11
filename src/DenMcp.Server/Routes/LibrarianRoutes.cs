using DenMcp.Core.Llm;
using DenMcp.Core.Models;

namespace DenMcp.Server.Routes;

public static class LibrarianRoutes
{
    public static void MapLibrarianRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/librarian");

        group.MapPost("/query", async (LibrarianService librarian, LlmConfig llmConfig,
            string projectId, LibrarianQueryRequest req) =>
        {
            if (string.IsNullOrEmpty(llmConfig.Endpoint))
                return Results.BadRequest(new { error = "Librarian is not configured. Set DenMcp:Llm:Endpoint in appsettings.json or pass --llm-endpoint." });

            try
            {
                var response = await librarian.QueryAsync(projectId, req.Query, req.TaskId, req.IncludeGlobal);
                return Results.Ok(response);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}

public record LibrarianQueryRequest(
    string Query,
    int? TaskId = null,
    bool IncludeGlobal = true);
