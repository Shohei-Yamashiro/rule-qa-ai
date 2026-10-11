using System.Text;
using RuleQA.Core.Domain;
using RuleQA.Core.Rules;

namespace RuleQA.Tests;

public class RuleParserTests
{
    [Fact]
    [Spec(SpecCategories.Import, "Markdown の見出しから条文に分ける", "「### 第4条（試用期間）」の形の条を、章・条番号・見出し・本文に分ける。題名と制定・改定の箇条書きは条文に入れず、最も新しい改定日を施行日とする。末尾の「附則」は 1 つの単位にする。")]
    public void Splits_markdown_articles_with_chapters()
    {
        var doc = Samples.Parse("01_");

        Assert.Equal("就業規則", doc.Title);
        Assert.Equal(new DateOnly(2025, 4, 1), doc.EffectiveDate);
        Assert.False(doc.IsAbolished);
        Assert.Equal([.. Enumerable.Range(1, 12).Select(n => $"第{n}条"), "附則"], doc.Articles.Select(a => a.Label));
        var probation = doc.Articles[3];
        Assert.Equal(("第2章 採用", "試用期間"), (probation.Chapter, probation.Heading));
        Assert.StartsWith("1. 新たに採用した者", probation.Body);
        Assert.Contains("3. 試用期間は、勤続年数に通算する。", probation.Body);
        Assert.Contains("2019年4月1日から施行", doc.Articles[^1].Body);
        Assert.Empty(doc.Warnings);
    }

    [Fact]
    [Spec(SpecCategories.Import, "法令風のテキスト（漢数字・全角・前の行の見出し）を読む", "「（年次有給休暇）」の次の行の「第二条　…」を第2条（見出し：年次有給休暇）とし、「第六条の二」は「第6条の2」にする。「２　…」の項や字下げした表は本文に残す。「附　則」の和暦（令和六年四月一日）を施行日として読む。")]
    public void Reads_legal_style_text_with_kanji_numerals()
    {
        var doc = Samples.Parse("02_");

        Assert.Equal(["第1条", "第2条", "第3条", "第4条", "第5条", "第6条", "第6条の2", "第7条", "附則"], doc.Articles.Select(a => a.Label));
        var annual = doc.Articles[1];
        Assert.Equal("年次有給休暇", annual.Heading);
        Assert.StartsWith("会社は、入社日から六か月間", annual.Body);
        Assert.Contains("六年六か月以上　二十日", annual.Body);
        Assert.Contains("３　前二項にかかわらず", annual.Body);
        Assert.Equal("リフレッシュ休暇", doc.Articles[6].Heading);
        Assert.Equal(new DateOnly(2024, 4, 1), doc.EffectiveDate);
    }

    [Fact]
    [Spec(SpecCategories.Import, "太字や「## 第1条 目的」の条も読み、番号の乱れを知らせる", "「**第5条（国内出張の日当）**」と「## 第3条 申請期限」の両方から見出しを取り出す。同じ条番号が 2 回あれば 2 つ目を「第7条(2)」として残し、抜けた条番号（第8条）と一緒に警告する。「2024/10/01」の形の改定日も読む。")]
    public void Reads_bold_and_heading_articles_and_warns_about_numbering()
    {
        var travel = Samples.Parse("03_");
        var expense = Samples.Parse("04_");

        Assert.Equal(9, travel.Articles.Count);
        Assert.Equal("国内出張の日当", travel.Articles[4].Heading);
        Assert.Contains("| 一般社員 | 1,000円 | 2,500円 |", travel.Articles[4].Body);
        Assert.Equal(new DateOnly(2025, 4, 1), travel.EffectiveDate);

        Assert.Equal(["第1条", "第2条", "第3条", "第4条", "第5条", "第6条", "第7条", "第7条(2)", "第9条"], expense.Articles.Select(a => a.Label));
        Assert.Equal(("申請期限", "書籍代"), (expense.Articles[2].Heading, expense.Articles[7].Heading));
        Assert.Equal(new DateOnly(2024, 10, 1), expense.EffectiveDate);
        Assert.Equal(2, expense.Warnings.Count);
        Assert.Contains(expense.Warnings, w => w.Contains("第7条 が 2 回"));
        Assert.Contains(expense.Warnings, w => w.Contains("第8条が見つかりません（第7条の次が第9条）"));
    }

    [Fact]
    [Spec(SpecCategories.Import, "長い条は項に分ける", "本文が 300 文字を超える条は「①②③④」の項ごとに「第7条第1項」〜「第7条第4項」に分け、見出し（費用負担）は各項に残す。短い条（「2 …」の項がある第3条）は分けない。BOM 付き UTF-8 と半角かっこの見出し「(目的)」も読む。")]
    public void Splits_long_articles_into_paragraphs()
    {
        var doc = Samples.Parse("06_");

        Assert.Equal(["第1条", "第2条", "第3条", "第4条", "第5条", "第6条", "第7条第1項", "第7条第2項", "第7条第3項", "第7条第4項", "第8条", "附則"],
            doc.Articles.Select(a => a.Label));
        Assert.Equal("目的", doc.Articles[0].Heading);
        Assert.All(doc.Articles.Where(a => a.Label.StartsWith("第7条")), a => Assert.Equal("費用負担", a.Heading));
        Assert.StartsWith("③ 在宅勤務の日の通勤交通費", doc.Articles[8].Body);
        Assert.Contains("2 定期的に在宅勤務", doc.Articles[2].Body);
        Assert.Equal(new DateOnly(2025, 10, 1), doc.EffectiveDate);
    }

    [Fact]
    [Spec(SpecCategories.Import, "条のない文書は見出しか段落で分ける", "条番号のない Markdown は「## 2. パスワード」の見出しごとに分ける。見出しもない FAQ は空行で区切った段落ごとに「段落1」〜に分け、題名に「よくある質問」があれば参考資料として扱う。")]
    public void Splits_documents_without_articles_by_sections_or_paragraphs()
    {
        var security = Samples.Parse("05_");
        var faq = Samples.Parse("07_");

        Assert.Equal(["1. 目的", "2. パスワード", "3. 私物端末の利用（BYOD）", "4. 社外への持ち出し", "5. 公衆無線LAN", "6. 生成AIの利用", "7. インシデントの報告"],
            security.Articles.Select(a => a.Label));
        Assert.Contains("12文字以上", security.Articles[1].Body);
        Assert.Equal(new DateOnly(2024, 7, 1), security.EffectiveDate);
        Assert.Equal(DocumentKind.Regulation, security.Kind);

        Assert.Equal(DocumentKind.Reference, faq.Kind);
        Assert.Equal(["段落1", "段落2", "段落3", "段落4", "段落5"], faq.Articles.Select(a => a.Label));
        Assert.Equal("Q. 在宅勤務はいつまでに申請すればよいですか？\nA. 前日の17時までに所属長へメールで連絡してください。", faq.Articles[1].Body);
        Assert.Null(faq.EffectiveDate);
    }

    [Fact]
    [Spec(SpecCategories.Import, "旧版と分かる文書を見分ける", "Shift_JIS・改行 CRLF の「旅費規程（2021年版）」を読み、版の表記を除いた名前（旅費規程）と施行日 2021-04-01 を取り出す。冒頭に「廃止」とあれば廃止扱いにし、廃止日は施行日にしない。見出しと本文が同じ行の「第1条（目的）この規程は…」も読む。")]
    public void Recognizes_old_versions()
    {
        var doc = Samples.Parse("old/03_");

        Assert.Equal("旅費規程（2021年版）", doc.Title);
        Assert.Equal("旅費規程", doc.BaseTitle);
        Assert.True(doc.IsAbolished);
        Assert.Equal(new DateOnly(2021, 4, 1), doc.EffectiveDate);
        Assert.Equal(("日当", "国内出張の日当は、日帰り800円、宿泊1泊あたり2,000円とする。海外出張の日当は1日あたり5,000円とする。"),
            (doc.Articles[3].Heading, doc.Articles[3].Body));
    }

    [Fact]
    [Spec(SpecCategories.Import, "行頭の条番号の参照を新しい条にしない", "「第5条の規定により…」「第3条及び第4条…」のように行頭で他の条を参照する行は、前の条の本文として扱う。")]
    public void Line_starting_with_a_reference_stays_in_the_article()
    {
        var doc = RuleDocumentParser.Parse("x.txt", "テスト規程\n第1条　申請は所属長に行う。\n第5条の規定により承認を得た者は、この限りでない。\n第3条及び第4条も同様とする。\n第2条　以上");

        Assert.Equal(["第1条", "第2条"], doc.Articles.Select(a => a.Label));
        Assert.Contains("第5条の規定により", doc.Articles[0].Body);
        Assert.Contains("第3条及び第4条", doc.Articles[0].Body);
    }

    [Theory]
    [InlineData("12", 12)]
    [InlineData("１２", 12)]
    [InlineData("十", 10)]
    [InlineData("二十三", 23)]
    [InlineData("百二", 102)]
    [InlineData("二〇二五", 2025)]
    [InlineData("元", 1)]
    [Spec(SpecCategories.Import, "算用数字・全角数字・漢数字を読む", "「12」「１２」「十」「二十三」「百二」「二〇二五」「元」をそれぞれ数として読む。")]
    public void Parses_japanese_numbers(string text, int expected) => Assert.Equal(expected, JapaneseText.ParseNumber(text));

    [Fact]
    [Spec(SpecCategories.Import, "西暦・和暦・スラッシュ区切りの日付を読む", "「2025年4月1日」「令和六年四月一日」「令和元年5月1日」「平成31年4月30日」「2024/10/01」を日付として読み、「2025年2月30日」のようなありえない日付は読まない。")]
    public void Finds_dates_in_several_formats()
    {
        var dates = JapaneseText.FindDates("2025年4月1日、令和六年四月一日、令和元年5月1日、平成31年4月30日、2024/10/01、2025年2月30日").ToList();

        Assert.Equal([new(2025, 4, 1), new(2024, 4, 1), new(2019, 5, 1), new(2019, 4, 30), new DateOnly(2024, 10, 1)], dates);
    }

    [Fact]
    [Spec(SpecCategories.Import, "文字コードを判定して読む", "BOM 付き UTF-8・BOM なし UTF-8・UTF-16・Shift_JIS のファイルを、文字化けせずに読む。")]
    public void Decodes_utf8_utf16_and_shift_jis()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string text = "第1条（目的）就業規則";

        Assert.Equal((text, "UTF-8 (BOM)"), TextDecoder.Decode([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)]));
        Assert.Equal((text, "UTF-8"), TextDecoder.Decode(Encoding.UTF8.GetBytes(text)));
        Assert.Equal((text, "UTF-16"), TextDecoder.Decode([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)]));
        Assert.Equal((text, "Shift_JIS"), TextDecoder.Decode(Encoding.GetEncoding(932).GetBytes(text)));
    }
}
