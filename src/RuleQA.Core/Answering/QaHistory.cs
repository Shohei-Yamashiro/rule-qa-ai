using Microsoft.EntityFrameworkCore;
using RuleQA.Core.Data;
using RuleQA.Core.Domain;
using RuleQA.Core.Search;

namespace RuleQA.Core.Answering;

/// <param name="Article">The current article with this key, or null when a re-import removed it.</param>
public sealed record ArticleRef(string Key, Article? Article);

/// <summary>A saved question and answer with its article keys resolved against the current rules.</summary>
public sealed record HistoryEntry(QaRecord Record, IReadOnlyList<ArticleRef> Citations, IReadOnlyList<ArticleRef> Candidates)
{
    /// <summary>The answer as the question page shows it; articles that no longer exist are left out.</summary>
    public RuleAnswer ToAnswer() => new(
        Record.Question,
        Record.Status,
        Record.AnswerText,
        Citations.Where(c => c.Article is not null).Select(c => c.Article!).ToList(),
        Candidates.Where(c => c.Article is not null).Select(c => new ArticleHit(c.Article!, 0, null, 0)).ToList(),
        [],
        Record.Error,
        Record.AiName);

    /// <summary>Keys of cited or candidate articles that are no longer in the rules.</summary>
    public IReadOnlyList<string> MissingKeys =>
        Citations.Concat(Candidates).Where(c => c.Article is null).Select(c => c.Key).Distinct().ToList();

    public ConversationTurn ToTurn() => new(Record.Question, Record.AnswerText);
}

/// <param name="Question">The latest wording of the question.</param>
/// <param name="Count">How many times it was asked (ignoring width, case, spaces and punctuation).</param>
public sealed record PopularQuestion(string Question, int Count, DateTime LastAskedUtc);

/// <param name="Text">Matched against the question and the answer, ignoring width and case.</param>
public sealed record HistoryQuery(string? Text = null, AnswerStatus? Status = null, int Limit = 200);

/// <summary>Saves questions and answers, and reads them back for the history page and for continuing a conversation.</summary>
public sealed class QaHistory(IDbContextFactory<RuleQaDbContext> dbFactory, TimeProvider clock)
{
    public static string NewConversationId() => Guid.NewGuid().ToString("N");

    public async Task<QaRecord> SaveAsync(string conversationId, RuleAnswer answer, TimeSpan elapsed, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var turn = await db.QaRecords.CountAsync(r => r.ConversationId == conversationId, ct) + 1;
        var record = new QaRecord
        {
            ConversationId = conversationId,
            Turn = turn,
            AskedAtUtc = clock.GetUtcNow().UtcDateTime,
            Question = answer.Question,
            Status = answer.Status,
            AnswerText = answer.Text,
            CitationKeys = string.Join("\n", answer.Citations.Select(a => a.Key)),
            CandidateKeys = string.Join("\n", answer.Candidates.Select(h => h.Article.Key)),
            AiName = answer.AiName,
            Error = answer.Error,
            ElapsedMs = (int)Math.Min(int.MaxValue, elapsed.TotalMilliseconds),
        };
        db.QaRecords.Add(record);
        await db.SaveChangesAsync(ct);
        return record;
    }

    /// <summary>Newest first.</summary>
    public async Task<IReadOnlyList<HistoryEntry>> ListAsync(HistoryQuery query, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var records = db.QaRecords.AsNoTracking();
        if (query.Status is { } status) records = records.Where(r => r.Status == status);
        var list = await records.OrderByDescending(r => r.AskedAtUtc).ThenByDescending(r => r.Id).ToListAsync(ct);
        var text = ArticleIndex.Normalize(query.Text ?? "");
        if (text.Length > 0)
            list = list.Where(r => ArticleIndex.Normalize(r.Question + r.AnswerText).Contains(text, StringComparison.Ordinal)).ToList();
        return await ResolveAsync(db, list.Take(Math.Max(1, query.Limit)).ToList(), ct);
    }

    /// <summary>The questions of one conversation in the order they were asked.</summary>
    public async Task<IReadOnlyList<HistoryEntry>> ConversationAsync(string conversationId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var list = await db.QaRecords.AsNoTracking().Where(r => r.ConversationId == conversationId).OrderBy(r => r.Turn).ToListAsync(ct);
        return await ResolveAsync(db, list, ct);
    }

    /// <summary>
    /// Questions asked most often in the last <paramref name="days"/> days, most asked first. Only questions that were answered,
    /// that start a conversation (follow-ups such as "海外の場合は？" make no sense alone) and that still have a cited article in
    /// the current rules, so the list follows changes to the rules.
    /// </summary>
    public async Task<IReadOnlyList<PopularQuestion>> PopularAsync(int count = 6, int days = 90, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var since = clock.GetUtcNow().UtcDateTime.AddDays(-days);
        var records = await db.QaRecords.AsNoTracking()
            .Where(r => r.Status == AnswerStatus.Answered && r.Turn == 1 && r.AskedAtUtc >= since)
            .ToListAsync(ct);
        var cited = records.SelectMany(r => Keys(r.CitationKeys)).Distinct().ToList();
        var existing = (await db.Articles.Where(a => cited.Contains(a.Key)).Select(a => a.Key).ToListAsync(ct)).ToHashSet();
        return records
            .Where(r => Keys(r.CitationKeys).Any(existing.Contains))
            .GroupBy(r => ArticleIndex.Normalize(r.Question))
            .Where(g => g.Key.Length > 0)
            .Select(g =>
            {
                var latest = g.OrderByDescending(r => r.AskedAtUtc).ThenByDescending(r => r.Id).First();
                return new PopularQuestion(latest.Question, g.Count(), latest.AskedAtUtc);
            })
            .OrderByDescending(p => p.Count).ThenByDescending(p => p.LastAskedUtc)
            .Take(count)
            .ToList();
    }

    public async Task<IReadOnlyDictionary<AnswerStatus, int>> CountsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var counts = await db.QaRecords.GroupBy(r => r.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return Enum.GetValues<AnswerStatus>().ToDictionary(s => s, s => counts.FirstOrDefault(c => c.Key == s)?.Count ?? 0);
    }

    /// <returns>How many records were deleted.</returns>
    public async Task<int> ClearAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.QaRecords.ExecuteDeleteAsync(ct);
    }

    private static async Task<IReadOnlyList<HistoryEntry>> ResolveAsync(RuleQaDbContext db, List<QaRecord> records, CancellationToken ct)
    {
        var keys = records.SelectMany(r => Keys(r.CitationKeys).Concat(Keys(r.CandidateKeys))).Distinct().ToList();
        var articles = keys.Count == 0
            ? []
            : await db.Articles.AsNoTracking().Include(a => a.Document).Where(a => keys.Contains(a.Key)).ToDictionaryAsync(a => a.Key, ct);
        List<ArticleRef> Refs(string joined) => Keys(joined).Select(k => new ArticleRef(k, articles.GetValueOrDefault(k))).ToList();
        return records.Select(r => new HistoryEntry(r, Refs(r.CitationKeys), Refs(r.CandidateKeys))).ToList();
    }

    private static IEnumerable<string> Keys(string joined) => joined.Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
