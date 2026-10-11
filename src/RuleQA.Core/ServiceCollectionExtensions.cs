using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RuleQA.Core.Ai;
using RuleQA.Core.Answering;
using RuleQA.Core.Data;
using RuleQA.Core.Evaluation;
using RuleQA.Core.Rules;
using RuleQA.Core.Search;
using RuleQA.Core.Settings;

namespace RuleQA.Core;

public static class ServiceCollectionExtensions
{
    /// <param name="dataDirectory">Everything the app writes goes under this folder.</param>
    /// <param name="baseDirectory">Relative rules folder paths are resolved against this.</param>
    public static IServiceCollection AddRuleQaCore(this IServiceCollection services, string dataDirectory, string baseDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        services.AddDbContextFactory<RuleQaDbContext>(o =>
            o.UseSqlite($"Data Source={Path.Combine(dataDirectory, "ruleqa.db")}"));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new SettingsStore(Path.Combine(dataDirectory, "settings.json")));
        services.AddSingleton(new AiClientFactory());
        services.AddSingleton<AiConnectionTester>();
        services.AddSingleton(sp => new RuleImporter(
            sp.GetRequiredService<IDbContextFactory<RuleQaDbContext>>(),
            sp.GetRequiredService<TimeProvider>(),
            baseDirectory,
            sp.GetRequiredService<ILogger<RuleImporter>>()));
        services.AddSingleton<RuleSearchService>();
        services.AddSingleton<RuleAnswerService>();
        services.AddSingleton<SearchEvaluator>();
        return services;
    }
}
