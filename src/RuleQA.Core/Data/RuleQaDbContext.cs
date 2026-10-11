using Microsoft.EntityFrameworkCore;
using RuleQA.Core.Domain;

namespace RuleQA.Core.Data;

public sealed class RuleQaDbContext(DbContextOptions<RuleQaDbContext> options) : DbContext(options)
{
    public DbSet<RuleDocument> Documents => Set<RuleDocument>();
    public DbSet<Article> Articles => Set<Article>();
    public DbSet<QaRecord> QaRecords => Set<QaRecord>();
    public DbSet<ImportLog> ImportLogs => Set<ImportLog>();
    public DbSet<EvaluationRun> EvaluationRuns => Set<EvaluationRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RuleDocument>(e =>
        {
            e.HasIndex(d => d.Path).IsUnique();
            e.Property(d => d.Kind).HasConversion<string>();
            e.Ignore(d => d.Key);
            e.HasMany(d => d.Articles).WithOne(a => a.Document).HasForeignKey(a => a.RuleDocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Article>(e =>
        {
            e.HasIndex(a => a.Key).IsUnique();
            e.Ignore(a => a.DisplayName);
            e.Ignore(a => a.EmbeddingText);
        });

        modelBuilder.Entity<QaRecord>(e =>
        {
            e.HasIndex(r => r.ConversationId);
            e.HasIndex(r => r.AskedAtUtc);
            e.Property(r => r.Status).HasConversion<string>();
            e.Property(r => r.Feedback).HasConversion<string>();
        });

        modelBuilder.Entity<EvaluationRun>(e =>
        {
            e.HasIndex(r => r.AtUtc);
            e.Property(r => r.Kind).HasConversion<string>();
        });

        modelBuilder.Entity<ImportLog>(e =>
        {
            e.HasIndex(l => l.AtUtc);
            e.Property(l => l.Mode).HasConversion<string>();
            e.Ignore(l => l.ModeLabel);
        });
    }
}
