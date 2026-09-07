using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VisiFlow.Data;

namespace VisiFlow.Tests;

/// <summary>
/// Opens a real SQLite connection to ":memory:" and keeps it open for the fixture's lifetime (an
/// in-memory SQLite database is destroyed the instant its only connection closes), then builds the
/// schema directly from the EF model via EnsureCreated().
///
/// Deliberately NOT the EF Core InMemory provider: VisiFlow relies on real SQL semantics elsewhere in
/// the codebase (e.g. the unique index on (CompanyId, CustomerNumber, Year, Month) for Customers, and
/// the unique index on Username) that the InMemory provider does not actually enforce, which would
/// hide real bugs. A real SQLite engine enforces them exactly like the app's production SQLite file
/// does.
///
/// One instance per test (xunit constructs a fresh test class instance per [Fact]/[Theory] by
/// default), so every test gets its own isolated database with no cross-test bleed.
/// </summary>
public sealed class SqliteDbFixture : IDisposable
{
    private readonly SqliteConnection _connection;
    public VisiFlowDbContext Db { get; }

    public SqliteDbFixture()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<VisiFlowDbContext>().UseSqlite(_connection).Options;
        Db = new VisiFlowDbContext(options);
        Db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }
}
