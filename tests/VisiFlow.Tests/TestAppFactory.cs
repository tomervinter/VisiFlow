using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VisiFlow.Data;

namespace VisiFlow.Tests;

/// <summary>
/// Boots the real VisiFlow.Api Program.cs app (in-process, via WebApplicationFactory) against a fresh
/// in-memory SQLite database instead of the app's normal SQLite-file/Postgres selection.
///
/// Program.cs's own startup code always runs (we don't/can't touch Program.cs): since DATABASE_URL is
/// unset in the test process, it takes the "usingPostgres == false" branch, which calls
/// db.Database.Migrate() against whatever VisiFlowDbContext is registered - this override just swaps
/// that registration for one pointing at our own already-open SQLite connection, so Program.cs's own
/// Migrate() call builds the real schema (via the app's real EF migrations) on our private in-memory
/// database instead of touching the real data/visiflow.db file.
///
/// Forces the "Development" environment so Program.cs's cookie setup
/// (options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.None :
/// CookieSecurePolicy.Always) leaves the auth cookie non-Secure - WebApplicationFactory's default
/// HttpClient talks to the TestServer over a plain "http://localhost" base address, and a Secure
/// cookie would silently never be sent back on subsequent requests, breaking every login-then-call
/// test in a very confusing way.
///
/// One instance per test (constructed directly in each test method, not shared via IClassFixture) so
/// every test gets a fully isolated database - no cross-test bleed from shared users/companies.
/// </summary>
public sealed class TestAppFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection;

    public TestAppFactory()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<VisiFlowDbContext>));
            if (descriptor != null) services.Remove(descriptor);
            services.AddDbContext<VisiFlowDbContext>(options => options.UseSqlite(_connection));
        });
    }

    /// <summary>Convenience for seeding directly against the same database the running app uses -
    /// forces the host (and therefore Program.cs's startup migration) to finish building first.</summary>
    public async Task SeedAsync(Func<VisiFlowDbContext, Task> seed)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VisiFlowDbContext>();
        await seed(db);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }
}
