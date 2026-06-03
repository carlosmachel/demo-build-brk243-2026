namespace WorkstreamManager.AgentLogic.ResponsesApi.Helpers;

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Posts emoji reactions to messages via Microsoft Graph setReaction. Used for both the
/// 👍 "I'm replying to this" signal and the 📌 "I logged this as a work item" signal.
/// Handles the team-channel vs chat URL forks Graph requires. Errors are logged but never
/// thrown - reactions are nice-to-have feedback and must never break the agent.
/// </summary>
internal class ReactionService
{
    private readonly ILogger _logger;
    private readonly string? _graphAccessToken;
    private readonly HttpClient _httpClient;

    public ReactionService(ILogger logger, string? graphAccessToken, HttpClient httpClient)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _graphAccessToken = graphAccessToken;
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>
    /// Sets a reaction on the message that triggered this turn. Resolves the Graph setReaction
    /// URL from the activity's channel data (Teams channel vs chat). No-op if the Graph token
    /// is unavailable, the channel isn't Teams, or required IDs are missing.
    /// </summary>
    /// <param name="reactionEmoji">The emoji to post, as the JSON-escaped Unicode codepoint
    /// (e.g. <c>"\uD83D\uDC4D"</c> for 👍, <c>"\uD83D\uDCCC"</c> for 📌). Pass the literal
    /// codepoint - this method emits it verbatim into the request body.</param>
    public async Task SetReactionAsync(string reactionEmoji, IActivity activity, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_graphAccessToken))
        {
            _logger.LogDebug("Skipping reaction: no Graph access token.");
            return;
        }

        if (activity == null || string.IsNullOrEmpty(activity.Id))
        {
            _logger.LogDebug("Skipping reaction: activity or activity.Id missing.");
            return;
        }

        if (!string.Equals(activity.ChannelId?.ToString(), "msteams", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Skipping reaction: channelId is '{ChannelId}', not msteams.", activity.ChannelId);
            return;
        }

        var setReactionUrl = BuildSetReactionUrl(activity);
        if (setReactionUrl == null)
        {
            _logger.LogWarning(
                "Reaction skipped: could not build setReaction URL. channelId={ChannelId} conversationId={ConversationId}",
                activity.ChannelId,
                activity.Conversation?.Id);
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, setReactionUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _graphAccessToken);
            request.Content = new StringContent(
                $"{{\"reactionType\": \"{reactionEmoji}\"}}",
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Graph setReaction succeeded ({Status}) at {Url}",
                    (int)response.StatusCode,
                    setReactionUrl);
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "Graph setReaction failed: {Status} {Body} at {Url}",
                    (int)response.StatusCode,
                    body,
                    setReactionUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error setting reaction at {Url}", setReactionUrl);
        }
    }

    /// <summary>
    /// Computes the Graph setReaction URL based on whether the activity arrived in a Teams
    /// channel (team-channel addressing) or a 1:1/group chat (chats addressing). Returns null
    /// when required identifiers are missing.
    /// </summary>
    private static string? BuildSetReactionUrl(IActivity activity)
    {
        string? teamId = null;
        string? channelId = null;
        if (activity.ChannelData is JsonElement channelData && channelData.ValueKind == JsonValueKind.Object)
        {
            if (channelData.TryGetProperty("team", out var teamProp) && teamProp.ValueKind == JsonValueKind.Object)
            {
                // Graph requires the AAD group id, not the Bot Framework thread id.
                if (teamProp.TryGetProperty("aadGroupId", out var aadGroupIdProp) &&
                    aadGroupIdProp.ValueKind == JsonValueKind.String)
                {
                    teamId = aadGroupIdProp.GetString();
                }
                else if (teamProp.TryGetProperty("id", out var teamIdProp) &&
                         teamIdProp.ValueKind == JsonValueKind.String)
                {
                    teamId = teamIdProp.GetString();
                }
            }
            if (channelData.TryGetProperty("channel", out var channelProp) &&
                channelProp.ValueKind == JsonValueKind.Object &&
                channelProp.TryGetProperty("id", out var channelIdProp) &&
                channelIdProp.ValueKind == JsonValueKind.String)
            {
                channelId = channelIdProp.GetString();
            }
        }

        if (!string.IsNullOrEmpty(teamId) && !string.IsNullOrEmpty(channelId))
        {
            // Teams channel addressing: parent thread root id is encoded in Conversation.Id as
            // "<channelThreadId>;messageid=<rootMessageId>" when the activity is a reply.
            // For a top-level channel post the suffix is absent and Activity.Id is the
            // root message itself.
            var convId = activity.Conversation?.Id ?? string.Empty;
            const string marker = ";messageid=";
            var idx = convId.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0)
            {
                var rootMessageId = convId.Substring(idx + marker.Length);
                var replyId = activity.Id;
                if (!string.Equals(rootMessageId, replyId, StringComparison.Ordinal))
                {
                    return $"https://graph.microsoft.com/v1.0/teams/{teamId}/channels/{channelId}/messages/{rootMessageId}/replies/{replyId}/setReaction";
                }
                return $"https://graph.microsoft.com/v1.0/teams/{teamId}/channels/{channelId}/messages/{rootMessageId}/setReaction";
            }
            return $"https://graph.microsoft.com/v1.0/teams/{teamId}/channels/{channelId}/messages/{activity.Id}/setReaction";
        }

        // 1:1 or group chat addressing.
        if (!string.IsNullOrEmpty(activity.Conversation?.Id))
        {
            return $"https://graph.microsoft.com/v1.0/chats/{activity.Conversation.Id}/messages/{activity.Id}/setReaction";
        }

        return null;
    }
}
