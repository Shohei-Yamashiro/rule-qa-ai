using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RuleQA.Core.Data;
using RuleQA.Core.Domain;
using RuleQA.Core.Settings;

namespace RuleQA.Core.Rules;

public sealed record ImportResult(int Added, int Updated, int Unchanged, int Removed, int KeptVectors, IReadOnlyList<string> Warnings)
{
    public int Changed => Added + Updated + Removed;
}

public sealed record LibraryStats(int Documents, int Superseded, int References, int Articles, int Embedded);

/// <summary>Imports the .md and .txt files of a folder (including subfolders) and decides which versions are current.</summary>
public sealed class RuleImporter(IDbContextFactory<RuleQaDbContext> dbFactory, TimeProvider clock, string baseDirectory, ILogger<RuleImporter> logger)
{
    private static readonly string[] Extensions = [".md", ".txt"];

    public string ResolveFolder(string folder) =>
        Path.IsPathRooted(folder) ? folder : Path.GetFullPath(Path.Combine(baseDirectory, folder));

    /// <summary>Only files whose content changed are parsed again; vectors are kept for articles whose text did not change.</summary>
    public async Task<ImportResult> ImportFolderAsync(string folder, CancellationToken ct = default)
    {
        var root = ResolveFolder(folder);
        if (!Directory.Exists(root)) return new(0, 0, 0, 0, 0, [$"フォルダが見つかりません: {root}"]);

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Documents.Include(d => d.Articles).ToDictionaryAsync(d => d.Path, StringComparer.OrdinalIgnoreCase, ct);
        var warnings = new List<string>();
        int added = 0, updated = 0, unchanged = 0, kept = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var path = Path.GetRelativePath(root, file).Replace('\\', '/');
            seen.Add(path);
            var bytes = await File.ReadAllBytesAsync(file, ct);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var old = existing.GetValueOrDefault(path);
            if (old?.Hash == hash)
            {
                unchanged++;
                continue;
            }

            var (text, encoding) = TextDecoder.Decode(bytes);
            var parsed = RuleDocumentParser.Parse(path, text);
            warnings.AddRange(parsed.Warnings.Select(w => $"{path}: {w}"));

            var document = new RuleDocument
            {
                Path = path,
                Title = parsed.Title,
                BaseTitle = parsed.BaseTitle,
                Kind = parsed.Kind,
                EffectiveDate = parsed.EffectiveDate,
                IsAbolished = parsed.IsAbolished,
                Encoding = encoding,
                Hash = hash,
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
                updated++;
            }
            else added++;
            db.Documents.Add(document);
            await db.SaveChangesAsync(ct);
        }

        var removed = existing.Values.Where(d => !seen.Contains(d.Path)).ToList();
        db.Documents.RemoveRange(removed);
        await db.SaveChangesAsync(ct);

        await MarkSupersededAsync(db, ct);
        logger.LogInformation("Imported rules from {Folder}: added {Added}, updated {Updated}, unchanged {Unchanged}, removed {Removed}",
            root, added, updated, unchanged, removed.Count);
        return new(added, updated, unchanged, removed.Count, kept, warnings);
    }

    /// <summary>Imports the folder in the settings when nothing has been imported, so the samples work right after unzipping.</summary>
    public async Task ImportIfEmptyAsync(RulesSettings settings, CancellationToken ct = default)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            if (await db.Documents.AnyAsync(ct)) return;
        }
        if (Directory.Exists(ResolveFolder(settings.Folder))) await ImportFolderAsync(settings.Folder, ct);
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
            model.Length == 0 ? 0 : await db.Articles.CountAsync(a => a.Embedding != null && a.EmbeddingModel == model, ct));
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
