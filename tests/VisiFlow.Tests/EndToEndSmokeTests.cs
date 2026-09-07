using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using VisiFlow.Data.Entities;
using VisiFlow.Data.Enums;
using Xunit;

namespace VisiFlow.Tests;

/// <summary>
/// End-to-end smoke test through the real HTTP surface: login -> seed a customer directly in the DB
/// (simulating an already-uploaded monthly customer file) -> call POST /api/visitplan/generate -> the
/// resulting VisitPlanEntries table for that company/month is non-empty and actually has a scheduled
/// (non-null PlannedDate) entry.
/// </summary>
public class EndToEndSmokeTests
{
    private const int Year = 2026;
    private const int Month = 3;

    [Fact]
    public async Task Login_ThenGenerateVisitPlan_ProducesNonEmptyPlanForCompanyAndMonth()
    {
        using var factory = new TestAppFactory();
        var companyId = 0;

        await factory.SeedAsync(async db =>
        {
            var company = new Company { Name = "Smoke Co" };
            db.Companies.Add(company);
            await db.SaveChangesAsync();
            companyId = company.Id;

            var user = new User { CompanyId = company.Id, Username = "smokeuser", DisplayName = "Smoke User", CreatedAt = DateTime.UtcNow };
            user.PasswordHash = TestHelpers.HashPassword(user, "Password123");
            db.Users.Add(user);

            db.Customers.Add(new Customer
            {
                CompanyId = company.Id, CustomerNumber = "SMOKE-1", Year = Year, Month = Month,
                CustomerName = "Smoke Customer", AgentIdNumber = "AG1", Status = CustomerStatus.Active
            });
            await db.SaveChangesAsync();
        });

        var client = factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new { Username = "smokeuser", Password = "Password123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var generate = await client.PostAsJsonAsync("/api/visitplan/generate", new { CompanyId = companyId, Year, Month, ConfirmClearExisting = false });
        Assert.Equal(HttpStatusCode.OK, generate.StatusCode);
        var result = await generate.Content.ReadFromJsonAsync<VisitPlanGenerationResult>();
        Assert.NotNull(result);
        Assert.True(result!.Scheduled >= 1, "expected at least the one seeded customer to be scheduled");

        await factory.SeedAsync(async db =>
        {
            var entries = await db.VisitPlanEntries
                .Where(e => e.CompanyId == companyId && e.PlanYear == Year && e.PlanMonth == Month)
                .ToListAsync();
            Assert.NotEmpty(entries);
            Assert.Contains(entries, e => e.PlannedDate != null);
        });
    }
}
