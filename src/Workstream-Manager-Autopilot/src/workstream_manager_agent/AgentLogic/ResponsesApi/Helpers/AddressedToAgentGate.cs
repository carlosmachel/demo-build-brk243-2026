namespace WorkstreamManager.AgentLogic.ResponsesApi.Helpers;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

/// <summary>
/// Decides whether the agent should send a reply for the current activity. Adapted from the
/// "thwagne" hello-world A365 sample. Always returns true for unambiguously agent-directed
/// activities (email, installation updates, 1:1 Teams chats, explicit @-mention of the agent).
/// For multi-participant Teams contexts an LLM-based judge is consulted so the agent only
/// chimes in when named or referenced by "you" in a thread it has been participating in.
///
/// This gate intentionally runs AFTER AccessControlService so unauthorized users keep getting
/// the canned access-control responses, and only authorized chatter falls through to "should
/// the agent respond?" filtering.
/// </summary>
internal class AddressedToAgentGate
{
    private readonly ILogger _logger;
    private readonly IConfiguration _configuration;
    private readonly ResponsesApiClient _responsesApiClient;
    private readonly TeamsActivityHelper _teamsHelper;
    private readonly HttpClient _httpClient;
    private readonly string? _graphAccessToken;

    // Per-process cache of "this bot's display name in this chat", keyed by conversationId.
    // The bot's chat-specific display name is set by whoever installed/added the bot and is
    // NOT delivered on inbound activities (Recipient.Name is null for agenticUser deliveries),
    // so we resolve it via Graph chat-members on first need and remember the answer.
    private static readonly ConcurrentDictionary<string, string?> BotDisplayNameCache = new();

    internal AddressedToAgentGate(
        ILogger logger,
        IConfiguration configuration,
        ResponsesApiClient responsesApiClient,
        TeamsActivityHelper teamsHelper,
        HttpClient httpClient,
        string? graphAccessToken)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _responsesApiClient = responsesApiClient ?? throw new ArgumentNullException(nameof(responsesApiClient));
        _teamsHelper = teamsHelper ?? throw new ArgumentNullException(nameof(teamsHelper));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _graphAccessToken = graphAccessToken;
    }

    internal async Task<AddressedVerdict> ShouldRespondAsync(
        ITurnContext turnContext,
        string? userMessage,
        string conversationId)
    {
        var activity = turnContext.Activity;
        var channelId = activity.ChannelId;
        var activityType = activity.Type;
        var conversation = activity.Conversation;
        var conversationType = conversation?.ConversationType;
        var isGroup = conversation?.IsGroup;
        var recipient = activity.Recipient;
        var sender = activity.From;

        var mentions = _teamsHelper.ExtractMentions(activity);
        var mentionedIds = string.Join(",", mentions.Select(m => m.MentionedId ?? "(null)"));
        var mentionedNames = string.Join(",", mentions.Select(m => m.MentionedName ?? "(null)"));

        // Teams-style <at>NAME</at> markup parsed from the raw text. Most reliable signal -
        // some agentic Teams deliveries strip Mentioned/Text from the strongly-typed mention
        // entities, leaving the entity collection effectively empty even when the message
        // clearly contains @-mentions in its body.
        var atTagNames = TeamsActivityHelper.ExtractAtTagNames(userMessage);
        var atTagNamesJoined = string.Join(",", atTagNames);

        var (recipientAgenticUserId, recipientAgenticAppId, recipientBotId, recipientRole) =
            _teamsHelper.ExtractRecipientAgenticIdentifiers(recipient);

        _logger.LogInformation(
            "ShouldRespond: evaluating. activityId={ActivityId} channelId={ChannelId} activityType={ActivityType} " +
            "conversationId={ConversationId} conversationType={ConversationType} isGroup={IsGroup} " +
            "botRecipientId={RecipientId} botRecipientAadObjectId={RecipientAadObjectId} botRecipientName={RecipientName} " +
            "botRecipientRole={RecipientRole} botAgenticUserId={AgenticUserId} botAgenticAppId={AgenticAppId} botId={BotId} " +
            "senderId={SenderId} senderAadObjectId={SenderAadObjectId} senderName={SenderName} " +
            "mentionCount={MentionCount} mentionedIds=[{MentionedIds}] mentionedNames=[{MentionedNames}] " +
            "atTagCount={AtTagCount} atTagNames=[{AtTagNames}] textLength={TextLength}",
            activity.Id,
            channelId,
            activityType,
            conversationId,
            conversationType,
            isGroup,
            recipient?.Id,
            recipient?.AadObjectId,
            recipient?.Name,
            recipientRole,
            recipientAgenticUserId,
            recipientAgenticAppId,
            recipientBotId,
            sender?.Id,
            sender?.AadObjectId,
            sender?.Name,
            mentions.Count,
            mentionedIds,
            mentionedNames,
            atTagNames.Count,
            atTagNamesJoined,
            (userMessage ?? string.Empty).Length);

        // Email channels: every inbound email is by definition addressed to the recipient.
        if (channelId == "email" || channelId == "agents:email")
        {
            _logger.LogInformation("ShouldRespond: YES (short-circuit: email channel '{ChannelId}')", channelId);
            return AddressedVerdict.Respond();
        }

        // System / non-message activities (installation updates, etc.) are agent-directed.
        if (activityType != ActivityTypes.Message)
        {
            _logger.LogInformation(
                "ShouldRespond: YES (short-circuit: non-message activityType '{ActivityType}')",
                activityType);
            return AddressedVerdict.Respond();
        }

        // Explicit @-mention of this agent (structured mention entity from Teams identifies
        // the recipient by id). When present this is a definitive signal - no LLM needed.
        if (recipient != null && !string.IsNullOrEmpty(recipient.Id))
        {
            var candidateIds = new[]
            {
                recipient.Id,
                recipient.AadObjectId,
                recipientAgenticUserId,
                recipientAgenticAppId,
                recipientBotId,
            }.Where(s => !string.IsNullOrEmpty(s)).ToArray();

            var matchedMention = mentions.FirstOrDefault(m =>
                !string.IsNullOrEmpty(m.MentionedId) &&
                candidateIds.Any(c => string.Equals(m.MentionedId, c, StringComparison.OrdinalIgnoreCase)));
            if (matchedMention != null)
            {
                _logger.LogInformation(
                    "ShouldRespond: YES (short-circuit: agent @-mentioned by id). botRecipientId={RecipientId} matchedMentionId={MatchedId} mentionText='{MentionText}'",
                    recipient.Id,
                    matchedMention.MentionedId,
                    matchedMention.Text);
                return AddressedVerdict.RespondWithExplicitMention();
            }

            if (mentions.Count > 0)
            {
                _logger.LogInformation(
                    "ShouldRespond: structured mentions present but none matched the agent by id. " +
                    "candidateBotIds=[{CandidateIds}] mentionedIds=[{MentionedIds}] mentionedNames=[{MentionedNames}]. " +
                    "Continuing with other heuristics.",
                    string.Join(",", candidateIds),
                    mentionedIds,
                    mentionedNames);
            }
        }

        var configuredAliases = GetConfiguredAgentAliases();
        var knownMentionNames = new List<string?>
        {
            recipient?.Name,
            "FoundryDigitalWorker",
        }
            .Concat(configuredAliases)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var matchedAtTagName = atTagNames.FirstOrDefault(tag =>
            knownMentionNames.Any(name => MentionNamesMatch(tag, name!)));
        if (matchedAtTagName is not null)
        {
            _logger.LogInformation(
                "ShouldRespond: YES (short-circuit: parsed @-mention matched known agent name). matchedTag={MatchedTag} knownMentionNames=[{KnownNames}] configuredAliases=[{Aliases}]",
                matchedAtTagName,
                string.Join(",", knownMentionNames),
                string.Join(",", configuredAliases));
            return AddressedVerdict.RespondWithExplicitMention();
        }

        // 1:1 personal chats only have two participants (user + agent), so every message
        // is necessarily directed at the agent.
        var isPersonalChat = string.Equals(conversationType, "personal", StringComparison.OrdinalIgnoreCase)
            || isGroup == false;
        if (channelId == "msteams" && isPersonalChat)
        {
            _logger.LogInformation(
                "ShouldRespond: YES (short-circuit: Teams personal chat). conversationType={ConversationType} isGroup={IsGroup}",
                conversationType,
                isGroup);
            return AddressedVerdict.Respond();
        }

        // NOTE: intentionally NOT short-circuiting NO on "Teams group chat + any <at> markup".
        // Empirically Teams delivers group-chat messages to an agenticUser bot even when the
        // user @-mentioned a different participant, so <at> tags alone aren't reliable as a
        // "addressed to me" signal. The LLM judge below is given the full list of @-tagged
        // names plus the agent's known names so it can decide name-by-name.

        _logger.LogInformation(
            "ShouldRespond: no short-circuit matched - delegating to LLM judge. channelId={ChannelId} conversationType={ConversationType} isGroup={IsGroup}",
            channelId,
            conversationType,
            isGroup);

        return await IsAddressedToAgentAsync(turnContext, userMessage ?? string.Empty, conversationId);
    }

    /// <summary>
    /// Uses the Responses API as a lightweight classifier to determine whether the most recent
    /// user message is addressed to this agent. The judge call shares the conversation's
    /// previous_response_id so it can resolve pronouns like "you" using prior chat history,
    /// but its own response id is NOT persisted, leaving the main conversation chain untouched.
    /// </summary>
    private async Task<AddressedVerdict> IsAddressedToAgentAsync(
        ITurnContext turnContext,
        string userMessage,
        string conversationId)
    {
        var agentName = turnContext.Activity.Recipient?.Name;
        if (string.IsNullOrWhiteSpace(agentName))
        {
            agentName = "FoundryDigitalWorker";
        }

        var senderName = turnContext.Activity.From?.Name ?? "the sender";
        var trimmedMessage = string.IsNullOrWhiteSpace(userMessage) ? "(no text)" : userMessage.Trim();
        var previousResponseId = _responsesApiClient.LoadPreviousResponseId(conversationId);

        var resolvedDisplayName = await TryResolveBotDisplayNameAsync(turnContext, conversationId);
        var aliases = GetConfiguredAgentAliases();
        var allAgentNames = new List<string> { agentName }
            .Concat(string.IsNullOrWhiteSpace(resolvedDisplayName)
                ? Array.Empty<string>()
                : new[] { resolvedDisplayName! })
            .Concat(aliases)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var atTagNames = TeamsActivityHelper.ExtractAtTagNames(userMessage);

        _logger.LogInformation(
            "Judge: invoking classifier. agentName={AgentName} resolvedDisplayName={ResolvedDisplayName} " +
            "aliases=[{Aliases}] allAgentNames=[{AllNames}] atTagNames=[{AtTagNames}] senderName={SenderName} " +
            "conversationId={ConversationId} hasPreviousResponseId={HasPrev} previousResponseId={PrevId} " +
            "messageLength={MessageLength}",
            agentName,
            resolvedDisplayName,
            string.Join(",", aliases),
            string.Join(",", allAgentNames),
            string.Join(",", atTagNames),
            senderName,
            conversationId,
            previousResponseId != null,
            previousResponseId,
            trimmedMessage.Length);

        if (string.IsNullOrWhiteSpace(resolvedDisplayName) && aliases.Count == 0)
        {
            _logger.LogWarning(
                "Judge: bot display name could not be resolved from Graph and no AgentDisplayNameAliases " +
                "are configured. The judge will only see the canonical agent name (\"{AgentName}\"), which " +
                "may not match the name users actually @-mention the bot with in this chat. To fix, ensure " +
                "the bot has Graph permission to read chat members, or set AgentDisplayNameAliases " +
                "(comma-separated) as a fallback.",
                agentName);
        }

        var allAgentNamesJoined = string.Join(", ", allAgentNames.Select(n => $"\"{n}\""));
        var atTagsJoined = atTagNames.Count == 0
            ? "(none)"
            : string.Join(", ", atTagNames.Select(n => $"\"{n}\""));

        var matchedMentionName = atTagNames.FirstOrDefault(tag =>
            allAgentNames.Any(name => MentionNamesMatch(tag, name)));
        if (matchedMentionName is not null)
        {
            _logger.LogInformation(
                "Judge: YES (short-circuit: parsed @-mention matched known agent name). matchedTag={MatchedTag} allAgentNames=[{AllNames}] conversationId={ConversationId}",
                matchedMentionName,
                string.Join(",", allAgentNames),
                conversationId);
            return AddressedVerdict.RespondWithExplicitMention();
        }

        var judgeInstructions =
            "You are a strict binary classifier. Your only job is to decide whether the most " +
            "recent user message in the ongoing conversation is addressed to the agent. The " +
            $"agent is known by ANY of these names/aliases: {allAgentNamesJoined}. Respond with " +
            "exactly one token: YES or NO. No punctuation, no explanation, no other text.";

        var judgeInput =
            $"Agent names/aliases (any of these refers to the agent): {allAgentNamesJoined}\n" +
            $"Sender of the latest message (a human participant): {senderName}\n" +
            $"@-mention tag names parsed from the message: [{atTagsJoined}]\n" +
            "\n" +
            "Note: Teams sometimes splits a multi-word display name across consecutive <at> tags\n" +
            "separated only by whitespace (e.g. <at>workstream</at> <at>manager</at> for the\n" +
            "single display name \"workstream manager\"). The parsed tag-names list above already\n" +
            "includes the space-joined concatenation of any such run as an additional candidate,\n" +
            "but you should ALSO treat adjacent <at> tags in the raw message as potentially\n" +
            "forming one mention when matching against the agent's known names.\n" +
            "\n" +
            "Decide whether the LATEST USER MESSAGE below is addressed to the agent.\n" +
            "Apply these rules IN ORDER:\n" +
            "  1. If any @-mention tag name in the message - taken individually OR as the\n" +
            "     concatenation of consecutive <at> tags separated only by whitespace - matches\n" +
            "     (case-insensitive, allowing small spelling variations) one of the agent's\n" +
            "     known names/aliases above, the message IS addressed to the agent. Answer YES.\n" +
            "  2. If the message contains @-mention tags but NONE of them (individually or as\n" +
            "     concatenated runs) match the agent's known names/aliases, the user is tagging\n" +
            "     other human participants - the message is NOT addressed to the agent. Answer\n" +
            "     NO (unless rule 3 applies decisively).\n" +
            "  3. If there are no @-mention tags, look at the content. The message IS addressed\n" +
            "     to the agent if it uses second person (\"you\", \"your\") and the most\n" +
            "     plausible referent of \"you\" given the prior conversation is the agent (for\n" +
            "     example a reply to something the agent just said). Otherwise, if it's general\n" +
            "     chatter between humans or a side conversation, the message is NOT addressed\n" +
            "     to the agent.\n" +
            "\n" +
            "LATEST USER MESSAGE:\n" +
            trimmedMessage + "\n" +
            "\n" +
            "Answer with exactly YES or NO.";

        try
        {
            var verdict = await _responsesApiClient.InvokeAsync(
                input: judgeInput,
                conversationId: conversationId,
                instructionsOverride: judgeInstructions,
                includeMcpTools: false,
                persistResponseId: false);

            var normalized = (verdict ?? string.Empty).Trim().TrimEnd('.', '!', '?', ',').ToUpperInvariant();
            var isYes = normalized.StartsWith("YES");
            var isNo = normalized.StartsWith("NO");

            _logger.LogInformation(
                "Judge: verdict received. rawVerdict='{Verdict}' normalized='{Normalized}' parsedYes={IsYes} parsedNo={IsNo} " +
                "decision={Decision} agentName={AgentName} allAgentNames=[{AllNames}] atTagNames=[{AtTagNames}] conversationId={ConversationId}",
                verdict,
                normalized,
                isYes,
                isNo,
                isYes ? "RESPOND" : "SKIP",
                agentName,
                string.Join(",", allAgentNames),
                string.Join(",", atTagNames),
                conversationId);

            // Judge-decided YES = user did NOT explicitly @-mention the agent (we'd have
            // short-circuited above). Reply via blockquote but skip the @-mention.
            return isYes ? AddressedVerdict.Respond() : AddressedVerdict.Skip();
        }
        catch (Exception ex)
        {
            // If the judge itself fails, fall back to responding so the agent doesn't appear
            // unresponsive due to a transient classifier failure.
            _logger.LogWarning(
                ex,
                "Judge: classifier call failed; defaulting to RESPOND=true. conversationId={ConversationId}",
                conversationId);
            return AddressedVerdict.Respond();
        }
    }

    /// <summary>
    /// Resolves the bot's per-chat display name (the name end-users actually @-mention it with)
    /// by querying Microsoft Graph chat members. The chat-specific display name is set by whoever
    /// added the bot to the chat - it can differ from instance to instance - and is NOT delivered
    /// on inbound activities for agenticUser bots. We match each member's userId against the bot's
    /// recipient identifiers and return the matching member's displayName. Cached per-conversationId
    /// for process lifetime. Returns null if we don't have a Graph token, the channel isn't Teams,
    /// or no member matches.
    /// </summary>
    private async Task<string?> TryResolveBotDisplayNameAsync(ITurnContext turnContext, string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(_graphAccessToken))
        {
            _logger.LogDebug("Skipping Graph display-name lookup: no Graph access token available.");
            return null;
        }

        var channelId = turnContext.Activity.ChannelId?.ToString();
        if (!string.Equals(channelId, "msteams", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (BotDisplayNameCache.TryGetValue(conversationId, out var cached))
        {
            _logger.LogInformation(
                "Bot display name resolution: cache hit. conversationId={ConversationId} displayName={DisplayName}",
                conversationId,
                cached);
            return cached;
        }

        var recipient = turnContext.Activity.Recipient;
        var candidateIds = _teamsHelper.GetBotCandidateIds(recipient);

        if (candidateIds.Count == 0)
        {
            _logger.LogWarning(
                "Bot display name resolution: no recipient identifiers to match against. conversationId={ConversationId}",
                conversationId);
            BotDisplayNameCache[conversationId] = null;
            return null;
        }

        var url = $"https://graph.microsoft.com/v1.0/chats/{Uri.EscapeDataString(conversationId)}/members";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _graphAccessToken);
            using var resp = await _httpClient.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Bot display name resolution: Graph chat-members lookup failed. conversationId={ConversationId} status={Status} body={Body}",
                    conversationId,
                    (int)resp.StatusCode,
                    body);
                BotDisplayNameCache[conversationId] = null;
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("value", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning(
                    "Bot display name resolution: Graph response missing 'value' array. conversationId={ConversationId} body={Body}",
                    conversationId,
                    body);
                BotDisplayNameCache[conversationId] = null;
                return null;
            }

            string? botDisplay = null;
            string? matchedOn = null;
            var memberCount = 0;
            foreach (var m in arr.EnumerateArray())
            {
                memberCount++;
                var memberDisplay = m.TryGetProperty("displayName", out var dnProp) && dnProp.ValueKind == JsonValueKind.String
                    ? dnProp.GetString()
                    : null;
                var memberUserId = m.TryGetProperty("userId", out var uidProp) && uidProp.ValueKind == JsonValueKind.String
                    ? uidProp.GetString()
                    : null;
                var memberId = m.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                    ? idProp.GetString()
                    : null;

                if (!string.IsNullOrEmpty(memberUserId) && candidateIds.Contains(memberUserId))
                {
                    botDisplay = memberDisplay;
                    matchedOn = $"userId={memberUserId}";
                    break;
                }
                if (!string.IsNullOrEmpty(memberId) && candidateIds.Contains(memberId))
                {
                    botDisplay = memberDisplay;
                    matchedOn = $"id={memberId}";
                    break;
                }
            }

            _logger.LogInformation(
                "Bot display name resolution: Graph chat-members lookup complete. conversationId={ConversationId} memberCount={MemberCount} candidateIds=[{Candidates}] resolved={Resolved} matchedOn={MatchedOn}",
                conversationId,
                memberCount,
                string.Join(",", candidateIds),
                botDisplay,
                matchedOn);

            BotDisplayNameCache[conversationId] = botDisplay;
            return botDisplay;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Bot display name resolution: exception calling Graph chat-members. conversationId={ConversationId}",
                conversationId);
            // Do NOT cache the failure permanently - a transient Graph error shouldn't doom
            // this chat for the lifetime of the process. Caller will retry on the next message.
            return null;
        }
    }

    /// <summary>
    /// Reads the configured Teams display-name aliases the agent should respond to. Operators
    /// set these via the "AgentDisplayNameAliases" config setting (comma- or semicolon-separated)
    /// because the bot's display name in a Teams group chat is set by whoever added the bot and
    /// is NOT delivered on the inbound activity (Recipient.Name is null for agenticUser deliveries).
    /// Without aliases the LLM judge can only match the configured agent name from instructions.
    /// </summary>
    private List<string> GetConfiguredAgentAliases()
    {
        var raw = _configuration["AgentDisplayNameAliases"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new List<string>();
        }
        return raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool MentionNamesMatch(string mentionName, string agentName)
    {
        var normalizedMention = NormalizeMentionName(mentionName);
        var normalizedAgentName = NormalizeMentionName(agentName);
        return normalizedMention.Length > 0 &&
               string.Equals(normalizedMention, normalizedAgentName, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeMentionName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        var chars = value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray();
        return new string(chars);
    }
}

/// <summary>
/// Verdict from <see cref="AddressedToAgentGate.ShouldRespondAsync"/>. Combines the should-reply
/// decision with whether the user explicitly @-mentioned the agent (vs. the agent inferring it
/// was being addressed via the LLM judge or by virtue of being the only other participant in a
/// 1:1 chat). Downstream code uses <see cref="WasExplicitlyMentioned"/> to decide whether to
/// @-mention the sender back in the response.
/// </summary>
internal readonly record struct AddressedVerdict(bool ShouldRespond, bool WasExplicitlyMentioned)
{
    /// <summary>
    /// Respond to the message but the user did not explicitly @-mention the agent (e.g. 1:1 DM,
    /// email, installation update, or the LLM judge inferred the message was addressed to the
    /// agent). The agent's reply should NOT prepend an @-mention of the sender.
    /// </summary>
    public static AddressedVerdict Respond() => new(ShouldRespond: true, WasExplicitlyMentioned: false);

    /// <summary>
    /// Respond to the message AND the user explicitly @-mentioned the agent (structured Mention
    /// entity by id, or a parsed &lt;at&gt; tag matching the agent's name/alias/Graph display
    /// name). The agent's reply should @-mention the sender back to match the conversational
    /// register.
    /// </summary>
    public static AddressedVerdict RespondWithExplicitMention() => new(ShouldRespond: true, WasExplicitlyMentioned: true);

    /// <summary>
    /// Do not respond. Used when the LLM judge decides the message is not addressed to the agent
    /// (e.g. side conversation between humans in a group chat).
    /// </summary>
    public static AddressedVerdict Skip() => new(ShouldRespond: false, WasExplicitlyMentioned: false);
}
