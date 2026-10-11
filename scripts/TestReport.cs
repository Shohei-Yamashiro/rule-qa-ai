#:property PublishAot=false
// Runs the tests and writes the test specification (docs/test-spec.md) and, optionally,
// the specification with results (<results-dir>/test-report.md).
// Usage: dotnet run scripts/TestReport.cs -- <repo-root> [<results-dir>]
// Exit code: 0 when all tests passed, 1 when some failed, 2 when the report could not be made.
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Xml.Linq;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: dotnet run scripts/TestReport.cs -- <repo-root> [<results-dir>]");
    return 2;
}
var root = Path.GetFullPath(args[0]);
var resultsDir = args.Length > 1 ? Path.GetFullPath(args[1]) : null;
var testProject = Path.Combine(root, "tests", "RuleQA.Tests");
var trxDir = Path.Combine(Path.GetTempPath(), "ruleqa-testreport-" + Guid.NewGuid().ToString("N"));

var started = DateTimeOffset.Now;
var (testExit, testLog) = Run("dotnet", $"test \"{testProject}\" --nologo --logger \"trx;LogFileName=results.trx\" --results-directory \"{trxDir}\"", root);
var trxPath = Path.Combine(trxDir, "results.trx");
if (!File.Exists(trxPath))
{
    Console.Error.WriteLine("テスト結果（trx）が作られませんでした。ビルドエラーの可能性があります。\n" + testLog);
    return 2;
}

var specs = LoadSpecs(Path.Combine(testProject, "bin", "Debug", "net10.0", "RuleQA.Tests.dll"));
var results = LoadResults(trxPath);
Directory.Delete(trxDir, recursive: true);

var commit = Run("git", "rev-parse --short HEAD", root).Output.Trim();
var branch = Run("git", "branch --show-current", root).Output.Trim() is { Length: > 0 } current ? current
    : Environment.GetEnvironmentVariable("GITHUB_HEAD_REF") is { Length: > 0 } head ? head
    : Environment.GetEnvironmentVariable("GITHUB_REF_NAME") ?? "(不明)";
var dirty = Run("git", "status --porcelain", root).Output.Trim().Length > 0;
var target = $"ブランチ `{branch}`・コミット `{commit}`" + (dirty ? "（未コミットの変更を含む）" : "");

var specPath = Path.Combine(root, "docs", "test-spec.md");
File.WriteAllText(specPath, Document(specs, null, null), new UTF8Encoding(false));
Console.WriteLine($"Wrote {specPath}");
if (resultsDir is not null)
{
    Directory.CreateDirectory(resultsDir);
    var reportPath = Path.Combine(resultsDir, "test-report.md");
    var run = new RunInfo(started, DateTimeOffset.Now - started, target);
    File.WriteAllText(reportPath, Document(specs, results, run), new UTF8Encoding(false));
    Console.WriteLine($"Wrote {reportPath}");
}

var failed = results.Values.SelectMany(r => r).Count(r => r.Outcome != "Passed");
var missing = specs.Count(s => !results.ContainsKey(s.Key));
Console.WriteLine(failed == 0 && missing == 0 && testExit == 0 ? "All tests passed." : $"Tests failed: {failed}, not run: {missing}");
return failed == 0 && missing == 0 && testExit == 0 ? 0 : 1;

static (int Exit, string Output) Run(string file, string arguments, string workingDirectory)
{
    var psi = new ProcessStartInfo(file, arguments)
    {
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEndAsync();
    var stderr = p.StandardError.ReadToEndAsync();
    p.WaitForExit();
    return (p.ExitCode, stdout.Result + stderr.Result);
}

static List<Spec> LoadSpecs(string dll)
{
    var categories = new List<string>();
    var specs = new List<Spec>();
    var assembly = Assembly.LoadFrom(dll);
    var categoriesField = assembly.GetType("RuleQA.Tests.SpecCategories")!.GetField("All")!;
    categories.AddRange((string[])categoriesField.GetValue(null)!);

    foreach (var type in assembly.GetTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(m => m.MetadataToken))
        {
            var attributes = method.GetCustomAttributesData();
            var spec = attributes.FirstOrDefault(a => a.AttributeType.Name == "SpecAttribute");
            if (spec is null) continue;
            var cases = attributes.Count(a => a.AttributeType.Name == "InlineDataAttribute");
            specs.Add(new Spec(
                $"{type.FullName}.{method.Name}",
                (string)spec.ConstructorArguments[0].Value!,
                (string)spec.ConstructorArguments[1].Value!,
                (string)spec.ConstructorArguments[2].Value!,
                Math.Max(1, cases)));
        }
    }
    return specs
        .OrderBy(s => categories.IndexOf(s.Category) is var i && i < 0 ? int.MaxValue : i)
        .ToList();
}

static Dictionary<string, List<CaseResult>> LoadResults(string trxPath)
{
    XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    var doc = XDocument.Load(trxPath);
    var methods = doc.Descendants(ns + "UnitTest").ToDictionary(
        u => (string)u.Attribute("id")!,
        u => u.Element(ns + "TestMethod") is { } m ? $"{(string)m.Attribute("className")!}.{(string)m.Attribute("name")!}" : (string)u.Attribute("name")!);

    return doc.Descendants(ns + "UnitTestResult")
        .Select(r =>
        {
            var output = r.Element(ns + "Output");
            var error = output?.Element(ns + "ErrorInfo");
            return (Key: methods[(string)r.Attribute("testId")!], Result: new CaseResult(
                (string)r.Attribute("testName")!,
                (string)r.Attribute("outcome")!,
                TimeSpan.TryParse((string?)r.Attribute("duration"), CultureInfo.InvariantCulture, out var d) ? d : TimeSpan.Zero,
                (string?)output?.Element(ns + "StdOut"),
                (string?)error?.Element(ns + "Message"),
                (string?)error?.Element(ns + "StackTrace")));
        })
        .GroupBy(x => x.Key)
        .ToDictionary(g => g.Key, g => g.Select(x => x.Result).ToList());
}

static string Document(List<Spec> specs, Dictionary<string, List<CaseResult>>? results, RunInfo? run)
{
    var withResults = results is not null && run is not null;
    var sb = new StringBuilder();
    var ids = Ids(specs);

    if (withResults)
    {
        var cases = results!.Values.SelectMany(r => r).ToList();
        int passed = cases.Count(c => c.Outcome == "Passed"), failed = cases.Count(c => c.Outcome == "Failed");
        var notRun = cases.Count - passed - failed + specs.Where(s => !results.ContainsKey(s.Key)).Sum(s => s.Cases);
        sb.AppendLine("# テスト仕様書・テスト結果報告書（社内規則 Q&A）");
        sb.AppendLine();
        sb.AppendLine("| 項目 | 内容 |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| 総合判定 | **{(failed == 0 && notRun == 0 ? "合格" : "不合格")}** |");
        sb.AppendLine($"| 結果 | 合格 {passed}・不合格 {failed}・未実行 {notRun}（テストケース数。{specs.Count} 項目） |");
        sb.AppendLine($"| 実行日時 | {run!.Started:yyyy-MM-dd HH:mm}（UTC{run.Started:zzz}、所要 {run.Elapsed.TotalSeconds:0} 秒） |");
        sb.AppendLine($"| 対象 | {run.Target} |");
        sb.AppendLine("| 実行方法 | `dotnet test`（`scripts/publish.ps1` または CI から自動実行） |");
        sb.AppendLine();
        sb.AppendLine("このファイルは `scripts/TestReport.cs` が自動で作ります。結果を含まない仕様書は `docs/test-spec.md` にあります。");
    }
    else
    {
        sb.AppendLine("# テスト仕様書（社内規則 Q&A）");
        sb.AppendLine();
        sb.AppendLine("このファイルは `scripts/TestReport.cs` がテストコードの説明（`[Spec]` 属性）から自動で作ります。直接編集せず、テストの `[Spec]` を直してください。");
        sb.AppendLine("最新の実行結果は、`scripts/publish.ps1` を実行すると `RuleQA-latest/test-report.md` に出力されます。CI では成果物 `test-report` に保存されます。");
    }

    sb.AppendLine();
    sb.AppendLine("## 1. 目的と範囲");
    sb.AppendLine();
    sb.AppendLine("業務ロジック（`src/RuleQA.Core`）が仕様どおりに動くことを、自動テスト（xUnit）で確認します。");
    sb.AppendLine("AI の接続先は偽の HTTP サーバーに置き換え、DB はテストごとに一時フォルダの SQLite を使うので、外部の環境がなくても同じ結果になります。");
    sb.AppendLine("テストデータには `samples/rules` の架空の規則 8 ファイル（旧版・書式の違い・規程どうしの矛盾を含む）と、評価用の質問セット `samples/questions.json` を使います。");
    sb.AppendLine();
    sb.AppendLine("### 自動テストの対象外（手動で確認する項目）");
    sb.AppendLine();
    sb.AppendLine("| 項目 | 確認方法 |");
    sb.AppendLine("|---|---|");
    sb.AppendLine("| 画面の表示と操作（Blazor） | `RuleQA-latest/RuleQA.exe` を起動してブラウザで操作する |");
    sb.AppendLine("| 実際の AI（Ollama・Gemini など）との接続 | 設定画面の「接続テスト」 |");
    sb.AppendLine("| 配布用 zip の展開と起動 | `scripts/publish.ps1` の実行後に exe を起動する |");
    sb.AppendLine();

    sb.AppendLine(withResults ? "## 2. テスト項目と結果" : "## 2. テスト項目");
    var number = 0;
    foreach (var group in specs.GroupBy(s => s.Category))
    {
        number++;
        sb.AppendLine();
        sb.AppendLine($"### 2.{number} {group.Key}");
        sb.AppendLine();
        sb.AppendLine(withResults ? "| ID | 項目 | 確認内容 | 結果 | 時間 |" : "| ID | 項目 | 確認内容 | ケース数 |");
        sb.AppendLine(withResults ? "|---|---|---|---|---|" : "|---|---|---|---|");
        foreach (var s in group)
        {
            var row = $"| {ids[s.Key]} | {Cell(s.Title)} | {Cell(s.Expected)} |";
            if (!withResults)
            {
                sb.AppendLine($"{row} {s.Cases} |");
                continue;
            }
            var cases = results!.GetValueOrDefault(s.Key) ?? [];
            var ok = cases.Count(c => c.Outcome == "Passed");
            var verdict = cases.Count == 0 ? "**未実行**"
                : ok == cases.Count ? (cases.Count > 1 ? $"合格（{ok}/{cases.Count}）" : "合格")
                : $"**不合格**（{ok}/{cases.Count}）";
            var ms = cases.Sum(c => c.Duration.TotalMilliseconds);
            sb.AppendLine($"{row} {verdict} | {(ms < 1000 ? $"{ms:0} ms" : $"{ms / 1000:0.0} 秒")} |");
        }
    }

    if (withResults)
    {
        var failures = specs.SelectMany(s => (results!.GetValueOrDefault(s.Key) ?? []).Where(c => c.Outcome != "Passed").Select(c => (Spec: s, Case: c))).ToList();
        sb.AppendLine();
        sb.AppendLine("## 3. 不合格の詳細");
        sb.AppendLine();
        if (failures.Count == 0) sb.AppendLine("なし");
        foreach (var (spec, c) in failures)
        {
            sb.AppendLine($"### {ids[spec.Key]} {spec.Title}");
            sb.AppendLine();
            sb.AppendLine($"- テスト: `{c.Name}`（{c.Outcome}）");
            sb.AppendLine();
            sb.AppendLine("```text");
            sb.AppendLine((c.Message ?? "").Trim());
            sb.AppendLine(string.Join('\n', (c.StackTrace ?? "").Trim().Split('\n').Take(8)));
            sb.AppendLine("```");
            sb.AppendLine();
        }

        var outputs = specs.SelectMany(s => (results!.GetValueOrDefault(s.Key) ?? []).Where(c => !string.IsNullOrWhiteSpace(c.StdOut)).Select(c => (Spec: s, Case: c))).ToList();
        sb.AppendLine();
        sb.AppendLine("## 4. 付録：テストの出力");
        foreach (var (spec, c) in outputs)
        {
            sb.AppendLine();
            sb.AppendLine($"### {ids[spec.Key]} {spec.Title}");
            sb.AppendLine();
            sb.AppendLine("```text");
            sb.AppendLine(c.StdOut!.Trim());
            sb.AppendLine("```");
        }
    }
    return sb.ToString().ReplaceLineEndings("\n");
}

static Dictionary<string, string> Ids(List<Spec> specs)
{
    var ids = new Dictionary<string, string>();
    var number = 0;
    foreach (var group in specs.GroupBy(s => s.Category))
    {
        number++;
        var i = 0;
        foreach (var s in group) ids[s.Key] = $"T{number:00}-{++i:00}";
    }
    return ids;
}

static string Cell(string text) =>
    text.Replace("|", "\\|").Replace("`", "\\`").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\n", " ");

record Spec(string Key, string Category, string Title, string Expected, int Cases);
record CaseResult(string Name, string Outcome, TimeSpan Duration, string? StdOut, string? Message, string? StackTrace);
record RunInfo(DateTimeOffset Started, TimeSpan Elapsed, string Target);
