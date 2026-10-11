using Microsoft.Extensions.DependencyInjection;
using RuleQA.Core.Ai;
using RuleQA.Core.Settings;

namespace RuleQA.Core;

public static class ServiceCollectionExtensions
{
    /// <param name="dataDirectory">Everything the app writes goes under this folder.</param>
    public static IServiceCollection AddRuleQaCore(this IServiceCollection services, string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new SettingsStore(Path.Combine(dataDirectory, "settings.json")));
        services.AddSingleton(new AiClientFactory());
        services.AddSingleton<AiConnectionTester>();
        return services;
    }
}
