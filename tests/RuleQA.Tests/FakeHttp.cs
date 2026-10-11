using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuleQA.Tests;

/// <summary>Stands in for an OpenAI-compatible server: records each request and answers with <c>respond</c>.</summary>
public sealed class FakeHandler(Func<CapturedRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<CapturedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
        var apiKey = request.Headers.TryGetValues("api-key", out var values) ? values.Single() : null;
        var captured = new CapturedRequest(request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), apiKey, body);
        Requests.Add(captured);
        return respond(captured);
    }

    public static HttpResponseMessage Reply(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Completion(string content) =>
        Reply(HttpStatusCode.OK, JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } }));

    public static HttpResponseMessage Embeddings(params float[][] vectors) =>
        Reply(HttpStatusCode.OK, JsonSerializer.Serialize(new { data = vectors.Select((v, i) => new { index = i, embedding = v }) }));
}

public sealed record CapturedRequest(string Url, string? Authorization, string? ApiKey, JsonNode Body);
