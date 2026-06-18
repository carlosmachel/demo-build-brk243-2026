// Copyright (c) Microsoft. All rights reserved.

#pragma warning disable OPENAI001 // Suppress experimental API warnings for Responses API usage.

using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.AI.Projects;
using Azure.Identity;
using OpenAI;

namespace Harness.Shared.Console;

/// <summary>
/// Creates the <see cref="OpenAIClient"/> used to call a Foundry project's responses service, choosing
/// between Microsoft Entra ID and a static API key depending on whether <c>AZURE_AI_API_KEY</c> is set.
/// </summary>
public static class FoundryOpenAIClientFactory
{
    /// <summary>
    /// Creates an <see cref="OpenAIClient"/> for the given Foundry project endpoint.
    /// </summary>
    /// <param name="endpoint">
    /// The Foundry project endpoint, e.g. "https://&lt;resource&gt;.services.ai.azure.com/api/projects/&lt;project&gt;".
    /// </param>
    /// <remarks>
    /// If the <c>AZURE_AI_API_KEY</c> environment variable is set, this authenticates directly against the
    /// project's OpenAI v1-compatible surface with that key, bypassing <see cref="AIProjectClient"/> entirely.
    /// WARNING: an API key grants whoever holds it full access to the resource and must be kept out of source
    /// control. It's convenient for local development; prefer Microsoft Entra ID (the default below) in production.
    /// </remarks>
    public static OpenAIClient Create(string endpoint)
    {
        var apiKey = Environment.GetEnvironmentVariable("AZURE_AI_API_KEY");
        if (!string.IsNullOrEmpty(apiKey))
        {
            return new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = new Uri($"{endpoint.TrimEnd('/')}/openai/v1") });
        }

        // WARNING: DefaultAzureCredential is convenient for development but requires careful consideration in production.
        // In production, consider using a specific credential (e.g., ManagedIdentityCredential) to avoid
        // latency issues, unintended credential probing, and potential security risks from fallback mechanisms.
        return new AIProjectClient(
            new Uri(endpoint),
            new DefaultAzureCredential(),
            new AIProjectClientOptions { RetryPolicy = new ClientRetryPolicy(3) })
            .GetProjectOpenAIClient();
    }
}
