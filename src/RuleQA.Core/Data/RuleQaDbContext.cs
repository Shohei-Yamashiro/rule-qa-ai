using Microsoft.EntityFrameworkCore;
using RuleQA.Core.Domain;

namespace RuleQA.Core.Data;

public sealed class RuleQaDbContext(DbContextOptions<RuleQaDbContext> options) : DbContext(options)
{
    public DbSet<RuleDocument> Documents => Set<RuleDocument>();
    public DbSet<Article> Articles => Set<Article>();

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
    }
}
