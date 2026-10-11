using Microsoft.EntityFrameworkCore;
using RuleQA.Core.Answering;
using RuleQA.Core.Domain;
using RuleQA.Core.Rules;
using RuleQA.Core.Search;

namespace RuleQA.Tests;

public sealed class HistoryAndLibraryTests : IDisposable
{
    private readonly TestServices _services = new();

    public void Dispose() => _services.Dispose();

    private async Task<RuleAnswer> AnswerAsync(string question, AnswerStatus status, string text, string[] citations, string[] candidates)
    {
        var library = _services.Get<RuleLibrary>();
        async Task<Article> Get(string key) => await library.ArticleAsync(key) ?? throw new InvalidOperationException(key);
        var cited = new List<Article>();
        foreach (var key in citations) cited.Add(await Get(key));
        var hits = new List<ArticleHit>();
        foreach (var key in candidates) hits.Add(new ArticleHit(await Get(key), 1, null, 1));
        return new RuleAnswer(question, status, text, cited, hits, [], null, "AI（m）");
    }

    [Fact]
    [Spec(SpecCategories.Library, "質問と回答を履歴に保存して読み出す", "回答ごとに会話 ID・会話の中の順番・結果・回答文・根拠と候補の条文キー・AI 名・所要時間を保存する。一覧は新しい順で、結果と文字（全角・半角を問わない）で絞り込める。会話 ID で 1 つの会話を順番どおりに読み出し、続きの質問に使える形（質問と回答文）にできる。結果ごとの件数を数え、全件削除できる。")]
    public async Task Saves_and_reads_questions_and_answers()
    {
        await _services.Get<RuleImporter>().ImportFolderAsync(Samples.RulesFolder);
        var history = _services.Get<QaHistory>();
        var first = QaHistory.NewConversationId();
        var second = QaHistory.NewConversationId();

        await history.SaveAsync(first, await AnswerAsync("国内出張の日当はいくら？", AnswerStatus.Answered, "日帰り1,000円です。", ["03_旅費規程#第5条"], ["03_旅費規程#第5条", "03_旅費規程#第7条"]), TimeSpan.FromSeconds(1.5));
        await history.SaveAsync(first, await AnswerAsync("海外の場合は？", AnswerStatus.Answered, "6,000円です。", ["03_旅費規程#第7条"], ["03_旅費規程#第7条"]), TimeSpan.FromSeconds(2));
        await history.SaveAsync(second, await AnswerAsync("給料日はいつ？", AnswerStatus.NotInRules, RuleAnswerService.NotInRulesText, [], ["01_就業規則#第12条"]), TimeSpan.Zero);

        var all = await history.ListAsync(new HistoryQuery());
        Assert.Equal(["給料日はいつ？", "海外の場合は？", "国内出張の日当はいくら？"], all.Select(e => e.Record.Question));
        Assert.Equal(["給料日はいつ？"], (await history.ListAsync(new HistoryQuery(Status: AnswerStatus.NotInRules))).Select(e => e.Record.Question));
        Assert.Equal(["国内出張の日当はいくら？"], (await history.ListAsync(new HistoryQuery("１，０００円"))).Select(e => e.Record.Question));

        var conversation = await history.ConversationAsync(first);
        Assert.Equal([1, 2], conversation.Select(e => e.Record.Turn));
        Assert.Equal("03_旅費規程#第5条", Assert.Single(conversation[0].ToAnswer().Citations).Key);
        Assert.Equal(2, conversation[0].ToAnswer().Candidates.Count);
        Assert.Equal(new ConversationTurn("海外の場合は？", "6,000円です。"), conversation[1].ToTurn());
        Assert.Equal(1500, conversation[0].Record.ElapsedMs);
        Assert.Equal("AI（m）", conversation[0].Record.AiName);

        var counts = await history.CountsAsync();
        Assert.Equal((2, 1, 0), (counts[AnswerStatus.Answered], counts[AnswerStatus.NotInRules], counts[AnswerStatus.OffTopic]));
        Assert.Equal(3, await history.ClearAsync());
        Assert.Empty(await history.ListAsync(new HistoryQuery()));
    }

    [Fact]
    [Spec(SpecCategories.Library, "よくある質問を履歴から出す", "直近 90 日に回答できた質問を、聞かれた回数の多い順に出す。全角・半角や記号だけが違う質問は 1 つにまとめ、最後の言い回しを使う。記載なしの質問、会話の続きの質問（2 問目以降）、90 日より前の質問、根拠の条文が今の規則にない質問は出さない。")]
    public async Task Lists_popular_questions()
    {
        await _services.Get<RuleImporter>().ImportFolderAsync(Samples.RulesFolder);
        var history = _services.Get<QaHistory>();
        async Task Save(string conversation, string question, AnswerStatus status, params string[] cited) =>
            await history.SaveAsync(conversation, await AnswerAsync(question, status, "回答", cited, cited), TimeSpan.Zero);

        await Save("a", "国内出張の日当はいくら？", AnswerStatus.Answered, "03_旅費規程#第5条");
        await Save("b", "国内出張の日当はいくら?", AnswerStatus.Answered, "03_旅費規程#第5条");
        await Save("c", "パスワードは何文字？", AnswerStatus.Answered, "05_情報セキュリティ規程#2. パスワード");
        await Save("c", "海外の場合は？", AnswerStatus.Answered, "03_旅費規程#第7条");
        await Save("d", "給料日はいつ？", AnswerStatus.NotInRules);
        await using (var db = _services.Db())
        {
            db.QaRecords.Add(new QaRecord { ConversationId = "e", Turn = 1, AskedAtUtc = DateTime.UtcNow.AddDays(-120), Question = "昔の質問", Status = AnswerStatus.Answered, CitationKeys = "03_旅費規程#第5条" });
            db.QaRecords.Add(new QaRecord { ConversationId = "f", Turn = 1, AskedAtUtc = DateTime.UtcNow, Question = "なくなった規程の質問", Status = AnswerStatus.Answered, CitationKeys = "99_廃止した規程#第1条" });
            await db.SaveChangesAsync();
        }

        var popular = await history.PopularAsync();

        Assert.Equal([("国内出張の日当はいくら?", 2), ("パスワードは何文字？", 1)], popular.Select(p => (p.Question, p.Count)));
    }

    [Fact]
    [Spec(SpecCategories.Library, "質問の例を規程をまたいで選ぶ", "よくある質問が足りない分は、条の見出しから質問の例を選ぶ。規程を順に回って選ぶので、3 件なら 3 つの規程から 1 つずつになり、同じ見出しは重ねない。見出しが足りなければある分だけ返す。見出しだけでは分かりにくいので、例の質問には規程名を入れる（「A規程のa1について教えてください」）。")]
    public void Picks_examples_across_regulations()
    {
        var topics = new List<RuleTopic>
        {
            new("A規程", "a.md", ["a1", "a2", "a3"]),
            new("B規程", "b.md", ["b1", "b2"]),
            new("C規程", "c.md", ["c1"]),
            new("D規程", "d.md", []),
        };

        var three = RuleLibrary.PickExamples(topics, 3, new Random(1));
        var all = RuleLibrary.PickExamples(topics, 10, new Random(2));

        Assert.Equal(["A規程", "B規程", "C規程"], three.Select(e => e.Title).Order());
        Assert.All(three, e => Assert.StartsWith(e.Title[..1].ToLowerInvariant(), e.Topic));
        Assert.Equal(6, all.Count);
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal("A規程のa1について教えてください", new RuleExample("A規程", "a1").Question);
    }

    [Fact]
    [Spec(SpecCategories.Library, "取り込み直しで消えた条文を履歴で見分ける", "履歴の根拠は条文キーで持つので、規則を取り込み直しても今の条文と結び付く。ファイルを消して条文がなくなったら、そのキーを「今の規則にない」として返し、会話の続きでは根拠から外す。")]
    public async Task History_survives_reimports()
    {
        var folder = _services.CopySamples();
        var importer = _services.Get<RuleImporter>();
        await importer.ImportFolderAsync(folder);
        var history = _services.Get<QaHistory>();
        var id = QaHistory.NewConversationId();
        await history.SaveAsync(id, await AnswerAsync("在宅勤務手当は？", AnswerStatus.Answered, "月3,000円です。", ["06_在宅勤務規程#第7条第1項", "07_在宅勤務FAQ#段落5"], ["06_在宅勤務規程#第7条第1項", "07_在宅勤務FAQ#段落5"]), TimeSpan.Zero);

        File.WriteAllText(Path.Combine(folder, "01_就業規則.md"), File.ReadAllText(Path.Combine(folder, "01_就業規則.md")) + "\n");
        File.Delete(Path.Combine(folder, "07_在宅勤務FAQ.md"));
        await importer.ImportFolderAsync(folder);

        var entry = Assert.Single(await history.ConversationAsync(id));
        Assert.Equal(["07_在宅勤務FAQ#段落5"], entry.MissingKeys);
        Assert.Equal(["06_在宅勤務規程#第7条第1項"], entry.ToAnswer().Citations.Select(a => a.Key));
    }

    [Fact]
    [Spec(SpecCategories.Library, "前の版の DB に履歴の表を足す", "履歴の表がない DB（前の版で作った DB）を開くと、取り込んだ規則を残したまま履歴の表を作り、保存できるようになる。")]
    public async Task Adds_history_table_to_older_databases()
    {
        await _services.Get<RuleImporter>().ImportFolderAsync(Samples.RulesFolder);
        await using (var db = _services.Db())
            await db.Database.ExecuteSqlRawAsync("DROP TABLE QaRecords");

        await using (var db = _services.Db())
            Core.Data.SchemaUpgrader.EnsureSchema(db);
        await _services.Get<QaHistory>().SaveAsync("c", new RuleAnswer("q", AnswerStatus.SearchOnly, "t", [], [], [], null, null), TimeSpan.Zero);

        Assert.Equal(8, (await _services.Get<RuleImporter>().StatsAsync(null)).Documents);
        Assert.Single(await _services.Get<QaHistory>().ListAsync(new HistoryQuery()));
    }

    [Fact]
    [Spec(SpecCategories.Library, "規則一覧に規程と条文を出す", "規程はファイルの順に並べ、旧版は指定したときだけ含める。旧版には新しい版のファイル、参考資料には種類、取り込み時の警告（経費精算規程の重複と欠番）、条文数を付ける。1 つの規程の条文は元の順で、章を持つ。")]
    public async Task Lists_documents_and_articles()
    {
        await _services.Get<RuleImporter>().ImportFolderAsync(Samples.RulesFolder);
        var library = _services.Get<RuleLibrary>();

        var current = await library.DocumentsAsync(includeSuperseded: false);
        var all = await library.DocumentsAsync(includeSuperseded: true);

        Assert.Equal(7, current.Count);
        Assert.Equal("01_就業規則.md", current[0].Path);
        var old = Assert.Single(all, d => d.IsSuperseded);
        Assert.Equal(("old/03_旅費規程_2021年版.txt", "03_旅費規程.md"), (old.Path, old.SupersededBy));
        Assert.Equal(DocumentKind.Reference, current.Single(d => d.Path == "07_在宅勤務FAQ.md").Kind);
        Assert.Equal(2, current.Single(d => d.Path == "04_経費精算規程.md").Warnings.Count);
        Assert.All(all, d => Assert.True(d.Articles > 0));

        var rules = await library.DocumentAsync(current[0].Id);
        Assert.NotNull(rules);
        Assert.Equal(current[0].Articles, rules.Articles.Count);
        Assert.Equal("第1条", rules.Articles[0].Label);
        Assert.Equal(rules.Articles.OrderBy(a => a.Order).Select(a => a.Key), rules.Articles.Select(a => a.Key));
        Assert.Contains(rules.Articles, a => a.Chapter is not null);
    }

    [Fact]
    [Spec(SpecCategories.Library, "質問できる内容を取り込んだ規程から作る", "今の規程（旧版と参考資料を除く）ごとに、条の見出しを順に並べる（旅費規程なら「国内出張の日当」）。条のない規程は節の名前から番号を除く（「2. パスワード」→「パスワード」）。「目的」「附則」のような、どの規程にもある見出しは除く。")]
    public async Task Lists_topics_from_imported_rules()
    {
        await _services.Get<RuleImporter>().ImportFolderAsync(Samples.RulesFolder);

        var topics = await _services.Get<RuleLibrary>().TopicsAsync();

        Assert.Equal(["就業規則", "休暇規程", "旅費規程", "経費精算規程", "情報セキュリティ規程", "在宅勤務規程"], topics.Select(t => t.Title));
        var travel = topics.Single(t => t.Title == "旅費規程").Topics;
        Assert.Contains("国内出張の日当", travel);
        Assert.DoesNotContain("目的", travel);
        Assert.Contains("パスワード", topics.Single(t => t.Title == "情報セキュリティ規程").Topics);
        Assert.All(topics, t => Assert.DoesNotContain(t.Topics, x => x.StartsWith("第") || x.Contains("附")));
    }

    [Fact]
    [Spec(SpecCategories.Library, "規則を足すと案内とプロンプトが追従する", "規則ファイルを 1 つ足して取り込み直すと、規程名の一覧・質問できる内容・最終取り込み日時・AI に渡す「取り込んだ規程」・対象外の案内（6 件を超えたら「など 7 件」）に、足した規程がそのまま反映される（文言を手で直す必要がない）。")]
    public async Task Wording_follows_added_rules()
    {
        var folder = _services.CopySamples();
        var importer = _services.Get<RuleImporter>();
        await importer.ImportFolderAsync(folder);
        var before = await importer.StatsAsync(null);
        File.WriteAllText(Path.Combine(folder, "08_社用車管理規程.md"), """
            # 社用車管理規程

            ### 第1条（目的）
            この規程は、社用車の利用について定める。

            ### 第2条（予約）
            社用車を利用するときは、利用日の2日前までに総務課に予約する。
            """);
        await importer.ImportFolderAsync(folder);

        var after = await importer.StatsAsync(null);
        var topics = await _services.Get<RuleLibrary>().TopicsAsync();
        var handler = new FakeHandler(_ => FakeHandler.Completion("""{"answer": "", "citations": [], "answered": false, "off_topic": true}"""));
        var service = new RuleAnswerService(_services.SearchWith(handler), new RuleQA.Core.Ai.AiClientFactory(handler), Microsoft.Extensions.Logging.Abstractions.NullLogger<RuleAnswerService>.Instance);
        var settings = new RuleQA.Core.Settings.AppSettings { Ai = { Provider = RuleQA.Core.Settings.AiProvider.OpenAiCompatible, Endpoint = "http://ai.test/v1", Model = "m" } };
        var answer = await service.AnswerAsync("明日の天気は？", [], settings);

        Assert.Equal("社用車管理規程", after.Titles[^1]);
        Assert.True(after.LastImportedUtc >= before.LastImportedUtc);
        Assert.Equal(["予約"], topics.Single(t => t.Title == "社用車管理規程").Topics);
        var prompt = handler.Requests.Single().Body["messages"]![1]!["content"]!.GetValue<string>().ReplaceLineEndings("\n");
        Assert.Contains("## 取り込んだ規程\n就業規則、休暇規程、旅費規程、経費精算規程、情報セキュリティ規程、在宅勤務規程、社用車管理規程\n", prompt);
        Assert.EndsWith("情報セキュリティ規程・在宅勤務規程 など 7 件）に関することを質問してください。", answer.Text);
    }

    [Fact]
    [Spec(SpecCategories.Library, "規則一覧で条文を絞り込む", "規程名・条番号・見出し・本文に含まれる文字で絞り込み、全角・大文字で入力した「ＶＰＮ」でも VPN の条文（情報セキュリティ規程と FAQ）が見つかる。「グリーン車」は旧版にしかないので、旧版を含めたときだけ見つかる。")]
    public async Task Filters_articles_by_text()
    {
        await _services.Get<RuleImporter>().ImportFolderAsync(Samples.RulesFolder);
        var library = _services.Get<RuleLibrary>();

        var vpn = await library.FindAsync("ＶＰＮ", includeSuperseded: false);

        Assert.Contains(vpn, a => a.Key.StartsWith("05_"));
        Assert.Contains(vpn, a => a.Key.StartsWith("07_"));
        Assert.All(vpn, a => Assert.Contains("vpn", ArticleIndex.Normalize(a.Document.Title + a.Heading + a.Body)));
        Assert.Empty(await library.FindAsync("グリーン車", includeSuperseded: false));
        Assert.Equal("old/03_旅費規程_2021年版#第3条", (await library.FindAsync("グリーン車", includeSuperseded: true)).Single().Key);
    }
}
