using Microsoft.EntityFrameworkCore;
using RuleQA.Core.Data;
using RuleQA.Core.Domain;
using RuleQA.Core.Search;

namespace RuleQA.Core.Rules;

/// <param name="SupersededBy">Path of the version that replaced this one.</param>
/// <param name="Warnings">Problems found when the file was split into articles.</param>
public sealed record DocumentSummary(
    int Id,
    string Path,
    string Title,
    DocumentKind Kind,
    DateOnly? EffectiveDate,
    bool IsSuperseded,
    bool IsAbolished,
    string? SupersededBy,
    int Articles,
    IReadOnlyList<string> Warnings,
    string Encoding,
    DateTime ImportedAtUtc);

/// <param name="Topics">Article headings that say what the regulation covers, without ones every regulation has (目的, 定義 ...).</param>
public sealed record RuleTopic(string Title, string Path, IReadOnlyList<string> Topics);

/// <summary>A question example built from a regulation and one of its headings.</summary>
public sealed record RuleExample(string Title, string Topic)
{
    /// <summary>Names the regulation, since headings such as "申請期限" are unclear on their own.</summary>
    public string Question => $"{Title}の{Topic}について教えてください";
}

/// <summary>Reads the imported regulations and articles for browsing (the rules page).</summary>
public sealed partial class RuleLibrary(IDbContextFactory<RuleQaDbContext> dbFactory)
{
    private static readonly HashSet<string> CommonHeadings =
        ["目的", "定義", "趣旨", "適用", "適用範囲", "適用対象", "対象", "用語の定義", "改廃", "施行", "施行日", "附則", "その他", "雑則", "はじめに", "概要"];

    /// <summary>
    /// What can be asked about: for each current regulation (not old versions or reference material), the headings of its
    /// articles or sections in order. Built from the imported files, so it follows whatever rules are imported.
    /// </summary>
    public async Task<IReadOnlyList<RuleTopic>> TopicsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var docs = await db.Documents.AsNoTracking()
            .Where(d => !d.IsSuperseded && d.Kind == DocumentKind.Regulation)
            .OrderBy(d => d.Path)
            .Select(d => new { d.BaseTitle, d.Path, Articles = d.Articles.OrderBy(a => a.Order).Select(a => new { a.Label, a.Heading }).ToList() })
            .ToListAsync(ct);
        return docs.Select(d => new RuleTopic(d.BaseTitle, d.Path, d.Articles
                .Select(a => TopicOf(a.Label, a.Heading))
                .Where(t => t is not null && !CommonHeadings.Contains(t))
                .Select(t => t!)
                .Distinct()
                .ToList()))
            .ToList();
    }

    /// <summary>
    /// Picks <paramref name="count"/> topics as question examples, one regulation after another so that different regulations
    /// appear, in an order that changes with <paramref name="random"/>.
    /// </summary>
    public static IReadOnlyList<RuleExample> PickExamples(IReadOnlyList<RuleTopic> topics, int count, Random random)
    {
        var queues = topics.Where(t => t.Topics.Count > 0)
            .Select(t => new Queue<RuleExample>(t.Topics.OrderBy(_ => random.Next()).Select(x => new RuleExample(t.Title, x))))
            .OrderBy(_ => random.Next())
            .ToList();
        var picked = new List<RuleExample>();
        while (picked.Count < count && queues.Any(q => q.Count > 0))
            foreach (var queue in queues.Where(q => q.Count > 0))
            {
                if (picked.Count == count) break;
                picked.Add(queue.Dequeue());
            }
        return picked;
    }

    /// <summary>The heading, or for a section without one its label without the number ("2. パスワード" → "パスワード").</summary>
    private static string? TopicOf(string label, string? heading)
    {
        if (!string.IsNullOrWhiteSpace(heading)) return heading.Trim();
        if (NumberedLabelPattern().IsMatch(label)) return null;
        var text = SectionNumberPattern().Replace(label, "").Trim();
        return text.Length == 0 ? null : text;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(第.+条|附\s*則|段落\d+)")]
    private static partial System.Text.RegularExpressions.Regex NumberedLabelPattern();

    [System.Text.RegularExpressions.GeneratedRegex(@"^[\d０-９]+(\.[\d０-９]+)*[.．、)）]?\s*")]
    private static partial System.Text.RegularExpressions.Regex SectionNumberPattern();

    /// <summary>In path order; old versions only when <paramref name="includeSuperseded"/> is set.</summary>
    public async Task<IReadOnlyList<DocumentSummary>> DocumentsAsync(bool includeSuperseded, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var docs = await db.Documents.AsNoTracking()
            .Where(d => includeSuperseded || !d.IsSuperseded)
            .OrderBy(d => d.Path)
            .Select(d => new { d.Id, d.Path, d.Title, d.Kind, d.EffectiveDate, d.IsSuperseded, d.IsAbolished, d.SupersededBy, Articles = d.Articles.Count, d.Warnings, d.Encoding, d.ImportedAtUtc })
            .ToListAsync(ct);
        return docs.Select(d => new DocumentSummary(
            d.Id, d.Path, d.Title, d.Kind, d.EffectiveDate, d.IsSuperseded, d.IsAbolished, d.SupersededBy, d.Articles,
            d.Warnings.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), d.Encoding, d.ImportedAtUtc)).ToList();
    }

    /// <summary>The document with its articles in their original order.</summary>
    public async Task<RuleDocument?> DocumentAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var doc = await db.Documents.AsNoTracking().Include(d => d.Articles).FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null) return null;
        doc.Articles = doc.Articles.OrderBy(a => a.Order).ToList();
        foreach (var a in doc.Articles) a.Document = doc;
        return doc;
    }

    public async Task<Article?> ArticleAsync(string key, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Articles.AsNoTracking().Include(a => a.Document).FirstOrDefaultAsync(a => a.Key == key, ct);
    }

    /// <summary>
    /// Articles whose regulation name, number, heading or text contains <paramref name="text"/>, ignoring full/half width,
    /// case, spaces and punctuation. In path and article order.
    /// </summary>
    public async Task<IReadOnlyList<Article>> FindAsync(string text, bool includeSuperseded, int limit = 100, CancellationToken ct = default)
    {
        var needle = ArticleIndex.Normalize(text);
        if (needle.Length == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var articles = await db.Articles.AsNoTracking().Include(a => a.Document)
            .Where(a => includeSuperseded || !a.Document.IsSuperseded)
            .OrderBy(a => a.Document.Path).ThenBy(a => a.Order)
            .ToListAsync(ct);
        return articles
            .Where(a => ArticleIndex.Normalize($"{a.Document.Title}{a.Label}{a.Heading}{a.Body}").Contains(needle, StringComparison.Ordinal))
            .Take(Math.Max(1, limit))
            .ToList();
    }
}
