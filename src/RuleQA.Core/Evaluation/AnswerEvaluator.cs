using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using RuleQA.Core.Ai;
using RuleQA.Core.Answering;
using RuleQA.Core.Data;
using RuleQA.Core.Domain;
using RuleQA.Core.Search;
using RuleQA.Core.Settings;

namespace RuleQA.Core.Evaluation;

/// <param name="Cited">Keys of the articles the answer cites.</param>
/// <param name="Found">Keys of the candidates, best first.</param>
public sealed record AnswerEvaluationRow(EvaluationQuestion Question, AnswerStatus Status, string Text, IReadOnlyList<string> Cited, IReadOnlyList<string> Found, string? Error, int ElapsedMs)
{
    public bool Answerable => !Question.NotInRules;
    public bool Failed => Error is not null;
    public bool NotAnswered => Status is AnswerStatus.NotInRules or AnswerStatus.OffTopic;

    /// <summary>An answer that cites an expected article, or no answer when the rules say nothing.</summary>
    public bool Correct => Answerable ? Status == AnswerStatus.Answered && Cited.Any(Question.Expected.Contains) : NotAnswered;

    /// <summary>The rules answer the question but the AI said they do not.</summary>
    public bool Missed => Answerable && NotAnswered;

    /// <summary>Cites articles that are not expected (not always wrong: a related article may help).</summary>
    public bool ExtraCitations => Answerable && Cited.Any(c => !Question.Expected.Contains(c));

    public string Verdict => Failed ? "エラー"
        : Correct ? "正解"
        : Missed ? "答えられるのに答えなかった"
        : Answerable ? "根拠の条文が違う"
        : "答えてしまった";
}

public sealed record AnswerEvaluationReport(string Model, int CandidateCount, bool UsedVectors, bool CustomInstructions, IReadOnlyList<AnswerEvaluationRow> Rows, IReadOnlyList<EvaluationQuestion> Skipped)
{
    public IReadOnlyList<AnswerEvaluationRow> Answerable => Rows.Where(r => r.Answerable).ToList();
    public IReadOnlyList<AnswerEvaluationRow> NoAnswer => Rows.Where(r => !r.Answerable).ToList();
    public IReadOnlyList<AnswerEvaluationRow> OffTopic => Rows.Where(r => r.Question.OffTopic).ToList();

    /// <summary>Share of answerable questions with an expected article among the candidates.</summary>
    public double SearchRate => Rate(Answerable, r => SearchEvaluator.Row(r.Question, r.Found).AnyFound);
    public double TopRate => Rate(Answerable, r => SearchEvaluator.Row(r.Question, r.Found).FirstRank == 1);

    /// <summary>Share of answerable questions answered with an expected article cited.</summary>
    public double CitationRate => Rate(Answerable, r => r.Correct);

    /// <summary>Share of questions the rules do not answer that were not answered.</summary>
    public double NoAnswerRate => Rate(NoAnswer, r => r.Correct);

    /// <summary>Share of off-topic questions judged off-topic (not just "not in the rules").</summary>
    public double OffTopicRate => Rate(OffTopic, r => r.Status == AnswerStatus.OffTopic);

    public int Missed => Rows.Count(r => r.Missed);
    public int ExtraCitations => Rows.Count(r => r.ExtraCitations);
    public int Errors => Rows.Count(r => r.Failed);
    public int AverageMs => Rows.Count == 0 ? 0 : (int)Rows.Average(r => r.ElapsedMs);

    private static double Rate(IReadOnlyList<AnswerEvaluationRow> rows, Func<AnswerEvaluationRow, bool> ok) =>
        rows.Count == 0 ? 0 : (double)rows.Count(ok) / rows.Count;
}

/// <summary>
/// Answers the evaluation questions with the given (possibly unsaved) settings and judges the citations, so changes to the
/// instructions, the model or the number of candidates can be measured before saving. Nothing goes into the history.
/// </summary>
public sealed class AnswerEvaluator(RuleSearchService search, AiClientFactory ai, RuleAnswerService answers, IDbContextFactory<RuleQaDbContext> dbFactory)
{
    /// <exception cref="InvalidOperationException">No AI is configured.</exception>
    public async Task<AnswerEvaluationReport> EvaluateAsync(IReadOnlyList<EvaluationQuestion> questions, AppSettings settings, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var chat = ai.CreateChat(settings.Ai) ?? throw new InvalidOperationException("AI が設定されていません。");
        var (applicable, skipped) = await EvaluationQuestions.SplitAsync(questions, dbFactory, ct);
        var rules = await search.OpenAsync(settings, ct);
        var rows = new List<AnswerEvaluationRow>();
        foreach (var q in applicable)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"{rows.Count + 1} / {applicable.Count}：{q.Question}");
            var watch = Stopwatch.StartNew();
            RuleAnswer answer;
            try
            {
                answer = await answers.AnswerAsync(rules, chat, q.Question, [], settings.Ai.Instructions, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                answer = new RuleAnswer(q.Question, AnswerStatus.SearchOnly, "", [], [], [], e.Message, chat.Name);
            }
            rows.Add(new AnswerEvaluationRow(
                q,
                answer.Status,
                answer.Text,
                answer.Citations.Select(a => a.Key).ToList(),
                answer.Candidates.Select(h => h.Article.Key).ToList(),
                answer.Error,
                (int)watch.ElapsedMilliseconds));
        }
        return new AnswerEvaluationReport(settings.Ai.Model, rules.CandidateCount, rules.UsesVectors,
            !RuleAnswerService.IsDefaultInstructions(settings.Ai.Instructions), rows, skipped);
    }
}

/// <summary>Keeps the results of evaluation runs for comparing settings.</summary>
public sealed class EvaluationLog(IDbContextFactory<RuleQaDbContext> dbFactory, TimeProvider clock)
{
    public Task<EvaluationRun> SaveAsync(SearchEvaluationReport report, CancellationToken ct = default) => SaveAsync(new EvaluationRun
    {
        Kind = EvaluationKind.Search,
        CandidateCount = report.CandidateCount,
        UsedVectors = report.UsedVectors,
        Questions = report.Rows.Count,
        Skipped = report.Skipped.Count,
        SearchRate = report.AnyRate,
        TopRate = report.TopRate,
    }, ct);

    public Task<EvaluationRun> SaveAsync(AnswerEvaluationReport report, CancellationToken ct = default) => SaveAsync(new EvaluationRun
    {
        Kind = EvaluationKind.Answer,
        Model = report.Model,
        CandidateCount = report.CandidateCount,
        UsedVectors = report.UsedVectors,
        CustomInstructions = report.CustomInstructions,
        Questions = report.Rows.Count,
        Skipped = report.Skipped.Count,
        SearchRate = report.SearchRate,
        TopRate = report.TopRate,
        CitationRate = report.CitationRate,
        NoAnswerRate = report.NoAnswerRate,
        OffTopicRate = report.OffTopic.Count == 0 ? null : report.OffTopicRate,
        MissedAnswers = report.Missed,
        Errors = report.Errors,
        AverageMs = report.AverageMs,
    }, ct);

    /// <summary>Newest first.</summary>
    public async Task<IReadOnlyList<EvaluationRun>> ListAsync(int limit = 30, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.EvaluationRuns.AsNoTracking().OrderByDescending(r => r.AtUtc).ThenByDescending(r => r.Id).Take(limit).ToListAsync(ct);
    }

    public async Task<int> ClearAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.EvaluationRuns.ExecuteDeleteAsync(ct);
    }

    private async Task<EvaluationRun> SaveAsync(EvaluationRun run, CancellationToken ct)
    {
        run.AtUtc = clock.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.EvaluationRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run;
    }
}
