using Microsoft.EntityFrameworkCore;

namespace DurianRoute.Api.Data;

/// <summary>
/// Adds columns introduced after a database was first created. <c>EnsureCreated</c> builds new
/// databases with the full current schema but never alters existing ones, so each change here is
/// idempotent and runs on every start. Moving to EF Core migrations would replace this class.
/// </summary>
public static class SchemaUpgrader
{
    private static readonly (string Table, string Column, string SqlServerType, string SqliteType)[] Columns =
    [
        ("LaneRecommendations", "Source", "int NOT NULL DEFAULT 0", "INTEGER NOT NULL DEFAULT 0"),
        ("LaneRecommendations", "RequestedBy", "nvarchar(64) NULL", "TEXT NULL")
    ];

    public static async Task UpgradeAsync(DurianDbContext db, ILogger logger, CancellationToken ct = default)
    {
        foreach (var (table, column, sqlServerType, sqliteType) in Columns)
        {
            if (await ColumnExistsAsync(db, table, column, ct)) continue;

            // Identifiers come from the constant list above, never from user input.
            var sql = db.Database.IsSqlServer()
                ? $"ALTER TABLE [{table}] ADD [{column}] {sqlServerType}"
                : $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {sqliteType}";
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync(sql, ct);
#pragma warning restore EF1002
            logger.LogInformation("Schema upgrade: added {Table}.{Column}", table, column);
        }
    }

    private static async Task<bool> ColumnExistsAsync(DurianDbContext db, string table, string column, CancellationToken ct)
    {
        var count = db.Database.IsSqlServer()
            ? await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = {table} AND COLUMN_NAME = {column}").SingleAsync(ct)
            : await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM pragma_table_info({table}) WHERE name = {column}").SingleAsync(ct);
        return count > 0;
    }
}
