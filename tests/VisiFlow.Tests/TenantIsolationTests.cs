using System.Net;
using System.Net.Http.Json;
using VisiFlow.Data;
using VisiFlow.Data.Entities;
using Xunit;

namespace VisiFlow.Tests;

/// <summary>
/// Exercises the real tenant-isolation contract end-to-end through the actual HTTP endpoints (via
/// WebApplicationFactory&lt;Program&gt;) rather than unit-testing ResolveCallerAsync in isolation, so a
/// regression in either the auth cookie plumbing or the per-endpoint company check would actually be
/// caught.
///
/// Regression coverage: /api/users once returned every company's users to a super-admin caller without
/// filtering by the requested companyId. GetUsers_SuperAdmin_ScopedToRequestedCompanyOnly is the
/// assertion that would have caught that class of bug.
/// </summary>
public class TenantIsolationTests
{
    private record UserRow(int Id, int CompanyId, string Username);
    private record CompanyRow(int Id, string Name, bool IsActive);

    private static async Task<HttpClient> LoginAsync(TestAppFactory factory, string username, string password)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { Username = username, Password = password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<(int CompanyAId, int CompanyBId)> SeedTwoCompaniesAsync(TestAppFactory factory)
    {
        int companyAId = 0, companyBId = 0;
        await factory.SeedAsync(async db =>
        {
            var companyA = new Company { Name = "Company A" };
            var companyB = new Company { Name = "Company B" };
            db.Companies.AddRange(companyA, companyB);
            await db.SaveChangesAsync();
            companyAId = companyA.Id;
            companyBId = companyB.Id;

            var userA = new User { CompanyId = companyA.Id, Username = "usera", DisplayName = "User A", CreatedAt = DateTime.UtcNow };
            userA.PasswordHash = TestHelpers.HashPassword(userA, "Password123");
            var userB = new User { CompanyId = companyB.Id, Username = "userb", DisplayName = "User B", CreatedAt = DateTime.UtcNow };
            userB.PasswordHash = TestHelpers.HashPassword(userB, "Password123");
            var userB2 = new User { CompanyId = companyB.Id, Username = "userb2", DisplayName = "User B2", CreatedAt = DateTime.UtcNow };
            userB2.PasswordHash = TestHelpers.HashPassword(userB2, "Password123");
            var superAdmin = new User { CompanyId = companyA.Id, Username = "superadmin", DisplayName = "Super Admin", CreatedAt = DateTime.UtcNow, IsSuperAdmin = true };
            superAdmin.PasswordHash = TestHelpers.HashPassword(superAdmin, "Password123");

            db.Users.AddRange(userA, userB, userB2, superAdmin);
            await db.SaveChangesAsync();
        });
        return (companyAId, companyBId);
    }

    [Fact]
    public async Task GetUsers_NonSuperAdmin_ForbiddenForOtherCompany()
    {
        using var factory = new TestAppFactory();
        var (companyAId, companyBId) = await SeedTwoCompaniesAsync(factory);
        var client = await LoginAsync(factory, "usera", "Password123");

        var response = await client.GetAsync($"/api/users?companyId={companyBId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetUsers_NonSuperAdmin_AllowedForOwnCompany()
    {
        using var factory = new TestAppFactory();
        var (companyAId, companyBId) = await SeedTwoCompaniesAsync(factory);
        var client = await LoginAsync(factory, "usera", "Password123");

        var response = await client.GetAsync($"/api/users?companyId={companyAId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var users = await response.Content.ReadFromJsonAsync<List<UserRow>>();
        Assert.NotNull(users);
        Assert.NotEmpty(users!);
        Assert.All(users!, u => Assert.Equal(companyAId, u.CompanyId));
    }

    /// <summary>Regression test for the real historical bug: GET /api/users returning every company's
    /// users to a super-admin instead of scoping to the requested companyId. A super-admin bypasses the
    /// "must be your own company" check, but the query result must still be scoped to exactly the
    /// companyId that was asked for.</summary>
    [Fact]
    public async Task GetUsers_SuperAdmin_ScopedToRequestedCompanyOnly()
    {
        using var factory = new TestAppFactory();
        var (companyAId, companyBId) = await SeedTwoCompaniesAsync(factory);
        var client = await LoginAsync(factory, "superadmin", "Password123");

        var response = await client.GetAsync($"/api/users?companyId={companyBId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var users = await response.Content.ReadFromJsonAsync<List<UserRow>>();
        Assert.NotNull(users);
        // Company B has 2 seeded users - if this came back as 1 (or as every user across both
        // companies), that's exactly the unfiltered-dump bug regressing.
        Assert.Equal(2, users!.Count);
        Assert.All(users!, u => Assert.Equal(companyBId, u.CompanyId));
        Assert.DoesNotContain(users!, u => u.CompanyId == companyAId);
    }

    [Fact]
    public async Task GetCompanies_NonSuperAdmin_OnlySeesOwnCompany()
    {
        using var factory = new TestAppFactory();
        var (companyAId, companyBId) = await SeedTwoCompaniesAsync(factory);
        var client = await LoginAsync(factory, "usera", "Password123");

        var response = await client.GetAsync("/api/companies");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var companies = await response.Content.ReadFromJsonAsync<List<CompanyRow>>();
        Assert.NotNull(companies);
        Assert.Single(companies!);
        Assert.Equal(companyAId, companies![0].Id);
    }

    [Fact]
    public async Task GetCompanies_SuperAdmin_SeesEveryCompany()
    {
        using var factory = new TestAppFactory();
        var (companyAId, companyBId) = await SeedTwoCompaniesAsync(factory);
        var client = await LoginAsync(factory, "superadmin", "Password123");

        var response = await client.GetAsync("/api/companies");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var companies = await response.Content.ReadFromJsonAsync<List<CompanyRow>>();
        Assert.NotNull(companies);
        Assert.Equal(2, companies!.Count);
        Assert.Contains(companies!, c => c.Id == companyAId);
        Assert.Contains(companies!, c => c.Id == companyBId);
    }
}
