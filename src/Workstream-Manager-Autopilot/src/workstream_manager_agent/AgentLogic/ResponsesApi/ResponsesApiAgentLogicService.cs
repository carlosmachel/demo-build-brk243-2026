namespace WorkstreamManager.AgentLogic.ResponsesApi;

using WorkstreamManager.Models;
using WorkstreamManager.Services;
using WorkstreamManager.AgentLogic.ResponsesApi.Helpers;
using Microsoft.Agents.A365.Notifications.Models;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core.Models;
using System.Text.Json;

/// <summary>
/// OpenAI Responses API-based implementation of AgentLogicService.
/// Uses MCP tool definitions directly via the Responses API's native MCP support.
/// </summary>
public class ResponsesApiAgentLogicService : IAgentLogicService
{
    private readonly ILogger _logger;
    private readonly IConfiguration _configuration;
    private readonly ResponsesApiClient _responsesApiClient;
    private readonly WorkItemToolHandler _workItemTools;
    private readonly TeamsActivityHelper _teamsHelper;
    private readonly AccessControlService _accessControl;
    private readonly AddressedToAgentGate _addressedToAgentGate;
    private readonly ReactionService _reactionService;

    public ResponsesApiAgentLogicService(
        AgentMetadata agent,
        IConfiguration configuration,
        ILogger logger,
        string accessToken,
        List<McpServerConfig> mcpServers,
        string? graphAccessToken = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var agentMetadata = agent ?? throw new ArgumentNullException(nameof(agent));

        var httpClient = new HttpClient();
        _responsesApiClient = new ResponsesApiClient(agentMetadata, _logger, _configuration, accessToken, mcpServers, httpClient);
        _reactionService = new ReactionService(_logger, graphAccessToken, httpClient);

        // Initialize WorkItemToolHandler
        WorkItemService? workItemService = null;
        var workItemsTableServiceUri = configuration["WorkItemsTableServiceUri"];
        if (!string.IsNullOrEmpty(workItemsTableServiceUri))
        {
            workItemService = new WorkItemService(configuration, new LoggerFactory().CreateLogger<WorkItemService>());
        }
        _workItemTools = new WorkItemToolHandler(agentMetadata, _logger, graphAccessToken, httpClient, workItemService, _reactionService);
        _teamsHelper = new TeamsActivityHelper(_logger);
        _accessControl = new AccessControlService(agentMetadata, _logger, _configuration, graphAccessToken, httpClient, _teamsHelper, _workItemTools);
        _addressedToAgentGate = new AddressedToAgentGate(_logger, _configuration, _responsesApiClient, _teamsHelper, httpClient, graphAccessToken);
    }

    public async Task NewActivityReceived(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        var incomingText = turnContext.Activity.Text;
        _logger.LogInformation("New activity received (Responses API): {IncomingText}", incomingText);

        var sender = turnContext.Activity.From;
        var rawUserMessage = incomingText ?? string.Empty;

        // Global AP tenant guard: if we can determine that the sender is from outside this
        // digital worker's tenant, return a deterministic canned response and skip LLM work.
        if (await _accessControl.TryHandleCrossTenantActivityAsync(turnContext, cancellationToken))
        {
            return;
        }

        if (turnContext.Activity.ChannelId == "email" || turnContext.Activity.ChannelId == "agents:email")
        {
            var subject = string.Empty;
            if (turnContext.Activity.ChannelData is JsonElement jsonElement && jsonElement.TryGetProperty("subject", out var subjectProperty))
            {
                subject = subjectProperty.GetString() ?? string.Empty;
            }
            incomingText = $"Please respond to this email From: {sender!.Id}\nSubject: {subject}\nMessage: {incomingText}";
        }
        else if (turnContext.Activity.ChannelId == "msteams")
        {
            incomingText = $"Respond to this chat message with chat id {turnContext.Activity.Conversation.Id} " +
                           $"From: {sender?.Name} ({sender?.Id})\n" +
                           $"Message: {incomingText}";
        }
        else if (turnContext.Activity.Type == ActivityTypes.InstallationUpdate)
        {
            incomingText = $"You were just added as a digital worker. Please send an email to {sender!.Id} with information on what you can do.";
        }

        var conversationId = turnContext.Activity.Conversation?.Id ?? "default";
        // Optional DM access control: in Teams 1:1 chats, only this digital worker's resolved
        // manager can trigger an LLM call. Everyone else gets a deterministic canned response.
        if (await _accessControl.TryHandleRestrictedDirectMessageAsync(turnContext, cancellationToken))
        {
            return;
        }

        // Optional group-chat access control: in Teams group chats, every participant must be
        // manager-approved (manager or allowlisted) before any LLM-based processing occurs.
        if (await _accessControl.TryHandleRestrictedGroupChatAsync(turnContext, cancellationToken))
        {
            return;
        }

        // Only respond if the message is actually addressed to this agent. In 1:1 personal
        // chats every message is by definition agent-directed; in group chats / channels we
        // use a mix of structured @-mention detection + a YES/NO LLM judge so the agent only
        // chimes in when actually named (or referenced via "you" in an active thread it has
        // been participating in). The verdict also captures whether the user explicitly
        // @-mentioned the agent so the response can mirror that addressing register.
        var verdict = await _addressedToAgentGate.ShouldRespondAsync(turnContext, rawUserMessage, conversationId);
        if (!verdict.ShouldRespond)
        {
            _logger.LogInformation(
                "Skipping text reply: message not addressed to agent. Running passive work-item detection. activityId={ActivityId} channelId={ChannelId} conversationId={ConversationId}",
                turnContext.Activity.Id,
                turnContext.Activity.ChannelId,
                conversationId);

            // Even when the agent isn't being addressed, passively scan the message for
            // commitments worth tracking. If a work item gets captured, the 📌 reaction fires
            // automatically and the agent stays silent (no text reply). This lets the agent
            // act as an autopilot observer of the workstream without intruding on every side
            // conversation.
            await TryPassiveWorkItemDetectionAsync(turnContext, rawUserMessage, conversationId);
            return;
        }

        // Capture activity context so work-item creation can target the originating message
        // with a 📌 reaction.
        _workItemTools.SetCurrentActivityContext(turnContext.Activity);

        var response = await _responsesApiClient.InvokeAsync(
            input: incomingText ?? string.Empty,
            conversationId: conversationId,
            additionalTools: _workItemTools.GetToolDefinitions(),
            localToolExecutor: _workItemTools.TryExecuteAsync);

        // For Teams group chat / channel we send a regular activity so the groupchat features
        // (@-mention entity + Teams reply blockquote) flow through unchanged. StreamingResponse
        // .QueueTextChunk delivers text only, not activity entities, so it cannot carry mention
        // markup. For 1:1 chats and other channels we use the streaming text path so the typing
        // indicator the Message handler opened in A365AgentApplication has a final chunk to
        // render and the channel does not display "No text was streamed".
        //
        // The streaming path is additionally gated on the EnableStreamingUpdates config flag.
        // The Message handler only opens a stream (via QueueInformativeUpdateAsync) when that
        // flag is true; if we queued text here while the flag is false the channel would have
        // no opened stream to render into, so we must fall through to SendActivityAsync instead.
        var enableStreamingUpdates = _configuration.GetValue<bool>("EnableStreamingUpdates");
        var outChannelId = turnContext.Activity.ChannelId?.ToString();
        var outConversationType = turnContext.Activity.Conversation?.ConversationType;
        var outIsGroup = turnContext.Activity.Conversation?.IsGroup;
        var isTeamsGroupOrChannel = string.Equals(outChannelId, "msteams", StringComparison.OrdinalIgnoreCase)
            && (outIsGroup == true
                || string.Equals(outConversationType, "groupChat", StringComparison.OrdinalIgnoreCase)
                || string.Equals(outConversationType, "channel", StringComparison.OrdinalIgnoreCase));

        // Empty response = silent turn (e.g. work-item-only capture - the 📌 reaction emitted
        // during create_work_item is the entire user-visible signal). Don't send anything,
        // don't 👍. 👍 is paired with the text reply so the two signals stay mutually exclusive
        // with 📌 (the work-item capture indicator).
        if (!string.IsNullOrEmpty(response))
        {
            // Skip the 👍 if a work item was captured this turn - 📌 wins. Teams' setReaction
            // allows only one reaction per bot per message, so firing both would overwrite the
            // 📌 with 👍 and the user would lose the "this was tracked" signal. Common case:
            // user says "Track this: ..." and the LLM both creates the work item AND replies
            // with a confirmation.
            if (!_workItemTools.WorkItemCreatedThisTurn)
            {
                // Fire 👍 in parallel with the outbound message so we don't delay delivery.
                // CancellationToken.None because the turn's token gets disposed when this method
                // returns and we want the reaction POST to complete on its own.
                _ = _reactionService.SetReactionAsync("\uD83D\uDC4D", turnContext.Activity, CancellationToken.None);
            }

            if (turnContext.Activity.Type == ActivityTypes.Message && !isTeamsGroupOrChannel && enableStreamingUpdates)
            {
                turnContext.StreamingResponse.QueueTextChunk(response);
            }
            else
            {
                var outboundActivity = _teamsHelper.BuildResponseActivity(
                    turnContext,
                    response,
                    includeMention: verdict.WasExplicitlyMentioned);
                await turnContext.SendActivityAsync(outboundActivity, cancellationToken);
            }
        }
    }

    public async Task<string> NewEmailReceived(string fromEmail, string subject, string messageBody)
    {
        var formattedMessage = $"Please respond to this email From: {fromEmail}\nSubject: {subject}\nMessage: {messageBody}";
        return await _responsesApiClient.InvokeAsync(
            input: formattedMessage,
            conversationId: $"email:{fromEmail}:{subject}",
            additionalTools: _workItemTools.GetToolDefinitions(),
            localToolExecutor: _workItemTools.TryExecuteAsync);
    }

    /// <summary>
    /// Passive work-item detection pass for messages NOT addressed to the agent. The agent
    /// silently scans the message for commitments worth tracking. If create_work_item is called
    /// the 📌 reaction fires automatically via WorkItemToolHandler. The LLM is instructed to
    /// produce no text response either way - this never adds a chat message. Skipped entirely
    /// when work-item tools aren't configured (no WorkItemsTableServiceUri).
    /// </summary>
    private async Task TryPassiveWorkItemDetectionAsync(
        ITurnContext turnContext,
        string userMessage,
        string conversationId)
    {
        var toolDefinitions = _workItemTools.GetToolDefinitions();
        if (toolDefinitions.Count == 0)
        {
            return;
        }

        if (turnContext.Activity.Type != ActivityTypes.Message)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(userMessage))
        {
            return;
        }

        // Tell WorkItemToolHandler which message to react to if a work item is captured.
        _workItemTools.SetCurrentActivityContext(turnContext.Activity);

        var sender = turnContext.Activity.From;
        var observed =
            $"Observed message in chat {turnContext.Activity.Conversation?.Id} " +
            $"from {sender?.Name} ({sender?.Id}): {userMessage}";

        var instructions =
            "You are a silent observer of a Teams group chat. Your ONLY job is to detect " +
            "commitments and action items mentioned in the message below and, if one is present " +
            "with a clear owner AND a clear deliverable, call create_work_item to track it.\n" +
            "\n" +
            "Examples of trackable commitments (CAPTURE these):\n" +
            "- \"Amanda will file a bug for that.\"\n" +
            "- \"Sustineo, remember to add notes to the doc by tomorrow.\"\n" +
            "- \"Can you revise the wording on the Figma screen by Friday?\"\n" +
            "- \"I'll send the recap by EOD.\"\n" +
            "\n" +
            "Do NOT capture:\n" +
            "- Questions, opinions, jokes, or general discussion.\n" +
            "- Past tense / already-completed work (\"I sent that yesterday\").\n" +
            "- Anything without a clear owner OR a clear deliverable.\n" +
            "- Hypothetical or aspirational statements (\"we should probably ...\").\n" +
            "\n" +
            "You MUST return an empty string as your text response. The user is NOT talking to " +
            "you - do NOT greet, confirm, explain, or ask clarifying questions. The 📌 reaction " +
            "posted on create_work_item is the only signal you may produce. If no trackable " +
            "commitment is present, do nothing and return empty.\n" +
            "\n" +
            "When you DO call create_work_item, infer name (short title), description, owner, " +
            "and eta from the message. Convert relative dates (\"tomorrow\", \"end of next week\") " +
            "to absolute ISO 8601 datetimes. If the owner isn't named, do NOT capture (a " +
            "commitment without an owner isn't trackable).";

        try
        {
            _logger.LogInformation(
                "Passive work-item detection: scanning message. activityId={ActivityId} senderName={SenderName} conversationId={ConversationId}",
                turnContext.Activity.Id,
                sender?.Name,
                conversationId);

            await _responsesApiClient.InvokeAsync(
                input: observed,
                conversationId: conversationId,
                instructionsOverride: instructions,
                includeMcpTools: false,
                persistResponseId: false,
                additionalTools: toolDefinitions,
                localToolExecutor: _workItemTools.TryExecuteAsync);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Passive work-item detection failed; ignoring. activityId={ActivityId} conversationId={ConversationId}", turnContext.Activity.Id, conversationId);
        }
    }

    public async Task<string> NewChatReceived(string chatId, string fromUser, string messageBody)
    {
        var formattedMessage = $"Respond to this chat message with chat id {chatId} " +
                               $"From: {fromUser}\nMessage: {messageBody}";
        return await _responsesApiClient.InvokeAsync(
            input: formattedMessage,
            conversationId: chatId,
            additionalTools: _workItemTools.GetToolDefinitions(),
            localToolExecutor: _workItemTools.TryExecuteAsync);
    }

    public Task HandleEmailNotificationAsync(ITurnContext turnContext, ITurnState turnState, AgentNotificationActivity emailEvent)
    {
        // Email feedback loop guard: previously this handler invoked the Responses API and called
        // turnContext.SendActivityAsync(...) with the result. In practice the inbound notification's
        // turnContext is the originating Teams chat/channel, so the Responses API output (often a
        // self-narration like "Your reply has been successfully sent to <user>") was being echoed
        // back into the same Teams thread that prompted the original message - producing duplicate
        // / "talking to itself" messages with every interaction.
        //
        // For now we acknowledge the notification and return without sending anything. To restore
        // real email-reply behavior in the future, route the response through an email-sending API
        // (e.g. Microsoft Graph /sendMail) rather than turnContext.SendActivityAsync, or first
        // verify turnContext.Activity.ChannelId is genuinely an email channel before sending.
        _logger.LogInformation(
            "Skipping email notification response to avoid Teams feedback loop. NotificationType: {NotificationType}, ChannelId: {ChannelId}",
            emailEvent.NotificationType,
            turnContext.Activity?.ChannelId);
        return Task.CompletedTask;
    }

    public Task HandleCommentNotificationAsync(ITurnContext turnContext, ITurnState turnState, AgentNotificationActivity commentEvent)
    {
        _logger.LogInformation("Processing comment notification (Responses API)");
        return Task.CompletedTask;
    }

    public Task HandleTeamsMessageAsync(ITurnContext turnContext, ITurnState turnState, AgentNotificationActivity teamsEvent)
    {
        _logger.LogInformation("Processing Teams message (Responses API)");
        return Task.CompletedTask;
    }

    public Task HandleInstallationUpdateAsync(ITurnContext turnContext, ITurnState turnState, AgentNotificationActivity installationEvent)
    {
        _logger.LogInformation("Processing installation update (Responses API)");
        return Task.CompletedTask;
    }

}

