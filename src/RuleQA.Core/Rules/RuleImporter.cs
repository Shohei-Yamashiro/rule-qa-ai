using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RuleQA.Core.Data;
using RuleQA.Core.Domain;
using RuleQA.Core.Settings;

namespace RuleQA.Core.Rules;

public enum FileChangeKind
{
    Added,
    Changed,
    Removed,
    Unchanged,
}

/// <summary>Article labels ("第5条") that were added, removed or whose heading or text changed.</summary>
public sealed record ArticleChanges(IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Changed)
{
    public static readonly ArticleChanges None = new([], [], []);

    public bool Any => Added.Count + Removed.Count + Changed.Count > 0;

    /// <summary>"変更 第5条・第6条／追加 第10条", or "" when nothing changed.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Changed.Count > 0) parts.Add("変更 " + string.Join("・", Changed));
        if (Added.Count > 0) parts.Add("追加 " + string.Join("・", Added));
        if (Removed.Count > 0) parts.Add("削除 " + string.Join("・", Removed));
        return string.Join("／", parts);
    }
}

/// <param name="Path">Relative to the rules folder, with "/" separators.</param>
public sealed record FileChange(string Path, FileChangeKind Kind, string Title, ArticleChanges Articles)
{
    public string KindLabel => Label(Kind);

    public static string Label(FileChangeKind kind) => kind switch
    {
        FileChangeKind.Added => "追加",
        FileChangeKind.Changed => "変更",
        FileChangeKind.Removed => "削除",
        _ => "変更なし",
    };
}

/// <summary>How the rules folder differs from what is imported.</summary>
public sealed record FolderCheck(string Folder, bool Exists, IReadOnlyList<FileChange> Files)
{
    public IReadOnlyList<FileChange> Pending => Files.Where(f => f.Kind != FileChangeKind.Unchanged).ToList();
    public bool HasChanges => Pending.Count > 0;
    public int Count(FileChangeKind kind) => Files.Count(f => f.Kind == kind);
}

/// <param name="Files">What happened to each file that was imported or removed.</param>
public sealed record ImportResult(int Added, int Updated, int Unchanged, int Removed, int KeptVectors, IReadOnlyList<string> Warnings, IReadOnlyList<FileChange> Files)
{
    public int Changed => Added + Updated + Removed;
}

/// <param name="Titles">Titles of the current regulations (not old versions or reference material), in path order.</param>
/// <param name="LastImportedUtc">When a document was last added or replaced; null when nothing is imported.</param>
public sealed record LibraryStats(int Documents, int Superseded, int References, int Articles, int Embedded, IReadOnlyList<string> Titles, DateTime? LastImportedUtc);

/// <summary>Imports the .md and .txt files of a folder (including subfolders) and decides which versions are current.</summary>
public sealed class RuleImporter(IDbContextFactory<RuleQaDbContext> dbFactory, TimeProvider clock, string baseDirectory, ILogger<RuleImporter> logger)
{
    private static readonly string[] Extensions = [".md", ".txt"];

    public string ResolveFolder(string folder) =>
        Path.IsPathRooted(folder) ? folder : Path.GetFullPath(Path.Combine(baseDirectory, folder));

    /// <summary>Compares the folder with the imported documents without changing anything.</summary>
    public async Task<FolderCheck> CheckAsync(string folder, CancellationToken ct = default)
    {
        var root = ResolveFolder(folder);
        if (!Directory.Exists(root)) return new(root, false, []);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Documents.AsNoTracking().Include(d => d.Articles).ToDictionaryAsync(d => d.Path, StringComparer.OrdinalIgnoreCase, ct);
        var changes = new List<FileChange>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in await ReadFolderAsync(root, ct))
        {
            seen.Add(file.Path);
            var old = existing.GetValueOrDefault(file.Path);
            if (old?.Hash == file.Hash)
            {
                changes.Add(new(file.Path, FileChangeKind.Unchanged, old.Title, ArticleChanges.None));
                continue;
            }
            var parsed = file.Parse();
            changes.Add(new(file.Path, old is null ? FileChangeKind.Added : FileChangeKind.Changed, parsed.Title,
                old is null ? new(parsed.Articles.Select(a => a.Label).ToList(), [], []) : Compare(old.Articles, parsed.Articles)));
        }
        changes.AddRange(existing.Values.Where(d => !seen.Contains(d.Path)).OrderBy(d => d.Path, StringComparer.Ordinal)
            .Select(d => new FileChange(d.Path, FileChangeKind.Removed, d.Title, new([], d.Articles.OrderBy(a => a.Order).Select(a => a.Label).ToList(), []))));
        return new(root, true, changes);
    }

    /// <summary>
    /// Imports the folder. Vectors are kept for articles whose text did not change, which documents are current is decided
    /// again, and the import is recorded in the import log.
    /// </summary>
    /// <param name="paths">For <see cref="ImportMode.Selected"/>: the relative paths to import (or remove, when the file is gone).</param>
    public async Task<ImportResult> ImportFolderAsync(string folder, ImportMode mode = ImportMode.Changes, IReadOnlyCollection<string>? paths = null, CancellationToken ct = default)
    {
        var root = ResolveFolder(folder);
        if (!Directory.Exists(root)) return new(0, 0, 0, 0, 0, [$"フォルダが見つかりません: {root}"], []);
        var selected = new HashSet<string>(paths ?? [], StringComparer.OrdinalIgnoreCase);
        bool Included(string path) => mode != ImportMode.Selected || selected.Contains(path);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Documents.Include(d => d.Articles).ToDictionaryAsync(d => d.Path, StringComparer.OrdinalIgnoreCase, ct);
        var warnings = new List<string>();
        var changes = new List<FileChange>();
        int unchanged = 0, kept = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in await ReadFolderAsync(root, ct))
        {
            seen.Add(file.Path);
            var old = existing.GetValueOrDefault(file.Path);
            if (!Included(file.Path) || (old?.Hash == file.Hash && mode is ImportMode.Changes or ImportMode.Initial))
            {
                unchanged++;
                continue;
            }

            var parsed = file.Parse();
            warnings.AddRange(parsed.Warnings.Select(w => $"{file.Path}: {w}"));
            var document = new RuleDocument
            {
                Path = file.Path,
                Title = parsed.Title,
                BaseTitle = parsed.BaseTitle,
                Kind = parsed.Kind,
                EffectiveDate = parsed.EffectiveDate,
                IsAbolished = parsed.IsAbolished,
                Encoding = file.Encoding,
                Hash = file.Hash,
                ImportedAtUtc = clock.GetUtcNow().UtcDateTime,
                Warnings = string.Join("\n", parsed.Warnings),
            };
            document.Articles = parsed.Articles.Select((a, i) => new Article
            {
                Document = document,
                Key = $"{document.Key}#{a.Label}",
                Order = i,
                Chapter = a.Chapter,
                Label = a.Label,
                Heading = a.Heading,
                Body = a.Body,
            }).ToList();

            if (old is not null)
            {
                changes.Add(new(file.Path, old.Hash == file.Hash ? FileChangeKind.Unchanged : FileChangeKind.Changed, parsed.Title, Compare(old.Articles, parsed.Articles)));
                var vectors = old.Articles.Where(a => a.Embedding is not null).ToDictionary(a => a.Key, a => a);
                foreach (var article in document.Articles)
                {
                    if (vectors.GetValueOrDefault(article.Key) is { } previous && previous.EmbeddingText == article.EmbeddingText)
                    {
                        article.Embedding = previous.Embedding;
                        article.EmbeddingModel = previous.EmbeddingModel;
                        kept++;
                    }
                }
                db.Documents.Remove(old);
                await db.SaveChangesAsync(ct);
            }
            else changes.Add(new(file.Path, FileChangeKind.Added, parsed.Title, new(parsed.Articles.Select(a => a.Label).ToList(), [], [])));
            db.Documents.Add(document);
            await db.SaveChangesAsync(ct);
        }

        var removed = existing.Values.Where(d => !seen.Contains(d.Path) && Included(d.Path)).OrderBy(d => d.Path, StringComparer.Ordinal).ToList();
        changes.AddRange(removed.Select(d => new FileChange(d.Path, FileChangeKind.Removed, d.Title, new([], d.Articles.OrderBy(a => a.Order).Select(a => a.Label).ToList(), []))));
        db.Documents.RemoveRange(removed);
        await db.SaveChangesAsync(ct);

        await MarkSupersededAsync(db, ct);
        var result = new ImportResult(
            changes.Count(c => c.Kind == FileChangeKind.Added),
            changes.Count(c => c.Kind is FileChangeKind.Changed or FileChangeKind.Unchanged),
            unchanged,
            removed.Count,
            kept,
            warnings,
            changes);
        if (result.Changed > 0 || mode != ImportMode.Changes)
        {
            db.ImportLogs.Add(new ImportLog
            {
                AtUtc = clock.GetUtcNow().UtcDateTime,
                Mode = mode,
                Folder = root,
                Added = result.Added,
                Updated = result.Updated,
                Removed = result.Removed,
                Unchanged = result.Unchanged,
                Details = string.Join("\n", changes.Select(Describe)),
                Warnings = string.Join("\n", warnings),
            });
            await db.SaveChangesAsync(ct);
        }
        logger.LogInformation("Imported rules from {Folder} ({Mode}): added {Added}, updated {Updated}, unchanged {Unchanged}, removed {Removed}",
            root, mode, result.Added, result.Updated, unchanged, removed.Count);
        return result;
    }

    /// <summary>Imports the folder in the settings when nothing has been imported, so the samples work right after unzipping.</summary>
    public async Task ImportIfEmptyAsync(RulesSettings settings, CancellationToken ct = default)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            if (await db.Documents.AnyAsync(ct)) return;
        }
        if (Directory.Exists(ResolveFolder(settings.Folder))) await ImportFolderAsync(settings.Folder, ImportMode.Initial, null, ct);
    }

    /// <summary>The import log, newest first.</summary>
    public async Task<IReadOnlyList<ImportLog>> LogsAsync(int limit = 50, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ImportLogs.AsNoTracking().OrderByDescending(l => l.AtUtc).ThenByDescending(l => l.Id).Take(limit).ToListAsync(ct);
    }

    public async Task<LibraryStats> StatsAsync(string? embeddingModel, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var model = embeddingModel?.Trim() ?? "";
        return new(
            await db.Documents.CountAsync(ct),
            await db.Documents.CountAsync(d => d.IsSuperseded, ct),
            await db.Documents.CountAsync(d => d.Kind == DocumentKind.Reference, ct),
            await db.Articles.CountAsync(ct),
            model.Length == 0 ? 0 : await db.Articles.CountAsync(a => a.Embedding != null && a.EmbeddingModel == model, ct),
            await db.Documents.Where(d => !d.IsSuperseded && d.Kind == DocumentKind.Regulation).OrderBy(d => d.Path).Select(d => d.BaseTitle).ToListAsync(ct),
            await db.Documents.MaxAsync(d => (DateTime?)d.ImportedAtUtc, ct));
    }

    /// <summary>"変更 03_旅費規程.md：変更 第5条／追加 第10条".</summary>
    private static string Describe(FileChange change)
    {
        var kind = change.Kind == FileChangeKind.Unchanged ? "読み直し" : change.KindLabel;
        var articles = change.Kind switch
        {
            FileChangeKind.Added => $"条文 {change.Articles.Added.Count} 件",
            FileChangeKind.Removed => $"条文 {change.Articles.Removed.Count} 件",
            _ => change.Articles.Any ? change.Articles.Describe() : "条文の変更なし",
        };
        return $"{kind} {change.Path}：{articles}";
    }

    private static ArticleChanges Compare(IEnumerable<Article> before, IReadOnlyList<ParsedArticle> after)
    {
        var old = before.OrderBy(a => a.Order).ToList();
        var oldByLabel = old.ToDictionary(a => a.Label);
        var newLabels = after.Select(a => a.Label).ToHashSet();
        return new(
            after.Where(a => !oldByLabel.ContainsKey(a.Label)).Select(a => a.Label).ToList(),
            old.Where(a => !newLabels.Contains(a.Label)).Select(a => a.Label).ToList(),
            after.Where(a => oldByLabel.TryGetValue(a.Label, out var o) && (o.Heading != a.Heading || o.Body != a.Body || o.Chapter != a.Chapter))
                .Select(a => a.Label).ToList());
    }

    private sealed record FolderFile(string Path, byte[] Bytes, string Hash)
    {
        private (string Text, string Encoding)? _decoded;
        private (string Text, string Encoding) Decoded => _decoded ??= TextDecoder.Decode(Bytes);

        public string Encoding => Decoded.Encoding;
        public ParsedDocument Parse() => RuleDocumentParser.Parse(Path, Decoded.Text);
    }

    private static async Task<List<FolderFile>> ReadFolderAsync(string root, CancellationToken ct)
    {
        var files = new List<FolderFile>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(f => Extensions.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                     .Order(StringComparer.Ordinal))
        {
            var bytes = await File.ReadAllBytesAsync(file, ct);
            files.Add(new(System.IO.Path.GetRelativePath(root, file).Replace('\\', '/'), bytes, Convert.ToHexString(SHA256.HashData(bytes))));
        }
        return files;
    }

    /// <summary>
    /// Among documents with the same base title, the one with the latest date that is not marked as abolished is current.
    /// Documents marked as abolished or old are never current.
    /// </summary>
    private static async Task MarkSupersededAsync(RuleQaDbContext db, CancellationToken ct)
    {
        var documents = await db.Documents.ToListAsync(ct);
        foreach (var group in documents.GroupBy(d => d.BaseTitle))
        {
            var current = group.Where(d => !d.IsAbolished)
                .OrderByDescending(d => d.EffectiveDate ?? DateOnly.MinValue)
                .ThenBy(d => d.Path, StringComparer.Ordinal)
                .FirstOrDefault();
            foreach (var d in group)
            {
                d.IsSuperseded = d != current;
                d.SupersededBy = d.IsSuperseded ? current?.Path : null;
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
