using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace RuleQA.Core.Data;

/// <summary>
/// The app uses EnsureCreated instead of migrations, which never changes an existing database.
/// This adds the tables, columns and indexes that newer versions define, so data from older versions is kept.
/// Only additions are supported; renamed or removed columns are left as they are.
/// </summary>
public static class SchemaUpgrader
{
    public static void EnsureSchema(this RuleQaDbContext db)
    {
        if (db.Database.EnsureCreated()) return;

        var model = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var create = db.GetService<IMigrationsModelDiffer>().GetDifferences(null, model);
        var existing = ExistingColumns(db);

        var operations = new List<MigrationOperation>();
        foreach (var table in create.OfType<CreateTableOperation>())
        {
            if (!existing.TryGetValue(table.Name, out var columns))
            {
                operations.Add(table);
                operations.AddRange(create.OfType<CreateIndexOperation>().Where(i => i.Table == table.Name));
                continue;
            }
            foreach (var column in table.Columns.Where(c => !columns.Contains(c.Name)))
            {
                column.Table = table.Name;
                if (!column.IsNullable && column.DefaultValue is null && column.DefaultValueSql is null)
                    column.DefaultValue = column.ClrType == typeof(string) ? "" : Activator.CreateInstance(column.ClrType);
                operations.Add(column);
            }
        }
        if (operations.Count == 0) return;

        var commands = db.GetService<IMigrationsSqlGenerator>().Generate(operations, db.GetService<IDesignTimeModel>().Model);
        foreach (var command in commands) db.Database.ExecuteSqlRaw(command.CommandText);
    }

    private static Dictionary<string, HashSet<string>> ExistingColumns(RuleQaDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) connection.Open();
        try
        {
            var tables = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
                using var reader = command.ExecuteReader();
                while (reader.Read()) tables.Add(reader.GetString(0));
            }
            var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in tables)
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT name FROM pragma_table_info('{table.Replace("'", "''")}')";
                using var reader = command.ExecuteReader();
                var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (reader.Read()) columns.Add(reader.GetString(0));
                result[table] = columns;
            }
            return result;
        }
        finally
        {
            if (opened) connection.Close();
        }
    }
}
