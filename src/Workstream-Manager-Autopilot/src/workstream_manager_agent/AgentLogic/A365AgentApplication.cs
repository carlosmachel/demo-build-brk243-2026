namespace WorkstreamManager.AgentLogic;

using WorkstreamManager.AgentLogic.ResponsesApi;
using WorkstreamManager.Models;
using WorkstreamManager.Services;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Core.Models;
using AgentNotification;
using Microsoft.Agents.A365.Notifications.Models;
using System.Collections.Concurrent;
using System.Text.Json;

/// <summary>
/// This is main handler for incoming activities, and is linked to Agent SDK infrastructure.
/// This will need to resolve the incoming activity to the correct agent instance.
/// </summary>
public class A365AgentApplication : AgentApplication
{
    private readonly ResponsesApiAgentLogicServiceFactory _factory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<A365AgentApplication> _logger;

    // Process-local dedupe for message activities. Foundry's hosted-agent passthrough is
    // at-least-once: on a cold-start race the upstream YARP forwarder can return 5xx and
    // Bot Service will retry, but both deliveries land on the same (now-warm) container.
    // Skipping the second delivery prevents duplicate Responses API invocations (which would
    // otherwise share previous_response_id) and duplicate outgoing replies.
    private static readonly ConcurrentDictionary<string, DateTime> _processedActivityIds = new();
    private static readonly TimeSpan _activityDedupeTtl = TimeSpan.FromMinutes(5);
    private static DateTime _nextDedupeSweepUtc = DateTime.UtcNow + TimeSpan.FromMinutes(1);

    public A365AgentApplication(
        AgentApplicationOptions options,
        ResponsesApiAgentLogicServiceFactory factory,
        ILogger<A365AgentApplication> logger,
        IConfiguration configuration) : base(options)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        // Configure the agent to handle message activities
        ConfigureMessageHandling();
        _logger = logger;
    }

    /// <summary>
    /// Configures message handling for the agent.
    /// </summary>
    private void ConfigureMessageHandling()
    {
        // Handle Email notifications using the AgentNotification extension
        this.OnAgenticEmailNotification(async (turnContext, turnState, agentNotificationActivity, cancellationToken) =>
        {
            var agent = await GetAgentFromRecipient(turnContext.Activity);
            var agentService = await _factory.CreateAsync(agent, turnContext, UserAuthorization);
            if (agent.IsMessagingEnabled || true)
            {
                // Use the specific email notification handler
                await agentService.HandleEmailNotificationAsync(turnContext, turnState, agentNotificationActivity);
            }
            else
            {
                await agentService.NewActivityReceived(turnContext, turnState, cancellationToken);
            }
        });

        // Handle Word notifications
        this.OnAgenticWordNotification(async (turnContext, turnState, agentNotificationActivity, cancellationToken) =>
        {
            var agent = await GetAgentFromRecipient(turnContext.Activity);
            var agentService = await _factory.CreateAsync(agent, turnContext, UserAuthorization);

            if (agent.IsMessagingEnabled)
            {
                // Use the specific comment notification handler for Word documents
                await agentService.HandleCommentNotificationAsync(turnContext, turnState, agentNotificationActivity);
            }
            else
            {
                await agentService.NewActivityReceived(turnContext, turnState, cancellationToken);
            }
        });

        // Handle Excel notifications
        this.OnAgenticExcelNotification(async (turnContext, turnState, agentNotificationActivity, cancellationToken) =>
        {
            var agent = await GetAgentFromRecipient(turnContext.Activity);
            var agentService = await _factory.CreateAsync(agent, turnContext, UserAuthorization);

            if (agent.IsMessagingEnabled)
            {
                // Use the specific comment notification handler for Excel documents
                await agentService.HandleCommentNotificationAsync(turnContext, turnState, agentNotificationActivity);
            }
            else
            {
                await agentService.NewActivityReceived(turnContext, turnState, cancellationToken);
            }
        });

        // Handle PowerPoint notifications
        this.OnAgenticPowerPointNotification(async (turnContext, turnState, agentNotificationActivity, cancellationToken) =>
        {
            var agent = await GetAgentFromRecipient(turnContext.Activity);
            var agentService = await _factory.CreateAsync(agent, turnContext, UserAuthorization);

            if (agent.IsMessagingEnabled)
            {
                // Use the specific comment notification handler for PowerPoint documents
                await agentService.HandleCommentNotificationAsync(turnContext, turnState, agentNotificationActivity);
            }
            else
            {
                await agentService.NewActivityReceived(turnContext, turnState, cancellationToken);
            }
        });
        
        OnActivity(ActivityTypes.Message, async (turnContext, turnState, cancellationToken) =>
        {
            // Suppress duplicate deliveries of the same activity (see _processedActivityIds).
            var activityId = turnContext.Activity.Id;
            if (!string.IsNullOrEmpty(activityId))
            {
                var now = DateTime.UtcNow;
                if (!_processedActivityIds.TryAdd(activityId, now))
                {
                    _logger.LogWarning(
                        "Duplicate message activity suppressed. activityId={ActivityId} channelId={ChannelId} conversationId={ConversationId}",
                        activityId,
                        turnContext.Activity.ChannelId,
                        turnContext.Activity.Conversation?.Id);
                    return;
                }

                // Best-effort sweep of expired entries to keep the dictionary bounded.
                if (now >= _nextDedupeSweepUtc)
                {
                    _nextDedupeSweepUtc = now + TimeSpan.FromMinutes(1);
                    var cutoff = now - _activityDedupeTtl;
                    foreach (var kv in _processedActivityIds)
                    {
                        if (kv.Value < cutoff)
                        {
                            _processedActivityIds.TryRemove(kv.Key, out _);
                        }
                    }
                }
            }

            var apChannelId = turnContext.Activity.ChannelId?.ToString();
            var apConversationType = turnContext.Activity.Conversation?.ConversationType;
            var isTeamsChannelActivity = string.Equals(apChannelId, "msteams", StringComparison.OrdinalIgnoreCase)
                && string.Equals(apConversationType, "channel", StringComparison.OrdinalIgnoreCase);

            // Reactions (👍 for "I'm replying", 📌 for "I logged a work item") are posted from
            // the agent logic service after the LLM call returns, so we can distinguish a
            // text-reply turn (👍 + reply) from a silent work-item-capture turn (📌 only).
            // See ReactionService + ResponsesApiAgentLogicService for the gating.

            // Open a Teams "typing indicator" stream for 1:1 chats and non-Teams channels so the
            // user sees the agent is working. We deliberately skip streaming for Teams group chat
            // and channel conversations because the response there flows through SendActivityAsync
            // with @-mention + reply-blockquote entities (see ResponsesApiAgentLogicService); a
            // StreamingResponse.QueueTextChunk cannot carry activity entities, so streaming would
            // strip the groupchat markup. Without an opened stream the channel will simply not
            // show the typing indicator for groupchat replies, which is acceptable.
            var inChannelId = turnContext.Activity.ChannelId?.ToString();
            var inConversationType = turnContext.Activity.Conversation?.ConversationType;
            var inIsGroup = turnContext.Activity.Conversation?.IsGroup;
            var isTeamsGroupOrChannel = string.Equals(inChannelId, "msteams", StringComparison.OrdinalIgnoreCase)
                && (inIsGroup == true
                    || string.Equals(inConversationType, "groupChat", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(inConversationType, "channel", StringComparison.OrdinalIgnoreCase));
            var streamingStarted = false;

            try
            {
                _logger.LogInformation("Received message activity: {ActivityId} from {UserId} activity {activity}", turnContext.Activity.Id, turnContext.Activity.From?.Id, System.Text.Json.JsonSerializer.Serialize(turnContext.Activity));
                // Based on the recipient, determine which agent to use
                var agent = await GetAgentFromRecipient(turnContext.Activity);

                if (isTeamsChannelActivity)
                {
                    _logger.LogInformation(
                        "Teams channel message received via Activity Protocol; bootstrapped channel watcher and skipping AP reaction/response. activityId={ActivityId} conversationId={ConversationId}",
                        turnContext.Activity.Id,
                        turnContext.Activity.Conversation?.Id);
                    return;
                }

                // Get agent logic service from factory
                var agentService = await _factory.CreateAsync(agent, turnContext, UserAuthorization);

                // Ignoring all other channel Ids to prevent duplicate notifications.
                if (agent.IsMessagingEnabled && turnContext.Activity.ChannelId != "msteams")
                {
                    return;
                }

                // Let the user know the agent is working on the prompt. This is a no-op on
                // channels that don't support streaming/typing updates. Gated behind the
                // EnableStreamingUpdates config flag (default false): when false the response
                // is delivered as a single SendActivityAsync, when true Adi's streaming pipeline
                // surfaces a "Working on your request..." typing indicator that resolves to the
                // streamed text. Disabled by default because in this agent it produces a second
                // bubble alongside the actual reply.
                var enableStreamingUpdates = _configuration.GetValue<bool>("EnableStreamingUpdates");
                if (!isTeamsGroupOrChannel && enableStreamingUpdates)
                {
                    await turnContext.StreamingResponse.QueueInformativeUpdateAsync(
                        "Working on your request...",
                        cancellationToken);
                    streamingStarted = true;
                }

                // Execute logic
                await agentService.NewActivityReceived(turnContext, turnState, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message activity");

                // Surface the hosted-agent session id alongside the exception so the user can
                // hand it to support / look it up in agent logs.
                var sessionId = Environment.GetEnvironmentVariable("FOUNDRY_AGENT_SESSION_ID");
                var sessionLine = string.IsNullOrEmpty(sessionId) ? "(not set)" : sessionId;
                var errorText =
                    $"Sorry, something went wrong while processing your message.{Environment.NewLine}" +
                    $"FOUNDRY_AGENT_SESSION_ID: {sessionLine}{Environment.NewLine}" +
                    $"Exception:{Environment.NewLine}{ex}";

                if (streamingStarted)
                {
                    try
                    {
                        turnContext.StreamingResponse.QueueTextChunk(errorText);
                    }
                    catch (Exception streamEx)
                    {
                        _logger.LogWarning(streamEx, "Failed to queue error text on streaming response");
                    }
                }
                else
                {
                    await turnContext.SendActivitiesAsync(new IActivity[]
                    {
                        new Activity
                        {
                            Type = ActivityTypes.Message,
                            Text = errorText
                        }
                    }, cancellationToken);
                }
            }
            finally
            {
                if (streamingStarted)
                {
                    try
                    {
                        // Always finalize the stream so the channel renders a final message
                        // instead of falling back to "No text was streamed".
                        await turnContext.StreamingResponse.EndStreamAsync(cancellationToken);
                    }
                    catch (Exception streamEx)
                    {
                        _logger.LogWarning(streamEx, "Failed to end streaming response");
                    }
                }
            }
        });

        // Keep existing handlers for backward compatibility
        OnActivity(ActivityTypes.Event, async (turnContext, turnState, cancellationToken) =>
        {
            var agent = await GetAgentFromRecipient(turnContext.Activity);
            var agentService = await _factory.CreateAsync(agent, turnContext, UserAuthorization);

            await agentService.NewActivityReceived(turnContext, turnState, cancellationToken);
        });

        OnActivity(ActivityTypes.InstallationUpdate, async (turnContext, turnState, cancellationToken) =>
        {
            var agent = await GetAgentFromRecipient(turnContext.Activity);
            var agentService = await _factory.CreateAsync(agent, turnContext, UserAuthorization);

            if (agent.IsMessagingEnabled)
            {
				// Create AgentNotificationActivity for installation updates
				var agentNotificationActivity = new AgentNotificationActivity(turnContext.Activity);
				await agentService.HandleInstallationUpdateAsync(turnContext, turnState, agentNotificationActivity);
            }
            else
            {
                await agentService.NewActivityReceived(turnContext, turnState, cancellationToken);
			}
		});
    }

    private async Task<AgentMetadata> GetAgentFromRecipient(IActivity activity)
    {
        ChannelAccount recipient = activity.Recipient;
        ConversationAccount conversation = activity.Conversation;

        if (recipient == null)  
        {
            throw new ArgumentNullException(nameof(recipient), "Recipient cannot be null.");
        }

        // Recipient will have an ID, but this may not be sufficient to determine the agent.
        // ChannelAccount recipient currently has an AadObjectId, which we can try using to identify the user.
        // If activityProtocol and SDK is changed to pass a new field, we can update this code to use that instead.
        var aadObjectId = Guid.TryParse(recipient.AadObjectId, out var parsedId) ? parsedId : Guid.Empty;
        var id = recipient.Id;
        var tenantId = Guid.TryParse(conversation.TenantId, out var parsedTenantId) ? parsedTenantId : Guid.Empty;
        var metadata = ConstructAgentMetadataFromActivity(activity);

        return metadata;
    }

    private AgentMetadata ConstructAgentMetadataFromActivity(IActivity activity)
    {
        if (activity == null)
        {
            throw new ArgumentNullException(nameof(activity), "Activity cannot be null.");
        }

        var recipient = activity.Recipient;
        var conversation = activity.Conversation;

        if (recipient == null || conversation == null)
        {
            throw new ArgumentException("Activity must have a recipient and conversation.");
        }

        var tenantId = Guid.TryParse(recipient.TenantId, out var parsedTenantId) ? parsedTenantId : Guid.Empty;

        // AAI
        var agenticAppId = Guid.TryParse(recipient.AgenticAppId, out var parsedAgenticAppId) ? parsedAgenticAppId : Guid.Empty;

        // This could be agentic user email?
        var agenticUserId = recipient.AgenticUserId ?? recipient.AadObjectId;



        return new AgentMetadata
        {
            UserId = Guid.Parse(agenticUserId),
            EmailId = recipient.Id.Contains('@') ? recipient.Id : recipient.Name,
            AgentId = agenticAppId,
            AgentApplicationId = recipient.Properties.TryGetValue("agenticAppBlueprintId", out var agentAppBlueprintId) ? Guid.Parse(agentAppBlueprintId.ToString()) : Guid.TryParse(recipient.Id, out var parsedId) ? parsedId : Guid.Empty,
            TenantId = tenantId,
        };
    }
}

