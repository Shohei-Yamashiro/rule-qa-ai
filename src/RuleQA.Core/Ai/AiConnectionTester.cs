using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuleQA.Core.Settings;

namespace RuleQA.Core.Ai;

/// <summary>Checks the settings screen's connection before saving: one short JSON answer and, if set, one embedding.</summary>
public sealed class AiConnectionTester(AiClientFactory factory)
{
    public static readonly JsonOutputFormat Format = new("connection_test", new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["ok"] = new JsonObject { ["type"] = "boolean" },
            ["message"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray("ok", "message"),
        ["additionalProperties"] = false,
    });

    private static readonly ChatMessage[] Messages =
    [
        ChatMessage.System("あなたは接続テストに答えるアシスタントです。JSON オブジェクト {\"ok\": true, \"message\": \"<一言のあいさつ>\"} だけを出力してください。"),
        ChatMessage.User("接続テストです。"),
    ];

    /// <summary>Returns a message for the user; failures are reported in the message instead of thrown.</summary>
    public async Task<string> TestAsync(AiSettings settings, CancellationToken ct)
    {
        var chat = factory.CreateChat(settings);
        if (chat is null) return "AI を使わない設定です。質問には、検索で見つかった条文をそのまま表示します。";

        var lines = new List<string>();
        var watch = Stopwatch.StartNew();
        try
        {
            var message = await chat.CompleteJsonAsync(Messages, Format, Parse, ct);
            lines.Add($"成功：{chat.Name}（{watch.ElapsedMilliseconds} ms）「{message}」");
        }
        catch (Exception e) when (e is AiConnectionException or FormatException)
        {
            lines.Add($"失敗：{chat.Name}：{e.Message}");
        }

        var embedder = factory.CreateEmbedder(settings);
        if (embedder is null)
        {
            lines.Add("埋め込みモデル：未設定（キーワード検索だけを使います）");
            return string.Join("\n", lines);
        }
        watch.Restart();
        try
        {
            var vectors = await embedder.EmbedAsync(["就業時間は何時から何時までですか"], ct);
            lines.Add($"成功：埋め込みモデル（{embedder.Model}）{vectors[0].Length} 次元（{watch.ElapsedMilliseconds} ms）");
        }
        catch (AiConnectionException e)
        {
            lines.Add($"失敗：埋め込みモデル（{embedder.Model}）：{e.Message}");
        }
        return string.Join("\n", lines);
    }

    /// <summary>Returns the "message" of {"ok": true, "message": "..."}; throws <see cref="FormatException"/> otherwise.</summary>
    public static string Parse(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                throw new FormatException("\"ok\": true がありません");
            return root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "";
        }
        catch (JsonException e)
        {
            throw new FormatException(e.Message, e);
        }
    }
}
