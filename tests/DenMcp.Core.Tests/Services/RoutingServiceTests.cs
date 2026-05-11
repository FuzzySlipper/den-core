using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;

namespace DenMcp.Core.Tests.Services;

public class RoutingServiceTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private RoutingService _service = null!;
    private DocumentRepository _docs = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _docs = new DocumentRepository(_testDb.Db);
        _service = new RoutingService(_docs);
        var projRepo = new ProjectRepository(_testDb.Db);
        await projRepo.CreateAsync(new Project { Id = "proj", Name = "Test" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    private async Task StoreRoutingDoc(RoutingConfig config)
    {
        await _docs.UpsertAsync(new Document
        {
            ProjectId = "proj",
            Slug = "dispatch-routing",
            Title = "Dispatch Routing",
            Content = JsonSerializer.Serialize(config, JsonOptions),
            DocType = DocType.Convention
        });
    }

    #region GetRoutingConfigAsync

    [Fact]
    public async Task GetRoutingConfig_NoDocument_ReturnsFallback()
    {
        var result = await _service.GetRoutingConfigAsync("proj");
        Assert.True(result.IsValid);
        Assert.True(result.IsFallback);
        Assert.Contains("implementer", result.Config.Roles.Keys);
        Assert.Contains("reviewer", result.Config.Roles.Keys);
        Assert.True(result.Config.Triggers.Count >= 3);
        Assert.False(result.Config.Defaults.LegacyDispatchEnabled);
    }

    [Fact]
    public async Task GetRoutingConfig_WithDocument_ParsesConfig()
    {
        var custom = new RoutingConfig
        {
            Roles = new Dictionary<string, string> { ["dev"] = "my-agent" },
            Triggers =
            [
                new RoutingTrigger
                {
                    Event = DispatchEvent.TaskStatusChanged,
                    ToStatus = "review",
                    DispatchTo = "dev"
                }
            ],
            Defaults = new RoutingDefaults { LegacyDispatchEnabled = true, ExpiryMinutes = 60 }
        };

        await _docs.UpsertAsync(new Document
        {
            ProjectId = "proj",
            Slug = "dispatch-routing",
            Title = "Dispatch Routing",
            Content = JsonSerializer.Serialize(custom, JsonOptions),
            DocType = DocType.Convention
        });

        var result = await _service.GetRoutingConfigAsync("proj");
        Assert.True(result.IsValid);
        Assert.False(result.IsFallback);
        Assert.Single(result.Config.Roles);
        Assert.Equal("my-agent", result.Config.Roles["dev"]);
        Assert.Single(result.Config.Triggers);
        Assert.True(result.Config.Defaults.LegacyDispatchEnabled);
        Assert.Equal(60, result.Config.Defaults.ExpiryMinutes);
    }

    [Fact]
    public async Task GetRoutingConfig_MalformedDocument_ReturnsInvalidWithError()
    {
        await _docs.UpsertAsync(new Document
        {
            ProjectId = "proj",
            Slug = "dispatch-routing",
            Title = "Dispatch Routing",
            Content = "this is not valid json {{{",
            DocType = DocType.Convention
        });

        var result = await _service.GetRoutingConfigAsync("proj");
        Assert.False(result.IsValid);
        Assert.NotNull(result.ValidationError);
        Assert.Contains("Malformed", result.ValidationError);
    }

    [Fact]
    public async Task GetRoutingConfig_DoesNotCreateDocument()
    {
        await _service.GetRoutingConfigAsync("proj");
        var doc = await _docs.GetAsync("proj", "dispatch-routing");
        Assert.Null(doc);
    }

    [Fact]
    public async Task GetRoutingConfig_UnknownTriggerEvent_ReturnsInvalid()
    {
        var config = new RoutingConfig
        {
            Roles = new Dictionary<string, string> { ["dev"] = "agent" },
            Triggers = [new RoutingTrigger { Event = "bogus_event", DispatchTo = "dev" }]
        };
        await StoreRoutingDoc(config);

        var result = await _service.GetRoutingConfigAsync("proj");
        Assert.False(result.IsValid);
        Assert.Contains("unknown event", result.ValidationError);
    }

    [Fact]
    public async Task GetRoutingConfig_BlankDispatchTo_ReturnsInvalid()
    {
        var config = new RoutingConfig
        {
            Triggers = [new RoutingTrigger { Event = DispatchEvent.TaskStatusChanged, DispatchTo = "  " }]
        };
        await StoreRoutingDoc(config);

        var result = await _service.GetRoutingConfigAsync("proj");
        Assert.False(result.IsValid);
        Assert.Contains("dispatch_to", result.ValidationError);
    }

    [Fact]
    public async Task GetRoutingConfig_ZeroExpiryMinutes_ReturnsInvalid()
    {
        var config = new RoutingConfig
        {
            Defaults = new RoutingDefaults { ExpiryMinutes = 0 }
        };
        await StoreRoutingDoc(config);

        var result = await _service.GetRoutingConfigAsync("proj");
        Assert.False(result.IsValid);
        Assert.Contains("expiry_minutes", result.ValidationError);
    }

    [Fact]
    public async Task GetRoutingConfig_BlankRoleAgent_ReturnsInvalid()
    {
        var config = new RoutingConfig
        {
            Roles = new Dictionary<string, string> { ["reviewer"] = "" }
        };
        await StoreRoutingDoc(config);

        var result = await _service.GetRoutingConfigAsync("proj");
        Assert.False(result.IsValid);
        Assert.Contains("blank", result.ValidationError);
    }

    [Fact]
    public async Task GetRoutingConfig_InvalidMessageIntent_ReturnsInvalid()
    {
        var config = new RoutingConfig
        {
            Triggers =
            [
                new RoutingTrigger
                {
                    Event = DispatchEvent.MessageReceived,
                    MessageIntent = "not_a_real_intent",
                    DispatchTo = "reviewer"
                }
            ]
        };
        await StoreRoutingDoc(config);

        var result = await _service.GetRoutingConfigAsync("proj");
        Assert.False(result.IsValid);
        Assert.Contains("Unknown message intent", result.ValidationError);
    }

    [Fact]
    public async Task GetRoutingConfig_FallbackIsNotSharedMutable()
    {
        var result1 = await _service.GetRoutingConfigAsync("proj");
        result1.Config.Roles["hacked"] = "evil-agent";
        result1.Config.Triggers.Clear();

        var result2 = await _service.GetRoutingConfigAsync("proj");
        // Second call should be unaffected by mutations to the first
        Assert.DoesNotContain("hacked", result2.Config.Roles.Keys);
        Assert.True(result2.Config.Triggers.Count >= 3);
    }

    #endregion

    #region MatchTrigger

    [Fact]
    public void MatchTrigger_TaskStatusChanged_MatchesToStatus()
    {
        var config = RoutingService.CreateDefaultConfig();
        var evt = new DispatchEvent
        {
            EventKind = DispatchEvent.TaskStatusChanged,
            ProjectId = "proj",
            ToStatus = "review",
            FromStatus = "in_progress",
            TaskId = 42
        };

        var trigger = _service.MatchTrigger(config, evt);
        Assert.NotNull(trigger);
        Assert.Equal("reviewer", trigger.DispatchTo);
    }

    [Fact]
    public void MatchTrigger_TaskStatusChanged_MatchesFromAndToStatus()
    {
        var config = RoutingService.CreateDefaultConfig();
        var evt = new DispatchEvent
        {
            EventKind = DispatchEvent.TaskStatusChanged,
            ProjectId = "proj",
            ToStatus = "planned",
            FromStatus = "review",
            TaskId = 42
        };

        var trigger = _service.MatchTrigger(config, evt);
        Assert.NotNull(trigger);
        Assert.Equal("implementer", trigger.DispatchTo);
    }

    [Fact]
    public void MatchTrigger_TaskStatusChanged_NoMatchWhenStatusDiffers()
    {
        var config = RoutingService.CreateDefaultConfig();
        var evt = new DispatchEvent
        {
            EventKind = DispatchEvent.TaskStatusChanged,
            ProjectId = "proj",
            ToStatus = "done",
            FromStatus = "review",
            TaskId = 42
        };

        var trigger = _service.MatchTrigger(config, evt);
        Assert.Null(trigger);
    }

    [Fact]
    public void MatchTrigger_MessageReceived_MatchesWithRecipient()
    {
        var config = RoutingService.CreateDefaultConfig();
        var evt = new DispatchEvent
        {
            EventKind = DispatchEvent.MessageReceived,
            ProjectId = "proj",
            Recipient = "claude-code",
            Sender = "codex",
            MessageIntent = MessageIntent.ReviewFeedback,
            MessageType = "review_feedback",
            MessageId = 100
        };

        var trigger = _service.MatchTrigger(config, evt);
        Assert.NotNull(trigger);
        Assert.Equal("{recipient}", trigger.DispatchTo);
    }

    [Fact]
    public void MatchTrigger_MessageReceived_MatchesWithTargetRole()
    {
        var config = RoutingService.CreateDefaultConfig();
        var evt = new DispatchEvent
        {
            EventKind = DispatchEvent.MessageReceived,
            ProjectId = "proj",
            MessageTargetRole = "reviewer",
            Sender = "claude-code",
            MessageIntent = MessageIntent.ReviewRequest,
            MessageType = "review_request",
            MessageId = 100
        };

        var trigger = _service.MatchTrigger(config, evt);
        Assert.NotNull(trigger);
        Assert.Equal("{target_role}", trigger.DispatchTo);
    }

    [Fact]
    public void MatchTrigger_MessageReceived_NoMatchWithoutRecipientOrTargetRole()
    {
        var config = RoutingService.CreateDefaultConfig();
        var evt = new DispatchEvent
        {
            EventKind = DispatchEvent.MessageReceived,
            ProjectId = "proj",
            Recipient = null,
            MessageTargetRole = null,
            Sender = "codex",
            MessageIntent = MessageIntent.ReviewFeedback,
            MessageType = "review_feedback",
            MessageId = 100
        };

        var trigger = _service.MatchTrigger(config, evt);
        Assert.Null(trigger);
    }

    [Fact]
    public void MatchTrigger_CustomMessageIntent()
    {
        var config = new RoutingConfig
        {
            Roles = new Dictionary<string, string> { ["planner"] = "codex" },
            Triggers =
            [
                new RoutingTrigger
                {
                    Event = DispatchEvent.MessageReceived,
                    MessageIntent = "handoff",
                    HasRecipient = true,
                    DispatchTo = "{recipient}"
                }
            ]
        };

        var match = new DispatchEvent
        {
            EventKind = DispatchEvent.MessageReceived,
            ProjectId = "proj",
            MessageIntent = MessageIntent.Handoff,
            HandoffKind = "planning_summary",
            Recipient = "codex",
            Sender = "claude-code",
            MessageId = 1
        };

        var noMatch = new DispatchEvent
        {
            EventKind = DispatchEvent.MessageReceived,
            ProjectId = "proj",
            MessageIntent = MessageIntent.ReviewRequest,
            Recipient = "codex",
            Sender = "claude-code",
            MessageId = 2
        };

        Assert.NotNull(_service.MatchTrigger(config, match));
        Assert.Null(_service.MatchTrigger(config, noMatch));
    }

    [Fact]
    public void MatchTrigger_LegacyMessageTypeAlias_UsesCanonicalIntent()
    {
        var config = new RoutingConfig
        {
            Triggers =
            [
                new RoutingTrigger
                {
                    Event = DispatchEvent.MessageReceived,
                    MessageType = "planning_summary",
                    HasRecipient = true,
                    DispatchTo = "{recipient}"
                }
            ]
        };

        var evt = new DispatchEvent
        {
            EventKind = DispatchEvent.MessageReceived,
            ProjectId = "proj",
            MessageIntent = MessageIntent.Handoff,
            HandoffKind = "planning_summary",
            Recipient = "codex",
            Sender = "claude-code",
            MessageId = 1
        };

        Assert.NotNull(_service.MatchTrigger(config, evt));
    }

    [Fact]
    public void MatchTrigger_PacketKind_DistinguishesPacketSubtypes()
    {
        var config = new RoutingConfig
        {
            Triggers =
            [
                new RoutingTrigger
                {
                    Event = DispatchEvent.MessageReceived,
                    MessageIntent = "review_request",
                    PacketKind = "rereview_request",
                    HasRecipient = true,
                    DispatchTo = "{recipient}"
                }
            ]
        };

        var rereview = new DispatchEvent
        {
            EventKind = DispatchEvent.MessageReceived,
            ProjectId = "proj",
            MessageIntent = MessageIntent.ReviewRequest,
            PacketKind = "rereview_request",
            Recipient = "codex",
            Sender = "claude-code",
            MessageId = 1
        };

        var initialReview = new DispatchEvent
        {
            EventKind = DispatchEvent.MessageReceived,
            ProjectId = "proj",
            MessageIntent = MessageIntent.ReviewRequest,
            PacketKind = "review_request",
            Recipient = "codex",
            Sender = "claude-code",
            MessageId = 2
        };

        Assert.NotNull(_service.MatchTrigger(config, rereview));
        Assert.Null(_service.MatchTrigger(config, initialReview));
    }

    [Fact]
    public void MatchTrigger_HandoffKind_DistinguishesHandoffSubtypes()
    {
        var config = new RoutingConfig
        {
            Triggers =
            [
                new RoutingTrigger
                {
                    Event = DispatchEvent.MessageReceived,
                    MessageIntent = "review_feedback",
                    HandoffKind = "review_feedback",
                    HasRecipient = true,
                    DispatchTo = "{recipient}"
                }
            ]
        };

        var feedback = new DispatchEvent
        {
            EventKind = DispatchEvent.MessageReceived,
            ProjectId = "proj",
            MessageIntent = MessageIntent.ReviewFeedback,
            HandoffKind = "review_feedback",
            Recipient = "codex",
            Sender = "claude-code",
            MessageId = 1
        };

        var planning = new DispatchEvent
        {
            EventKind = DispatchEvent.MessageReceived,
            ProjectId = "proj",
            MessageIntent = MessageIntent.Handoff,
            HandoffKind = "planning_summary",
            Recipient = "codex",
            Sender = "claude-code",
            MessageId = 2
        };

        Assert.NotNull(_service.MatchTrigger(config, feedback));
        Assert.Null(_service.MatchTrigger(config, planning));
    }

    [Fact]
    public void MatchTrigger_FirstMatchWins()
    {
        var config = new RoutingConfig
        {
            Roles = new Dictionary<string, string>(),
            Triggers =
            [
                new RoutingTrigger
                {
                    Event = DispatchEvent.TaskStatusChanged,
                    ToStatus = "review",
                    DispatchTo = "first-agent",
                    PromptTemplate = "first"
                },
                new RoutingTrigger
                {
                    Event = DispatchEvent.TaskStatusChanged,
                    ToStatus = "review",
                    DispatchTo = "second-agent",
                    PromptTemplate = "second"
                }
            ]
        };

        var evt = new DispatchEvent
        {
            EventKind = DispatchEvent.TaskStatusChanged,
            ProjectId = "proj",
            ToStatus = "review"
        };

        var trigger = _service.MatchTrigger(config, evt);
        Assert.Equal("first-agent", trigger!.DispatchTo);
    }

    [Fact]
    public void MatchTrigger_CaseInsensitive()
    {
        var config = RoutingService.CreateDefaultConfig();
        var evt = new DispatchEvent
        {
            EventKind = "TASK_STATUS_CHANGED",
            ProjectId = "proj",
            ToStatus = "REVIEW"
        };

        var trigger = _service.MatchTrigger(config, evt);
        Assert.NotNull(trigger);
    }

    #endregion

    #region ResolveAgent

    [Fact]
    public void ResolveAgent_RoleLookup()
    {
        var config = RoutingService.CreateDefaultConfig();
        var trigger = new RoutingTrigger { Event = "x", DispatchTo = "reviewer" };
        var evt = new DispatchEvent { EventKind = "x", ProjectId = "proj" };

        Assert.Equal("pi", _service.ResolveAgent(config, trigger, evt));
    }

    [Fact]
    public void ResolveAgent_RecipientInterpolation()
    {
        var config = RoutingService.CreateDefaultConfig();
        var trigger = new RoutingTrigger { Event = "x", DispatchTo = "{recipient}" };
        var evt = new DispatchEvent
        {
            EventKind = "x",
            ProjectId = "proj",
            Recipient = "claude-code"
        };

        Assert.Equal("claude-code", _service.ResolveAgent(config, trigger, evt));
    }

    [Fact]
    public void ResolveAgent_RecipientNull_ReturnsNull()
    {
        var config = RoutingService.CreateDefaultConfig();
        var trigger = new RoutingTrigger { Event = "x", DispatchTo = "{recipient}" };
        var evt = new DispatchEvent { EventKind = "x", ProjectId = "proj", Recipient = null };

        Assert.Null(_service.ResolveAgent(config, trigger, evt));
    }

    [Fact]
    public void ResolveAgent_TargetRoleInterpolation()
    {
        var config = RoutingService.CreateDefaultConfig();
        var trigger = new RoutingTrigger { Event = "x", DispatchTo = "{target_role}" };
        var evt = new DispatchEvent
        {
            EventKind = "x",
            ProjectId = "proj",
            MessageTargetRole = "reviewer"
        };

        Assert.Equal("pi", _service.ResolveAgent(config, trigger, evt));
    }

    [Fact]
    public void ResolveAgent_TargetRoleNull_ReturnsNull()
    {
        var config = RoutingService.CreateDefaultConfig();
        var trigger = new RoutingTrigger { Event = "x", DispatchTo = "{target_role}" };
        var evt = new DispatchEvent { EventKind = "x", ProjectId = "proj", MessageTargetRole = null };

        Assert.Null(_service.ResolveAgent(config, trigger, evt));
    }

    [Fact]
    public void ResolveAgent_TargetRoleUnknownRole_ReturnsNull()
    {
        var config = RoutingService.CreateDefaultConfig();
        var trigger = new RoutingTrigger { Event = "x", DispatchTo = "{target_role}" };
        var evt = new DispatchEvent
        {
            EventKind = "x",
            ProjectId = "proj",
            MessageTargetRole = "coordinator"
        };

        Assert.Null(_service.ResolveAgent(config, trigger, evt));
    }

    [Fact]
    public void ResolveAgent_LiteralAgent()
    {
        var config = new RoutingConfig { Roles = new Dictionary<string, string>() };
        var trigger = new RoutingTrigger { Event = "x", DispatchTo = "my-custom-agent" };
        var evt = new DispatchEvent { EventKind = "x", ProjectId = "proj" };

        Assert.Equal("my-custom-agent", _service.ResolveAgent(config, trigger, evt));
    }

    #endregion

    #region InterpolateTemplate

    [Fact]
    public void InterpolateTemplate_AllPlaceholders()
    {
        var template = "Review task #{task_id} ({task_title}) on {branch} in {project_id}. From {sender}, role: {target_role}, intent: {message_intent}, type: {message_type}, packet: {packet_kind}, handoff: {handoff_kind}.";
        var evt = new DispatchEvent
        {
            EventKind = DispatchEvent.TaskStatusChanged,
            ProjectId = "quillforge",
            TaskId = 546,
            TaskTitle = "forge-stats lore accounting",
            Branch = "task/546-forge-stats",
            Sender = "codex",
            MessageTargetRole = "reviewer",
            MessageIntent = MessageIntent.ReviewFeedback,
            MessageType = "review_feedback"
        };

        var result = _service.InterpolateTemplate(template, evt);
        Assert.Equal("Review task #546 (forge-stats lore accounting) on task/546-forge-stats in quillforge. From codex, role: reviewer, intent: review_feedback, type: review_feedback, packet: , handoff: .", result);
    }

    [Fact]
    public void InterpolateTemplate_SubtypePlaceholders()
    {
        var template = "intent={message_intent}; packet={packet_kind}; handoff={handoff_kind}";
        var evt = new DispatchEvent
        {
            EventKind = DispatchEvent.MessageReceived,
            ProjectId = "proj",
            MessageIntent = MessageIntent.ReviewApproval,
            HandoffKind = "merge_request",
            PacketKind = "review_findings"
        };

        var result = _service.InterpolateTemplate(template, evt);
        Assert.Equal("intent=review_approval; packet=review_findings; handoff=merge_request", result);
    }

    [Fact]
    public void InterpolateTemplate_MissingValues_FallbackBranch()
    {
        var template = "Work on branch {branch}";
        var evt = new DispatchEvent
        {
            EventKind = DispatchEvent.TaskStatusChanged,
            ProjectId = "proj",
            TaskId = 42,
            Branch = null
        };

        var result = _service.InterpolateTemplate(template, evt);
        Assert.Equal("Work on branch task/42-*", result);
    }

    [Fact]
    public void InterpolateTemplate_StatusPlaceholders()
    {
        var template = "Task moved from {from_status} to {to_status}";
        var evt = new DispatchEvent
        {
            EventKind = DispatchEvent.TaskStatusChanged,
            ProjectId = "proj",
            FromStatus = "review",
            ToStatus = "planned"
        };

        var result = _service.InterpolateTemplate(template, evt);
        Assert.Equal("Task moved from review to planned", result);
    }

    #endregion
}
