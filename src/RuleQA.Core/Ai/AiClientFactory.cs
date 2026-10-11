using RuleQA.Core.Settings;

namespace RuleQA.Core.Ai;

/// <param name="handler">Replaces the network connection in tests; null uses a shared connection pool.</param>
public sealed class AiClientFactory(HttpMessageHandler? handler = null)
{
    private static readonly SocketsHttpHandler SharedHandler = new() { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };

    /// <summary>Null when no AI is set; questions are then answered with the search results only.</summary>
    public IChatClient? CreateChat(AiSettings settings) =>
        settings.Provider == AiProvider.OpenAiCompatible ? new OpenAiCompatibleChatClient(CreateHttp(settings), settings) : null;

    /// <summary>Null when no embedding model is set; article search then uses keywords only.</summary>
    public IEmbedder? CreateEmbedder(AiSettings settings) =>
        settings.Provider == AiProvider.OpenAiCompatible && !string.IsNullOrWhiteSpace(settings.EmbeddingModel)
            ? new OpenAiCompatibleEmbedder(CreateHttp(settings), settings)
            : null;

    private HttpClient CreateHttp(AiSettings settings) =>
        new(handler ?? SharedHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(Math.Max(5, settings.TimeoutSeconds)) };
}
