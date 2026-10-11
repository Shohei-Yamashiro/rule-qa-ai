using System.Text.RegularExpressions;

namespace RuleQA.Core.Rules;

/// <summary>Numbers and dates as written in Japanese regulations.</summary>
public static partial class JapaneseText
{
    /// <summary>Characters that can make up a number: ASCII and full-width digits and kanji numerals.</summary>
    public const string NumberChars = "0-9０-９〇零一二三四五六七八九十百千";

    private const string KanjiDigits = "〇一二三四五六七八九";

    /// <summary>"12", "１２", "十二", "二〇二五" or "元" (the first year of an era). Null when it is not a number.</summary>
    public static int? ParseNumber(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        if (text == "元") return 1;

        if (text.All(c => char.IsAsciiDigit(c) || c is >= '０' and <= '９' || KanjiDigits.Contains(c) || c == '零'))
        {
            var value = 0;
            foreach (var c in text) value = value * 10 + Digit(c);
            return value;
        }

        var total = 0;
        var current = 0;
        foreach (var c in text)
        {
            switch (c)
            {
                case '十': total += (current == 0 ? 1 : current) * 10; current = 0; break;
                case '百': total += (current == 0 ? 1 : current) * 100; current = 0; break;
                case '千': total += (current == 0 ? 1 : current) * 1000; current = 0; break;
                default:
                    if (!(char.IsAsciiDigit(c) || c is >= '０' and <= '９' || KanjiDigits.Contains(c) || c == '零')) return null;
                    current = current * 10 + Digit(c);
                    break;
            }
        }
        return total + current;
    }

    private static int Digit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= '０' and <= '９' => c - '０',
        '零' => 0,
        _ => KanjiDigits.IndexOf(c),
    };

    [GeneratedRegex($@"(?:(?<era>令和|平成)\s*(?<ey>元|[{NumberChars}]+)|(?<y>[0-9０-９]{{4}}))\s*年\s*(?<m>[{NumberChars}]+)\s*月\s*(?<d>[{NumberChars}]+)\s*日|(?<y2>[0-9０-９]{{4}})\s*[/／.．\-－]\s*(?<m2>[0-9０-９]{{1,2}})\s*[/／.．\-－]\s*(?<d2>[0-9０-９]{{1,2}})")]
    private static partial Regex DatePattern();

    /// <summary>Dates such as "2025年4月1日", "令和六年四月一日", "令和元年5月1日" and "2024/10/01".</summary>
    public static IEnumerable<DateOnly> FindDates(string text)
    {
        foreach (Match m in DatePattern().Matches(text))
        {
            int? year, month, day;
            if (m.Groups["y2"].Success)
            {
                (year, month, day) = (ParseNumber(m.Groups["y2"].Value), ParseNumber(m.Groups["m2"].Value), ParseNumber(m.Groups["d2"].Value));
            }
            else
            {
                year = m.Groups["era"].Success
                    ? ParseNumber(m.Groups["ey"].Value) + (m.Groups["era"].Value == "令和" ? 2018 : 1988)
                    : ParseNumber(m.Groups["y"].Value);
                (month, day) = (ParseNumber(m.Groups["m"].Value), ParseNumber(m.Groups["d"].Value));
            }
            if (year is >= 1900 and <= 2200 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year.Value, month.Value))
                yield return new DateOnly(year.Value, month.Value, day!.Value);
        }
    }
}
