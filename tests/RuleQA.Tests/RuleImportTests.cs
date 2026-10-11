using System.Net;
using Microsoft.EntityFrameworkCore;
using RuleQA.Core.Domain;
using RuleQA.Core.Rules;
using RuleQA.Core.Settings;
using Xunit.Abstractions;

namespace RuleQA.Tests;

public sealed class RuleImportTests(ITestOutputHelper output) : IDisposable
{
    private readonly TestServices _services = new();

    public void Dispose() => _services.Dispose();

    [Fact]
    [Spec(SpecCategories.Import, "サンプル規則を取り込み、旧版を見分ける", "サブフォルダを含む 8 ファイルを取り込み、同じ規程名の 2021年版（廃止）を旧版にして最新版を記録する。FAQ は参考資料になる。文字コードと番号の乱れの警告を記録する。取り込んだ条文の一覧は付録に載せる。")]
    public async Task Imports_samples_and_marks_old_versions()
    {
        var result = await _services.Get<RuleImporter>().ImportFolderAsync(Samples.RulesFolder);

        Assert.Equal((8, 0, 0), (result.Added, result.Updated, result.Removed));
        await using var db = _services.Db();
        var docs = await db.Documents.AsNoTracking().OrderBy(d => d.Path).ToListAsync();
        var old = docs.Single(d => d.Path == "old/03_旅費規程_2021年版.txt");
        Assert.True(old.IsSuperseded);
        Assert.Equal("03_旅費規程.md", old.SupersededBy);
        Assert.Equal("Shift_JIS", old.Encoding);
        Assert.Equal(["old/03_旅費規程_2021年版.txt"], docs.Where(d => d.IsSuperseded).Select(d => d.Path));
        Assert.Equal(DocumentKind.Reference, docs.Single(d => d.Path == "07_在宅勤務FAQ.md").Kind);
        Assert.Equal("UTF-8 (BOM)", docs.Single(d => d.Path == "06_在宅勤務規程.txt").Encoding);
        Assert.Equal(2, result.Warnings.Count(w => w.StartsWith("04_経費精算規程.md: ")));

        var stats = await _services.Get<RuleImporter>().StatsAsync(null);
        foreach (var article in await _services.ArticlesAsync())
            output.WriteLine($"{article.Key,-34} {(article.Document.IsSuperseded ? "[旧版] " : "")}{article.Heading} | {Preview(article.Body)}");
        output.WriteLine($"規程 {stats.Documents} 件（旧版 {stats.Superseded}・参考資料 {stats.References}）・条文 {stats.Articles} 件");
        foreach (var w in result.Warnings) output.WriteLine("警告: " + w);
    }

    [Fact]
    [Spec(SpecCategories.Import, "変わったファイルだけ取り込み直す", "2 回目の取り込みでは内容が同じファイルを読み飛ばす。1 ファイルを書き換えるとその規程だけを入れ替え、本文の変わらない条文のベクトルは残す。ファイルを消すと規程も消える。")]
    public async Task Reimports_only_changed_files_and_keeps_vectors()
    {
        var folder = _services.CopySamples();
        var importer = _services.Get<RuleImporter>();
        await importer.ImportFolderAsync(folder);
        await using (var db = _services.Db())
        {
            foreach (var a in await db.Articles.Where(a => a.Key.StartsWith("01_")).ToListAsync())
            {
                a.Embedding = [1, 2, 3, 4];
                a.EmbeddingModel = "m";
            }
            await db.SaveChangesAsync();
        }

        var again = await importer.ImportFolderAsync(folder);
        var rules = Path.Combine(folder, "01_就業規則.md");
        await File.WriteAllTextAsync(rules, (await File.ReadAllTextAsync(rules)).Replace("始業時刻は午前9時", "始業時刻は午前9時30分"));
        File.Delete(Path.Combine(folder, "07_在宅勤務FAQ.md"));
        var changed = await importer.ImportFolderAsync(folder);

        Assert.Equal((0, 0, 8, 0), (again.Added, again.Updated, again.Unchanged, again.Removed));
        Assert.Equal((0, 1, 6, 1), (changed.Added, changed.Updated, changed.Unchanged, changed.Removed));
        var articles = await _services.ArticlesAsync();
        Assert.DoesNotContain(articles, a => a.Key.StartsWith("07_"));
        var rulesArticles = articles.Where(a => a.Key.StartsWith("01_")).ToList();
        Assert.Contains("午前9時30分", rulesArticles.Single(a => a.Label == "第5条").Body);
        Assert.Null(rulesArticles.Single(a => a.Label == "第5条").Embedding);
        Assert.Equal(rulesArticles.Count - 1, changed.KeptVectors);
    }

    [Fact]
    [Spec(SpecCategories.Import, "初回だけサンプルを自動で取り込む", "DB が空なら設定のフォルダ（既定は samples/rules）を取り込み、既に取り込んでいれば何もしない。フォルダがなければ警告を返す。")]
    public async Task Imports_samples_only_when_empty()
    {
        var importer = _services.Get<RuleImporter>();

        await importer.ImportIfEmptyAsync(new RulesSettings());
        await importer.ImportIfEmptyAsync(new RulesSettings { Folder = "no-such-folder" });
        var missing = await importer.ImportFolderAsync("no-such-folder");

        Assert.Equal(8, (await importer.StatsAsync(null)).Documents);
        Assert.Contains("フォルダが見つかりません", Assert.Single(missing.Warnings));
    }

    [Fact]
    [Spec(SpecCategories.Evaluation, "評価用の質問セットの正解が実在する", "samples/questions.json の各質問の正解の条文がサンプルの取り込み結果にあり、旧版の条文ではない。記載なしの質問には正解の条文がない。規則と無関係な質問（対象外）は、答えないのが正解の質問として印を付ける。")]
    public async Task Evaluation_questions_point_to_existing_articles()
    {
        await _services.Get<RuleImporter>().ImportFolderAsync(Samples.RulesFolder);
        var articles = (await _services.ArticlesAsync()).ToDictionary(a => a.Key);
        var questions = Samples.Questions;

        Assert.True(questions.Count >= 30);
        Assert.True(questions.Count(q => q.OffTopic) >= 2);
        Assert.Equal(questions.Count, questions.Select(q => q.Id).Distinct().Count());
        Assert.All(questions, q =>
        {
            Assert.Equal(q.NotInRules, q.Expected.Count == 0);
            Assert.True(!q.OffTopic || q.NotInRules, $"{q.Id}: 対象外の質問は notInRules も true にする");
            Assert.All(q.Expected, key => Assert.True(articles.TryGetValue(key, out var a) && !a.Document.IsSuperseded, $"{q.Id}: {key} がありません"));
        });
    }

    [Fact]
    [Spec(SpecCategories.Search, "条文のベクトルを作る", "埋め込みモデルの /embeddings に条文（規程名・条番号・見出し・本文）を送り、ベクトルを保存する。2 回目は作成済みの条文を送らない。")]
    public async Task Builds_article_embeddings_once()
    {
        await _services.Get<RuleImporter>().ImportFolderAsync(Samples.RulesFolder);
        var handler = new FakeHandler(r => FakeHandler.Embeddings(r.Body["input"]!.AsArray().Select(_ => new[] { 1f, 0f }).ToArray()));
        var search = _services.SearchWith(handler);
        var ai = new AiSettings { Provider = AiProvider.OpenAiCompatible, EmbeddingModel = "e" };

        var first = await search.BuildEmbeddingsAsync(ai);
        var second = await search.BuildEmbeddingsAsync(ai);

        var total = (await _services.ArticlesAsync()).Count;
        Assert.Equal((total, 0), (first, second));
        Assert.Equal(total, (await _services.Get<RuleImporter>().StatsAsync("e")).Embedded);
        Assert.Contains(handler.Requests, r => r.Body["input"]!.AsArray().Any(x => x!.GetValue<string>().StartsWith("就業規則 第2章 採用 第4条 試用期間\n")));
    }

    private static string Preview(string body)
    {
        var line = body.Replace('\n', ' ');
        return line.Length > 40 ? line[..40] + "…" : line;
    }
}
