using VisiFlow.Data.Entities;
using VisiFlow.Data.Enums;
using Xunit;

namespace VisiFlow.Tests;

/// <summary>
/// Exercises VisitPlanGenerator.GenerateAsync directly against a real (SQLite) VisiFlowDbContext -
/// see VisitPlanGeneratorTests's sibling SqliteDbFixture for why a real SQL engine is used instead of
/// the EF Core InMemory provider.
///
/// All tests use March 2026 (year=2026, month=3): IsraeliHolidays.cs has no entries for that month, so
/// the work calendar is the plain Sun-Thu(Full)/Fri(Half)/Sat(Off) default with nothing to override.
/// </summary>
public class VisitPlanGeneratorTests : IDisposable
{
    private const int Year = 2026;
    private const int Month = 3;
    private readonly SqliteDbFixture _fixture = new();
    private VisiFlow.Data.VisiFlowDbContext Db => _fixture.Db;

    public void Dispose() => _fixture.Dispose();

    private async Task<Company> SeedCompanyAsync()
    {
        var company = new Company { Name = "Test Co" };
        Db.Companies.Add(company);
        await Db.SaveChangesAsync();
        return company;
    }

    [Fact]
    public async Task GenerateAsync_CustomerWithMultipleDistributionDaysInSameWeek_NeverScheduledTwiceInOneWeek()
    {
        var company = await SeedCompanyAsync();
        const string customerNumber = "C-MULTI";

        Db.Customers.Add(new Customer
        {
            CompanyId = company.Id, CustomerNumber = customerNumber, Year = Year, Month = Month,
            CustomerName = "Multi Distribution Customer", AgentIdNumber = "AG1", Status = CustomerStatus.Active
        });
        // Two distribution days in the same week (Sunday AND Wednesday) - the generator must still
        // only ever propose ONE target date per (Sun-Sat) week for this customer, no matter how many
        // of that week's days are delivery days.
        Db.CustomerDistributionDays.Add(new CustomerDistributionDay
        {
            CompanyId = company.Id, CustomerNumber = customerNumber, Year = Year, Month = Month,
            Sunday = true, Wednesday = true
        });
        // A visit standard of 2/week pushes the monthly target well above the number of weeks in the
        // month, so the only thing that can be limiting the actual number of visits placed is the
        // "never twice in the same week" rule being enforced, not this test accidentally asking for
        // too few visits to ever exercise it.
        Db.CustomerVisitStandards.Add(new CustomerVisitStandard
        {
            CompanyId = company.Id, CustomerNumber = customerNumber, RequiredVisitsPerWeek = 2m, UpdatedAt = DateTime.UtcNow
        });
        await Db.SaveChangesAsync();

        var result = await VisitPlanGenerator.GenerateAsync(Db, company.Id, Year, Month);

        var entries = Db.VisitPlanEntries
            .Where(e => e.CompanyId == company.Id && e.CustomerNumber == customerNumber && e.PlannedDate != null)
            .ToList();

        Assert.NotEmpty(entries);
        Assert.Equal(entries.Count, result.Scheduled);

        var byWeek = entries.GroupBy(e => TestHelpers.WeekStart(e.PlannedDate!.Value)).ToList();
        Assert.Equal(entries.Count, byWeek.Count); // one week per entry -> no week has more than one
        Assert.All(byWeek, g => Assert.Single(g));
    }

    [Fact]
    public async Task GenerateAsync_MoreCustomersThanDailyCapacity_LeavesExcessUnscheduledRatherThanOverflowing()
    {
        var company = await SeedCompanyAsync();

        // Deliberately tiny daily capacity so the total capacity across the whole month is smaller
        // than the number of customers competing for it.
        Db.VisitPlanWeights.Add(new VisitPlanWeights { CompanyId = company.Id, FullDayCapacity = 1, HalfDayCapacity = 1 });

        const int customerCount = 35;
        for (var i = 0; i < customerCount; i++)
        {
            Db.Customers.Add(new Customer
            {
                CompanyId = company.Id, CustomerNumber = $"C-{i:000}", Year = Year, Month = Month,
                CustomerName = $"Customer {i}", AgentIdNumber = "AG1", Status = CustomerStatus.Active
                // No distribution days, no visit standard -> a single, no-preference monthly request
                // each, placed wherever the greedy scan finds room first.
            });
        }
        await Db.SaveChangesAsync();

        var result = await VisitPlanGenerator.GenerateAsync(Db, company.Id, Year, Month);

        // Replicate the generator's own day-type default (no calendar overrides/holidays exist for
        // March 2026) to compute exactly how many slots this one agent has all month, independent of
        // the generator's internals.
        var daysInMonth = DateTime.DaysInMonth(Year, Month);
        var totalCapacity = 0;
        for (var d = new DateTime(Year, Month, 1); d < new DateTime(Year, Month, 1).AddDays(daysInMonth); d = d.AddDays(1))
        {
            totalCapacity += d.DayOfWeek switch { DayOfWeek.Saturday => 0, DayOfWeek.Friday => 1, _ => 1 };
        }

        Assert.True(totalCapacity < customerCount, "test setup assumption: capacity must be smaller than demand");
        Assert.Equal(totalCapacity, result.Scheduled);
        Assert.Equal(customerCount - totalCapacity, result.Unscheduled);
        Assert.True(result.Unscheduled > 0);

        var scheduledCount = Db.VisitPlanEntries.Count(e => e.CompanyId == company.Id && e.PlannedDate != null);
        var unscheduledCount = Db.VisitPlanEntries.Count(e => e.CompanyId == company.Id && e.PlannedDate == null);
        Assert.Equal(result.Scheduled, scheduledCount);
        Assert.Equal(result.Unscheduled, unscheduledCount);
    }

    [Fact]
    public async Task GenerateAsync_FiveFactorsCombineIntoWeightedZeroToHundredPriorityScore()
    {
        var company = await SeedCompanyAsync();
        // Default weights (20/20/20/20/20, summing to 100) - the generator auto-creates these when
        // none exist yet for the company.

        // "Hi": worst on every factor (biggest sales drop, highest real purchase frequency, highest
        // visit standard, longest since last visit, has a distribution day) - within a 2-customer
        // agent group this normalizes to exactly 1.0 on all five components, so its combined score
        // should land at exactly the weight total: 100.0.
        Db.Customers.Add(new Customer
        {
            CompanyId = company.Id, CustomerNumber = "C-HI", Year = Year, Month = Month,
            CustomerName = "Hi Priority", AgentIdNumber = "AG1", Status = CustomerStatus.Active,
            SalesYtdPreviousYear = 100000m, SalesYtdCurrentYear = 0m, // max sales drop
            AvgMonthlyOrders = 50m // max real purchase frequency
        });
        Db.CustomerDistributionDays.Add(new CustomerDistributionDay
        {
            CompanyId = company.Id, CustomerNumber = "C-HI", Year = Year, Month = Month, Sunday = true
        });
        Db.CustomerVisitStandards.Add(new CustomerVisitStandard
        {
            CompanyId = company.Id, CustomerNumber = "C-HI", RequiredVisitsPerWeek = 5m, UpdatedAt = DateTime.UtcNow
        });
        // Never visited -> DaysSince sentinel (9999), which always scores as maximally overdue.

        // "Lo": best (least urgent) on every factor - no sales drop, no real purchases, no visit
        // standard, visited today, no distribution day - normalizes to exactly 0.0 on all five
        // components, so its combined score should land at exactly 0.0.
        Db.Customers.Add(new Customer
        {
            CompanyId = company.Id, CustomerNumber = "C-LO", Year = Year, Month = Month,
            CustomerName = "Lo Priority", AgentIdNumber = "AG1", Status = CustomerStatus.Active,
            SalesYtdPreviousYear = 0m, SalesYtdCurrentYear = 0m,
            AvgMonthlyOrders = 0m
        });
        Db.CustomerVisits.Add(new CustomerVisit
        {
            CompanyId = company.Id, CustomerNumber = "C-LO", VisitDate = DateTime.Today,
            Outcome = VisitOutcome.Visited, CreatedAt = DateTime.UtcNow
        });
        await Db.SaveChangesAsync();

        await VisitPlanGenerator.GenerateAsync(Db, company.Id, Year, Month);

        var hi = Db.VisitPlanEntries.First(e => e.CompanyId == company.Id && e.CustomerNumber == "C-HI");
        var lo = Db.VisitPlanEntries.First(e => e.CompanyId == company.Id && e.CustomerNumber == "C-LO");

        Assert.Equal(1m, hi.SalesDropScore);
        Assert.Equal(1m, hi.FrequencyScore);
        Assert.Equal(1m, hi.VisitStandardScore);
        Assert.Equal(1m, hi.DaysSinceVisitScore);
        Assert.Equal(1m, hi.DistributionScore);
        Assert.Equal(100.0m, hi.PriorityScore);

        Assert.Equal(0m, lo.SalesDropScore);
        Assert.Equal(0m, lo.FrequencyScore);
        Assert.Equal(0m, lo.VisitStandardScore);
        Assert.Equal(0m, lo.DaysSinceVisitScore);
        Assert.Equal(0m, lo.DistributionScore);
        Assert.Equal(0.0m, lo.PriorityScore);

        // The plan is ranked by PriorityScore - a sane combination must at least keep Hi strictly
        // ahead of Lo.
        Assert.True(hi.PriorityScore > lo.PriorityScore);
    }

    // Regression coverage for a real bug report: a customer's distribution day landing on the 1st (or
    // 2nd) of the month has no earlier working day left to target "2 days before delivery" WITHIN that
    // month, so the generator used to fall back to scheduling the visit on the delivery day itself -
    // silently violating the "visit N days before delivery" rule for exactly the customers whose
    // delivery is earliest in the month. March 1, 2026 is a Sunday (and, like February 2026, has no
    // IsraeliHolidays entries - see this file's header comment), so a Sunday-only distribution customer
    // reproduces the exact shape of the reported case without depending on real holiday data.

    [Fact]
    public async Task GenerateAsync_DistributionOccurrenceOnFirstOfMonth_SpillsVisitIntoPreviousMonth()
    {
        var company = await SeedCompanyAsync();
        const string customerNumber = "C-EDGE";

        Db.Customers.Add(new Customer
        {
            CompanyId = company.Id, CustomerNumber = customerNumber, Year = Year, Month = Month,
            CustomerName = "First Of Month Customer", AgentIdNumber = "AG1", Status = CustomerStatus.Active
        });
        Db.CustomerDistributionDays.Add(new CustomerDistributionDay
        {
            CompanyId = company.Id, CustomerNumber = customerNumber, Year = Year, Month = Month, Sunday = true
        });
        await Db.SaveChangesAsync();

        await VisitPlanGenerator.GenerateAsync(Db, company.Id, Year, Month);

        var entry = Db.VisitPlanEntries.Single(e => e.CompanyId == company.Id && e.CustomerNumber == customerNumber);
        // 2 days before Sunday March 1 is Friday February 27 - a valid (Half-capacity, not Off) working
        // day, so that's where the visit belongs, not March 1 (the delivery day itself).
        Assert.Equal(new DateTime(2026, 2, 27), entry.PlannedDate);
        // Still recorded as belonging to THIS month's plan/customer snapshot - only PlannedDate spills
        // backward, PlanYear/PlanMonth never do.
        Assert.Equal(Year, entry.PlanYear);
        Assert.Equal(Month, entry.PlanMonth);
    }

    [Fact]
    public async Task GenerateAsync_SpilloverDateAlreadyFullFromPriorMonthsPlan_FallsBackToDeliveryDayInsteadOfDoubleBooking()
    {
        var company = await SeedCompanyAsync();
        const string customerNumber = "C-EDGE2";
        const string agentId = "AG1";

        // Fill Friday February 27's entire capacity (4, the HalfDayCapacity default) with a real,
        // already-existing FEBRUARY plan for four other customers of the same agent - simulating that
        // last month's plan was already generated and genuinely has no room left that day.
        Db.VisitPlanWeights.Add(new VisitPlanWeights { CompanyId = company.Id }); // defaults: Full=8, Half=4
        for (var i = 0; i < 4; i++)
        {
            var priorCustomerNumber = $"C-FEB-{i}";
            Db.Customers.Add(new Customer
            {
                CompanyId = company.Id, CustomerNumber = priorCustomerNumber, Year = Year, Month = Month - 1,
                CustomerName = $"Feb Customer {i}", AgentIdNumber = agentId, Status = CustomerStatus.Active
            });
            Db.VisitPlanEntries.Add(new VisitPlanEntry
            {
                CompanyId = company.Id, PlanYear = Year, PlanMonth = Month - 1, CustomerNumber = priorCustomerNumber,
                PlannedDate = new DateTime(2026, 2, 27), AgentName = agentId, GeneratedAt = DateTime.UtcNow
            });
        }

        Db.Customers.Add(new Customer
        {
            CompanyId = company.Id, CustomerNumber = customerNumber, Year = Year, Month = Month,
            CustomerName = "First Of Month Customer 2", AgentIdNumber = agentId, Status = CustomerStatus.Active
        });
        Db.CustomerDistributionDays.Add(new CustomerDistributionDay
        {
            CompanyId = company.Id, CustomerNumber = customerNumber, Year = Year, Month = Month, Sunday = true
        });
        await Db.SaveChangesAsync();

        await VisitPlanGenerator.GenerateAsync(Db, company.Id, Year, Month);

        var entry = Db.VisitPlanEntries.Single(e => e.CompanyId == company.Id && e.CustomerNumber == customerNumber);
        // Feb 27 is already fully booked by February's own plan - must NOT be double-booked past
        // capacity, so this customer falls back to the delivery day itself (March 1) instead.
        Assert.Equal(new DateTime(2026, 3, 1), entry.PlannedDate);
    }
}
