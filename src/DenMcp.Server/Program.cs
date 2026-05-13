using System.Text.Json;
using System.Text.Json.Serialization;
using DenMcp.Core;
using DenMcp.Core.Data;
using DenMcp.Core.Llm;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using DenMcp.Server;
using DenMcp.Server.Realtime;
using DenMcp.Server.Routes;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

var builder = WebApplication.CreateBuilder(args);

// Configuration (appsettings.json + environment variables + CLI args)
var options = new DenMcpOptions();
PreparePiSessionHostArraysForBinding(options.PiSessionHost);
builder.Configuration.GetSection("DenMcp").Bind(options);
ApplyPiSessionHostArrayDefaults(options.PiSessionHost);

// CLI overrides: --port and --db-path
if (builder.Configuration["port"] is { } port)
    options.ListenUrl = $"http://localhost:{port}";
if (builder.Configuration["db-path"] is { } dbPathOverride)
    options.DatabasePath = dbPathOverride;

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(options.PiSessionHost);
var trustedPublisherOptions = new TrustedPublisherOptions();
builder.Configuration.GetSection("DenMcp:TrustedPublisher").Bind(trustedPublisherOptions);
builder.Services.AddSingleton(trustedPublisherOptions);

// LLM (librarian)
var llmConfig = new LlmConfig();
builder.Configuration.GetSection("DenMcp:Llm").Bind(llmConfig);
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
builder.Services.AddSingleton<IPiSessionRepository, PiSessionRepository>();
builder.Services.AddSingleton<IDesktopSnapshotRepository, DesktopSnapshotRepository>();
builder.Services.AddSingleton<IDesktopSessionEventRepository, DesktopSessionEventRepository>();
builder.Services.AddSingleton<ICollaborationRepository, CollaborationRepository>();
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
builder.Services.AddSingleton<IPiDockerLaunchProfileRenderer, PiDockerLaunchProfileRenderer>();
builder.Services.AddSingleton<IProcessRunner, SystemProcessRunner>();
builder.Services.AddSingleton<IPiSessionHost, TmuxDockerPiSessionHost>();
builder.Services.AddSingleton<IPiSessionService, PiSessionService>();

// Dispatch
builder.Services.AddSingleton<IRoutingService, RoutingService>();
builder.Services.AddSingleton<IPromptGenerationService, PromptGenerationService>();
builder.Services.AddSingleton<IDispatchContextService, DispatchContextService>();
builder.Services.AddSingleton<IDispatchDetectionService, DispatchDetectionService>();

// Librarian
builder.Services.AddSingleton<LibrarianGatherer>();
builder.Services.AddSingleton<LibrarianService>();

// MCP endpoint hosted by Core. den-mcp adapter mode proxies public /mcp here so
// Core remains the sole SQLite owner/writer while preserving the existing MCP
// tool surface for Hermes clients.
builder.Services.AddMcpServer()
    .WithHttpTransport()
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
app.MapBlackboardRoutes();
app.MapAgentGuidanceRoutes();
app.MapAgentRoutes();
app.MapDispatchRoutes();
app.MapAgentStreamRoutes();
app.MapChannelsContractRoutes();
app.MapSubagentRunRoutes();
app.MapAgentWorkspaceRoutes();
app.MapDesktopSnapshotRoutes();
app.MapDesktopSessionEventRoutes();
app.MapCollaborationRoutes();
app.MapAttentionRoutes();
app.MapGitInspectionRoutes();
app.MapPiLaunchProfileRoutes();
app.MapPiSessionRoutes();
app.MapLibrarianRoutes();

// MCP endpoint
app.MapMcp("/mcp");

// SPA fallback — serves index.html for unmatched routes
app.MapFallbackToFile("index.html");

static void PreparePiSessionHostArraysForBinding(PiDockerLaunchProfileOptions options)
{
    // Microsoft.Extensions.Configuration binds array values by extending existing
    // initialized collections. Clear array defaults before binding so configured
    // arrays replace defaults instead of appending to them.
    options.TmuxShellCommand = [];
    options.ProviderSecretEnvironmentVariables = [];
    options.RequiredPiStatePaths = [];
}

static void ApplyPiSessionHostArrayDefaults(PiDockerLaunchProfileOptions options)
{
    if (options.TmuxShellCommand.Length == 0)
        options.TmuxShellCommand = PiDockerLaunchProfileDefaults.TmuxShellCommand.ToArray();
    if (options.ProviderSecretEnvironmentVariables.Length == 0)
        options.ProviderSecretEnvironmentVariables = PiDockerLaunchProfileDefaults.ProviderSecretEnvironmentVariables.ToArray();
    if (options.RequiredPiStatePaths.Length == 0)
        options.RequiredPiStatePaths = ["agent/settings.json"];
}

app.Run();

public partial class Program;
