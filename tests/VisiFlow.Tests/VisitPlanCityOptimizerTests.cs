using VisiFlow.Data.Entities;
using VisiFlow.Data.Enums;
using Xunit;

namespace VisiFlow.Tests;

/// <summary>
/// Exercises VisitPlanCityOptimizer.OptimizeAsync directly against a real (SQLite) VisiFlowDbContext,
/// seeding VisitPlanEntry/Customer rows by hand (bypassing VisitPlanGenerator entirely) so each
/// scenario's starting layout is fully controlled.
///
/// All tests use March 2026 (year=2026, month=3) - no IsraeliHolidays entries that month, so the work
/// calendar is the plain Sun-Thu(Full)/Fri(Half)/Sat(Off) default.
/// </summary>
public class VisitPlanCityOptimizerTests : IDisposable
{
    private const int Year = 2026;
    private const int Month = 3;
    private readonly SqliteDbFixture _fixture = new();
    private VisiFlow.Data.VisiFlowDbContext Db => _fixture.Db;

    public void Dispose() => _fixture.Dispose();

    private async Task<Company> SeedCompanyAsync(int fullDayCapacity = 8, int halfDayCapacity = 4)
    {
        var company = new Company { Name = "Test Co" };
        Db.Companies.Add(company);
        await Db.SaveChangesAsync();
        Db.VisitPlanWeights.Add(new VisitPlanWeights { CompanyId = company.Id, FullDayCapacity = fullDayCapacity, HalfDayCapacity = halfDayCapacity });
        await Db.SaveChangesAsync();
        return company;
    }

    private Customer AddCustomer(int companyId, string number, string agentId, string city) =>
        new() { CompanyId = companyId, CustomerNumber = number, Year = Year, Month = Month, CustomerName = number, AgentIdNumber = agentId, City = city, Status = CustomerStatus.Active };

    private VisitPlanEntry AddEntry(int companyId, string customerNumber, string agentName, DateTime date, decimal priority) =>
        new() { CompanyId = companyId, PlanYear = Year, PlanMonth = Month, CustomerNumber = customerNumber, AgentName = agentName, PlannedDate = date, PriorityScore = priority, GeneratedAt = DateTime.UtcNow };

    /// <summary>The full (7-day) Sun-Sat weeks of the month, in order - used so tests can pick concrete
    /// dates without hard-coding which weekday March 1, 2026 happens to fall on. Index 0 is skipped
    /// (it's usually a partial week at the start of the month).</summary>
    private static List<List<DateTime>> FullWeeks()
    {
        var monthStart = new DateTime(Year, Month, 1);
        var days = Enumerable.Range(0, DateTime.DaysInMonth(Year, Month)).Select(i => monthStart.AddDays(i)).ToList();
        return days.GroupBy(TestHelpers.WeekStart).OrderBy(g => g.Key).Where(g => g.Count() == 7).Select(g => g.OrderBy(d => d).ToList()).ToList();
    }

    [Fact]
    public async Task OptimizeAsync_SameAgentSameCityTwiceInWeek_MovesToSameDay()
    {
        var company = await SeedCompanyAsync();
        var week = FullWeeks()[0];
        var day1 = week.First(d => d.DayOfWeek == DayOfWeek.Sunday);
        var day2 = week.First(d => d.DayOfWeek == DayOfWeek.Tuesday);

        // Three visits to the same city ("Haifa") for the same agent, in the same week, split 2/1
        // across two different days - the optimizer should consolidate the minority day onto the
        // majority day (day1).
        Db.Customers.AddRange(
            AddCustomer(company.Id, "C1", "AG1", "Haifa"),
            AddCustomer(company.Id, "C2", "AG1", "Haifa"),
            AddCustomer(company.Id, "C3", "AG1", "Haifa"));
        Db.VisitPlanEntries.AddRange(
            AddEntry(company.Id, "C1", "AG1", day1, 50),
            AddEntry(company.Id, "C2", "AG1", day1, 50),
            AddEntry(company.Id, "C3", "AG1", day2, 50));
        await Db.SaveChangesAsync();

        var result = await VisitPlanCityOptimizer.OptimizeAsync(Db, company.Id, Year, Month);

        var c3 = Db.VisitPlanEntries.Single(e => e.CustomerNumber == "C3");
        Assert.Equal(day1, c3.PlannedDate);
        Assert.NotNull(c3.CityOptimizedAt);
        Assert.True(result.SwapsApplied >= 1);
        Assert.True(result.FragmentationAfter < result.FragmentationBefore);

        // C1/C2 stay put (they were already on the majority day).
        Assert.Equal(day1, Db.VisitPlanEntries.Single(e => e.CustomerNumber == "C1").PlannedDate);
        Assert.Equal(day1, Db.VisitPlanEntries.Single(e => e.CustomerNumber == "C2").PlannedDate);
    }

    /// <summary>
    /// Regression test reconstructed from VisitPlanCityOptimizer.cs's LegalDatesFor comments: "A
    /// customer's OTHER entries this month (their other weekly occurrences) already own their own
    /// dates - never propose one of those as legal for this entry too, or two of this customer's own
    /// visits could end up landing on the very same day" (and the no-distribution-days branch's
    /// otherWeeks check, which blocks landing in the same WEEK as another of the customer's own
    /// entries). The exact original bug report isn't available verbatim, so this is a best-effort
    /// reconstruction of the shape described: the pairwise city-swap pass (Pass 2) proposing a move
    /// that would put a customer's own two monthly visits in the same (Sun-Sat) week, purely because
    /// doing so would reduce city fragmentation for an unrelated pairing - which must never happen.
    /// </summary>
    [Fact]
    public async Task OptimizeAsync_NeverDoubleBooksSameCustomerTwiceInSameWeek()
    {
        var company = await SeedCompanyAsync(fullDayCapacity: 8);
        var weeks = FullWeeks();
        var week1 = weeks[0];
        var week2 = weeks[1];
        var dB = week1.First(d => d.DayOfWeek == DayOfWeek.Sunday); // week 1 day - shared by C1's first visit and C2
        var dA = week2.First(d => d.DayOfWeek == DayOfWeek.Sunday); // week 2 day - C1's second visit, currently alone

        // C1: two monthly visits to the same city (Haifa, since City is a per-customer field - both of
        // a customer's own entries always share it), one already on dB (week1) and one alone on dA
        // (week2).
        Db.Customers.Add(AddCustomer(company.Id, "C1", "AG1", "Haifa"));
        var c1Week1 = AddEntry(company.Id, "C1", "AG1", dB, 40);
        var c1Week2 = AddEntry(company.Id, "C1", "AG1", dA, 40);

        // C2: another Haifa customer, also on dB (so dB already has 2 Haifa visits - deliberately
        // already consolidated, nothing for tier 1 to do here).
        Db.Customers.Add(AddCustomer(company.Id, "C2", "AG1", "Haifa"));
        var c2 = AddEntry(company.Id, "C2", "AG1", dB, 40);

        // C3: a different city (TelAviv), also on dB. Swapping C1's week-2 visit (alone on dA) with
        // C3 (on dB) would strictly reduce fragmentation (dA: {Haifa} + dB: {Haifa,TelAviv} = 3 total
        // distinct-city-per-day pairs, vs dA: {TelAviv} + dB: {Haifa} = 2 after swapping) - an
        // "improving" swap the pairwise pass actively looks for. It must still be refused, because it
        // would land C1's own two visits in the same week (week1).
        Db.Customers.Add(AddCustomer(company.Id, "C3", "AG1", "TelAviv"));
        var c3 = AddEntry(company.Id, "C3", "AG1", dB, 40);

        Db.VisitPlanEntries.AddRange(c1Week1, c1Week2, c2, c3);
        await Db.SaveChangesAsync();

        var result = await VisitPlanCityOptimizer.OptimizeAsync(Db, company.Id, Year, Month);

        var reloadedC1Week1 = Db.VisitPlanEntries.Single(e => e.Id == c1Week1.Id);
        var reloadedC1Week2 = Db.VisitPlanEntries.Single(e => e.Id == c1Week2.Id);

        // The core regression assertion: C1's two entries must never end up in the same week.
        Assert.NotEqual(TestHelpers.WeekStart(reloadedC1Week1.PlannedDate!.Value), TestHelpers.WeekStart(reloadedC1Week2.PlannedDate!.Value));
        // Nothing should have moved at all - the one "improving" swap available was illegal, and there
        // was nothing else to do (every group at dB was already a single date).
        Assert.Equal(dB, reloadedC1Week1.PlannedDate);
        Assert.Equal(dA, reloadedC1Week2.PlannedDate);
        Assert.Equal(0, result.SwapsApplied);
    }

    /// <summary>
    /// Regression test for the "eviction priority floor" behavior called out in TryEvict's comments:
    /// "No PriorityScore floor against the mover: the entry needing to consolidate is very often
    /// ITSELF the lowest-priority one on the target day ... requiring a still-lower-priority victim
    /// would make eviction never fire for exactly the cases this exists to help. Picks the day's least
    /// urgent evictable entry instead, unconditionally." This asserts the CORRECT (fixed) behavior: the
    /// mover being the single lowest-priority entry in the whole scenario must NOT prevent eviction of
    /// a higher-priority occupant standing in its way.
    /// </summary>
    [Fact]
    public async Task OptimizeAsync_EvictionHasNoPriorityFloorAgainstTheMover()
    {
        var company = await SeedCompanyAsync(fullDayCapacity: 3); // tight capacity so eviction is required
        var week = FullWeeks()[0];
        var day1 = week.First(d => d.DayOfWeek == DayOfWeek.Sunday); // consolidation target - already full
        var day2 = week.First(d => d.DayOfWeek == DayOfWeek.Tuesday); // where the low-priority mover starts

        Db.Customers.AddRange(
            AddCustomer(company.Id, "H1", "AG1", "Haifa"),
            AddCustomer(company.Id, "H2", "AG1", "Haifa"),
            AddCustomer(company.Id, "HX", "AG1", "Haifa"), // the mover - lowest priority of everyone involved
            AddCustomer(company.Id, "TA", "AG1", "TelAviv")); // the only evictable occupant of day1

        // day1: H1 + H2 (Haifa, capacity taken) + TA (TelAviv) = 3 entries = at capacity (3).
        var h1 = AddEntry(company.Id, "H1", "AG1", day1, 50);
        var h2 = AddEntry(company.Id, "H2", "AG1", day1, 50);
        var ta = AddEntry(company.Id, "TA", "AG1", day1, 95); // deliberately HIGH priority
        // day2: HX alone (Haifa) - the mover, deliberately the LOWEST priority of anyone in this test.
        var hx = AddEntry(company.Id, "HX", "AG1", day2, 5);

        Db.VisitPlanEntries.AddRange(h1, h2, ta, hx);
        await Db.SaveChangesAsync();

        var result = await VisitPlanCityOptimizer.OptimizeAsync(Db, company.Id, Year, Month);

        var reloadedHx = Db.VisitPlanEntries.Single(e => e.Id == hx.Id);
        var reloadedTa = Db.VisitPlanEntries.Single(e => e.Id == ta.Id);

        // HX (the low-priority mover) still gets consolidated onto day1...
        Assert.Equal(day1, reloadedHx.PlannedDate);
        Assert.NotNull(reloadedHx.CityOptimizedNote);
        // ...at the expense of TA (the high-priority occupant), which had to be displaced elsewhere in
        // the same week - a floor requiring TA's priority to be BELOW HX's (5) would never have allowed
        // this, since TA's priority (95) is nowhere near lower than HX's.
        Assert.NotEqual(day1, reloadedTa.PlannedDate);
        Assert.Equal(TestHelpers.WeekStart(day1), TestHelpers.WeekStart(reloadedTa.PlannedDate!.Value));
        Assert.NotNull(reloadedTa.CityOptimizedNote);

        Assert.Equal(0, result.NotConsolidated);
        Assert.True(result.Displaced >= 1);
    }
}
