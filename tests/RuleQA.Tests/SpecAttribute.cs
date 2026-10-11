namespace RuleQA.Tests;

/// <summary>
/// Describes a test in Japanese for the generated test specification (scripts/TestReport.cs).
/// Every test method must have one; <see cref="SpecCoverageTests"/> enforces it.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SpecAttribute(string category, string title, string expected) : Attribute
{
    public string Category { get; } = category;
    public string Title { get; } = title;
    public string Expected { get; } = expected;
}

public static class SpecCategories
{
    public const string Import = "規則の取り込みと分割";
    public const string Search = "条文の検索（RAG）";
    public const string Ai = "AI 連携（OpenAI 互換 API）";
    public const string Answer = "回答と引用の検証";
    public const string Evaluation = "フィードバックと評価";
    public const string Settings = "設定";
    public const string Meta = "テスト基盤";

    /// <summary>Order of sections in the document.</summary>
    public static readonly string[] All = [Import, Search, Ai, Answer, Evaluation, Settings, Meta];
}

public class SpecCoverageTests
{
    [Fact]
    [Spec(SpecCategories.Meta, "全テストに仕様の説明が付いている", "テストメソッドすべてに区分・項目名・確認内容があり、区分は決められた一覧のどれかである。")]
    public void Every_test_has_a_spec()
    {
        var tests = typeof(SpecCoverageTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods())
            .Where(m => m.IsDefined(typeof(FactAttribute), inherit: true))
            .ToList();

        Assert.NotEmpty(tests);
        Assert.All(tests, m =>
        {
            var spec = m.GetCustomAttributes(typeof(SpecAttribute), false).Cast<SpecAttribute>().SingleOrDefault();
            Assert.True(spec is not null, $"{m.DeclaringType!.Name}.{m.Name} に [Spec] がありません。");
            Assert.Contains(spec!.Category, SpecCategories.All);
        });
    }
}
