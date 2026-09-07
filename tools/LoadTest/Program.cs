using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VisiFlow.Data;
using VisiFlow.Data.Entities;
using VisiFlow.Data.Enums;

// ============================================================================================
// VisiFlow load-test tool.
//
// Measures how VisitPlanGenerator (priority scoring + greedy capacity placement) and
// VisitPlanCityOptimizer (post-generation city-day consolidation) scale as the number of
// customers/companies in the database grows, plus a representative "dashboard load" read query
// (the same shape as GET /api/visitplan in Program.cs).
//
// Every configuration runs against its OWN freshly-migrated, throwaway SQLite file under the OS
// temp directory, deleted at the end of the run. This never touches data/visiflow.db or any
// Postgres/Supabase connection - see VisiFlowDbContextFactory.cs for the same Sqlite-based
// pattern the real migrations are authored against.
// ============================================================================================

const int TargetYear = 2026;
const int TargetMonth = 9; // September - 30 days, a plain Sun-Thu/Fri/Sat month with no long holiday run

// Small fixed list of real Israeli city names, reused across companies so the city-grouping logic
// in VisitPlanCityOptimizer actually has repeats to consolidate.
var cities = new[]
{
    "תל אביב", "ירושלים", "חיפה", "ראשון לציון", "פתח תקווה",
    "אשדוד", "נתניה", "באר שבע", "חולון", "בני ברק"
};
var channels = new[] { "קמעונאי", "סיטונאי", "אונליין", "ישיר" };
var sizes = new[] { "קטן", "בינוני", "גדול" };
var standardOptions = new decimal[] { 0.25m, 0.5m, 1m, 2m };

Console.WriteLine("VisiFlow load test - VisitPlanGenerator / VisitPlanCityOptimizer / dashboard read");
Console.WriteLine("===================================================================================");
Console.WriteLine();
PrintIndexCoverageCheck();
Console.WriteLine();

// At minimum the 4 configurations requested: a 500-customer baseline (matches current real-world
// scale), 5x that many companies at the same per-company size (multi-tenant growth with the
// per-company algorithm run held constant), a single company 4x bigger (in-algorithm growth), and
// a much larger multi-tenant scenario. A 5000-customer single-company point is added too, since a
// 500/2000/5000 progression makes it much easier to eyeball linear vs. super-linear growth than
// just two points.
var configs = new (string Label, int Companies, int CustomersPerCompany)[]
{
    ("Baseline: 1 company x 500", 1, 500),
    ("1 company x 2000", 1, 2000),
    ("1 company x 5000", 1, 5000),
    ("5 companies x 500 each", 5, 500),
    ("20 companies x 1000 each", 20, 1000),
};

// One small throwaway run first, discarded - JITting VisitPlanGenerator/VisitPlanCityOptimizer/EF's
// query pipeline for the first time in the process is itself a fixed cost that would otherwise
// land entirely on the baseline row and make the table look like generation time doesn't grow
// with data size at all.
Console.WriteLine("Warming up (JIT) with a small throwaway run ...");
await RunConfigAsync("(warm-up, discarded)", 1, 50);
Console.WriteLine();

var rows = new List<ResultRow>();
foreach (var cfg in configs)
{
    Console.WriteLine($"Running: {cfg.Label} ...");
    var row = await RunConfigAsync(cfg.Label, cfg.Companies, cfg.CustomersPerCompany);
    rows.Add(row);
    Console.WriteLine(
        $"  seed={row.SeedMs}ms gen={row.GenerationMs}ms opt={row.OptimizationMs}ms dashboard={row.DashboardMs}ms " +
        $"(scheduled={row.Scheduled}, unscheduled={row.Unscheduled}, swaps={row.Swaps})");
}

Console.WriteLine();
PrintTable(rows);
return 0;

// -------------------------------------------------------------------------------------------

async Task<ResultRow> RunConfigAsync(string label, int companyCount, int customersPerCompany)
{
    var dbPath = Path.Combine(Path.GetTempPath(), $"visiflow_loadtest_{Guid.NewGuid():N}.db");
    try
    {
        var options = new DbContextOptionsBuilder<VisiFlowDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        await using (var migrateDb = new VisiFlowDbContext(options))
        {
            await migrateDb.Database.MigrateAsync();
            // Throwaway file that gets deleted seconds later - trade durability for insert speed.
            await migrateDb.Database.ExecuteSqlRawAsync("PRAGMA synchronous = OFF;");
            await migrateDb.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = MEMORY;");
        }

        var seedSw = Stopwatch.StartNew();
        var firstCompanyId = await SeedAsync(options, companyCount, customersPerCompany);
        seedSw.Stop();

        long genMs, optMs, dashMs;
        int scheduled, unscheduled, swaps;

        // A fresh DbContext per phase, same as a real ASP.NET Core request would get a fresh
        // scoped context - avoids one phase's change-tracker cache making a later phase's timing
        // look artificially cheap.
        await using (var genDb = new VisiFlowDbContext(options))
        {
            var sw = Stopwatch.StartNew();
            var genResult = await VisitPlanGenerator.GenerateAsync(genDb, firstCompanyId, TargetYear, TargetMonth);
            sw.Stop();
            genMs = sw.ElapsedMilliseconds;
            scheduled = genResult.Scheduled;
            unscheduled = genResult.Unscheduled;
        }

        await using (var optDb = new VisiFlowDbContext(options))
        {
            var sw = Stopwatch.StartNew();
            var optResult = await VisitPlanCityOptimizer.OptimizeAsync(optDb, firstCompanyId, TargetYear, TargetMonth);
            sw.Stop();
            optMs = sw.ElapsedMilliseconds;
            swaps = optResult.SwapsApplied;
        }

        await using (var dashDb = new VisiFlowDbContext(options))
        {
            var sw = Stopwatch.StartNew();
            // Same query shape as GET /api/visitplan in src/VisiFlow.Api/Program.cs (the
            // dashboard-facing visit-plan read): all of this month's plan entries for the company
            // (sorted in memory - SQLite/EF can't translate ORDER BY on a decimal column), joined
            // in-memory against this month's customer snapshot and the per-customer visit
            // standards.
            var entries = (await dashDb.VisitPlanEntries
                    .Where(e => e.CompanyId == firstCompanyId && e.PlanYear == TargetYear && e.PlanMonth == TargetMonth)
                    .ToListAsync())
                .OrderByDescending(e => e.PriorityScore)
                .ToList();
            var custByNumber = await dashDb.Customers
                .Where(c => c.CompanyId == firstCompanyId && c.Year == TargetYear && c.Month == TargetMonth)
                .ToDictionaryAsync(c => c.CustomerNumber);
            var standardsByNumber = await dashDb.CustomerVisitStandards
                .Where(s => s.CompanyId == firstCompanyId)
                .ToDictionaryAsync(s => s.CustomerNumber);
            // Touch every joined field once, same as VisitPlanEntryDto.From's projection would,
            // so the dictionary lookups are actually paid for in the timing.
            var projected = entries.Select(e =>
            {
                custByNumber.TryGetValue(e.CustomerNumber, out var cust);
                standardsByNumber.TryGetValue(e.CustomerNumber, out var std);
                return (e.PlannedDate, e.PriorityScore, cust?.CustomerName, cust?.City, std?.RequiredVisitsPerWeek);
            }).ToList();
            sw.Stop();
            dashMs = sw.ElapsedMilliseconds;
            GC.KeepAlive(projected);
        }

        return new ResultRow(label, companyCount, customersPerCompany, companyCount * customersPerCompany,
            seedSw.ElapsedMilliseconds, genMs, optMs, dashMs, scheduled, unscheduled, swaps);
    }
    finally
    {
        // Release SQLite's file handles before deleting - otherwise the delete below can fail on
        // Windows while the pooled connection still has the file open.
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var f = dbPath + suffix;
            if (File.Exists(f))
            {
                try { File.Delete(f); } catch { /* best-effort cleanup of a throwaway temp file */ }
            }
        }
    }
}

// Seeds companyCount companies, each with customersPerCompany customers plus the
// CustomerDistributionDay / WorkCalendarDay rows the algorithms need. Returns the first
// company's id (the one the timed phases run against).
async Task<int> SeedAsync(DbContextOptions<VisiFlowDbContext> options, int companyCount, int customersPerCompany)
{
    var rng = new Random(12345); // fixed seed - runs are comparable across configurations
    await using var db = new VisiFlowDbContext(options);
    db.ChangeTracker.AutoDetectChangesEnabled = false; // bulk insert - nothing here needs change detection

    var daysInMonth = DateTime.DaysInMonth(TargetYear, TargetMonth);
    var monthStart = new DateTime(TargetYear, TargetMonth, 1);
    var deliveryWeekdays = new[]
    {
        DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday
    };

    var companies = new List<Company>();
    for (var c = 0; c < companyCount; c++)
        companies.Add(new Company { Name = $"חברת בדיקה {c + 1}", IsActive = true });
    db.Companies.AddRange(companies);
    await db.SaveChangesAsync();
    var firstCompanyId = companies[0].Id;

    foreach (var company in companies)
    {
        // Enough agents that capacity contention is realistic (many customers sharing each
        // agent's daily/monthly capacity), not one agent per customer.
        var agentCount = Math.Max(5, customersPerCompany / 40);
        var agents = Enumerable.Range(1, agentCount)
            .Select(i => (Id: $"A{company.Id}-{i:000}", Name: $"סוכן {company.Id}-{i}"))
            .ToArray();

        var customers = new List<Customer>(customersPerCompany);
        var distDays = new List<CustomerDistributionDay>(customersPerCompany);
        var standards = new List<CustomerVisitStandard>();

        for (var i = 1; i <= customersPerCompany; i++)
        {
            var custNumber = $"C{i:00000}";
            var agent = agents[rng.Next(agents.Length)];

            customers.Add(new Customer
            {
                CompanyId = company.Id,
                CustomerNumber = custNumber,
                Year = TargetYear,
                Month = TargetMonth,
                CustomerName = $"לקוח מספר {custNumber}",
                AgentName = agent.Name,
                AgentIdNumber = agent.Id,
                Channel = channels[rng.Next(channels.Length)],
                SalesYtdCurrentYear = Math.Round((decimal)(rng.NextDouble() * 100_000), 2),
                SalesYtdPreviousYear = Math.Round((decimal)(rng.NextDouble() * 100_000), 2),
                CustomerSize = sizes[rng.Next(sizes.Length)],
                Phone = $"05{rng.Next(0, 10)}-{rng.Next(1_000_000, 9_999_999)}",
                Address = $"רחוב הבדיקה {rng.Next(1, 200)}",
                City = cities[rng.Next(cities.Length)],
                AvgMonthlyOrders = Math.Round((decimal)(rng.NextDouble() * 12), 2),
                WasActiveAllPeriod = rng.NextDouble() > 0.1,
                Status = CustomerStatus.Active,
                UpdatedAt = DateTime.UtcNow
            });

            // 1-2 delivery weekdays per customer - enough for VisitPlanGenerator to have a real
            // preferred-date target (occurrence minus 1/2 days) instead of falling back to "no
            // preference", and enough repeats across customers for the city optimizer to have
            // same-day/same-city groups worth consolidating.
            var dd = new CustomerDistributionDay
            {
                CompanyId = company.Id,
                CustomerNumber = custNumber,
                Year = TargetYear,
                Month = TargetMonth
            };
            var chosenDays = deliveryWeekdays.OrderBy(_ => rng.Next()).Take(rng.Next(1, 3)).ToHashSet();
            dd.Sunday = chosenDays.Contains(DayOfWeek.Sunday);
            dd.Monday = chosenDays.Contains(DayOfWeek.Monday);
            dd.Tuesday = chosenDays.Contains(DayOfWeek.Tuesday);
            dd.Wednesday = chosenDays.Contains(DayOfWeek.Wednesday);
            dd.Thursday = chosenDays.Contains(DayOfWeek.Thursday);
            distDays.Add(dd);

            // ~30% of customers have an explicit visit standard, exercising that scoring factor
            // and the monthly-target-scaling / due-date-cycle branches in VisitPlanGenerator.
            if (rng.NextDouble() < 0.3)
            {
                standards.Add(new CustomerVisitStandard
                {
                    CompanyId = company.Id,
                    CustomerNumber = custNumber,
                    RequiredVisitsPerWeek = standardOptions[rng.Next(standardOptions.Length)],
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }

        db.Customers.AddRange(customers);
        db.CustomerDistributionDays.AddRange(distDays);
        db.CustomerVisitStandards.AddRange(standards);

        // A couple of explicit admin-set calendar overrides per company (WorkCalendarDay rows are
        // sparse in real usage - see the entity's own doc comment - everything else legally falls
        // back to the generator's own Sun-Thu/Fri/Sat + IsraeliHolidays default).
        var overrideDays = Enumerable.Range(0, daysInMonth)
            .Select(d => monthStart.AddDays(d))
            .Where(d => d.DayOfWeek != DayOfWeek.Friday && d.DayOfWeek != DayOfWeek.Saturday)
            .OrderBy(_ => rng.Next())
            .Take(2)
            .Select(d => new WorkCalendarDay { CompanyId = company.Id, Date = d, DayType = WorkDayType.Off })
            .ToList();
        db.WorkCalendarDays.AddRange(overrideDays);

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    return firstCompanyId;
}

void PrintIndexCoverageCheck()
{
    Console.WriteLine("Index coverage check (static, from VisiFlowDbContext.OnModelCreating):");
    Console.WriteLine("  Customers        : unique index on (CompanyId, CustomerNumber, Year, Month) + a separate index on AgentIdNumber.");
    Console.WriteLine("                     Every algorithm/dashboard query filters Customers by (CompanyId, Year, Month) WITHOUT");
    Console.WriteLine("                     CustomerNumber - that is NOT a usable left-prefix of the existing unique index (CustomerNumber");
    Console.WriteLine("                     is the 2nd column, before Year/Month), so this query pattern is NOT covered by an index today.");
    Console.WriteLine("  VisitPlanEntries : index on (CompanyId, PlanYear, PlanMonth) - exactly the pattern VisitPlanGenerator,");
    Console.WriteLine("                     VisitPlanCityOptimizer, and the /api/visitplan dashboard read all filter by. This IS fully");
    Console.WriteLine("                     covered by an existing index.");
}

void PrintTable(List<ResultRow> rows)
{
    Console.WriteLine("Timing results:");
    var cols = new (string Header, int Width)[]
    {
        ("Configuration", 28), ("Companies", 9), ("Cust/Co", 8), ("TotalCust", 9),
        ("SeedMs", 8), ("GenMs", 8), ("OptMs", 8), ("DashMs", 8),
        ("Sched", 7), ("Unsched", 8), ("Swaps", 7)
    };
    var headerLine = string.Join(" | ", cols.Select(c => c.Header.PadRight(c.Width)));
    Console.WriteLine(headerLine);
    Console.WriteLine(new string('-', headerLine.Length));
    foreach (var r in rows)
    {
        var cells = new[]
        {
            r.Label.PadRight(cols[0].Width),
            r.Companies.ToString().PadRight(cols[1].Width),
            r.CustomersPerCompany.ToString().PadRight(cols[2].Width),
            r.TotalCustomers.ToString().PadRight(cols[3].Width),
            r.SeedMs.ToString().PadRight(cols[4].Width),
            r.GenerationMs.ToString().PadRight(cols[5].Width),
            r.OptimizationMs.ToString().PadRight(cols[6].Width),
            r.DashboardMs.ToString().PadRight(cols[7].Width),
            r.Scheduled.ToString().PadRight(cols[8].Width),
            r.Unscheduled.ToString().PadRight(cols[9].Width),
            r.Swaps.ToString().PadRight(cols[10].Width),
        };
        Console.WriteLine(string.Join(" | ", cells));
    }
    Console.WriteLine();
    Console.WriteLine("GenMs/OptMs/DashMs are all against a SINGLE company's data (the algorithms are always scoped to one");
    Console.WriteLine("company+month) - the 5x/20x-company rows show whether growing the REST of the database (other tenants'");
    Console.WriteLine("rows the query has to filter past) affects a single company's time, separately from growing that one");
    Console.WriteLine("company's own customer count (the 500/2000/5000 rows).");
}

record ResultRow(
    string Label, int Companies, int CustomersPerCompany, int TotalCustomers,
    long SeedMs, long GenerationMs, long OptimizationMs, long DashboardMs,
    int Scheduled, int Unscheduled, int Swaps);
