using System.Net.Http.Json;
using System.Text.Json;
using RuleQA.Core.Settings;

namespace RuleQA.Core.Ai;

public interface IEmbedder
{
    string Model { get; }

    /// <summary>Returns one L2-normalized vector per input, in the same order.</summary>
    Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct);
}

/// <summary>Calls the OpenAI-compatible /embeddings endpoint on the same server and key as the chat model.</summary>
public sealed class OpenAiCompatibleEmbedder(HttpClient http, AiSettings settings) : IEmbedder
{
    private const int BatchSize = 64;

    public string Model => settings.EmbeddingModel.Trim();

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        var result = new List<float[]>(inputs.Count);
        foreach (var batch in inputs.Chunk(BatchSize))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, AiEndpoint.Url(settings.Endpoint, "embeddings"))
            {
                Content = JsonContent.Create(new { model = Model, input = batch }),
            };
            AiEndpoint.Authorize(request, settings);

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
            {
                throw new AiConnectionException($"埋め込みモデルに接続できません（{settings.Endpoint}）: {e.Message}", e);
            }
            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new AiConnectionException($"埋め込みモデルがエラーを返しました（HTTP {(int)response.StatusCode}）: {(text.Length > 300 ? text[..300] + "…" : text)}");
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    var data = doc.RootElement.GetProperty("data").EnumerateArray()
                        .OrderBy(d => d.TryGetProperty("index", out var i) ? i.GetInt32() : 0)
                        .Select(d => Normalize(d.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray()))
                        .ToList();
                    if (data.Count != batch.Length) throw new InvalidOperationException($"{batch.Length} 件に対して {data.Count} 件の応答");
                    result.AddRange(data);
                }
                catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
                {
                    throw new AiConnectionException($"埋め込みの応答形式が想定と異なります: {e.Message}", e);
                }
            }
        }
        return result.ToArray();
    }

    public static float[] Normalize(float[] v)
    {
        var norm = Math.Sqrt(v.Sum(x => (double)x * x));
        if (norm == 0) return v;
        return v.Select(x => (float)(x / norm)).ToArray();
    }
}
