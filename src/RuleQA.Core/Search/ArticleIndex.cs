using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using RuleQA.Core.Domain;

namespace RuleQA.Core.Search;

/// <param name="Keyword">BM25 score of the question's character terms (0 when none match).</param>
/// <param name="Vector">Cosine similarity of the embeddings, when both sides have them.</param>
/// <param name="Score">Reciprocal rank fusion of the keyword and vector rankings.</param>
public sealed record ArticleHit(Article Article, double Keyword, double? Vector, double Score);

/// <summary>
/// In-memory search over the articles: keyword search (BM25 over character bigrams and single kanji, katakana,
/// letters and digits, so one- and two-character words work), optional vector search, combined by reciprocal rank fusion.
/// Old versions are left out unless asked for, and reference material (FAQ) ranks below the regulations.
/// </summary>
public sealed class ArticleIndex
{
    /// <summary>Keyword scores of reference material are multiplied by this.</summary>
    public const double ReferenceWeight = 0.7;

    private const int RrfK = 60;
    private const double K1 = 1.2;
    private const double B = 0.75;

    private readonly List<Entry> _entries;
    private readonly Dictionary<string, int> _documentFrequency = [];
    private readonly double _averageLength;

    private ArticleIndex(List<Entry> entries)
    {
        _entries = entries;
        foreach (var term in entries.SelectMany(e => e.Terms.Keys))
            _documentFrequency[term] = _documentFrequency.GetValueOrDefault(term) + 1;
        _averageLength = entries.Count == 0 ? 1 : Math.Max(1, entries.Average(e => e.Length));
    }

    public int Count => _entries.Count;

    /// <summary>True when every article has a vector for the embedding model in use.</summary>
    public bool HasVectors => _entries.Count > 0 && _entries.All(e => e.Vector is not null);

    /// <param name="articles">Articles with their <see cref="Article.Document"/> loaded.</param>
    public static ArticleIndex Create(IEnumerable<Article> articles, string? embeddingModel = null, bool includeSuperseded = false) =>
        new(articles.Where(a => includeSuperseded || !a.Document.IsSuperseded).Select(a => new Entry(a, embeddingModel)).ToList());

    public IReadOnlyList<ArticleHit> Search(string query, float[]? queryVector, int top)
    {
        if (_entries.Count == 0 || top <= 0) return [];
        var terms = Terms(query).Distinct().ToList();
        var hits = _entries.Select(e => new ArticleHit(
            e.Article,
            Bm25(e, terms) * (e.Article.Document.Kind == DocumentKind.Reference ? ReferenceWeight : 1),
            queryVector is { Length: > 0 } && e.Vector is not null ? Dot(queryVector, e.Vector) : null,
            0)).ToList();

        var fused = new Dictionary<ArticleHit, double>();
        var byKeyword = hits.Where(h => h.Keyword > 0).OrderByDescending(h => h.Keyword).ToList();
        for (var i = 0; i < byKeyword.Count; i++) fused[byKeyword[i]] = 1.0 / (RrfK + i + 1);
        var byVector = hits.Where(h => h.Vector is not null).OrderByDescending(h => h.Vector).Take(top * 2).ToList();
        for (var i = 0; i < byVector.Count; i++) fused[byVector[i]] = fused.GetValueOrDefault(byVector[i]) + 1.0 / (RrfK + i + 1);

        return fused.OrderByDescending(f => f.Value).ThenByDescending(f => f.Key.Keyword).Take(top)
            .Select(f => f.Key with { Score = f.Value })
            .ToList();
    }

    private double Bm25(Entry entry, List<string> terms)
    {
        double score = 0;
        foreach (var term in terms)
        {
            if (!entry.Terms.TryGetValue(term, out var tf)) continue;
            var df = _documentFrequency[term];
            var idf = Math.Log(1 + (_entries.Count - df + 0.5) / (df + 0.5));
            score += idf * tf * (K1 + 1) / (tf + K1 * (1 - B + B * entry.Length / _averageLength));
        }
        return score;
    }

    /// <summary>
    /// Character bigrams (pairs of hiragana only are skipped: they are mostly endings such as "です") and single
    /// characters other than hiragana, after full-width to half-width and lower case conversion.
    /// </summary>
    public static IEnumerable<string> Terms(string text)
    {
        var s = Normalize(text);
        for (var i = 0; i < s.Length; i++)
        {
            if (!IsHiragana(s[i])) yield return s[i].ToString();
            if (i + 1 < s.Length && !(IsHiragana(s[i]) && IsHiragana(s[i + 1]))) yield return s.Substring(i, 2);
        }
    }

    /// <summary>Full-width to half-width, lower case, letters and digits only.</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormKC))
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLower(c, CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    public static byte[] ToBytes(float[] vector) => MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();

    public static float[] ToVector(byte[] bytes) => MemoryMarshal.Cast<byte, float>(bytes).ToArray();

    private static bool IsHiragana(char c) => c is >= '\u3041' and <= '\u309F';

    private static double Dot(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double sum = 0;
        for (var i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    private sealed class Entry
    {
        public Entry(Article article, string? embeddingModel)
        {
            Article = article;
            var heading = $"{article.Label} {article.Heading}";
            var terms = Terms(article.Document.BaseTitle).Concat(Terms(heading)).Concat(Terms(heading)).Concat(Terms(article.Body)).ToList();
            Terms = terms.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
            Length = terms.Count;
            if (article.Embedding is { Length: > 0 } && !string.IsNullOrEmpty(embeddingModel) && article.EmbeddingModel == embeddingModel)
                Vector = ToVector(article.Embedding);
        }

        public Article Article { get; }
        public Dictionary<string, int> Terms { get; }
        public int Length { get; }
        public float[]? Vector { get; }
    }
}
