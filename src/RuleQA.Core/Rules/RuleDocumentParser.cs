using System.Text;
using System.Text.RegularExpressions;
using RuleQA.Core.Domain;
using static RuleQA.Core.Rules.JapaneseText;

namespace RuleQA.Core.Rules;

public sealed record ParsedArticle(string Label, string? Chapter, string? Heading, string Body);

public sealed record ParsedDocument(
    string Title,
    string BaseTitle,
    DocumentKind Kind,
    DateOnly? EffectiveDate,
    bool IsAbolished,
    IReadOnlyList<ParsedArticle> Articles,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Splits a regulation into articles (第○条). Articles longer than <see cref="MaxArticleChars"/> are split into
/// their paragraphs (項). Documents without articles are split by Markdown headings, or else by blank lines.
/// Handles Markdown and plain text, full-width and kanji numerals, headings written before or on the article line,
/// and the supplementary provisions (附則).
/// </summary>
public static partial class RuleDocumentParser
{
    public const int MaxArticleChars = 300;

    /// <summary>Lines at the top that may say the document is abolished or an old version.</summary>
    private const int HeaderLines = 8;

    [GeneratedRegex(@"^#{1,6}[\s　]+")]
    private static partial Regex MarkdownHeading();

    [GeneratedRegex($@"^第[\s　]*(?<n>[{NumberChars}]+)[\s　]*章[\s　]*(?<t>.*)$")]
    private static partial Regex ChapterLine();

    [GeneratedRegex(@"^附[\s　]*則[\s　]*(?:[（(].*[）)])?$")]
    private static partial Regex SupplementaryLine();

    [GeneratedRegex(@"^[（(](?<h>[^）)]{1,30})[）)]$")]
    private static partial Regex HeadingOnlyLine();

    [GeneratedRegex($@"^第[\s　]*(?<n>[{NumberChars}]+)[\s　]*条(?:の(?<s>[{NumberChars}]+))?(?<rest>.*)$")]
    private static partial Regex ArticleLine();

    /// <summary>"第5条の規定により…" at the start of a line is a reference, not a new article.</summary>
    [GeneratedRegex(@"^(?:の規定|に定め|により|に基づ|を準用|及び|又は|から|まで|[、，,])")]
    private static partial Regex ReferenceContinuation();

    [GeneratedRegex(@"^[（(](?<h>[^）)]{1,30})[）)][\s　]*(?<b>.*)$")]
    private static partial Regex InlineHeading();

    [GeneratedRegex(@"^(?:(?<n>[0-9０-９]{1,2})[\s　.．、)）]|(?<c>[①-⑳]))")]
    private static partial Regex ItemMarker();

    [GeneratedRegex(@"[（(【\[][^）)】\]]*[）)】\]]")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"[_\-－]?(?:\d{4}年度?版|旧版|改定前)$")]
    private static partial Regex VersionSuffix();

    [GeneratedRegex(@"FAQ|よくある質問|Q&A|Ｑ＆Ａ|手引|ガイド", RegexOptions.IgnoreCase)]
    private static partial Regex ReferenceTitle();

    [GeneratedRegex(@"施行|改定|改正|制定")]
    private static partial Regex DateKeyword();

    private sealed record Line(string Text, bool IsHeading, bool IsBold);

    private sealed class Unit(string label, string? chapter, string? heading)
    {
        public string Label { get; set; } = label;
        public string? Chapter { get; } = chapter;
        public string? Heading { get; } = heading;
        public List<string> Lines { get; } = [];
    }

    public static ParsedDocument Parse(string fileName, string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(Clean).ToList();
        var warnings = new List<string>();

        var titleIndex = lines.FindIndex(l => l.Text.Length > 0);
        var title = titleIndex < 0 ? Path.GetFileNameWithoutExtension(fileName) : lines[titleIndex].Text;
        var body = titleIndex < 0 ? [] : lines.Skip(titleIndex + 1).ToList();

        var header = string.Join("\n", lines.Take(HeaderLines).Select(l => l.Text));
        var isAbolished = header.Contains("廃止") || header.Contains("旧版") || title.StartsWith("【旧");
        var effective = lines
            .Where(l => DateKeyword().IsMatch(l.Text) && !l.Text.Contains("廃止"))
            .SelectMany(l => FindDates(l.Text))
            .Select(d => (DateOnly?)d)
            .Max();

        var units = SplitArticles(body, warnings);
        if (units.Count == 0)
            units = body.Any(l => l.IsHeading) ? SplitSections(body) : SplitParagraphs(body);

        var articles = new List<ParsedArticle>();
        var seen = new Dictionary<string, int>();
        foreach (var unit in units)
        {
            var content = Join(unit.Lines);
            if (content.Length == 0 && unit.Heading is null)
            {
                if (unit.Label.StartsWith('第')) warnings.Add($"{unit.Label} に本文がありません。");
                continue;
            }
            var count = seen[unit.Label] = seen.GetValueOrDefault(unit.Label) + 1;
            if (count > 1)
            {
                warnings.Add($"{unit.Label} が {count} 回出てきます。{count} つ目を「{unit.Label}({count})」として取り込みます。");
                unit.Label = $"{unit.Label}({count})";
            }
            articles.AddRange(SplitItems(unit.Label, unit.Chapter, unit.Heading, content));
        }
        if (articles.Count == 0) warnings.Add("条文を読み取れませんでした。");

        return new ParsedDocument(title, BaseTitleOf(title), ReferenceTitle().IsMatch(title) ? DocumentKind.Reference : DocumentKind.Regulation,
            effective, isAbolished, articles, warnings);
    }

    /// <summary>"旅費規程（2021年版）" → "旅費規程".</summary>
    public static string BaseTitleOf(string title)
    {
        var s = Bracketed().Replace(title, "");
        s = new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
        s = VersionSuffix().Replace(s, "");
        return s.Length > 0 ? s : title.Trim();
    }

    private static Line Clean(string raw)
    {
        var text = raw.Trim();
        var isHeading = false;
        if (MarkdownHeading().Match(text) is { Success: true } h)
        {
            isHeading = h.Value.TrimEnd().Length >= 2;
            text = text[h.Length..];
        }
        var isBold = text.StartsWith("**") || text.StartsWith("__");
        text = text.Replace("**", "").Replace("__", "").Trim();
        if (text.StartsWith("> ")) text = text[2..];
        return new Line(text, isHeading, isBold);
    }

    private static List<Unit> SplitArticles(List<Line> lines, List<string> warnings)
    {
        var units = new List<Unit>();
        string? chapter = null;
        string? pendingHeading = null;
        var supplementary = false;
        var lastNumber = 0;
        Unit? current = null;

        foreach (var line in lines)
        {
            var text = line.Text;
            if (text.Length == 0)
            {
                current?.Lines.Add("");
                continue;
            }

            if (ChapterLine().Match(text) is { Success: true } ch && ParseNumber(ch.Groups["n"].Value) is { } chapterNumber)
            {
                chapter = $"第{chapterNumber}章 {ch.Groups["t"].Value.Trim()}".Trim();
                current = null;
                pendingHeading = null;
                continue;
            }

            if (SupplementaryLine().IsMatch(text))
            {
                supplementary = true;
                chapter = "附則";
                pendingHeading = null;
                current = new Unit("附則", chapter, null);
                units.Add(current);
                continue;
            }

            if (HeadingOnlyLine().Match(text) is { Success: true } ho && (current is null || current.Lines.Count == 0 || current.Lines[^1].Length == 0 || IsSentenceEnd(current.Lines[^1])))
            {
                pendingHeading = ho.Groups["h"].Value.Trim();
                continue;
            }

            if (ArticleLine().Match(text) is { Success: true } a
                && ParseNumber(a.Groups["n"].Value) is { } number
                && !ReferenceContinuation().IsMatch(a.Groups["rest"].Value.TrimStart()))
            {
                var sub = a.Groups["s"].Success ? ParseNumber(a.Groups["s"].Value) : null;
                var label = $"第{number}条" + (sub is null ? "" : $"の{sub}");
                if (supplementary) label = "附則" + label;
                else if (sub is null)
                {
                    if (number > lastNumber + 1 && lastNumber > 0)
                    {
                        var missing = number == lastNumber + 2 ? $"第{lastNumber + 1}条" : $"第{lastNumber + 1}条から第{number - 1}条";
                        warnings.Add($"{missing}が見つかりません（第{lastNumber}条の次が第{number}条）。");
                    }
                    lastNumber = Math.Max(lastNumber, number);
                }

                var rest = a.Groups["rest"].Value.TrimStart(' ', '　', ':', '：', '.', '．');
                var heading = pendingHeading;
                var first = rest;
                if (InlineHeading().Match(rest) is { Success: true } ih)
                {
                    heading = ih.Groups["h"].Value.Trim();
                    first = ih.Groups["b"].Value;
                }
                else if (rest.Length is > 0 and <= 20 && !rest.Contains('。') && (line.IsHeading || line.IsBold))
                {
                    heading = rest;
                    first = "";
                }

                if (current is { Label: "附則" } && Join(current.Lines).Length == 0) units.Remove(current);
                current = new Unit(label, chapter, heading);
                if (first.Length > 0) current.Lines.Add(first);
                units.Add(current);
                pendingHeading = null;
                continue;
            }

            if (pendingHeading is not null && current is not null)
            {
                current.Lines.Add($"（{pendingHeading}）");
                pendingHeading = null;
            }
            current?.Lines.Add(text);
        }
        return units.Any(u => u.Label.Contains('条')) ? units : [];
    }

    private static List<Unit> SplitSections(List<Line> lines)
    {
        var units = new List<Unit>();
        Unit? current = null;
        foreach (var line in lines)
        {
            if (line.IsHeading)
            {
                current = new Unit(line.Text.Length > 40 ? line.Text[..40] : line.Text, null, null);
                units.Add(current);
            }
            else current?.Lines.Add(line.Text);
        }
        return units;
    }

    private static List<Unit> SplitParagraphs(List<Line> lines)
    {
        var units = new List<Unit>();
        Unit? current = null;
        foreach (var line in lines)
        {
            if (line.Text.Length == 0)
            {
                current = null;
                continue;
            }
            if (current is null)
            {
                current = new Unit($"段落{units.Count + 1}", null, null);
                units.Add(current);
            }
            current.Lines.Add(line.Text);
        }
        return units;
    }

    /// <summary>Splits a long article at its paragraph numbers ("２　…", "2 …", "①"), when they count up from 1 or 2.</summary>
    private static IEnumerable<ParsedArticle> SplitItems(string label, string? chapter, string? heading, string body)
    {
        if (body.Length <= MaxArticleChars || !label.StartsWith('第')) return [new ParsedArticle(label, chapter, heading, body)];

        var items = new List<(int Number, List<string> Lines)> { (1, []) };
        foreach (var line in body.Split('\n'))
        {
            if (ItemMarker().Match(line) is { Success: true } m)
            {
                var n = m.Groups["c"].Success ? m.Groups["c"].Value[0] - '①' + 1 : ParseNumber(m.Groups["n"].Value) ?? 0;
                if (n == items[^1].Number + 1 || (n == 1 && items is [{ Lines.Count: 0 }]))
                {
                    if (items[^1].Lines.Count == 0) items.RemoveAt(items.Count - 1);
                    items.Add((n, [line]));
                    continue;
                }
            }
            items[^1].Lines.Add(line);
        }
        if (items.Count < 2) return [new ParsedArticle(label, chapter, heading, body)];
        return items.Select(i => new ParsedArticle($"{label}第{i.Number}項", chapter, heading, Join(i.Lines)));
    }

    private static bool IsSentenceEnd(string line) => line.EndsWith('。') || line.EndsWith('）') || line.EndsWith(')');

    private static string Join(IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        var blank = false;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                blank = sb.Length > 0;
                continue;
            }
            if (sb.Length > 0) sb.Append(blank ? "\n\n" : "\n");
            sb.Append(line);
            blank = false;
        }
        return sb.ToString();
    }
}
