using RuleQA.Core.Settings;

namespace RuleQA.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ruleqa-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    [Spec(SpecCategories.Settings, "初期設定は AI なし", "設定ファイルがないときは「AI を使わない」で、埋め込みモデルは空欄になる。")]
    public void Defaults_to_no_ai()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));

        Assert.Equal(AiProvider.None, store.Current.Ai.Provider);
        Assert.Equal("", store.Current.Ai.EmbeddingModel);
    }

    [Fact]
    [Spec(SpecCategories.Settings, "API キーを暗号化して保存する", "保存したファイルに API キーの平文は含まれず、読み込み直すと元の API キーと接続先に戻る。")]
    public void Saves_api_key_encrypted_and_loads_it_back()
    {
        var path = Path.Combine(_dir, "settings.json");
        var settings = new AppSettings();
        settings.Ai.Provider = AiProvider.OpenAiCompatible;
        settings.Ai.Endpoint = "https://api.example.com/v1";
        settings.Ai.ApiKey = "sk-secret-123";

        new SettingsStore(path).Save(settings);

        Assert.DoesNotContain("sk-secret-123", File.ReadAllText(path));
        var loaded = new SettingsStore(path).Current;
        Assert.Equal(AiProvider.OpenAiCompatible, loaded.Ai.Provider);
        Assert.Equal("https://api.example.com/v1", loaded.Ai.Endpoint);
        Assert.Equal("sk-secret-123", loaded.Ai.ApiKey);
    }

    [Fact]
    [Spec(SpecCategories.Settings, "読み出せない API キーは空欄にする", "別のユーザーや PC で暗号化された値・壊れた値は、例外にせず空欄として読み込む。")]
    public void Unreadable_secret_becomes_empty()
    {
        Assert.Equal("", SecretProtector.Unprotect("dpapi:not-base64!"));
        Assert.Equal("", SecretProtector.Unprotect("unknown"));
        Assert.Equal("abc", SecretProtector.Unprotect(SecretProtector.Protect("abc")));
    }
}
