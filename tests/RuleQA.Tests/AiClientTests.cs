using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuleQA.Core.Ai;
using RuleQA.Core.Settings;

namespace RuleQA.Tests;

public class AiClientTests
{
    private static readonly JsonOutputFormat Format = new("answer", new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } },
        ["required"] = new JsonArray("text"),
        ["additionalProperties"] = false,
    });

    private static readonly ChatMessage[] Messages = [ChatMessage.System("指示"), ChatMessage.User("有給休暇は何日？")];

    private static string ParseText(string content)
    {
        try
        {
            return JsonNode.Parse(content)?["text"]?.GetValue<string>() ?? throw new FormatException("text がありません");
        }
        catch (JsonException e)
        {
            throw new FormatException(e.Message, e);
        }
    }

    [Fact]
    [Spec(SpecCategories.Ai, "接続先・モデル・API キー・JSON スキーマを送る", "エンドポイント末尾の「/」を補って /chat/completions に送り、モデル名・メッセージ・Bearer 認証・json_schema 指定（名前とスキーマ）・temperature 0 が含まれる。応答の JSON を読み取れる。")]
    public async Task Sends_schema_and_api_key_to_chat_completions()
    {
        var handler = new FakeHandler(_ => FakeHandler.Completion("""{"text": "20日です"}"""));
        var chat = Create(handler, new AiSettings { Endpoint = "https://api.example.com/v1/", Model = "m1", ApiKey = "secret" });

        var text = await chat.CompleteJsonAsync(Messages, Format, ParseText, CancellationToken.None);

        Assert.Equal("20日です", text);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.example.com/v1/chat/completions", request.Url);
        Assert.Equal("Bearer secret", request.Authorization);
        Assert.Equal("m1", request.Body["model"]!.GetValue<string>());
        Assert.Equal(0, request.Body["temperature"]!.GetValue<int>());
        Assert.Equal(["system", "user"], request.Body["messages"]!.AsArray().Select(m => m!["role"]!.GetValue<string>()));
        Assert.Equal("有給休暇は何日？", request.Body["messages"]![1]!["content"]!.GetValue<string>());
        var format = request.Body["response_format"]!;
        Assert.Equal("json_schema", format["type"]!.GetValue<string>());
        Assert.Equal("answer", format["json_schema"]!["name"]!.GetValue<string>());
        Assert.Equal("text", format["json_schema"]!["schema"]!["required"]![0]!.GetValue<string>());
    }

    [Fact]
    [Spec(SpecCategories.Ai, "JSON スキーマ非対応の接続先では自動で切り替える", "json_schema が HTTP 400 で拒否されたら json_object、それも拒否されたら形式の指定なしで送り直して成功する。API キーが空なら認証ヘッダーを付けない。")]
    public async Task Falls_back_step_by_step_when_response_format_is_rejected()
    {
        var handler = new FakeHandler(r => r.Body["response_format"] is not null
            ? FakeHandler.Reply(HttpStatusCode.BadRequest, """{"error": "response_format not supported"}""")
            : FakeHandler.Completion("""{"text": "ok"}"""));
        var chat = Create(handler, new AiSettings { Endpoint = "http://localhost:11434/v1", Model = "m" });

        var text = await chat.CompleteJsonAsync(Messages, Format, ParseText, CancellationToken.None);

        Assert.Equal("ok", text);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("json_object", handler.Requests[1].Body["response_format"]!["type"]!.GetValue<string>());
        Assert.Null(handler.Requests[2].Body["response_format"]);
        Assert.All(handler.Requests, r => Assert.Null(r.Authorization));
    }

    [Fact]
    [Spec(SpecCategories.Ai, "Azure OpenAI には api-key ヘッダーで送る", "接続先が Azure OpenAI（*.openai.azure.com）なら、API キーを api-key ヘッダーで送り、Bearer 認証は付けない。モデル名（デプロイ名）はそのまま送る。")]
    public async Task Sends_api_key_header_to_azure_openai()
    {
        var handler = new FakeHandler(_ => FakeHandler.Completion("""{"text": "ok"}"""));
        var chat = Create(handler, new AiSettings { Endpoint = "https://contoso.openai.azure.com/openai/v1", Model = "my-deployment", ApiKey = "azure-key" });

        await chat.CompleteJsonAsync(Messages, Format, ParseText, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://contoso.openai.azure.com/openai/v1/chat/completions", request.Url);
        Assert.Equal("azure-key", request.ApiKey);
        Assert.Null(request.Authorization);
        Assert.Equal("my-deployment", request.Body["model"]!.GetValue<string>());
    }

    [Fact]
    [Spec(SpecCategories.Ai, "temperature を受け付けないモデルでは指定を外す", "temperature の指定が HTTP 400 で拒否されたら、temperature だけを外して送り直す（JSON スキーマ指定は残す）。")]
    public async Task Retries_without_temperature_for_models_that_reject_it()
    {
        var handler = new FakeHandler(r => r.Body["temperature"] is not null
            ? FakeHandler.Reply(HttpStatusCode.BadRequest, """{"error": {"message": "Unsupported value: 'temperature' does not support 0 with this model."}}""")
            : FakeHandler.Completion("""{"text": "ok"}"""));
        var chat = Create(handler, new AiSettings { Model = "reasoning-model" });

        await chat.CompleteJsonAsync(Messages, Format, ParseText, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Null(handler.Requests[1].Body["temperature"]);
        Assert.Equal("json_schema", handler.Requests[1].Body["response_format"]!["type"]!.GetValue<string>());
    }

    [Fact]
    [Spec(SpecCategories.Ai, "JSON でない応答には 1 回だけ出し直しを頼む", "1 回目の応答が読み取れないとき、1 回目の応答と読めなかった理由を会話に足して出し直させ、2 回目の応答を使う。")]
    public async Task Asks_once_more_when_output_cannot_be_parsed()
    {
        var replies = new Queue<string>(["20日です。", """{"text": "20日です"}"""]);
        var handler = new FakeHandler(_ => FakeHandler.Completion(replies.Dequeue()));
        var chat = Create(handler, new AiSettings { Model = "m" });

        var text = await chat.CompleteJsonAsync(Messages, Format, ParseText, CancellationToken.None);

        Assert.Equal("20日です", text);
        var messages = handler.Requests[1].Body["messages"]!.AsArray();
        Assert.Equal(["system", "user", "assistant", "user"], messages.Select(m => m!["role"]!.GetValue<string>()));
        Assert.Equal("20日です。", messages[2]!["content"]!.GetValue<string>());
        Assert.Contains("JSON", messages[3]!["content"]!.GetValue<string>());
    }

    [Fact]
    [Spec(SpecCategories.Ai, "出し直しても読めなければ諦める", "2 回目の応答も読み取れなければ、それ以上は送らずに読み取りの失敗（FormatException）を返す。")]
    public async Task Gives_up_after_one_retry()
    {
        var handler = new FakeHandler(_ => FakeHandler.Completion("JSON ではありません"));
        var chat = Create(handler, new AiSettings { Model = "m" });

        await Assert.ThrowsAsync<FormatException>(() => chat.CompleteJsonAsync(Messages, Format, ParseText, CancellationToken.None));

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    [Spec(SpecCategories.Ai, "接続先のエラーを分かる形で伝える", "HTTP 401 などのエラーでは、ステータスコードと接続先のメッセージを含む例外にする。")]
    public async Task Reports_server_errors_clearly()
    {
        var handler = new FakeHandler(_ => FakeHandler.Reply(HttpStatusCode.Unauthorized, """{"error": "invalid api key"}"""));
        var chat = Create(handler, new AiSettings { Model = "m" });

        var e = await Assert.ThrowsAsync<AiConnectionException>(() => chat.CompleteJsonAsync(Messages, Format, ParseText, CancellationToken.None));

        Assert.Contains("401", e.Message);
        Assert.Contains("invalid api key", e.Message);
    }

    [Fact]
    [Spec(SpecCategories.Ai, "埋め込みを入力の順に正規化して返す", "/embeddings にモデル名と入力を送り、応答の index の順に並べ直して、長さ 1 に正規化したベクトルを返す。")]
    public async Task Embedder_returns_normalized_vectors_in_input_order()
    {
        var handler = new FakeHandler(_ => FakeHandler.Reply(HttpStatusCode.OK,
            """{"data": [{"index": 1, "embedding": [0, 2]}, {"index": 0, "embedding": [3, 4]}]}"""));
        var embedder = new OpenAiCompatibleEmbedder(new HttpClient(handler), new AiSettings { Endpoint = "http://localhost:11434/v1", EmbeddingModel = " bge-m3 " });

        var vectors = await embedder.EmbedAsync(["年次有給休暇", "出張旅費"], CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://localhost:11434/v1/embeddings", request.Url);
        Assert.Equal("bge-m3", request.Body["model"]!.GetValue<string>());
        Assert.Equal(["年次有給休暇", "出張旅費"], request.Body["input"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal([0.6f, 0.8f], vectors[0]);
        Assert.Equal([0f, 1f], vectors[1]);
    }

    [Fact]
    [Spec(SpecCategories.Ai, "AI と埋め込みモデルは設定したときだけ使う", "「使わない」ならチャットも埋め込みも作らない。OpenAI 互換でも埋め込みモデルが空欄なら埋め込みは作らない（キーワード検索のみ）。")]
    public void Factory_creates_clients_only_when_configured()
    {
        var factory = new AiClientFactory();

        Assert.Null(factory.CreateChat(new AiSettings { Provider = AiProvider.None, EmbeddingModel = "bge-m3" }));
        Assert.Null(factory.CreateEmbedder(new AiSettings { Provider = AiProvider.None, EmbeddingModel = "bge-m3" }));
        Assert.NotNull(factory.CreateChat(new AiSettings { Provider = AiProvider.OpenAiCompatible }));
        Assert.Null(factory.CreateEmbedder(new AiSettings { Provider = AiProvider.OpenAiCompatible, EmbeddingModel = " " }));
        Assert.NotNull(factory.CreateEmbedder(new AiSettings { Provider = AiProvider.OpenAiCompatible, EmbeddingModel = "bge-m3" }));
    }

    [Fact]
    [Spec(SpecCategories.Ai, "接続テストでチャットと埋め込みを確かめる", "チャットの応答（JSON の message）と埋め込みの次元数を「成功」として示す。接続先がエラーなら例外にせず「失敗」と理由を示す。")]
    public async Task Connection_test_reports_chat_and_embedding_results()
    {
        var handler = new FakeHandler(r => r.Url.EndsWith("/embeddings")
            ? FakeHandler.Embeddings([1f, 0f, 0f])
            : FakeHandler.Completion("""{"ok": true, "message": "こんにちは"}"""));
        var tester = new AiConnectionTester(new AiClientFactory(handler));
        var settings = new AiSettings { Provider = AiProvider.OpenAiCompatible, Model = "m", EmbeddingModel = "e" };

        var ok = await tester.TestAsync(settings, CancellationToken.None);

        Assert.Contains("成功：AI（m）", ok);
        Assert.Contains("「こんにちは」", ok);
        Assert.Contains("成功：埋め込みモデル（e）3 次元", ok);

        var failing = new AiConnectionTester(new AiClientFactory(new FakeHandler(_ => FakeHandler.Reply(HttpStatusCode.NotFound, """{"error": "model not found"}"""))));
        var ng = await failing.TestAsync(settings, CancellationToken.None);

        Assert.Contains("失敗：AI（m）", ng);
        Assert.Contains("404", ng);
        Assert.Contains("失敗：埋め込みモデル（e）", ng);
    }

    private static OpenAiCompatibleChatClient Create(FakeHandler handler, AiSettings settings) =>
        new(new HttpClient(handler), settings);
}
