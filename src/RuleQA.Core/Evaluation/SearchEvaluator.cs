using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RuleQA.Core.Data;
using RuleQA.Core.Search;
using RuleQA.Core.Settings;

namespace RuleQA.Core.Evaluation;

/// <param name="Expected">Keys of the articles a correct answer is based on; empty when the rules say nothing.</param>
/// <param name="NotInRules">The correct answer is not to answer: "規則には記載がありません" (or off-topic).</param>
/// <param name="Kind">What the question tests, for example "言い換え", "旧版", "矛盾".</param>
/// <param name="OffTopic">The question has nothing to do with the company, so it should be judged off-topic.</param>
public sealed record EvaluationQuestion(string Id, string Question, IReadOnlyList<string> Expected, bool NotInRules, string Kind, string? Note = null, bool OffTopic = false);

public static class EvaluationQuestions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<EvaluationQuestion> Load(string path) =>
        JsonSerializer.Deserialize<List<EvaluationQuestion>>(File.ReadAllText(path), JsonOptions) ?? [];

    /// <summary>
    /// Splits the questions into those that can be judged against the imported rules and those whose expected articles are
    /// not there (for example after replacing the samples with a company's own rules).
    /// </summary>
    public static async Task<(IReadOnlyList<EvaluationQuestion> Applicable, IReadOnlyList<EvaluationQuestion> Skipped)> SplitAsync(
        IReadOnlyList<EvaluationQuestion> questions, IDbContextFactory<RuleQaDbContext> dbFactory, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var expected = questions.SelectMany(q => q.Expected).Distinct().ToList();
        var existing = (await db.Articles.Where(a => expected.Contains(a.Key)).Select(a => a.Key).ToListAsync(ct)).ToHashSet();
        var applicable = questions.Where(q => q.Expected.All(existing.Contains)).ToList();
        return (applicable, questions.Except(applicable).ToList());
    }
}

/// <summary>
/// Where the evaluation questions are read from: <c>questions.json</c> in the data folder when someone has put their own
/// questions there, otherwise the sample questions next to the app.
/// </summary>
public sealed class EvaluationQuestionSet(string dataDirectory, string baseDirectory)
{
    public string CustomPath => Path.Combine(dataDirectory, "questions.json");
    public string SamplePath => Path.Combine(baseDirectory, "samples", "questions.json");
    public string FilePath => File.Exists(CustomPath) ? CustomPath : SamplePath;
    public bool Exists => File.Exists(FilePath);

    public IReadOnlyList<EvaluationQuestion> Load() => Exists ? EvaluationQuestions.Load(FilePath) : [];
}

/// <param name="Found">Keys of the candidates, best first.</param>
/// <param name="FirstRank">1-based rank of the first expected article, or null when none was found.</param>
public sealed record SearchEvaluationRow(EvaluationQuestion Question, IReadOnlyList<string> Found, int? FirstRank)
{
    public bool AnyFound => FirstRank is not null;
    public bool AllFound => Question.Expected.All(Found.Contains);
}

/// <param name="Skipped">Questions whose expected articles are not in the imported rules.</param>
public sealed record SearchEvaluationReport(int CandidateCount, bool UsedVectors, IReadOnlyList<SearchEvaluationRow> Rows, IReadOnlyList<EvaluationQuestion> Skipped)
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
public sealed class SearchEvaluator(RuleSearchService search, IDbContextFactory<RuleQaDbContext> dbFactory)
{
    public async Task<SearchEvaluationReport> EvaluateAsync(IReadOnlyList<EvaluationQuestion> questions, AppSettings settings, CancellationToken ct = default)
    {
        var (applicable, skipped) = await EvaluationQuestions.SplitAsync(questions, dbFactory, ct);
        var opened = await search.OpenAsync(settings, ct);
        var rows = new List<SearchEvaluationRow>();
        foreach (var q in applicable)
        {
            var found = (await opened.SearchAsync(q.Question, ct)).Select(h => h.Article.Key).ToList();
            rows.Add(Row(q, found));
        }
        return new SearchEvaluationReport(opened.CandidateCount, opened.UsesVectors, rows, skipped);
    }

    public static SearchEvaluationRow Row(EvaluationQuestion question, IReadOnlyList<string> found)
    {
        var rank = found.ToList().FindIndex(question.Expected.Contains);
        return new SearchEvaluationRow(question, found, rank < 0 ? null : rank + 1);
    }
}
