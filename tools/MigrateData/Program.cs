using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using VisiFlow.Data;

// One-time copy of every row from the local SQLite database into a freshly-created Postgres database
// (Supabase), preserving original Ids, then resetting Postgres's identity sequences so new rows
// continue from the right number afterward. Not meant to be run more than once against the same
// Postgres target - re-running would violate the unique/PK constraints on rows already copied.
//
// Usage: dotnet run --project tools/MigrateData -- <sqlitePath> <postgresConnectionString>
// postgresConnectionString can be either a postgres:// URI (as Supabase gives it) or an already-
// formed Npgsql "Host=...;Username=...;..." string.

if (args.Length < 2)
{
    Console.WriteLine("Usage: dotnet run --project tools/MigrateData -- <sqlitePath> <postgresConnectionStringOrUrl>");
    return 1;
}

var sqlitePath = args[0];
var pgArg = args[1];
var pgConnectionString = pgArg.StartsWith("postgres://") || pgArg.StartsWith("postgresql://")
    ? NpgsqlConnectionStringFromUrl(pgArg)
    : pgArg;

if (!File.Exists(sqlitePath))
{
    Console.WriteLine($"SQLite file not found: {sqlitePath}");
    return 1;
}

using var sqliteDb = new VisiFlowDbContext(new DbContextOptionsBuilder<VisiFlowDbContext>()
    .UseSqlite($"Data Source={sqlitePath}").Options);
using var pgDb = new VisiFlowDbContext(new DbContextOptionsBuilder<VisiFlowDbContext>()
    .UseNpgsql(pgConnectionString).Options);

// EnsureCreated() only creates the schema when the DATABASE itself doesn't exist yet - against a
// managed host like Supabase, the "postgres" database always already exists (it's pre-provisioned),
// so EnsureCreated silently does nothing and every table would be missing. CreateTablesAsync() is the
// lower-level call that actually creates the tables from the current model regardless of whether the
// database existed - guarded so re-running this tool against an already-migrated target doesn't blow
// up on "relation already exists".
Console.WriteLine("Creating Postgres schema...");
try
{
    await pgDb.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>().CreateTablesAsync();
}
catch (PostgresException ex) when (ex.SqlState == "42P07") // relation already exists - fine, already created
{
    Console.WriteLine("Schema already exists, continuing.");
}

// Parent tables before the tables that reference them (Company first; CustomerVisit last since it
// references both Company and NonVisitReason). Also the order TRUNCATE runs in below, reversed.
var tableNames = new[]
{
    "Companies", "Users", "NonVisitReasons", "Customers", "CustomerVisitStandards",
    "CustomerDistributionDays", "WorkCalendarDays", "CustomerVisits", "VisitPlanWeights", "VisitPlanEntries"
};

// Makes this tool safely re-runnable against the same target (e.g. after a mid-run failure like the
// one this hit during development) instead of failing on duplicate-key on whatever table got copied
// last time - clears anything already there first, children before parents.
Console.WriteLine("Clearing any previously-copied rows...");
foreach (var t in tableNames.Reverse())
{
#pragma warning disable EF1002
    await pgDb.Database.ExecuteSqlRawAsync($"DELETE FROM \"{t}\"");
#pragma warning restore EF1002
}

await CopyTableAsync("Companies", sqliteDb.Companies, pgDb.Companies, pgDb);
await CopyTableAsync("Users", sqliteDb.Users, pgDb.Users, pgDb);
await CopyTableAsync("NonVisitReasons", sqliteDb.NonVisitReasons, pgDb.NonVisitReasons, pgDb);
await CopyTableAsync("Customers", sqliteDb.Customers, pgDb.Customers, pgDb);
await CopyTableAsync("CustomerVisitStandards", sqliteDb.CustomerVisitStandards, pgDb.CustomerVisitStandards, pgDb);
await CopyTableAsync("CustomerDistributionDays", sqliteDb.CustomerDistributionDays, pgDb.CustomerDistributionDays, pgDb);
await CopyTableAsync("WorkCalendarDays", sqliteDb.WorkCalendarDays, pgDb.WorkCalendarDays, pgDb);
await CopyTableAsync("CustomerVisits", sqliteDb.CustomerVisits, pgDb.CustomerVisits, pgDb);
await CopyTableAsync("VisitPlanWeights", sqliteDb.VisitPlanWeights, pgDb.VisitPlanWeights, pgDb);
await CopyTableAsync("VisitPlanEntries", sqliteDb.VisitPlanEntries, pgDb.VisitPlanEntries, pgDb);

Console.WriteLine("Done.");
return 0;

async Task CopyTableAsync<T>(string tableName, DbSet<T> source, DbSet<T> target, VisiFlowDbContext targetDb) where T : class
{
    var rows = await source.AsNoTracking().ToListAsync();
    if (rows.Count == 0) { Console.WriteLine($"{tableName}: 0 rows (skipped)"); return; }
    // SQLite doesn't track DateTimeKind at all - every DateTime read back from it comes out as
    // Unspecified, but Npgsql requires exactly Kind=Utc for a "timestamp with time zone" column (and
    // every DateTime in this app's model IS always UTC in practice - see the DateTime.UtcNow calls
    // throughout Program.cs). Re-tagging with SpecifyKind doesn't change the actual instant, just
    // marks it the way Npgsql needs.
    foreach (var prop in typeof(T).GetProperties())
    {
        if (prop.PropertyType == typeof(DateTime) && prop.CanWrite)
        {
            foreach (var row in rows)
            {
                var value = (DateTime)prop.GetValue(row)!;
                if (value.Kind == DateTimeKind.Unspecified) prop.SetValue(row, DateTime.SpecifyKind(value, DateTimeKind.Utc));
            }
        }
        else if (prop.PropertyType == typeof(DateTime?) && prop.CanWrite)
        {
            foreach (var row in rows)
            {
                var value = (DateTime?)prop.GetValue(row);
                if (value.HasValue && value.Value.Kind == DateTimeKind.Unspecified)
                    prop.SetValue(row, DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
            }
        }
    }
    target.AddRange(rows);
    await targetDb.SaveChangesAsync();
    // Postgres serial columns don't know about explicitly-inserted Id values - without this, the
    // next auto-generated Id would collide with one we just copied in. tableName is always one of
    // this file's own hardcoded literals above, never external input - a table/column name is a SQL
    // identifier, which can't be passed as a query parameter anyway, so raw interpolation is the only
    // option here regardless.
#pragma warning disable EF1002
    await targetDb.Database.ExecuteSqlRawAsync(
        $"SELECT setval(pg_get_serial_sequence('\"{tableName}\"', 'Id'), COALESCE((SELECT MAX(\"Id\") FROM \"{tableName}\"), 1))");
#pragma warning restore EF1002
    Console.WriteLine($"{tableName}: {rows.Count} rows copied");
}

static string NpgsqlConnectionStringFromUrl(string url)
{
    var uri = new Uri(url);
    var userInfo = uri.UserInfo.Split(':', 2);
    var csb = new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.Port > 0 ? uri.Port : 5432,
        Username = Uri.UnescapeDataString(userInfo[0]),
        Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "",
        Database = uri.AbsolutePath.TrimStart('/'),
        SslMode = SslMode.Require
    };
    return csb.ConnectionString;
}
