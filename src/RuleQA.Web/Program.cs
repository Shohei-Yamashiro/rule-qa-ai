using System.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.FluentUI.AspNetCore.Components;
using RuleQA.Core;
using RuleQA.Core.Data;
using RuleQA.Core.Rules;
using RuleQA.Core.Settings;
using RuleQA.Web.Components;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

var dataDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, builder.Configuration["DataDirectory"] ?? "data"));

var dataProtection = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));
if (OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddFluentUIComponents();
builder.Services.AddRuleQaCore(dataDirectory, AppContext.BaseDirectory);

var app = builder.Build();

using (var db = app.Services.GetRequiredService<IDbContextFactory<RuleQaDbContext>>().CreateDbContext())
{
    db.EnsureSchema();
}
await app.Services.GetRequiredService<RuleImporter>()
    .ImportIfEmptyAsync(app.Services.GetRequiredService<SettingsStore>().Current.Rules);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

if (app.Configuration.GetValue("OpenBrowser", false))
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var url = app.Urls.FirstOrDefault() ?? "http://localhost:5090";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    });
}

app.Logger.LogInformation("データフォルダ: {DataDirectory}", dataDirectory);
app.Logger.LogInformation("このウィンドウを閉じるとアプリが終了します。");

app.Run();
