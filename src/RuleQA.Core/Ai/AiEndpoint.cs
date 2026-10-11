using System.Net.Http.Headers;
using RuleQA.Core.Settings;

namespace RuleQA.Core.Ai;

/// <summary>URL and authentication for OpenAI-compatible servers, including Azure OpenAI (v1 API).</summary>
public static class AiEndpoint
{
    private static readonly string[] AzureHostSuffixes = [".openai.azure.com", ".cognitiveservices.azure.com", ".services.ai.azure.com"];

    /// <param name="path">For example "chat/completions" or "embeddings".</param>
    public static Uri Url(string endpoint, string path) => new(endpoint.Trim().TrimEnd('/') + "/" + path);

    public static bool IsAzure(string endpoint) =>
        Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)
        && AzureHostSuffixes.Any(s => uri.Host.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>Azure OpenAI takes the key in the "api-key" header; other servers take a Bearer token.</summary>
    public static void Authorize(HttpRequestMessage request, AiSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey)) return;
        if (IsAzure(settings.Endpoint)) request.Headers.Add("api-key", settings.ApiKey.Trim());
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
    }
}
