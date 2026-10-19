using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using VisiFlow.Data.Entities;
using VisiFlow.Data.Enums;
using Xunit;

namespace VisiFlow.Tests;

/// <summary>
/// Pending-check (צ'קים ממתינים) upload: full-replace semantics, the Preview (dry-run) mode that makes a
/// destructive upload visible before it happens, tenant isolation, and that the agent endpoints actually
/// surface the flag.
/// </summary>
public class PendingChecksTests
{
    private record ImportResult(bool Preview, int Added, int Removed, int Kept, int Unknown);
    private record AgentRow(string CustomerNumber, DateTime? PendingCheckUpdatedAt);

    private static byte[] BuildXlsx(params string[] customerNumbers)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("checks");
        ws.Cell(1, 1).Value = "מספר לקוח";
        for (var i = 0; i < customerNumbers.Length; i++) ws.Cell(i + 2, 1).Value = customerNumbers[i];
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static MultipartFormDataContent Upload(int companyId, byte[] xlsx, bool preview)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(companyId.ToString()), "CompanyId" },
            { new StringContent(preview ? "true" : "false"), "Preview" },
            { new ByteArrayContent(xlsx), "file", "checks.xlsx" }
        };
        return content;
    }

    private static async Task<(int CompanyA, int CompanyB)> SeedAsync(TestAppFactory factory)
    {
        int a = 0, b = 0;
        await factory.SeedAsync(async db =>
        {
            var ca = new Company { Name = "A" };
            var cb = new Company { Name = "B" };
            db.Companies.AddRange(ca, cb);
            await db.SaveChangesAsync();
            a = ca.Id; b = cb.Id;

            var user = new User { CompanyId = ca.Id, Username = "usera", DisplayName = "A", CreatedAt = DateTime.UtcNow };
            user.PasswordHash = TestHelpers.HashPassword(user, "Password123");
            db.Users.Add(user);

            foreach (var n in new[] { "1", "2", "3" })
                db.Customers.Add(new Customer { CompanyId = ca.Id, CustomerNumber = n, Year = 2026, Month = 3, CustomerName = $"C{n}", AgentIdNumber = "AG1", Status = CustomerStatus.Active });
            db.PendingChecks.Add(new PendingCheck { CompanyId = ca.Id, CustomerNumber = "1", UpdatedAt = DateTime.UtcNow.AddDays(-3) });
            db.PendingChecks.Add(new PendingCheck { CompanyId = ca.Id, CustomerNumber = "2", UpdatedAt = DateTime.UtcNow.AddDays(-3) });
            await db.SaveChangesAsync();
        });
        return (a, b);
    }

    private static async Task<HttpClient> LoginAsync(TestAppFactory factory)
    {
        var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/login", new { Username = "usera", Password = "Password123" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return client;
    }

    [Fact]
    public async Task Preview_ReportsCountsButWritesNothing()
    {
        using var factory = new TestAppFactory();
        var (a, _) = await SeedAsync(factory);
        var client = await LoginAsync(factory);

        // Existing: {1,2}. File: {2,3,99}. -> add 3 and 99, remove 1, keep 2; 99 matches no customer.
        var res = await client.PostAsync("/api/customers/pendingchecks/import", Upload(a, BuildXlsx("2", "3", "99"), preview: true));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var result = await res.Content.ReadFromJsonAsync<ImportResult>();
        Assert.True(result!.Preview);
        Assert.Equal(2, result.Added);
        Assert.Equal(1, result.Removed);
        Assert.Equal(1, result.Kept);
        Assert.Equal(1, result.Unknown);

        await factory.SeedAsync(async db =>
        {
            var numbers = await db.PendingChecks.Where(p => p.CompanyId == a).Select(p => p.CustomerNumber).OrderBy(n => n).ToListAsync();
            Assert.Equal(new[] { "1", "2" }, numbers); // untouched
        });
    }

    [Fact]
    public async Task Apply_FullyReplacesTheList()
    {
        using var factory = new TestAppFactory();
        var (a, _) = await SeedAsync(factory);
        var client = await LoginAsync(factory);

        var res = await client.PostAsync("/api/customers/pendingchecks/import", Upload(a, BuildXlsx("2", "3"), preview: false));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var numbers = await db.PendingChecks.Where(p => p.CompanyId == a).Select(p => p.CustomerNumber).OrderBy(n => n).ToListAsync();
            Assert.Equal(new[] { "2", "3" }, numbers); // 1 was collected (absent from file) -> gone
            // A kept customer's UpdatedAt is refreshed - the upload re-confirmed it's still pending.
            var kept = await db.PendingChecks.SingleAsync(p => p.CompanyId == a && p.CustomerNumber == "2");
            Assert.True(kept.UpdatedAt > DateTime.UtcNow.AddMinutes(-5));
        });
    }

    [Fact]
    public async Task HeaderOnlyFile_ClearsEverything()
    {
        using var factory = new TestAppFactory();
        var (a, _) = await SeedAsync(factory);
        var client = await LoginAsync(factory);

        var res = await client.PostAsync("/api/customers/pendingchecks/import", Upload(a, BuildXlsx(), preview: false));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        await factory.SeedAsync(async db => Assert.Empty(await db.PendingChecks.Where(p => p.CompanyId == a).ToListAsync()));
    }

    [Fact]
    public async Task FileWithoutTheCustomerNumberColumn_IsRejectedAndChangesNothing()
    {
        using var factory = new TestAppFactory();
        var (a, _) = await SeedAsync(factory);
        var client = await LoginAsync(factory);

        byte[] wrong;
        using (var wb = new XLWorkbook())
        {
            wb.Worksheets.Add("x").Cell(1, 1).Value = "שם אחר";
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            wrong = ms.ToArray();
        }
        var res = await client.PostAsync("/api/customers/pendingchecks/import", Upload(a, wrong, preview: false));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        await factory.SeedAsync(async db => Assert.Equal(2, await db.PendingChecks.CountAsync(p => p.CompanyId == a)));
    }

    [Fact]
    public async Task OtherCompany_IsForbiddenForBothReadAndImport()
    {
        using var factory = new TestAppFactory();
        var (_, b) = await SeedAsync(factory);
        var client = await LoginAsync(factory);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/customers/pendingchecks?companyId={b}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsync("/api/customers/pendingchecks/import", Upload(b, BuildXlsx("1"), preview: false))).StatusCode);
    }

    [Fact]
    public async Task AgentEndpoint_SurfacesThePendingCheckFlagOnlyForAffectedCustomers()
    {
        using var factory = new TestAppFactory();
        var (a, _) = await SeedAsync(factory);
        var client = await LoginAsync(factory);

        var gen = await client.PostAsJsonAsync("/api/visitplan/generate", new { CompanyId = a, Year = 2026, Month = 3, ConfirmClearExisting = false });
        Assert.Equal(HttpStatusCode.OK, gen.StatusCode);

        // The agent app has no login - a fresh client with no cookie, like the real thing.
        var agentClient = factory.CreateClient();
        var rows = await agentClient.GetFromJsonAsync<List<AgentRow>>("/api/agent/visitplan/range?agentIdNumber=AG1&startDate=2026-03-01&endDate=2026-03-31");
        Assert.NotNull(rows);
        Assert.NotEmpty(rows!);
        Assert.NotNull(rows.First(r => r.CustomerNumber == "1").PendingCheckUpdatedAt);
        Assert.NotNull(rows.First(r => r.CustomerNumber == "2").PendingCheckUpdatedAt);
        Assert.Null(rows.First(r => r.CustomerNumber == "3").PendingCheckUpdatedAt);
    }

    [Fact]
    public async Task AgentCollected_RemovesTheCheck_OnlyForTheAssignedAgent()
    {
        using var factory = new TestAppFactory();
        var (a, _) = await SeedAsync(factory);
        var agentClient = factory.CreateClient(); // no login, like the real agent app

        var wrongAgent = await agentClient.PostAsJsonAsync("/api/agent/pendingchecks/collected", new { AgentIdNumber = "OTHER", CompanyId = a, CustomerNumber = "1" });
        Assert.Equal(HttpStatusCode.Forbidden, wrongAgent.StatusCode);
        await factory.SeedAsync(async db => Assert.Equal(2, await db.PendingChecks.CountAsync(p => p.CompanyId == a)));

        var ok = await agentClient.PostAsJsonAsync("/api/agent/pendingchecks/collected", new { AgentIdNumber = "AG1", CompanyId = a, CustomerNumber = "1" });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        // Second tap (or admin upload already removed it) is still a success.
        var again = await agentClient.PostAsJsonAsync("/api/agent/pendingchecks/collected", new { AgentIdNumber = "AG1", CompanyId = a, CustomerNumber = "1" });
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var left = await db.PendingChecks.Where(p => p.CompanyId == a).Select(p => p.CustomerNumber).ToListAsync();
            Assert.Equal(new[] { "2" }, left);
            Assert.Equal(1, await db.AuditLogEntries.CountAsync(x => x.CompanyId == a && x.Action == "צ'ק סומן כנאסף"));
        });
    }
}
