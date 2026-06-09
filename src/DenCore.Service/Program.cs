using System.Text.Json;
using System.Text.Json.Serialization;
using DenCore;
using DenCore.Data;
using DenCore.Llm;
using DenCore.Models;
using DenCore.Services;
using DenCore.Mcp;
using DenCore.Service;
using DenCore.Service.Realtime;
using DenCore.Service.Routes;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Configuration (appsettings.json + environment variables + CLI args)
// DenCore is the primary config section; DenMcp is checked as a legacy fallback
// so existing appsettings.json files continue to work without changes.
var options = new DenCoreOptions();
var coreSection = builder.Configuration.GetSection("DenCore");
var legacySection = builder.Configuration.GetSection("DenMcp");
var activeConfigSection = coreSection.Exists() ? coreSection : legacySection;
activeConfigSection.Bind(options);

// CLI overrides: --port and --db-path
if (builder.Configuration["port"] is { } port)
    options.ListenUrl = $"http://localhost:{port}";
if (builder.Configuration["db-path"] is { } dbPathOverride)
    options.DatabasePath = dbPathOverride;

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(options.BlockedTaskEscalation);
var trustedPublisherOptions = new TrustedPublisherOptions();
activeConfigSection.GetSection("TrustedPublisher").Bind(trustedPublisherOptions);
builder.Services.AddSingleton(trustedPublisherOptions);
var denPublishFacadeOptions = new DenPublishFacadeOptions();
activeConfigSection.GetSection("DenPublishFacade").Bind(denPublishFacadeOptions);
builder.Services.AddSingleton(denPublishFacadeOptions);

// LLM (librarian)
var llmConfig = new LlmConfig();
activeConfigSection.GetSection("Llm").Bind(llmConfig);
if (builder.Configuration["llm-endpoint"] is { } llmEndpoint)
    llmConfig.Endpoint = llmEndpoint;
if (builder.Configuration["llm-api-key"] is { } llmApiKey)
    llmConfig.ApiKey = llmApiKey;
if (builder.Configuration["llm-model"] is { } llmModel)
    llmConfig.Model = llmModel;
if (builder.Configuration["llm-max-tokens"] is { } llmMaxTokens &&
    int.TryParse(llmMaxTokens, out var parsedMaxTokens))
    llmConfig.MaxTokens = parsedMaxTokens;
if (builder.Configuration["llm-context-token-budget"] is { } llmContextTokenBudget &&
    int.TryParse(llmContextTokenBudget, out var parsedContextTokenBudget))
    llmConfig.ContextTokenBudget = parsedContextTokenBudget;
builder.Services.AddSingleton(llmConfig);
builder.Services.AddSingleton<ILlmClient, OpenAiCompatibleLlmClient>();

// Kestrel
builder.WebHost.UseUrls(options.ListenUrl);

// JSON serialization
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// Database
var dbPath = options.GetResolvedDatabasePath();
var initializer = new DatabaseInitializer(dbPath, NullLogger<DatabaseInitializer>.Instance);
builder.Services.AddSingleton(new DbConnectionFactory(initializer.ConnectionString));

// Repositories
builder.Services.AddSingleton<IProjectRepository, ProjectRepository>();
builder.Services.AddSingleton<ITopicRepository, TopicRepository>();
builder.Services.AddSingleton<ITopicClipQueueRepository, TopicClipQueueRepository>();
builder.Services.AddSingleton<ITaskRepository, TaskRepository>();
builder.Services.AddSingleton<IReviewRoundRepository, ReviewRoundRepository>();
builder.Services.AddSingleton<IReviewFindingRepository, ReviewFindingRepository>();
builder.Services.AddSingleton<IMessageRepository, MessageRepository>();
builder.Services.AddSingleton<IDocumentRepository, DocumentRepository>();
builder.Services.AddSingleton<IBlackboardRepository, BlackboardRepository>();
builder.Services.AddSingleton<IAgentGuidanceRepository, AgentGuidanceRepository>();
builder.Services.AddSingleton<IAgentSessionRepository, AgentSessionRepository>();
builder.Services.AddSingleton<IAgentInstanceBindingRepository, AgentInstanceBindingRepository>();
builder.Services.AddSingleton<DispatchRepository>();
builder.Services.AddSingleton<IAgentStreamRepository, AgentStreamRepository>();
builder.Services.AddSingleton<IAgentRunRepository, AgentRunRepository>();
builder.Services.AddSingleton<IAgentWorkspaceRepository, AgentWorkspaceRepository>();
builder.Services.AddSingleton<IDesktopSnapshotRepository, DesktopSnapshotRepository>();
builder.Services.AddSingleton<IDesktopSessionEventRepository, DesktopSessionEventRepository>();
builder.Services.AddSingleton<ICollaborationRepository, CollaborationRepository>();
builder.Services.AddSingleton<IDiscussionRepository, DiscussionRepository>();
builder.Services.AddSingleton<IUsageCostRepository, UsageCostRepository>();
builder.Services.AddSingleton<IUsageCostService, UsageCostService>();
builder.Services.AddSingleton<IWorkerPoolRepository, WorkerPoolRepository>();
builder.Services.AddSingleton<ICapabilityRepository, CapabilityRepository>();
builder.Services.AddScoped<ICapabilityInvocationService, CapabilityInvocationService>();
builder.Services.AddHttpClient<IHttpExecutorClient, DefaultHttpExecutorClient>();
builder.Services.AddSingleton<AgentStreamRealtimeHub>();
builder.Services.AddSingleton<INotificationChannel, NoOpNotificationChannel>();
builder.Services.AddSingleton<IAgentStreamOpsService, AgentStreamOpsService>();
builder.Services.AddSingleton<IDispatchRepository>(services =>
    new AgentStreamDispatchRepository(
        services.GetRequiredService<DispatchRepository>(),
        services.GetRequiredService<IAgentStreamOpsService>()));
builder.Services.AddSingleton<IReviewWorkflowService, ReviewWorkflowService>();
builder.Services.AddSingleton<IReviewFindingTriageService, ReviewFindingTriageService>();
builder.Services.AddSingleton<IAgentRecipientResolver, AgentRecipientResolver>();
builder.Services.AddSingleton<IAgentStreamMessageService, AgentStreamMessageService>();
builder.Services.AddSingleton<ISubagentRunService, SubagentRunService>();
builder.Services.AddSingleton<IAttentionService, AttentionService>();
builder.Services.AddSingleton<IGitInspectionService, GitInspectionService>();
builder.Services.AddSingleton<ITrustedPublisherService, TrustedPublisherService>();
builder.Services.AddHttpClient<IDenPublishFacadeService, DenPublishFacadeService>((services, client) =>
{
    var facadeOptions = services.GetRequiredService<DenPublishFacadeOptions>();
    client.BaseAddress = new Uri(facadeOptions.Endpoint.TrimEnd('/'));
});
builder.Services.AddSingleton<IProcessRunner, SystemProcessRunner>();

// Dispatch
builder.Services.AddSingleton<IRoutingService, RoutingService>();
builder.Services.AddSingleton<IDispatchDetectionService, DispatchDetectionService>();

// Blocked task escalation
builder.Services.AddSingleton<IBlockedTaskEscalationService, BlockedTaskEscalationService>();

// Stale worker attention routing
builder.Services.AddSingleton<IStaleAttentionRoutingService, StaleAttentionRoutingService>();

// Librarian
builder.Services.AddSingleton<LibrarianGatherer>();
builder.Services.AddSingleton<LibrarianService>();

// MCP endpoint hosted by Core. den-mcp adapter mode proxies public /mcp here so
// Core remains the sole SQLite owner/writer while preserving the existing MCP
// tool surface for Hermes clients.
builder.Services.AddSingleton(McpToolProfileRegistry.CreateDefault());
builder.Services.AddMcpServer()
    .WithHttpTransport(options =>
    {
        options.ConfigureSessionOptions = (httpContext, mcpServerOptions, cancellationToken) =>
        {
            var registry = httpContext.RequestServices.GetRequiredService<McpToolProfileRegistry>();
            mcpServerOptions.ApplyToolFiltering(registry, httpContext);
            return Task.CompletedTask;
        };
    })
    .WithToolsFromAssembly();

var app = builder.Build();

// Initialize database on startup
await initializer.InitializeAsync();

// Static files (web frontend)
app.UseDefaultFiles();
app.UseStaticFiles();

// Health check
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    version = BuildInfo.Version,
    informationalVersion = BuildInfo.InformationalVersion,
    commit = BuildInfo.Commit
}));

// REST API
app.MapProjectRoutes();
app.MapSpaceRoutes();
app.MapTopicRoutes();
app.MapTopicClipQueueRoutes();
app.MapTaskRoutes();
app.MapMessageRoutes();
app.MapDocumentRoutes();
app.MapMemoryRoutes();
app.MapBlackboardRoutes();
app.MapAgentGuidanceRoutes();
app.MapAgentRoutes();
app.MapDispatchRoutes();
app.MapAgentStreamRoutes();
app.MapChannelsContractRoutes();
app.MapGatewayContractRoutes();
app.MapDirectDeliveryContractRoutes();
app.MapSubagentRunRoutes();
app.MapAgentWorkspaceRoutes();
app.MapDesktopSnapshotRoutes();
app.MapDesktopSessionEventRoutes();
app.MapCollaborationRoutes();
app.MapAttentionRoutes();
app.MapGitInspectionRoutes();
app.MapLibrarianRoutes();
app.MapDiscussionRoutes();
app.MapWorkerPoolRoutes();
app.MapCapabilityRoutes();

// MCP endpoint
app.MapMcp("/mcp");

// SPA fallback — serves index.html for unmatched routes
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
