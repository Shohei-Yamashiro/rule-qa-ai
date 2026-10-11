using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace RuleQA.Core.Settings;

public enum AiProvider
{
    /// <summary>No AI: questions are answered by showing the matching articles.</summary>
    None,
    OpenAiCompatible,
}

public sealed class AppSettings
{
    public AiSettings Ai { get; set; } = new();
    public RulesSettings Rules { get; set; } = new();

    public AppSettings Copy() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;
}

public sealed class AiSettings
{
    public AiProvider Provider { get; set; } = AiProvider.None;
    public string Endpoint { get; set; } = "http://localhost:11434/v1";
    public string Model { get; set; } = "qwen2.5:7b";
    public string ApiKey { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 120;
    public bool UseJsonSchema { get; set; } = true;

    /// <summary>Embedding model on the same endpoint for article search; empty uses keyword search only.</summary>
    public string EmbeddingModel { get; set; } = "";

    /// <summary>Edited instructions for answering; empty uses the built-in ones.</summary>
    public string Instructions { get; set; } = "";
}

public sealed class RulesSettings
{
    /// <summary>Folder of .md and .txt files; relative paths are resolved against the app folder.</summary>
    public string Folder { get; set; } = "samples/rules";

    /// <summary>How many articles a search returns (and are sent to the AI).</summary>
    public int CandidateCount { get; set; } = 6;

    /// <summary>Also search old versions and abolished regulations.</summary>
    public bool IncludeSuperseded { get; set; }
}

/// <summary>Persists settings as JSON; secrets are encrypted for the current Windows user.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Lock _lock = new();
    private AppSettings _current;

    public SettingsStore(string path)
    {
        _path = path;
        _current = Load(path);
    }

    public event Action? Changed;

    /// <summary>Returns a copy; edit it and pass it to <see cref="Save"/>.</summary>
    public AppSettings Current
    {
        get
        {
            lock (_lock) return Clone(_current);
        }
    }

    public void Save(AppSettings settings)
    {
        var stored = Clone(settings);
        stored.Ai.ApiKey = SecretProtector.Protect(stored.Ai.ApiKey);

        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(_path, JsonSerializer.Serialize(stored, JsonOptions));
            _current = Clone(settings);
        }
        Changed?.Invoke();
    }

    private static AppSettings Load(string path)
    {
        if (!File.Exists(path)) return new AppSettings();
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings();
        settings.Ai.ApiKey = SecretProtector.Unprotect(settings.Ai.ApiKey);
        return settings;
    }

    private static AppSettings Clone(AppSettings s) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, JsonOptions), JsonOptions)!;
}

public static class SecretProtector
{
    private const string DpapiPrefix = "dpapi:";
    private const string PlainPrefix = "plain:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RuleQA");

    public static string Protect(string secret)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        if (!OperatingSystem.IsWindows()) return PlainPrefix + secret;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
        return DpapiPrefix + Convert.ToBase64String(bytes);
    }

    /// <summary>Returns "" when the value was encrypted by another user or machine.</summary>
    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (stored.StartsWith(PlainPrefix, StringComparison.Ordinal)) return stored[PlainPrefix.Length..];
        if (!stored.StartsWith(DpapiPrefix, StringComparison.Ordinal) || !OperatingSystem.IsWindows()) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored[DpapiPrefix.Length..]), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return "";
        }
    }
}
