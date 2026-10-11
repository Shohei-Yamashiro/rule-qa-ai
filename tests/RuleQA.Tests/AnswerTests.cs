using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RuleQA.Core.Ai;
using RuleQA.Core.Answering;
using RuleQA.Core.Rules;
using RuleQA.Core.Search;
using RuleQA.Core.Settings;

namespace RuleQA.Tests;

public sealed class AnswerTests : IDisposable
{
    private readonly TestServices _services = new();

    public AnswerTests() => _services.Get<RuleImporter>().ImportFolderAsync(Samples.RulesFolder).GetAwaiter().GetResult();

    public void Dispose() => _services.Dispose();

    private static AppSettings AiSettings() => new() { Ai = { Provider = AiProvider.OpenAiCompatible, Endpoint = "http://ai.test/v1", Model = "m" } };

    private RuleAnswerService ServiceWith(FakeHandler handler) =>
        new(_services.SearchWith(handler), new AiClientFactory(handler), NullLogger<RuleAnswerService>.Instance);

    private static FakeHandler Replying(object answer) =>
        new(_ => FakeHandler.Completion(JsonSerializer.Serialize(answer)));

    private static string Prompt(CapturedRequest request) =>
        request.Body["messages"]![1]!["content"]!.GetValue<string>();

    [Fact]
    [Spec(SpecCategories.Answer, "候補の条文と回答の指示を AI に送る", "システムプロンプトに「候補の条文だけを根拠に答える」「記載がなければ answered を false」などの指示が入る。ユーザーのメッセージに、候補の条文が [A1] 形式の ID・規程名・条番号・本文の形で入り、参考資料や施行日の印も付く。JSON スキーマ（answer・citations・answered）を指定する。")]
    public async Task Sends_candidates_and_instructions()
    {
        var handler = Replying(new { answer = "国内出張の日当は日帰り1,000円です。", citations = new[] { "A1" }, answered = true });

        await ServiceWith(handler).AnswerAsync("国内出張の日当はいくらですか？", [], AiSettings());

        var request = Assert.Single(handler.Requests);
        var system = request.Body["messages"]![0]!["content"]!.GetValue<string>();
        Assert.Contains("候補の条文」に書かれていることだけを根拠に答える", system);
        Assert.Contains("answered を false", system);
        var prompt = Prompt(request);
        Assert.Contains("[A1] 旅費規程 第5条", prompt);
        Assert.Contains("1,000円", prompt);
        Assert.Contains("2025年4月1日施行・改定", prompt);
        Assert.EndsWith("## 質問\n国内出張の日当はいくらですか？", prompt.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("old/", prompt);
        var format = request.Body["response_format"]!;
        Assert.Equal("rule_answer", format["json_schema"]!["name"]!.GetValue<string>());
        Assert.Equal(["answer", "citations", "answered", "off_topic"], format["json_schema"]!["schema"]!["required"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    [Spec(SpecCategories.Answer, "根拠の条文を候補と突き合わせる", "AI が返した ID のうち候補にあるものだけを根拠にし、候補にない ID（A99、作り話の条番号）は消して記録する。「［A1］」のように書き方が違っても読み取る。回答文に残った ID は規程名と条番号に置き換える。")]
    public async Task Keeps_only_citations_that_were_candidates()
    {
        var handler = Replying(new { answer = "日帰りは1,000円です[A1]。", citations = new[] { "［A1］", "A99", "旅費規程第20条", "A1" }, answered = true });

        var answer = await ServiceWith(handler).AnswerAsync("国内出張の日当はいくらですか？", [], AiSettings());

        Assert.Equal(AnswerStatus.Answered, answer.Status);
        Assert.Equal("03_旅費規程#第5条", Assert.Single(answer.Citations).Key);
        Assert.Equal(["A99", "旅費規程第20条"], answer.DroppedCitations);
        Assert.Equal("日帰りは1,000円です（旅費規程 第5条）。", answer.Text);
        Assert.Equal("AI（m）", answer.AiName);
    }

    [Fact]
    [Spec(SpecCategories.Answer, "規程名と条番号で返された根拠も候補と突き合わせる", "ID の代わりに「在宅勤務規程 第3条」「就業規則第9条（在宅勤務）」のような規程名と条番号や、「A1: 在宅勤務規程 第3条」の形で返されても、候補の条文と一致すれば根拠にする（小さなモデルでよく起きる）。候補にない条文の名前（在宅勤務FAQ 段落9）は外す。システムプロンプトでは ID だけを入れるよう指示する。")]
    public async Task Accepts_citations_written_as_article_names()
    {
        var handler = Replying(new
        {
            answer = "在宅勤務規程では3営業日前までに申請します。",
            citations = new[] { "在宅勤務規程 第3条", "就業規則第9条（在宅勤務）", "A1: 在宅勤務規程 第3条", "在宅勤務FAQ 段落9" },
            answered = true,
        });

        var answer = await ServiceWith(handler).AnswerAsync("在宅勤務はいつまでに申請する？", [], AiSettings());

        Assert.Equal(AnswerStatus.Answered, answer.Status);
        Assert.Equal(["06_在宅勤務規程#第3条", "01_就業規則#第9条"], answer.Citations.Select(a => a.Key));
        Assert.Equal(["在宅勤務FAQ 段落9"], answer.DroppedCitations);
        Assert.Contains("規程名や条番号ではない", Assert.Single(handler.Requests).Body["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Theory]
    [Spec(SpecCategories.Answer, "根拠がなければ「記載なし」と答える", "AI が answered を false にしたとき、answered が true でも根拠が候補にない ID だけのとき、根拠が空のときは、AI の回答文を使わず「規則には記載がありません。」と答え、候補は「関連しそうな条文」として残す。")]
    [InlineData("""{"answer": "給料日は規則に書かれていません。", "citations": [], "answered": false}""")]
    [InlineData("""{"answer": "給料日は毎月25日です。", "citations": ["A42"], "answered": true}""")]
    [InlineData("""{"answer": "給料日は毎月25日です。", "citations": [], "answered": true}""")]
    public async Task Answers_not_in_rules_without_valid_citations(string reply)
    {
        var handler = new FakeHandler(_ => FakeHandler.Completion(reply));

        var answer = await ServiceWith(handler).AnswerAsync("給料日はいつですか？", [], AiSettings());

        Assert.Equal(AnswerStatus.NotInRules, answer.Status);
        Assert.Equal(RuleAnswerService.NotInRulesText, answer.Text);
        Assert.Empty(answer.Citations);
        Assert.NotEmpty(answer.Candidates);
    }

    [Fact]
    [Spec(SpecCategories.Answer, "規則と関係のない質問は対象外と答える", "AI が off_topic を true にしたとき（「明日の天気は？」など）は、記載なしとは分けて「社内規則についての質問ではないようです」と答え、取り込んだ規程の名前（旧版と参考資料を除き、6 件まで）を案内に入れる。システムプロンプトには、給与や福利厚生など会社や仕事の質問は候補に答えがなくても対象外にしない指示が入る。")]
    public async Task Off_topic_questions_are_told_apart()
    {
        var handler = Replying(new { answer = "", citations = Array.Empty<string>(), answered = false, off_topic = true });

        var answer = await ServiceWith(handler).AnswerAsync("明日の天気は？", [], AiSettings());

        Assert.Equal(AnswerStatus.OffTopic, answer.Status);
        Assert.Equal("社内規則についての質問ではないようです。取り込んだ規則（就業規則・休暇規程・旅費規程・経費精算規程・情報セキュリティ規程・在宅勤務規程）に関することを質問してください。", answer.Text);
        Assert.Empty(answer.Citations);
        Assert.Contains("候補に答えがなくても off_topic を false", Assert.Single(handler.Requests).Body["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    [Spec(SpecCategories.Answer, "自社の規則に入れ替えても同じように答える", "サンプルの代わりに別の会社の規程（社用車管理規程）だけを取り込んでも、候補の条文・根拠の突き合わせ・対象外の案内が、その規程の内容と名前で動く（サンプルの規程名や会社名に頼らない）。")]
    public async Task Works_with_replaced_rules()
    {
        using var other = new TestServices();
        var folder = Path.Combine(other.DataDirectory, "my-rules");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "社用車管理規程.md"), """
            # 社用車管理規程

            ### 第1条（目的）
            この規程は、社用車の利用について定める。

            ### 第2条（予約）
            社用車を利用するときは、利用日の2日前までに総務課に予約する。
            """);
        await other.Get<RuleImporter>().ImportFolderAsync(folder);
        var handler = new FakeHandler(r => FakeHandler.Completion(Prompt(r).Contains("天気")
            ? """{"answer": "", "citations": [], "answered": false, "off_topic": true}"""
            : """{"answer": "利用日の2日前までに予約します。", "citations": ["社用車管理規程 第2条"], "answered": true, "off_topic": false}"""));
        var service = new RuleAnswerService(other.SearchWith(handler), new AiClientFactory(handler), NullLogger<RuleAnswerService>.Instance);

        var answer = await service.AnswerAsync("社用車の予約はいつまで？", [], AiSettings());
        var offTopic = await service.AnswerAsync("明日の天気は？", [], AiSettings());

        Assert.Equal("社用車管理規程#第2条", Assert.Single(answer.Citations).Key);
        Assert.Equal("社内規則についての質問ではないようです。取り込んだ規則（社用車管理規程）に関することを質問してください。", offTopic.Text);
        Assert.Equal(["社用車管理規程"], (await other.Get<RuleImporter>().StatsAsync(null)).Titles);
    }

    [Fact]
    [Spec(SpecCategories.Answer, "矛盾する規程の条文をどちらも AI に渡す", "在宅勤務の申請期限を聞くと、在宅勤務規程第3条（3営業日前）と就業規則第9条（前日まで）がどちらも候補として送られる。両方を引用した回答はそのまま根拠 2 件になる。システムプロンプトには、食い違いを伝えて優先の定めに従う指示が入る。")]
    public async Task Sends_conflicting_articles_together()
    {
        var handler = new FakeHandler(r =>
        {
            var prompt = Prompt(r);
            string Id(string name) => prompt.Split('\n').First(l => l.Contains(name))[1..3];
            return FakeHandler.Completion(JsonSerializer.Serialize(new
            {
                answer = "在宅勤務規程では3営業日前、就業規則では前日までと食い違っています。",
                citations = new[] { Id("在宅勤務規程 第3条"), Id("就業規則 第9条") },
                answered = true,
            }));
        });

        var answer = await ServiceWith(handler).AnswerAsync("在宅勤務はいつまでに申請する？", [], AiSettings());

        var request = Assert.Single(handler.Requests);
        Assert.Contains("食い違う", request.Body["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal(["06_在宅勤務規程#第3条", "01_就業規則#第9条"], answer.Citations.Select(a => a.Key));
    }

    [Fact]
    [Spec(SpecCategories.Answer, "会話の続きを前の質問と合わせて扱う", "「海外の場合は？」だけの検索では 1 位が旅費規程第3条（海外出張の申請）で、国内の日当の条文は入らない。直前の質問（国内出張の日当）と合わせた検索も行って順位を合わせるので、旅費規程第7条（海外出張の日当）が 1 位になり、第5条（国内出張の日当）も候補に入る。プロンプトには直近 3 往復までの質問と回答が入る。")]
    public async Task Follow_up_questions_use_the_conversation()
    {
        var handler = Replying(new { answer = "海外出張の日当は地域で異なります。", citations = new[] { "A1" }, answered = true });
        var history = new List<ConversationTurn>
        {
            new("有給は入社してからいつもらえる？", "6か月後です。"),
            new("始業時刻は？", "9時です。"),
            new("副業はできますか？", "許可制です。"),
            new("国内出張の日当はいくらですか？", "日帰り1,000円、宿泊2,500円です。"),
        };

        var rules = await _services.Get<RuleSearchService>().OpenAsync(new AppSettings());
        var alone = await RuleAnswerService.FindCandidatesAsync(rules, "海外の場合は？", []);

        var answer = await ServiceWith(handler).AnswerAsync("海外の場合は？", history, AiSettings());

        Assert.Equal("03_旅費規程#第3条", alone[0].Article.Key);
        Assert.DoesNotContain(alone, h => h.Article.Key == "03_旅費規程#第5条");
        Assert.Equal("03_旅費規程#第7条", answer.Candidates[0].Article.Key);
        Assert.Contains(answer.Candidates, h => h.Article.Key == "03_旅費規程#第5条");
        var prompt = Prompt(Assert.Single(handler.Requests));
        Assert.Contains("## これまでの会話", prompt);
        Assert.Contains("回答：日帰り1,000円、宿泊2,500円です。", prompt);
        Assert.Contains("質問：始業時刻は？", prompt);
        Assert.DoesNotContain("有給は入社してから", prompt);
    }

    [Fact]
    [Spec(SpecCategories.Answer, "AI なしでは候補の条文を返す", "AI を使わない設定では AI に何も送らず、質問に近い条文を「該当しそうな条文」として返す（1 位は旅費規程第5条）。")]
    public async Task Without_ai_returns_search_results()
    {
        var handler = Replying(new { answer = "", citations = Array.Empty<string>(), answered = false });

        var answer = await ServiceWith(handler).AnswerAsync("国内出張の日当はいくらですか？", [], new AppSettings());

        Assert.Equal(AnswerStatus.SearchOnly, answer.Status);
        Assert.Equal(RuleAnswerService.SearchOnlyText, answer.Text);
        Assert.Equal("03_旅費規程#第5条", answer.Candidates[0].Article.Key);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    [Spec(SpecCategories.Answer, "AI のエラーでは候補の条文を返す", "AI の接続先がエラーを返したときや、出し直しを頼んでも JSON として読めないときは、エラーの内容を添えて候補の条文を返す。")]
    public async Task Ai_errors_fall_back_to_search_results()
    {
        var failing = new FakeHandler(_ => FakeHandler.Reply(HttpStatusCode.InternalServerError, """{"error": "down"}"""));
        var broken = new FakeHandler(_ => FakeHandler.Completion("日当は1,000円です。"));

        var error = await ServiceWith(failing).AnswerAsync("国内出張の日当はいくらですか？", [], AiSettings());
        var notJson = await ServiceWith(broken).AnswerAsync("国内出張の日当はいくらですか？", [], AiSettings());

        Assert.Equal(AnswerStatus.SearchOnly, error.Status);
        Assert.Contains("HTTP 500", error.Error);
        Assert.NotEmpty(error.Candidates);
        Assert.Equal(AnswerStatus.SearchOnly, notJson.Status);
        Assert.Contains("読み取れませんでした", notJson.Error);
        Assert.Equal(2, broken.Requests.Count);
    }
}
