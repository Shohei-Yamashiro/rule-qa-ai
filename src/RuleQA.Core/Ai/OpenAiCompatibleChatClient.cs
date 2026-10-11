using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuleQA.Core.Settings;

namespace RuleQA.Core.Ai;

public sealed class AiConnectionException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record ChatMessage(string Role, string Content)
{
    public static ChatMessage System(string content) => new("system", content);
    public static ChatMessage User(string content) => new("user", content);
    public static ChatMessage Assistant(string content) => new("assistant", content);
}

/// <param name="Name">Schema name sent with json_schema (letters, digits, "_" and "-").</param>
public sealed record JsonOutputFormat(string Name, JsonObject Schema);

public interface IChatClient
{
    string Name { get; }

    /// <summary>
    /// Asks for a JSON object and converts it with <paramref name="parse"/>.
    /// When <paramref name="parse"/> throws <see cref="FormatException"/>, the model is asked once to output the JSON again.
    /// </summary>
    Task<T> CompleteJsonAsync<T>(IReadOnlyList<ChatMessage> messages, JsonOutputFormat format, Func<string, T> parse, CancellationToken ct);
}

/// <summary>
/// Calls any server that implements the OpenAI chat completions API (Ollama, LM Studio, Gemini, OpenAI, Azure OpenAI, ...).
/// Structured output support differs by server, so the response format falls back step by step on HTTP 400.
/// Reasoning models that only accept the default temperature are retried without it.
/// </summary>
public sealed class OpenAiCompatibleChatClient(HttpClient http, AiSettings settings) : IChatClient
{
    private enum FormatMode
    {
        None,
        JsonObject,
        JsonSchema,
    }

    private FormatMode _mode = settings.UseJsonSchema ? FormatMode.JsonSchema : FormatMode.JsonObject;
    private bool _sendTemperature = true;

    public string Name => $"AI（{settings.Model}）";

    public async Task<T> CompleteJsonAsync<T>(IReadOnlyList<ChatMessage> messages, JsonOutputFormat format, Func<string, T> parse, CancellationToken ct)
    {
        var conversation = messages.ToList();
        var content = await CompleteAsync(conversation, format, ct);
        try
        {
            return parse(content);
        }
        catch (FormatException e)
        {
            conversation.Add(ChatMessage.Assistant(content));
            conversation.Add(ChatMessage.User($"出力を JSON として解釈できませんでした（{e.Message}）。指定のスキーマに従った JSON オブジェクトだけを出力してください。"));
            return parse(await CompleteAsync(conversation, format, ct));
        }
    }

    private async Task<string> CompleteAsync(List<ChatMessage> messages, JsonOutputFormat format, CancellationToken ct)
    {
        while (true)
        {
            var body = new Dictionary<string, object?>
            {
                ["model"] = settings.Model,
                ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }),
            };
            if (_sendTemperature) body["temperature"] = 0;
            if (_mode == FormatMode.JsonSchema)
                body["response_format"] = new { type = "json_schema", json_schema = new { name = format.Name, strict = true, schema = format.Schema } };
            else if (_mode == FormatMode.JsonObject)
                body["response_format"] = new { type = "json_object" };

            using var request = new HttpRequestMessage(HttpMethod.Post, AiEndpoint.Url(settings.Endpoint, "chat/completions")) { Content = JsonContent.Create(body) };
            AiEndpoint.Authorize(request, settings);

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (HttpRequestException e)
            {
                throw new AiConnectionException($"AI の接続先に接続できません（{settings.Endpoint}）: {e.Message}", e);
            }
            catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
            {
                throw new AiConnectionException($"AI の応答が {http.Timeout.TotalSeconds:0} 秒以内に返りませんでした。", e);
            }

            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
                {
                    if (_sendTemperature && text.Contains("temperature", StringComparison.OrdinalIgnoreCase))
                    {
                        _sendTemperature = false;
                        continue;
                    }
                    if (_mode != FormatMode.None)
                    {
                        _mode--;
                        continue;
                    }
                }
                if (!response.IsSuccessStatusCode)
                    throw new AiConnectionException($"AI の接続先がエラーを返しました（HTTP {(int)response.StatusCode}）: {Truncate(text, 300)}");

                try
                {
                    using var doc = JsonDocument.Parse(text);
                    return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
                }
                catch (Exception e) when (e is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
                {
                    throw new AiConnectionException($"AI の応答形式が想定と異なります: {Truncate(text, 300)}", e);
                }
            }
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
