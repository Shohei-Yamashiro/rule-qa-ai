using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RuleQA.Core;
using RuleQA.Core.Ai;
using RuleQA.Core.Search;
using RuleQA.Core.Data;
using RuleQA.Core.Domain;
using RuleQA.Core.Evaluation;
using RuleQA.Core.Rules;

namespace RuleQA.Tests;

public static class Samples
{
    public static string RulesFolder => Path.Combine(AppContext.BaseDirectory, "samples", "rules");
    public static string QuestionsPath => Path.Combine(AppContext.BaseDirectory, "samples", "questions.json");

    public static IReadOnlyList<EvaluationQuestion> Questions => EvaluationQuestions.Load(QuestionsPath);

    /// <summary>Parses one sample file by the start of its name, for example "03_" or "old/03_".</summary>
    public static ParsedDocument Parse(string prefix)
    {
        var file = Directory.EnumerateFiles(RulesFolder, "*", SearchOption.AllDirectories)
            .Single(f => Path.GetRelativePath(RulesFolder, f).Replace('\\', '/').StartsWith(prefix, StringComparison.Ordinal));
        return RuleDocumentParser.Parse(Path.GetFileName(file), TextDecoder.Decode(File.ReadAllBytes(file)).Text);
    }
}

/// <summary>The app's services on a database in a temporary folder.</summary>
public sealed class TestServices : IDisposable
{
    public TestServices()
    {
        Directory.CreateDirectory(DataDirectory);
        var services = new ServiceCollection().AddLogging();
        services.AddRuleQaCore(DataDirectory, AppContext.BaseDirectory);
        Provider = services.BuildServiceProvider();
        using var db = Db();
        db.EnsureSchema();
    }

    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "ruleqa-tests-" + Guid.NewGuid().ToString("N"));
    public ServiceProvider Provider { get; }

    public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();

    public RuleQaDbContext Db() => Get<IDbContextFactory<RuleQaDbContext>>().CreateDbContext();

    /// <summary>A search service whose AI connection goes to <paramref name="handler"/>.</summary>
    public RuleSearchService SearchWith(HttpMessageHandler handler) =>
        new(Get<IDbContextFactory<RuleQaDbContext>>(), new AiClientFactory(handler), NullLogger<RuleSearchService>.Instance);

    public async Task<List<Article>> ArticlesAsync()
    {
        await using var db = Db();
        return await db.Articles.AsNoTracking().Include(a => a.Document).OrderBy(a => a.Document.Path).ThenBy(a => a.Order).ToListAsync();
    }

    /// <summary>A copy of the sample rules that a test may change.</summary>
    public string CopySamples()
    {
        var target = Path.Combine(DataDirectory, "rules");
        foreach (var file in Directory.EnumerateFiles(Samples.RulesFolder, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(Samples.RulesFolder, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
        return target;
    }

    public void Dispose()
    {
        Provider.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(DataDirectory)) Directory.Delete(DataDirectory, recursive: true);
    }
}
