using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RuleQA.Core.Ai;
using RuleQA.Core.Data;
using RuleQA.Core.Settings;

namespace RuleQA.Core.Search;

/// <summary>Searches for many questions with one loaded index; embedding errors fall back to keyword search for the rest.</summary>
public sealed class RuleSearch(ArticleIndex index, IEmbedder? embedder, int candidateCount, ILogger logger)
{
    private IEmbedder? _embedder = index.HasVectors ? embedder : null;

    public ArticleIndex Index => index;
    public bool UsesVectors => _embedder is not null;
    public int CandidateCount => candidateCount;

    public async Task<IReadOnlyList<ArticleHit>> SearchAsync(string query, CancellationToken ct)
    {
        float[]? vector = null;
        if (_embedder is not null && !string.IsNullOrWhiteSpace(query))
        {
            try
            {
                vector = (await _embedder.EmbedAsync([query], ct))[0];
            }
            catch (AiConnectionException e)
            {
                logger.LogWarning(e, "Embedding failed; article search uses keywords only");
                _embedder = null;
            }
        }
        return index.Search(query, vector, candidateCount);
    }
}

/// <summary>Builds article embeddings and opens searches over the imported rules (the retrieval part of RAG).</summary>
public sealed class RuleSearchService(IDbContextFactory<RuleQaDbContext> dbFactory, AiClientFactory ai, ILogger<RuleSearchService> logger)
{
    public const int MaxCandidates = 20;

    public async Task<RuleSearch> OpenAsync(AppSettings settings, CancellationToken ct = default)
    {
        var embedder = ai.CreateEmbedder(settings.Ai);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var articles = await db.Articles.AsNoTracking().Include(a => a.Document)
            .OrderBy(a => a.Document.Path).ThenBy(a => a.Order)
            .ToListAsync(ct);
        var index = ArticleIndex.Create(articles, embedder?.Model, settings.Rules.IncludeSuperseded);
        return new RuleSearch(index, embedder, Math.Clamp(settings.Rules.CandidateCount, 1, MaxCandidates), logger);
    }

    public async Task<IReadOnlyList<ArticleHit>> SearchAsync(string query, AppSettings settings, CancellationToken ct = default) =>
        await (await OpenAsync(settings, ct)).SearchAsync(query, ct);

    /// <summary>Embeds articles that have no vector for the configured model. Returns how many were built.</summary>
    public async Task<int> BuildEmbeddingsAsync(AiSettings settings, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var embedder = ai.CreateEmbedder(settings) ?? throw new InvalidOperationException("埋め込みモデルが設定されていません。");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var targets = await db.Articles.Include(a => a.Document)
            .Where(a => a.Embedding == null || a.EmbeddingModel != embedder.Model)
            .ToListAsync(ct);
        var done = 0;
        foreach (var batch in targets.Chunk(32))
        {
            progress?.Report($"ベクトル作成中 {done}/{targets.Count}");
            var vectors = await embedder.EmbedAsync(batch.Select(a => a.EmbeddingText).ToList(), ct);
            for (var i = 0; i < batch.Length; i++)
            {
                batch[i].Embedding = ArticleIndex.ToBytes(vectors[i]);
                batch[i].EmbeddingModel = embedder.Model;
            }
            await db.SaveChangesAsync(ct);
            done += batch.Length;
        }
        return done;
    }
}
