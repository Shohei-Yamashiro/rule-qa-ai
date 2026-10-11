using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RuleQA.Core.Ai;
using RuleQA.Core.Domain;
using RuleQA.Core.Search;
using RuleQA.Core.Settings;

namespace RuleQA.Core.Answering;

/// <summary>An earlier question and the answer text shown for it.</summary>
public sealed record ConversationTurn(string Question, string Answer);

/// <param name="Citations">Articles the answer is based on; always among <paramref name="Candidates"/>.</param>
/// <param name="Candidates">Articles found by search (and sent to the AI).</param>
/// <param name="DroppedCitations">IDs the AI returned that were not candidates.</param>
/// <param name="Error">Why the AI could not be used, when it failed.</param>
public sealed record RuleAnswer(
    string Question,
    AnswerStatus Status,
    string Text,
    IReadOnlyList<Article> Citations,
    IReadOnlyList<ArticleHit> Candidates,
    IReadOnlyList<string> DroppedCitations,
    string? Error,
    string? AiName);

/// <summary>
/// Answers a question from the rules (the generation part of RAG): searches for candidate articles, asks the AI to answer
/// only from them with the IDs it relied on, and keeps only citations that were really candidates.
/// </summary>
public sealed partial class RuleAnswerService(RuleSearchService search, AiClientFactory ai, ILogger<RuleAnswerService> logger)
{
    /// <summary>How many earlier question-and-answer pairs are sent with a question.</summary>
    public const int HistoryTurns = 3;

    public const string NotInRulesText = "規則には記載がありません。";
    public const string SearchOnlyText = "AI を使わない設定のため、質問に近い条文を表示します。";
    public const string NoCandidatesText = "質問に近い条文は見つかりませんでした。";

    private const int RrfK = 60;

    /// <summary>The instructions an administrator can edit in the settings (<see cref="AiSettings.Instructions"/>).</summary>
    public const string DefaultInstructions = """
        あなたは社内規則についての質問に答えるアシスタントです。次のルールを守って、日本語で答えてください。
        - 「候補の条文」に書かれていることだけを根拠に答える。候補にないことは、一般論や推測で補わない。
        - off_topic は、質問が会社や仕事とまったく関係がない（天気、ニュース、一般的な知識、雑談、飲食店など）ときだけ true にする。「取り込んだ規程」の名前に関係する質問や、給与・賞与・退職金・福利厚生・社内行事・人事・勤怠・設備など、会社や仕事についての質問は、候補に答えがなくても off_topic を false にする（そのときは answered を false にする）。
        - 規程どうしで内容が食い違うときは、両方の条文を citations に入れ、食い違っていることを回答に書く。どちらを優先するかの定めが候補の条文にあれば、それに従って結論を書く。
        - 〔参考資料〕の文書より規程を優先する。参考資料と規程が食い違うときは規程に従い、参考資料だけが根拠のときはそのことを書く。
        - 〔旧版〕の条文は、今の取り扱いの根拠にしない。過去の取り扱いを聞かれたときだけ使う。
        - 会話の続きの質問は、これまでの会話を踏まえて何を聞いているかを判断する。
        """;

    /// <summary>Always appended to the instructions, because the answer is read and its citations checked by these rules.</summary>
    public const string FixedInstructions = """
        - 根拠にした条文の ID を citations に入れる。ID は条文の前の [ ] の中の「A1」「A2」などで、規程名や条番号ではない。回答文には ID ではなく規程名と条番号を書く。
        - 候補の条文に答えが書かれていなければ、answered を false、citations を空にする。
        出力は次の JSON オブジェクトだけにする。
        {"answer": "回答文", "citations": ["A1"], "answered": true, "off_topic": false}
        """;

    public static bool IsDefaultInstructions(string? instructions) =>
        string.IsNullOrWhiteSpace(instructions) || instructions.ReplaceLineEndings("\n").Trim() == DefaultInstructions.ReplaceLineEndings("\n").Trim();

    /// <param name="instructions">Edited instructions; empty uses <see cref="DefaultInstructions"/>.</param>
    public static string SystemPrompt(string? instructions) =>
        (IsDefaultInstructions(instructions) ? DefaultInstructions : instructions!).Trim() + "\n" + FixedInstructions;

    /// <summary>The reply to an off-topic question, naming the imported regulations so it fits any company's rules.</summary>
    public static string OffTopicText(IReadOnlyList<string> titles)
    {
        var shown = titles.Take(6).ToList();
        var list = shown.Count == 0 ? "" : $"（{string.Join("・", shown)}{(titles.Count > shown.Count ? $" など {titles.Count} 件" : "")}）";
        return $"社内規則についての質問ではないようです。取り込んだ規則{list}に関することを質問してください。";
    }

    public static readonly JsonOutputFormat Format = new("rule_answer", new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["answer"] = new JsonObject { ["type"] = "string" },
            ["citations"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["answered"] = new JsonObject { ["type"] = "boolean" },
            ["off_topic"] = new JsonObject { ["type"] = "boolean" },
        },
        ["required"] = new JsonArray("answer", "citations", "answered", "off_topic"),
        ["additionalProperties"] = false,
    });

    public async Task<RuleAnswer> AnswerAsync(string question, IReadOnlyList<ConversationTurn> history, AppSettings settings, CancellationToken ct = default)
    {
        var rules = await search.OpenAsync(settings, ct);
        return await AnswerAsync(rules, ai.CreateChat(settings.Ai), question, history, settings.Ai.Instructions, ct);
    }

    /// <param name="instructions">Edited instructions; empty uses <see cref="DefaultInstructions"/>.</param>
    public async Task<RuleAnswer> AnswerAsync(RuleSearch rules, IChatClient? chat, string question, IReadOnlyList<ConversationTurn> history, string? instructions = null, CancellationToken ct = default)
    {
        var recent = history.TakeLast(HistoryTurns).ToList();
        var candidates = await FindCandidatesAsync(rules, question, recent, ct);

        if (chat is null)
            return new RuleAnswer(question, AnswerStatus.SearchOnly, candidates.Count == 0 ? NoCandidatesText : SearchOnlyText, [], candidates, [], null, null);
        if (candidates.Count == 0)
            return new RuleAnswer(question, AnswerStatus.NotInRules, NotInRulesText, [], candidates, [], null, chat.Name);

        AiAnswer reply;
        try
        {
            reply = await chat.CompleteJsonAsync(BuildMessages(question, recent, candidates, rules.Index.Titles, instructions), Format, Parse, ct);
        }
        catch (Exception e) when (e is AiConnectionException or FormatException)
        {
            logger.LogWarning(e, "Answer generation failed; showing the search results");
            var error = e is FormatException ? $"AI の出力を読み取れませんでした（{e.Message}）。" : e.Message;
            return new RuleAnswer(question, AnswerStatus.SearchOnly, "AI で回答を作れなかったため、質問に近い条文を表示します。", [], candidates, [], error, chat.Name);
        }

        var citations = new List<Article>();
        var dropped = new List<string>();
        foreach (var id in reply.Citations)
        {
            var article = ArticleOf(id, candidates);
            if (article is null) dropped.Add(id);
            else if (!citations.Contains(article)) citations.Add(article);
        }
        if (dropped.Count > 0) logger.LogInformation("Dropped citations that were not candidates: {Ids}", string.Join(", ", dropped));

        if (reply.OffTopic)
            return new RuleAnswer(question, AnswerStatus.OffTopic, OffTopicText(rules.Index.Titles), [], candidates, [], null, chat.Name);
        if (!reply.Answered || citations.Count == 0 || string.IsNullOrWhiteSpace(reply.Answer))
            return new RuleAnswer(question, AnswerStatus.NotInRules, NotInRulesText, [], candidates, dropped, null, chat.Name);
        return new RuleAnswer(question, AnswerStatus.Answered, ReplaceIds(reply.Answer.Trim(), candidates), citations, candidates, dropped, null, chat.Name);
    }

    /// <summary>
    /// Searches with the question alone and, in a conversation, with the previous question added (for follow-ups such as
    /// "それは試用期間中も同じ？"), then merges the two rankings by reciprocal rank fusion.
    /// </summary>
    public static async Task<IReadOnlyList<ArticleHit>> FindCandidatesAsync(RuleSearch rules, string question, IReadOnlyList<ConversationTurn> history, CancellationToken ct = default)
    {
        var current = await rules.SearchAsync(question, ct);
        if (history.Count == 0) return current;
        var withPrevious = await rules.SearchAsync($"{history[^1].Question} {question}", ct);

        var fused = new Dictionary<string, (ArticleHit Hit, double Score)>();
        foreach (var hits in new[] { current, withPrevious })
            for (var i = 0; i < hits.Count; i++)
            {
                var key = hits[i].Article.Key;
                var (hit, score) = fused.GetValueOrDefault(key, (hits[i], 0));
                fused[key] = (hit, score + 1.0 / (RrfK + i + 1));
            }
        return fused.Values.OrderByDescending(f => f.Score).Take(rules.CandidateCount).Select(f => f.Hit with { Score = f.Score }).ToList();
    }

    /// <param name="titles">The current regulations, so the AI knows the scope of the rules when judging off-topic questions.</param>
    public static List<ChatMessage> BuildMessages(string question, IReadOnlyList<ConversationTurn> history, IReadOnlyList<ArticleHit> candidates, IReadOnlyList<string> titles, string? instructions = null)
    {
        var sb = new StringBuilder();
        if (titles.Count > 0)
        {
            sb.AppendLine("## 取り込んだ規程");
            sb.AppendLine(string.Join("、", titles));
            sb.AppendLine();
        }
        sb.AppendLine("## 候補の条文");
        for (var i = 0; i < candidates.Count; i++)
        {
            var article = candidates[i].Article;
            sb.AppendLine();
            sb.AppendLine($"[{IdOf(i)}] {article.DisplayName}{Tags(article.Document)}");
            sb.AppendLine(article.Body.Trim());
        }
        if (history.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## これまでの会話");
            foreach (var turn in history)
            {
                sb.AppendLine($"質問：{turn.Question}");
                sb.AppendLine($"回答：{turn.Answer}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("## 質問");
        sb.Append(question.Trim());
        return [ChatMessage.System(SystemPrompt(instructions)), ChatMessage.User(sb.ToString())];
    }

    private static string IdOf(int index) => $"A{index + 1}";

    private static string Tags(RuleDocument document)
    {
        var tags = new List<string>();
        if (document.Kind == DocumentKind.Reference) tags.Add("参考資料");
        if (document.IsSuperseded) tags.Add("旧版");
        if (document.EffectiveDate is { } date) tags.Add($"{date:yyyy年M月d日}施行・改定");
        return tags.Count == 0 ? "" : $"〔{string.Join("・", tags)}〕";
    }

    /// <summary>
    /// Finds the candidate an ID refers to: "A1" (also "[A1]" or "A1: 旅費規程 第5条"), or the regulation name and article
    /// number of a candidate ("旅費規程 第5条（国内出張の日当）"), which some models return instead of the ID.
    /// </summary>
    private static Article? ArticleOf(string id, IReadOnlyList<ArticleHit> candidates)
    {
        var text = id.Normalize(NormalizationForm.FormKC).Trim();
        var match = IdPattern().Match(text);
        if (match.Success)
            return int.TryParse(match.Groups[1].Value, out var n) && n >= 1 && n <= candidates.Count ? candidates[n - 1].Article : null;

        var compact = Compact(text);
        if (compact.Length == 0) return null;
        var articles = candidates.Select(h => h.Article).ToList();
        var byName = articles.Where(a => a.Key == text || Compact(a.Document.Title + a.Label) == compact || Compact(a.Document.BaseTitle + a.Label) == compact).ToList();
        if (byName.Count == 0) byName = articles.Where(a => Compact(a.Label) == compact).ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    /// <summary>Without spaces, brackets and parenthesized headings, so "旅費規程 第5条（日当）" equals "旅費規程第5条".</summary>
    private static string Compact(string text) => CompactPattern().Replace(text.Normalize(NormalizationForm.FormKC), "");

    /// <summary>Writes "（旅費規程 第5条）" where the answer text still contains an ID such as "[A1]".</summary>
    private static string ReplaceIds(string text, IReadOnlyList<ArticleHit> candidates) =>
        BracketedIdPattern().Replace(text, m =>
        {
            var article = ArticleOf(m.Groups[1].Value, candidates);
            return article is null ? m.Value : $"（{article.Document.Title} {article.Label}）";
        });

    private sealed record AiAnswer(string Answer, IReadOnlyList<string> Citations, bool Answered, bool OffTopic);

    private static AiAnswer Parse(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end < start) throw new FormatException("JSON オブジェクトがありません");
        try
        {
            var node = JsonNode.Parse(content[start..(end + 1)]) as JsonObject ?? throw new FormatException("JSON オブジェクトではありません");
            var answer = node["answer"]?.GetValue<string>() ?? throw new FormatException("answer がありません");
            var citations = node["citations"] switch
            {
                JsonArray array => array.Select(c => c?.ToString() ?? "").Where(c => c.Length > 0).ToList(),
                JsonValue value => [value.ToString()],
                _ => [],
            };
            var answered = node["answered"] is JsonValue a && a.TryGetValue<bool>(out var b) ? b : citations.Count > 0;
            var offTopic = node["off_topic"] is JsonValue o && o.TryGetValue<bool>(out var ob) && ob;
            return new AiAnswer(answer, citations, answered, offTopic);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            throw new FormatException(e.Message, e);
        }
    }

    [GeneratedRegex(@"^[\W_]*A(\d+)(?![\w])", RegexOptions.IgnoreCase)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"\([^)]*\)|[\s「」『』\[\]【】・:：]")]
    private static partial Regex CompactPattern();

    [GeneratedRegex(@"[\[［(（]\s*(A\d+)\s*[\]］)）]")]
    private static partial Regex BracketedIdPattern();
}
