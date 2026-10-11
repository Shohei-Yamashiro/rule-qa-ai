using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RuleQA.Core.Ai;
using RuleQA.Core.Answering;
using RuleQA.Core.Data;
using RuleQA.Core.Domain;
using RuleQA.Core.Evaluation;
using RuleQA.Core.Rules;
using RuleQA.Core.Settings;

namespace RuleQA.Tests;

public sealed class EvaluationAndFeedbackTests : IDisposable
{
    private readonly TestServices _services = new();

    public EvaluationAndFeedbackTests() => _services.Get<RuleImporter>().ImportFolderAsync(Samples.RulesFolder).GetAwaiter().GetResult();

    public void Dispose() => _services.Dispose();

    private static AppSettings AiSettings() => new() { Ai = { Provider = AiProvider.OpenAiCompatible, Endpoint = "http://ai.test/v1", Model = "m" } };

    private RuleAnswerService ServiceWith(FakeHandler handler) =>
        new(_services.SearchWith(handler), new AiClientFactory(handler), NullLogger<RuleAnswerService>.Instance);

    private AnswerEvaluator EvaluatorWith(FakeHandler handler) =>
        new(_services.SearchWith(handler), new AiClientFactory(handler), ServiceWith(handler), _services.Get<IDbContextFactory<RuleQaDbContext>>());

    private static string SystemPrompt(CapturedRequest request) => request.Body["messages"]![0]!["content"]!.GetValue<string>();

    private static string QuestionOf(CapturedRequest request)
    {
        var prompt = request.Body["messages"]![1]!["content"]!.GetValue<string>().ReplaceLineEndings("\n");
        return prompt[(prompt.LastIndexOf("## 質問\n", StringComparison.Ordinal) + "## 質問\n".Length)..];
    }

    private static HttpResponseMessage Reply(string answer, string[] citations, bool answered, bool offTopic = false) =>
        FakeHandler.Completion(JsonSerializer.Serialize(new { answer, citations, answered, off_topic = offTopic }));

    private async Task<QaRecord> SaveAsync(string question)
    {
        var answer = new RuleAnswer(question, AnswerStatus.Answered, "回答", [], [], [], null, "AI（m）");
        return await _services.Get<QaHistory>().SaveAsync(QaHistory.NewConversationId(), answer, TimeSpan.Zero);
    }

    [Fact]
    [Spec(SpecCategories.Evaluation, "回答に「役に立った・立たなかった」を記録する", "回答ごとに記録し、押し直しで変更、null で取り消せる。理由は「役に立たなかった」のときだけ残す。件数と「役に立った」の割合を出し、履歴をフィードバックで絞り込める。消えた履歴には記録しない。")]
    public async Task Records_feedback_on_answers()
    {
        var history = _services.Get<QaHistory>();
        var a = await SaveAsync("国内出張の日当は？");
        var b = await SaveAsync("在宅勤務の申請期限は？");
        var c = await SaveAsync("パスワードは何文字？");

        await history.SetFeedbackAsync(a.Id, Feedback.Helpful, "理由は残さない");
        await history.SetFeedbackAsync(b.Id, Feedback.NotHelpful, "  FAQ と食い違っている  ");
        await history.SetFeedbackAsync(c.Id, Feedback.Helpful);
        await history.SetFeedbackAsync(c.Id, null);
        var missing = await history.SetFeedbackAsync(9999, Feedback.Helpful);

        var counts = await history.FeedbackCountsAsync();
        Assert.Equal((1, 1), (counts.Helpful, counts.NotHelpful));
        Assert.Equal(0.5, counts.HelpfulRate);
        Assert.Null(missing);
        var notHelpful = Assert.Single(await history.ListAsync(new HistoryQuery(Feedback: Feedback.NotHelpful)));
        Assert.Equal(("在宅勤務の申請期限は？", "FAQ と食い違っている"), (notHelpful.Record.Question, notHelpful.Record.FeedbackComment));
        Assert.NotNull(notHelpful.Record.FeedbackAtUtc);
        var all = (await history.ListAsync(new HistoryQuery())).ToDictionary(e => e.Record.Id, e => e.Record);
        Assert.Null(all[a.Id].FeedbackComment);
        Assert.Null(all[c.Id].Feedback);
        Assert.Null(all[c.Id].FeedbackAtUtc);
        Assert.Null(new FeedbackCounts(0, 0).HelpfulRate);
    }

    [Fact]
    [Spec(SpecCategories.Evaluation, "前の版の DB にフィードバックと評価の記録を足す", "フィードバックの列と評価の記録の表がない DB（前の版で作った DB）を開くと、履歴を残したまま列と表を足し、記録できるようになる。")]
    public async Task Adds_feedback_columns_and_evaluation_table_to_older_databases()
    {
        var record = await SaveAsync("国内出張の日当は？");
        await using (var db = _services.Db())
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE QaRecords DROP COLUMN Feedback");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE QaRecords DROP COLUMN FeedbackComment");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE QaRecords DROP COLUMN FeedbackAtUtc");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE EvaluationRuns");
        }

        await using (var db = _services.Db())
            db.EnsureSchema();
        await _services.Get<QaHistory>().SetFeedbackAsync(record.Id, Feedback.NotHelpful, "古い");
        var report = await _services.Get<SearchEvaluator>().EvaluateAsync(Samples.Questions, new AppSettings());
        await _services.Get<EvaluationLog>().SaveAsync(report);

        var entry = Assert.Single(await _services.Get<QaHistory>().ListAsync(new HistoryQuery()));
        Assert.Equal((Feedback.NotHelpful, "古い"), (entry.Record.Feedback, entry.Record.FeedbackComment));
        Assert.Single(await _services.Get<EvaluationLog>().ListAsync());
    }

    [Fact]
    [Spec(SpecCategories.Ai, "編集したプロンプトで回答する", "設定で指示文を編集すると、その指示文を AI に送る。空欄や既定と同じ文なら既定の指示文を送る。どちらでも、条文 ID で引用する決まりと JSON の出力形式（編集できない部分）を後ろに付ける。")]
    public async Task Sends_edited_instructions_with_fixed_rules()
    {
        var handler = new FakeHandler(_ => Reply("", [], false));
        var service = ServiceWith(handler);
        var edited = AiSettings();
        edited.Ai.Instructions = "あなたは総務部の担当者です。敬語で、短く答えてください。";
        var same = AiSettings();
        same.Ai.Instructions = "\r\n" + RuleAnswerService.DefaultInstructions.ReplaceLineEndings("\r\n") + "  ";

        await service.AnswerAsync("国内出張の日当は？", [], edited);
        await service.AnswerAsync("国内出張の日当は？", [], AiSettings());
        await service.AnswerAsync("国内出張の日当は？", [], same);

        var prompts = handler.Requests.Select(SystemPrompt).Select(p => p.ReplaceLineEndings("\n")).ToList();
        var fixedPart = RuleAnswerService.FixedInstructions.ReplaceLineEndings("\n");
        Assert.StartsWith("あなたは総務部の担当者です。敬語で、短く答えてください。\n", prompts[0]);
        Assert.DoesNotContain("候補の条文」に書かれていることだけ", prompts[0]);
        Assert.All(prompts, p => Assert.EndsWith(fixedPart, p));
        Assert.Contains("規程名や条番号ではない", fixedPart);
        Assert.Contains("\"off_topic\"", fixedPart);
        Assert.Equal(prompts[1], prompts[2]);
        Assert.StartsWith(RuleAnswerService.DefaultInstructions.ReplaceLineEndings("\n").Trim(), prompts[1]);
        Assert.True(RuleAnswerService.IsDefaultInstructions(same.Ai.Instructions));
        Assert.False(RuleAnswerService.IsDefaultInstructions(edited.Ai.Instructions));
    }

    [Fact]
    [Spec(SpecCategories.Evaluation, "評価用の質問で回答を評価する", "保存前の設定で質問ごとに AI に回答させ、根拠の条文が正解なら「正解」、答えられるのに記載なしなら「答えられるのに答えなかった」、正解以外の条文だけなら「根拠の条文が違う」、記載なしが正解の質問に答えたら「答えてしまった」と判定する。根拠の一致・答えないのが正解の質問・対象外の判定の割合、エラーの件数を出す。正解の条文が取り込んだ規則にない質問は除く。評価の質問は履歴に残さない。")]
    public async Task Evaluates_answers_against_expected_articles()
    {
        var handler = new FakeHandler(r => QuestionOf(r) switch
        {
            var q when q.Contains("日当") => Reply("日帰り1,000円です。", ["旅費規程 第5条"], true),
            var q when q.Contains("申請") => Reply("", [], false),
            var q when q.Contains("タクシー") => Reply("公共交通機関がないときです。", ["A1"], true),
            var q when q.Contains("結婚") => FakeHandler.Reply(HttpStatusCode.InternalServerError, "{}"),
            var q when q.Contains("天気") => Reply("", [], false, offTopic: true),
            var q when q.Contains("ペット") => Reply("連れてこられます。", ["A1"], true),
            _ => Reply("", [], false),
        });
        EvaluationQuestion Q(string id, string question, string[] expected, bool offTopic = false) =>
            new(id, question, expected, expected.Length == 0, offTopic ? "対象外" : "基本", null, offTopic);
        var questions = new[]
        {
            Q("E1", "国内出張の日当はいくらですか？", ["03_旅費規程#第5条"]),
            Q("E2", "海外出張の申請はいつまでに？", ["03_旅費規程#第3条"]),
            Q("E3", "タクシーを使っていいのはどんなとき？", ["05_情報セキュリティ規程#2. パスワード"]),
            Q("E4", "結婚したときの休暇は何日？", ["02_休暇規程#第6条"]),
            Q("E5", "給料日はいつですか？", []),
            Q("E6", "明日の天気は？", [], offTopic: true),
            Q("E7", "ペットを連れて出社できますか", []),
            Q("E8", "社用車の予約は？", ["08_社用車管理規程#第2条"]),
        };
        var settings = AiSettings();
        settings.Ai.Instructions = "短く答える。";
        var progress = new List<string>();

        var report = await EvaluatorWith(handler).EvaluateAsync(questions, settings, new SyncProgress(progress.Add));

        Assert.Equal(["E8"], report.Skipped.Select(q => q.Id));
        Assert.Equal(
            ["正解", "答えられるのに答えなかった", "根拠の条文が違う", "エラー", "正解", "正解", "答えてしまった"],
            report.Rows.Select(r => r.Verdict));
        Assert.Equal((0.25, 2.0 / 3, 1.0), (report.CitationRate, report.NoAnswerRate, report.OffTopicRate));
        Assert.Equal((1, 1), (report.Missed, report.Errors));
        Assert.Equal(("m", true, 6), (report.Model, report.CustomInstructions, report.CandidateCount));
        Assert.Equal(7, progress.Count);
        Assert.StartsWith("1 / 7：", progress[0]);
        Assert.All(handler.Requests, r => Assert.StartsWith("短く答える。", SystemPrompt(r)));
        Assert.Empty(await _services.Get<QaHistory>().ListAsync(new HistoryQuery()));
    }

    [Fact]
    [Spec(SpecCategories.Evaluation, "評価の結果を記録して比べる", "検索と回答の評価の結果を、日時・条件（モデル・候補の数・ベクトル・プロンプトを変えたか）・各割合とともに記録し、新しい順に返す。記録は消せる。")]
    public async Task Keeps_evaluation_runs_for_comparison()
    {
        var log = _services.Get<EvaluationLog>();
        var handler = new FakeHandler(_ => Reply("", [], false));
        var questions = Samples.Questions.Where(q => q.Id is "Q12" or "Q33" or "Q37").ToList();
        var settings = AiSettings();
        settings.Rules.CandidateCount = 3;

        var search = await _services.Get<SearchEvaluator>().EvaluateAsync(questions, settings);
        await log.SaveAsync(search);
        await log.SaveAsync(await EvaluatorWith(handler).EvaluateAsync(questions, settings));

        var runs = await log.ListAsync();
        Assert.Equal([EvaluationKind.Answer, EvaluationKind.Search], runs.Select(r => r.Kind));
        var answer = runs[0];
        Assert.Equal(("m", 3, false, 3, 0), (answer.Model, answer.CandidateCount, answer.CustomInstructions, answer.Questions, answer.Skipped));
        Assert.Equal<double?[]>([0.0, 1.0, 0.0], [answer.CitationRate, answer.NoAnswerRate, answer.OffTopicRate]);
        Assert.Equal(1, answer.MissedAnswers);
        var searchRun = runs[1];
        Assert.Null(searchRun.Model);
        Assert.Null(searchRun.CitationRate);
        Assert.Equal(1.0, searchRun.SearchRate);
        Assert.Equal(2, await log.ClearAsync());
        Assert.Empty(await log.ListAsync());
    }

    [Fact]
    [Spec(SpecCategories.Evaluation, "data フォルダの質問セットを優先する", "data フォルダに questions.json があればそれを、なければアプリの samples/questions.json を評価に使う（自社の規則に入れ替えたとき用）。")]
    public void Prefers_questions_in_the_data_folder()
    {
        var set = new EvaluationQuestionSet(_services.DataDirectory, AppContext.BaseDirectory);
        var sample = set.Load();
        File.WriteAllText(set.CustomPath, """[{ "id": "C1", "question": "社用車の予約は？", "expected": ["08_社用車管理規程#第2条"], "notInRules": false, "kind": "基本" }]""");

        Assert.Equal(Samples.Questions.Count, sample.Count);
        Assert.Equal(set.CustomPath, set.FilePath);
        Assert.Equal("C1", Assert.Single(set.Load()).Id);
    }

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
