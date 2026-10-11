using System.Text.Json;
using RuleQA.Core.Search;
using RuleQA.Core.Settings;

namespace RuleQA.Core.Evaluation;

/// <param name="Expected">Keys of the articles a correct answer is based on; empty when the rules say nothing.</param>
/// <param name="NotInRules">The correct answer is "規則には記載がありません".</param>
/// <param name="Kind">What the question tests, for example "言い換え", "旧版", "矛盾".</param>
public sealed record EvaluationQuestion(string Id, string Question, IReadOnlyList<string> Expected, bool NotInRules, string Kind, string? Note = null);

public static class EvaluationQuestions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<EvaluationQuestion> Load(string path) =>
        JsonSerializer.Deserialize<List<EvaluationQuestion>>(File.ReadAllText(path), JsonOptions) ?? [];
}

/// <param name="Found">Keys of the candidates, best first.</param>
/// <param name="FirstRank">1-based rank of the first expected article, or null when none was found.</param>
public sealed record SearchEvaluationRow(EvaluationQuestion Question, IReadOnlyList<string> Found, int? FirstRank)
{
    public bool AnyFound => FirstRank is not null;
    public bool AllFound => Question.Expected.All(Found.Contains);
}

public sealed record SearchEvaluationReport(int CandidateCount, bool UsedVectors, IReadOnlyList<SearchEvaluationRow> Rows)
{
    /// <summary>Questions with expected articles; "not in the rules" questions are judged when answering.</summary>
    public IReadOnlyList<SearchEvaluationRow> Answerable => Rows.Where(r => r.Question.Expected.Count > 0).ToList();

    /// <summary>Share of answerable questions with at least one expected article among the candidates.</summary>
    public double AnyRate => Rate(r => r.AnyFound);

    /// <summary>Share of answerable questions with every expected article among the candidates.</summary>
    public double AllRate => Rate(r => r.AllFound);

    /// <summary>Share of answerable questions whose first candidate is an expected article.</summary>
    public double TopRate => Rate(r => r.FirstRank == 1);

    private double Rate(Func<SearchEvaluationRow, bool> ok) => Answerable.Count == 0 ? 0 : (double)Answerable.Count(ok) / Answerable.Count;
}

/// <summary>Measures whether the search puts the right articles among the candidates (no AI needed unless vectors are used).</summary>
public sealed class SearchEvaluator(RuleSearchService search)
{
    public async Task<SearchEvaluationReport> EvaluateAsync(IReadOnlyList<EvaluationQuestion> questions, AppSettings settings, CancellationToken ct = default)
    {
        var opened = await search.OpenAsync(settings, ct);
        var rows = new List<SearchEvaluationRow>();
        foreach (var q in questions)
        {
            var found = (await opened.SearchAsync(q.Question, ct)).Select(h => h.Article.Key).ToList();
            var rank = found.FindIndex(q.Expected.Contains);
            rows.Add(new SearchEvaluationRow(q, found, rank < 0 ? null : rank + 1));
        }
        return new SearchEvaluationReport(Math.Clamp(settings.Rules.CandidateCount, 1, RuleSearchService.MaxCandidates), opened.UsesVectors, rows);
    }
}
