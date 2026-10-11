using Microsoft.EntityFrameworkCore;
using RuleQA.Core.Domain;
using RuleQA.Core.Rules;
using RuleQA.Core.Settings;

namespace RuleQA.Tests;

public sealed class RuleUpdateTests : IDisposable
{
    private const string Travel = "03_旅費規程.md";
    private const string Faq = "07_在宅勤務FAQ.md";
    private const string Car = "08_社用車管理規程.md";

    private readonly TestServices _services = new();

    public void Dispose() => _services.Dispose();

    private RuleImporter Importer => _services.Get<RuleImporter>();

    /// <summary>Imports a copy of the samples, then changes 第3条 of the travel rules, adds a regulation and deletes the FAQ.</summary>
    private async Task<string> ImportAndChangeAsync()
    {
        var folder = _services.CopySamples();
        await Importer.ImportFolderAsync(folder);
        var travel = Path.Combine(folder, Travel);
        await File.WriteAllTextAsync(travel, (await File.ReadAllTextAsync(travel)).Replace("出張の5営業日前まで", "出張の7営業日前まで"));
        await File.WriteAllTextAsync(Path.Combine(folder, Car), """
            # 社用車管理規程

            ### 第1条（目的）
            この規程は、社用車の利用について定める。

            ### 第2条（予約）
            社用車を利用するときは、利用日の2日前までに総務課に予約する。
            """);
        File.Delete(Path.Combine(folder, Faq));
        return folder;
    }

    [Fact]
    [Spec(SpecCategories.Import, "取り込む前に変更を確認する", "規則フォルダと取り込んだ内容を比べ、ファイルごとに追加・変更・削除・変更なしを返す。変更は条文の単位で示す（旅費規程は「変更 第3条」、足した規程は条文 2 件、消した FAQ はその条文）。確認だけでは DB を変えない。")]
    public async Task Checks_changes_by_article_without_importing()
    {
        var folder = await ImportAndChangeAsync();

        var check = await Importer.CheckAsync(folder);

        Assert.True(check.HasChanges);
        var travel = check.Files.Single(f => f.Path == Travel);
        Assert.Equal(FileChangeKind.Changed, travel.Kind);
        Assert.Equal(("変更 第3条", "旅費規程"), (travel.Articles.Describe(), travel.Title));
        var car = check.Files.Single(f => f.Path == Car);
        Assert.Equal((FileChangeKind.Added, "社用車管理規程"), (car.Kind, car.Title));
        Assert.Equal(["第1条", "第2条"], car.Articles.Added);
        var faq = check.Files.Single(f => f.Path == Faq);
        Assert.Equal(FileChangeKind.Removed, faq.Kind);
        Assert.NotEmpty(faq.Articles.Removed);
        Assert.Equal(6, check.Count(FileChangeKind.Unchanged));

        var articles = await _services.ArticlesAsync();
        Assert.Contains("5営業日前", articles.Single(a => a.Key == "03_旅費規程#第3条").Body);
        Assert.Contains(articles, a => a.Key.StartsWith("07_"));
        Assert.DoesNotContain(articles, a => a.Key.StartsWith("08_"));
        await using var db = _services.Db();
        Assert.Equal(1, await db.ImportLogs.CountAsync());
    }

    [Fact]
    [Spec(SpecCategories.Import, "選んだ規程だけを取り込む", "選んだファイルだけを取り込み、選ばなかった追加と削除は保留のまま残る（次の確認でも変更として出る）。消えたファイルは、選んだときだけ規程を消す。")]
    public async Task Imports_only_selected_files()
    {
        var folder = await ImportAndChangeAsync();

        var first = await Importer.ImportFolderAsync(folder, ImportMode.Selected, [Travel]);
        var pending = await Importer.CheckAsync(folder);
        var second = await Importer.ImportFolderAsync(folder, ImportMode.Selected, [Faq]);
        var rest = await Importer.CheckAsync(folder);

        Assert.Equal((0, 1, 0), (first.Added, first.Updated, first.Removed));
        Assert.Equal([(Car, FileChangeKind.Added), (Faq, FileChangeKind.Removed)], pending.Pending.Select(f => (f.Path, f.Kind)));
        Assert.Equal((0, 0, 1), (second.Added, second.Updated, second.Removed));
        Assert.Equal([Car], rest.Pending.Select(f => f.Path));
        var articles = await _services.ArticlesAsync();
        Assert.Contains("7営業日前", articles.Single(a => a.Key == "03_旅費規程#第3条").Body);
        Assert.DoesNotContain(articles, a => a.Key.StartsWith("07_") || a.Key.StartsWith("08_"));
    }

    [Fact]
    [Spec(SpecCategories.Import, "すべて取り込み直す", "変更のないファイルも含めて全ファイルを読み直す。条文が同じなら作成済みのベクトルはすべて残す。")]
    public async Task Reimports_all_files_and_keeps_vectors()
    {
        var folder = _services.CopySamples();
        await Importer.ImportFolderAsync(folder);
        await using (var db = _services.Db())
        {
            foreach (var a in await db.Articles.ToListAsync())
            {
                a.Embedding = [1, 2];
                a.EmbeddingModel = "m";
            }
            await db.SaveChangesAsync();
        }

        var result = await Importer.ImportFolderAsync(folder, ImportMode.All);

        var articles = await _services.ArticlesAsync();
        Assert.Equal((0, 8, 0, 0), (result.Added, result.Updated, result.Unchanged, result.Removed));
        Assert.Equal(articles.Count, result.KeptVectors);
        Assert.All(articles, a => Assert.NotNull(a.Embedding));
        Assert.All(result.Files, f => Assert.False(f.Articles.Any));
    }

    [Fact]
    [Spec(SpecCategories.Import, "一部の取り込みでも最新版を決め直す", "新しい版のファイルだけを選んで取り込むと、選ばなかった今の版が旧版になる。その新しい版のファイルを消して取り込むと、元の版が最新版に戻る。")]
    public async Task Recalculates_current_versions_after_partial_import()
    {
        var folder = _services.CopySamples();
        await Importer.ImportFolderAsync(folder);
        const string newer = "new/03_旅費規程_2027年版.md";
        Directory.CreateDirectory(Path.Combine(folder, "new"));
        await File.WriteAllTextAsync(Path.Combine(folder, newer), """
            # 旅費規程

            | 項目 | 内容 |
            |---|---|
            | 最終改定 | 2027年4月1日 |

            **第1条（目的）**
            この規程は、出張の旅費について定める。
            """);

        await Importer.ImportFolderAsync(folder, ImportMode.Selected, [newer]);
        var replaced = await DocumentAsync(Travel);
        File.Delete(Path.Combine(folder, newer));
        await Importer.ImportFolderAsync(folder, ImportMode.Selected, [newer]);
        var restored = await DocumentAsync(Travel);

        Assert.Equal((true, newer), (replaced.IsSuperseded, replaced.SupersededBy));
        Assert.Equal((false, null), (restored.IsSuperseded, restored.SupersededBy));
    }

    [Fact]
    [Spec(SpecCategories.Import, "取り込みの記録を残す", "取り込むたびに日時・種類・件数・ファイルごとの条文の変更・警告を記録し、新しい順に返す。初回の自動取り込みも記録する。変更がなかった通常の取り込みは記録しない。")]
    public async Task Records_import_log()
    {
        await Importer.ImportIfEmptyAsync(new RulesSettings());
        var folder = await ImportAndChangeAsync();
        await Importer.ImportFolderAsync(folder);
        await Importer.ImportFolderAsync(folder);

        var logs = await Importer.LogsAsync();

        Assert.Equal([ImportMode.Changes, ImportMode.Initial], logs.Select(l => l.Mode));
        Assert.Equal((8, 0, 0), (logs[^1].Added, logs[^1].Updated, logs[^1].Removed));
        Assert.Contains("04_経費精算規程.md: ", logs[^1].Warnings);
        var latest = logs[0];
        Assert.Equal((1, 1, 1, 6), (latest.Added, latest.Updated, latest.Removed, latest.Unchanged));
        var details = latest.Details.Split('\n');
        Assert.Contains("変更 03_旅費規程.md：変更 第3条", details);
        Assert.Contains("追加 08_社用車管理規程.md：条文 2 件", details);
        Assert.Contains(details, d => d.StartsWith("削除 07_在宅勤務FAQ.md：条文 "));
        Assert.Equal("変更を取り込み", latest.ModeLabel);
    }

    [Fact]
    [Spec(SpecCategories.Import, "前の版の DB に取り込みの記録の表を足す", "取り込みの記録の表がない DB（前の版で作った DB）を開くと、取り込んだ規則を残したまま表を作り、記録できるようになる。")]
    public async Task Adds_import_log_table_to_older_databases()
    {
        var folder = _services.CopySamples();
        await Importer.ImportFolderAsync(folder);
        await using (var db = _services.Db())
            await db.Database.ExecuteSqlRawAsync("DROP TABLE ImportLogs");

        await using (var db = _services.Db())
            Core.Data.SchemaUpgrader.EnsureSchema(db);
        await Importer.ImportFolderAsync(folder, ImportMode.All);

        Assert.Equal(8, (await Importer.StatsAsync(null)).Documents);
        Assert.Equal(ImportMode.All, Assert.Single(await Importer.LogsAsync()).Mode);
    }

    private async Task<RuleDocument> DocumentAsync(string path)
    {
        await using var db = _services.Db();
        return await db.Documents.AsNoTracking().SingleAsync(d => d.Path == path);
    }
}
