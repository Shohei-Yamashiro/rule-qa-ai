using RuleQA.Core.Ai;
using RuleQA.Core.Domain;
using RuleQA.Core.Evaluation;
using RuleQA.Core.Rules;
using RuleQA.Core.Search;
using RuleQA.Core.Settings;
using Xunit.Abstractions;

namespace RuleQA.Tests;

public sealed class SearchTests(ITestOutputHelper output) : IDisposable
{
    private readonly TestServices _services = new();

    public void Dispose() => _services.Dispose();

    [Fact]
    [Spec(SpecCategories.Search, "評価用の質問で検索正解率を出す（AI なし）", "samples/questions.json の回答できる質問について、キーワード検索の上位 6 件に正解の条文が 1 件以上入る割合が 85% 以上、1 位が正解の割合が 75% 以上。旧版の条文は候補に入らない。質問ごとの結果は付録に載せる（言葉が違う言い換えは、キーワード検索だけでは外れることがある）。")]
    public async Task Keyword_search_finds_expected_articles_for_sample_questions()
    {
        await _services.Get<RuleImporter>().ImportFolderAsync(Samples.RulesFolder);
        var settings = new AppSettings();

        var report = await _services.Get<SearchEvaluator>().EvaluateAsync(Samples.Questions, settings);

        foreach (var row in report.Rows)
        {
            var mark = row.Question.Expected.Count == 0 ? "－" : row.AllFound ? "◎" : row.AnyFound ? "○" : "×";
            output.WriteLine($"{mark} {row.Question.Id} [{row.Question.Kind}] {row.Question.Question}");
            output.WriteLine($"     正解: {(row.Question.Expected.Count == 0 ? "（記載なし）" : string.Join(", ", row.Question.Expected))}");
            output.WriteLine($"     候補: {string.Join(", ", row.Found.Select((k, i) => $"{i + 1}.{k}"))}");
        }
        output.WriteLine($"上位 {report.CandidateCount} 件に正解が 1 件以上: {report.AnyRate:P0}・すべて: {report.AllRate:P0}・1 位が正解: {report.TopRate:P0}（回答できる質問 {report.Answerable.Count} 問）");
        output.WriteLine("◎ 正解がすべて候補に入った　○ 一部が入った　× 入らなかった　－ 記載なしが正解（回答の段階で判定）");

        Assert.False(report.UsedVectors);
        Assert.DoesNotContain(report.Rows.SelectMany(r => r.Found), k => k.StartsWith("old/"));
        Assert.True(report.AnyRate >= 0.85, $"正解が候補に入った割合 {report.AnyRate:P0}");
        Assert.True(report.TopRate >= 0.75, $"1 位が正解の割合 {report.TopRate:P0}");
    }

    [Fact]
    [Spec(SpecCategories.Search, "旧版は設定したときだけ検索する", "既定では廃止された 2021年版の条文を検索しない。「旧版も検索する」をオンにすると、「グリーン車」で 2021年版の第3条も見つかる。")]
    public async Task Old_versions_are_searched_only_when_asked()
    {
        await _services.Get<RuleImporter>().ImportFolderAsync(Samples.RulesFolder);
        var search = _services.Get<RuleSearchService>();
        var settings = new AppSettings();

        var current = await search.SearchAsync("グリーン車", settings);
        settings.Rules.IncludeSuperseded = true;
        var withOld = await search.SearchAsync("グリーン車", settings);

        Assert.DoesNotContain(current, h => h.Article.Document.IsSuperseded);
        Assert.Equal("old/03_旅費規程_2021年版#第3条", withOld[0].Article.Key);
    }

    [Fact]
    [Spec(SpecCategories.Search, "1〜2 文字の語でも検索できる", "1 文字の語（「寮」）と 2 文字の語（「日当」、全角で入力した「ｐｃ」）でも、その語を含む条文が見つかる。ひらがなだけの言い回し（「ですか」）では見つからない。")]
    public void Short_words_are_searchable()
    {
        var index = ArticleIndex.Create(
        [
            Article("就業規則", "第1条", "社員寮の利用は総務部に申し込む。"),
            Article("就業規則", "第2条", "国内出張の日当は1,000円とする。"),
            Article("情報セキュリティ規程", "3. 端末", "私物のPCを使う場合は登録する。"),
        ]);

        Assert.Equal("就業規則#第1条", index.Search("寮", null, 3).Single().Article.Key);
        Assert.Equal("就業規則#第2条", index.Search("日当は？", null, 3)[0].Article.Key);
        Assert.Equal("情報セキュリティ規程#3. 端末", index.Search("ｐｃ", null, 3)[0].Article.Key);
        Assert.Empty(index.Search("ですか", null, 3));
    }

    [Fact]
    [Spec(SpecCategories.Search, "参考資料（FAQ）は規程より下に並べる", "同じくらい言葉が一致しても、FAQ の段落は規程の条文より後になる（キーワードの点数を 0.7 倍）。")]
    public void Reference_material_ranks_below_regulations()
    {
        var faq = Article("在宅勤務FAQ", "段落1", "在宅勤務の申請は前日までに行う。", DocumentKind.Reference);
        var rule = Article("在宅勤務規程", "第3条", "在宅勤務の申請は3営業日前までに行う。");

        var hits = ArticleIndex.Create([faq, rule]).Search("在宅勤務の申請", null, 2);

        Assert.Equal(["在宅勤務規程#第3条", "在宅勤務FAQ#段落1"], hits.Select(h => h.Article.Key));
    }

    [Fact]
    [Spec(SpecCategories.Search, "ベクトル検索とキーワード検索を合わせる", "埋め込みがある条文は、言葉が一致しなくても意味の近さで候補に入る。両方で上位の条文が先に並ぶ（RRF）。別のモデルで作ったベクトルは使わない。")]
    public async Task Vector_search_adds_articles_by_meaning()
    {
        var embedder = new FakeEmbedder();
        var articles = new[]
        {
            Article("勤務規程", "第8条", "所定の勤務時間を超えて勤務するときは、事前に所属長の承認を得る。"),
            Article("旅費規程", "第5条", "国内出張の日当は1,000円とする。"),
            Article("休暇規程", "第2条", "年次有給休暇は入社6か月後に付与する。"),
        };
        var vectors = await embedder.EmbedAsync(articles.Select(a => a.EmbeddingText).ToList(), CancellationToken.None);
        for (var i = 0; i < articles.Length; i++)
        {
            articles[i].Embedding = ArticleIndex.ToBytes(vectors[i]);
            articles[i].EmbeddingModel = embedder.Model;
        }
        var index = ArticleIndex.Create(articles, embedder.Model);
        var query = "残業のルール";

        var hits = index.Search(query, (await embedder.EmbedAsync([query], CancellationToken.None))[0], 2);

        Assert.True(index.HasVectors);
        Assert.Equal("勤務規程#第8条", hits[0].Article.Key);
        Assert.Equal(0, hits[0].Keyword);
        Assert.True(hits[0].Vector > 0.99);
        Assert.False(ArticleIndex.Create(articles, "other-model").HasVectors);
    }

    [Fact]
    [Spec(SpecCategories.Search, "埋め込みに失敗したらキーワード検索で続ける", "検索中に埋め込みモデルがエラーを返したら、その回からキーワード検索だけで候補を返し、以後は埋め込みを呼ばない。")]
    public async Task Falls_back_to_keywords_when_embedding_fails()
    {
        var article = Article("旅費規程", "第5条", "国内出張の日当は1,000円とする。");
        article.Embedding = ArticleIndex.ToBytes([1f, 0f]);
        article.EmbeddingModel = "e";
        var handler = new FakeHandler(_ => FakeHandler.Reply(System.Net.HttpStatusCode.InternalServerError, "{}"));
        var embedder = new AiClientFactory(handler).CreateEmbedder(new AiSettings { Provider = AiProvider.OpenAiCompatible, EmbeddingModel = "e" });
        var search = new RuleSearch(ArticleIndex.Create([article], "e"), embedder, 6, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var first = await search.SearchAsync("日当", CancellationToken.None);
        var second = await search.SearchAsync("日当", CancellationToken.None);

        Assert.Equal("旅費規程#第5条", first.Single().Article.Key);
        Assert.Single(second);
        Assert.False(search.UsesVectors);
        Assert.Single(handler.Requests);
    }

    private static Article Article(string title, string label, string body, DocumentKind kind = DocumentKind.Regulation)
    {
        var doc = new RuleDocument { Path = title + ".md", Title = title, BaseTitle = title, Kind = kind };
        return new Article { Document = doc, Key = $"{title}#{label}", Label = label, Body = body };
    }

    /// <summary>Two-dimensional "meaning": overtime and everything else.</summary>
    private sealed class FakeEmbedder : IEmbedder
    {
        public string Model => "fake";

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct) =>
            Task.FromResult(inputs.Select(t => OpenAiCompatibleEmbedder.Normalize(
            [
                t.Contains("残業") || t.Contains("勤務時間を超えて") ? 1f : 0.01f,
                t.Contains("日当") || t.Contains("有給") ? 1f : 0.01f,
            ])).ToArray());
    }
}
