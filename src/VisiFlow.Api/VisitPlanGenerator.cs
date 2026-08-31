using VisiFlow.Data;
using VisiFlow.Data.Entities;
using VisiFlow.Data.Enums;
using Microsoft.EntityFrameworkCore;

// Deliberately no namespace here - Program.cs's top-level statements live in the implicit global
// namespace (same as every DTO record declared at the bottom of that file), so this matches that
// convention instead of requiring an extra `using`.

public record VisitPlanGenerationResult(int Scheduled, int Unscheduled);

/// <summary>
/// Generates a next-month visit plan: a weighted priority score per customer (5 equally-weighted-by-
/// default factors, adjustable per company via VisitPlanWeights) used both to rank the output and to
/// resolve conflicts when placing visits against limited daily agent capacity.
///
/// Design decisions made without further user sign-off (stated back to the user, open to correction):
///  - Monthly visit count: RequiredVisitsPerWeek converted to a monthly count if set, else 1. Capped
///    to 1 regardless of the standard when AvgMonthlyOrders indicates a low-activity account (&lt;=1.5),
///    so a customer with lots of distribution days but rare real purchases isn't visited weekly.
///  - Distribution-day target date: the occurrence date minus 1 day; falls back to minus 2, then the
///    nearest earlier working day, if that lands on a zero-capacity (Off) day.
///  - "&gt;30 days since last visit" gets a flat score boost on top of linear normalization, so crossing
///    the threshold visibly matters rather than blending into a smooth gradient.
///  - Never-visited customers score as maximally overdue on that factor.
///  - All 5 raw factor values are min-max normalized to 0-1 across the company's active customers,
///    then combined using the weights (which sum to 100) directly as percentages -&gt; a 0-100 score.
/// </summary>
public static class VisitPlanGenerator
{
    private record VisitRequest(Customer Customer, DateTime? PreferredDate, decimal PriorityScore,
        decimal SalesDropScore, decimal DistributionScore, decimal FrequencyScore, decimal VisitStandardScore, decimal DaysSinceVisitScore,
        int? DaysSinceLastVisit);

    public static async Task<VisitPlanGenerationResult> GenerateAsync(VisiFlowDbContext db, int companyId, int year, int month)
    {
        // Customer data is a monthly snapshot (see Customer.cs) - only THIS month's own upload is used,
        // never a different month's. The caller (the /api/visitplan/generate endpoint) already checked
        // that a snapshot exists for this month before calling in.
        var customers = await db.Customers.Where(c => c.CompanyId == companyId && c.Year == year && c.Month == month && c.Status == CustomerStatus.Active).ToListAsync();

        // Clear any previous plan for this month before (re)generating.
        await db.VisitPlanEntries.Where(e => e.CompanyId == companyId && e.PlanYear == year && e.PlanMonth == month).ExecuteDeleteAsync();
        if (customers.Count == 0) return new VisitPlanGenerationResult(0, 0);

        // The visit standard is set once per real customer (not per monthly snapshot - see
        // CustomerVisitStandard.cs) so it survives automatically across months without being re-entered.
        var standardByCustomer = await db.CustomerVisitStandards
            .Where(s => s.CompanyId == companyId).ToDictionaryAsync(s => s.CustomerNumber, s => s.RequiredVisitsPerWeek);

        var weights = await db.VisitPlanWeights.FirstOrDefaultAsync(w => w.CompanyId == companyId);
        if (weights == null)
        {
            weights = new VisitPlanWeights { CompanyId = companyId };
            db.VisitPlanWeights.Add(weights);
            await db.SaveChangesAsync();
        }

        var daysInMonth = DateTime.DaysInMonth(year, month);
        var monthStart = new DateTime(year, month, 1);
        var monthEnd = monthStart.AddDays(daysInMonth - 1);

        // ---- distribution days: target month, else fall back up to 12 months back per customer ----
        var distByCustomer = await LoadDistributionDaysWithFallback(db, companyId, year, month, customers.Select(c => c.CustomerNumber).ToList());

        // ---- work calendar: explicit overrides for the target month + the Sun-Thu/Fri/Sat default ----
        var overrides = await db.WorkCalendarDays
            .Where(d => d.CompanyId == companyId && d.Date >= monthStart && d.Date <= monthEnd)
            .ToDictionaryAsync(d => d.Date.Date, d => d.DayType);
        WorkDayType DayTypeOf(DateTime date) => overrides.TryGetValue(date.Date, out var t) ? t : IsraeliHolidays.TypeFor(date) ?? date.DayOfWeek switch
        {
            DayOfWeek.Saturday => WorkDayType.Off,
            DayOfWeek.Friday => WorkDayType.Half,
            _ => WorkDayType.Full
        };
        // Admin-configurable per company (see VisitPlanWeights) - defaults to 8/4, adjustable from the
        // "תוכנית ביקורים" screen when capacity is too tight to fit everyone and a re-run is needed.
        int CapacityFor(WorkDayType t) => t switch { WorkDayType.Full => weights.FullDayCapacity, WorkDayType.Half => weights.HalfDayCapacity, _ => 0 };

        // ---- last visit date per customer (all-time, Visited outcomes only) ----
        var customerNumbers = customers.Select(c => c.CustomerNumber).ToList();
        var lastVisitByCustomer = await db.CustomerVisits
            .Where(v => v.CompanyId == companyId && v.Outcome == VisitOutcome.Visited && customerNumbers.Contains(v.CustomerNumber))
            .GroupBy(v => v.CustomerNumber)
            .Select(g => new { CustomerNumber = g.Key, LastVisit = g.Max(v => v.VisitDate) })
            .ToDictionaryAsync(x => x.CustomerNumber, x => x.LastVisit);

        var today = DateTime.Today;

        // ---- raw factor values per customer ----
        var raw = customers.Select(c =>
        {
            var salesDrop = Math.Max(0m, (c.SalesYtdPreviousYear ?? 0) - (c.SalesYtdCurrentYear ?? 0));
            distByCustomer.TryGetValue(c.CustomerNumber, out var dd);
            var distDays = dd == null ? new List<DayOfWeek>() : ActiveWeekdays(dd);
            var avgOrders = c.AvgMonthlyOrders ?? 0m;
            var reqPerWeek = standardByCustomer.TryGetValue(c.CustomerNumber, out var std) ? (std ?? 0m) : 0m;
            var daysSince = lastVisitByCustomer.TryGetValue(c.CustomerNumber, out var lv) ? (today - lv).Days : 9999;
            return (Customer: c, SalesDrop: salesDrop, DistDays: distDays, AvgOrders: avgOrders, ReqPerWeek: reqPerWeek, DaysSince: daysSince);
        }).ToList();

        // ---- normalize (min-max PER AGENT, not company-wide) ----
        // Each agent's visit plan is ranked against their own book of customers only - an agent whose
        // whole portfolio is smaller/lower-frequency than another agent's shouldn't have every one of
        // their customers score low just because a *different* agent's customers are bigger. So min/max
        // for each of the 4 normalized factors below is taken within the customer's own agent group,
        // not across the whole company.
        static decimal Norm(decimal v, decimal min, decimal max) => max > min ? (v - min) / (max - min) : 0m;
        // Capped before normalizing so a handful of "never visited" (9999) customers don't compress
        // every real day-count difference into a sliver of the 0-1 range - they still end up tied at
        // the top (score 1.0), which is exactly the desired "max priority" behavior.
        var daysSinceCapped = raw.Select(r => (decimal)Math.Min(r.DaysSince, 180)).ToList();

        var normScores = new (decimal Sales, decimal Freq, decimal Std, decimal Days)[raw.Count];
        foreach (var group in raw.Select((r, i) => (r, i)).GroupBy(x => AgentKey(x.r.Customer)))
        {
            var items = group.ToList();
            var (minSales, maxSales) = (items.Min(x => x.r.SalesDrop), items.Max(x => x.r.SalesDrop));
            var (minOrders, maxOrders) = (items.Min(x => x.r.AvgOrders), items.Max(x => x.r.AvgOrders));
            var (minReq, maxReq) = (items.Min(x => x.r.ReqPerWeek), items.Max(x => x.r.ReqPerWeek));
            var (minDays, maxDays) = (items.Min(x => daysSinceCapped[x.i]), items.Max(x => daysSinceCapped[x.i]));
            foreach (var (r, i) in items)
            {
                normScores[i] = (
                    Norm(r.SalesDrop, minSales, maxSales),
                    Norm(r.AvgOrders, minOrders, maxOrders),
                    Norm(r.ReqPerWeek, minReq, maxReq),
                    Norm(daysSinceCapped[i], minDays, maxDays)
                );
            }
        }

        var requests = new List<VisitRequest>();
        for (var i = 0; i < raw.Count; i++)
        {
            var r = raw[i];
            var (salesScore, freqScore, stdScore, daysScore) = normScores[i];
            var distScore = r.DistDays.Count > 0 ? 1m : 0m;
            if (r.DaysSince > 30) daysScore = Math.Min(1m, daysScore + 0.25m);

            var priority = Math.Round(
                weights.SalesDropWeight * salesScore +
                weights.DistributionWeight * distScore +
                weights.FrequencyWeight * freqScore +
                weights.VisitStandardWeight * stdScore +
                weights.DaysSinceVisitWeight * daysScore, 1);

            // Monthly visit count. Three cases:
            //  - No standard set (ReqPerWeek is 0/null): a single visit, as before.
            //  - Standard is 1/week or more: scaled to a monthly count, same as before - capped to 1
            //    for customers whose real purchase frequency is low even if the standard says otherwise.
            //  - Standard is BELOW 1/week (e.g. once every 2/3 weeks, once a month/quarter/half-year/
            //    year - entered as a fractional weekly value, such as 0.5 for "once every 2 weeks"):
            //    a monthly ratio makes no sense here (you can't schedule "0.23 visits"). Instead this is
            //    a due-date cycle - only schedule this month if at least one interval's worth of days
            //    has passed since the customer's last recorded visit (or they were never visited).
            //    Otherwise they're simply not due yet and get skipped this month entirely (0 requests,
            //    not "unscheduled" - they just don't belong in this month's plan).
            int monthlyTarget;
            if (r.ReqPerWeek >= 1)
            {
                monthlyTarget = Math.Max(1, (int)Math.Round(r.ReqPerWeek * daysInMonth / 7m, MidpointRounding.AwayFromZero));
                if (r.AvgOrders is > 0 and <= 1.5m) monthlyTarget = Math.Min(monthlyTarget, 1);
            }
            else if (r.ReqPerWeek > 0)
            {
                var intervalDays = (int)Math.Round(7m / r.ReqPerWeek, MidpointRounding.AwayFromZero);
                monthlyTarget = r.DaysSince >= intervalDays ? 1 : 0; // DaysSince's 9999 "never visited" sentinel always qualifies
            }
            else
            {
                monthlyTarget = 1;
            }

            var preferredDates = PreferredDates(r.DistDays, monthlyTarget, monthStart, daysInMonth, DayTypeOf);
            int? daysSinceRaw = r.DaysSince >= 9999 ? null : r.DaysSince; // 9999 is the "never visited" sentinel
            foreach (var date in preferredDates)
                requests.Add(new VisitRequest(r.Customer, date, priority, salesScore, distScore, freqScore, stdScore, daysScore, daysSinceRaw));
        }

        // ---- greedy placement: highest priority first, into the customer's own agent's capacity ----
        var capacity = new Dictionary<(string Agent, DateTime Date), int>();
        int RemainingCapacity(string agent, DateTime date)
        {
            var key = (agent, date.Date);
            if (!capacity.TryGetValue(key, out var cap)) { cap = CapacityFor(DayTypeOf(date)); capacity[key] = cap; }
            return cap;
        }

        // Two visit requests for the SAME customer must never land on the same date, even if agent
        // capacity would technically allow it (capacity is tracked per agent+date, not per
        // customer+date) - e.g. a capacity-driven fallback for one of that customer's own later-week
        // visits landing on a date already used by an earlier one of their own visits.
        var usedDatesByCustomer = new Dictionary<string, HashSet<DateTime>>();
        // Business rule: an agent never visits the same customer more than once in the same
        // (Sun-Sat) week. PreferredDates() already only proposes one target date per week per
        // customer, but the capacity-driven fallback below can walk a date into a neighboring week -
        // this blocks that from ever landing a customer's second visit in a week it's already used.
        var usedWeeksByCustomer = new Dictionary<string, HashSet<DateTime>>();

        var entries = new List<VisitPlanEntry>();
        var scheduled = 0;
        var unscheduled = 0;
        foreach (var req in requests.OrderByDescending(r => r.PriorityScore))
        {
            var agent = AgentKey(req.Customer);
            var customerNumber = req.Customer.CustomerNumber;
            if (!usedDatesByCustomer.TryGetValue(customerNumber, out var usedDates)) usedDatesByCustomer[customerNumber] = usedDates = new HashSet<DateTime>();
            if (!usedWeeksByCustomer.TryGetValue(customerNumber, out var usedWeeks)) usedWeeksByCustomer[customerNumber] = usedWeeks = new HashSet<DateTime>();
            DateTime? placed = null;
            bool CanUse(DateTime candidate) => RemainingCapacity(agent, candidate) > 0 && !usedDates.Contains(candidate.Date) && !usedWeeks.Contains(WeekStartOf(candidate));

            if (req.PreferredDate is DateTime pref)
            {
                // Try the preferred date, then walk outward (+/-1, +/-2, ...) within the month.
                for (var offset = 0; offset <= daysInMonth && placed == null; offset++)
                {
                    foreach (var candidate in offset == 0 ? new[] { pref } : new[] { pref.AddDays(-offset), pref.AddDays(offset) })
                    {
                        if (candidate < monthStart || candidate > monthEnd) continue;
                        if (CanUse(candidate)) { placed = candidate; break; }
                    }
                }
            }
            else
            {
                // No date preference: take the first day in the month with room.
                for (var d = monthStart; d <= monthEnd && placed == null; d = d.AddDays(1))
                    if (CanUse(d)) placed = d;
            }

            if (placed is DateTime finalDate)
            {
                capacity[(agent, finalDate.Date)]--;
                usedDates.Add(finalDate.Date);
                usedWeeks.Add(WeekStartOf(finalDate));
                scheduled++;
            }
            else
            {
                unscheduled++;
            }

            entries.Add(new VisitPlanEntry
            {
                CompanyId = companyId,
                PlanYear = year,
                PlanMonth = month,
                CustomerNumber = req.Customer.CustomerNumber,
                PlannedDate = placed,
                AgentName = req.Customer.AgentName,
                PriorityScore = req.PriorityScore,
                SalesDropScore = Math.Round(req.SalesDropScore, 3),
                DistributionScore = req.DistributionScore,
                FrequencyScore = Math.Round(req.FrequencyScore, 3),
                VisitStandardScore = Math.Round(req.VisitStandardScore, 3),
                DaysSinceVisitScore = Math.Round(req.DaysSinceVisitScore, 3),
                DaysSinceLastVisit = req.DaysSinceLastVisit,
                GeneratedAt = DateTime.UtcNow
            });
        }

        db.VisitPlanEntries.AddRange(entries);
        await db.SaveChangesAsync();
        return new VisitPlanGenerationResult(scheduled, unscheduled);
    }

    /// <summary>The real-world identity to group/schedule an agent by - AgentIdNumber, falling back
    /// to AgentName only for a row with no ID at all. Never AgentName alone: two different real
    /// agents can share a display name, and grouping by name would silently merge their capacity,
    /// priority normalization, and (in the city optimizer) their daily schedules into one.
    /// (An earlier version of this preferred AgentName, because a since-corrected source file had a
    /// different AgentIdNumber on every customer row instead of one per agent - re-verify that
    /// AgentIdNumber is actually one-per-agent in the loaded data before ever flipping this back.)
    /// Internal so VisitPlanCityOptimizer uses the same key.</summary>
    internal static string AgentKey(Customer c) => c.AgentIdNumber ?? c.AgentName ?? "__ללא_סוכן__";

    /// <summary>Sunday of the (Sun-Sat) week containing <paramref name="date"/> - the shared unit for
    /// enforcing "never visit the same customer twice in one week". Internal (not private) so
    /// VisitPlanCityOptimizer can reuse the exact same week-boundary definition.</summary>
    internal static DateTime WeekStartOf(DateTime date) => date.AddDays(-(int)date.DayOfWeek);

    internal static List<DayOfWeek> ActiveWeekdays(CustomerDistributionDay d)
    {
        var days = new List<DayOfWeek>();
        if (d.Sunday) days.Add(DayOfWeek.Sunday);
        if (d.Monday) days.Add(DayOfWeek.Monday);
        if (d.Tuesday) days.Add(DayOfWeek.Tuesday);
        if (d.Wednesday) days.Add(DayOfWeek.Wednesday);
        if (d.Thursday) days.Add(DayOfWeek.Thursday);
        if (d.Friday) days.Add(DayOfWeek.Friday);
        if (d.Saturday) days.Add(DayOfWeek.Saturday);
        return days;
    }

    /// <summary>Looks up each customer's distribution days for (year, month); for any customer with no
    /// row that month, scans backward up to 12 months for the most recent one on file (distribution
    /// days are normally fixed, so an older record is a reasonable stand-in for a month nobody re-
    /// uploaded).</summary>
    internal static async Task<Dictionary<string, CustomerDistributionDay>> LoadDistributionDaysWithFallback(
        VisiFlowDbContext db, int companyId, int year, int month, List<string> customerNumbers)
    {
        var result = new Dictionary<string, CustomerDistributionDay>();
        var target = await db.CustomerDistributionDays.Where(d => d.CompanyId == companyId && d.Year == year && d.Month == month).ToListAsync();
        foreach (var d in target) result[d.CustomerNumber] = d;

        var missing = customerNumbers.Except(result.Keys).ToList();
        var (fy, fm) = (year, month);
        for (var i = 0; i < 12 && missing.Count > 0; i++)
        {
            fm--; if (fm < 1) { fm = 12; fy--; }
            var fallback = await db.CustomerDistributionDays
                .Where(d => d.CompanyId == companyId && d.Year == fy && d.Month == fm && missing.Contains(d.CustomerNumber))
                .ToListAsync();
            foreach (var d in fallback) { result[d.CustomerNumber] = d; missing.Remove(d.CustomerNumber); }
        }
        return result;
    }

    /// <summary>The week-earliest distribution-day occurrence for each (Sun-Sat) week in the month -
    /// the shared basis for both the deterministic pick in PreferredDates and the optimizer's widened
    /// candidate window. Internal so VisitPlanCityOptimizer can recompute the same occurrences.</summary>
    internal static List<DateTime> WeeklyOccurrences(List<DayOfWeek> distDays, DateTime monthStart, int daysInMonth)
    {
        var monthEnd = monthStart.AddDays(daysInMonth - 1);
        var occurrenceByWeek = new SortedDictionary<DateTime, DateTime>();
        for (var d = monthStart; d <= monthEnd; d = d.AddDays(1))
        {
            if (!distDays.Contains(d.DayOfWeek)) continue;
            var week = WeekStartOf(d);
            if (!occurrenceByWeek.ContainsKey(week)) occurrenceByWeek[week] = d; // keep only the week's earliest occurrence
        }
        return occurrenceByWeek.Values.ToList();
    }

    /// <summary>Every working-day candidate near a distribution occurrence that's an acceptable visit
    /// date, in priority order: 2 days before delivery (the default), then 1 day before, then (only if
    /// neither of those lands on a working day within the month) the nearest earlier working day. The
    /// FIRST element is what PreferredDates uses as the deterministic pick; the FULL list is what
    /// VisitPlanCityOptimizer uses to widen the legal window for an entry that's already off that pick
    /// (capacity-bumped during generation) - still always strictly before delivery, never on/after it.</summary>
    internal static List<DateTime> CandidatesNearOccurrence(DateTime occurrence, DateTime monthStart, Func<DateTime, WorkDayType> dayType)
    {
        var results = new List<DateTime>();
        var twoBefore = occurrence.AddDays(-2);
        if (twoBefore >= monthStart && dayType(twoBefore) != WorkDayType.Off) results.Add(twoBefore);
        var oneBefore = occurrence.AddDays(-1);
        if (oneBefore >= monthStart && dayType(oneBefore) != WorkDayType.Off) results.Add(oneBefore);
        if (results.Count == 0)
        {
            // Neither the ideal (2 days before) nor the fallback (1 day before) landed on a working day
            // within the month - walk back to the nearest one instead of leaving the customer unplaced.
            var walk = occurrence.AddDays(-1);
            while (walk >= monthStart && dayType(walk) == WorkDayType.Off) walk = walk.AddDays(-1);
            results.Add(walk >= monthStart ? walk : occurrence);
        }
        return results;
    }

    /// <summary>Computes up to <paramref name="count"/> preferred visit dates for a customer, never more
    /// than one per (Sun-Sat) week - an agent doesn't visit the same customer twice in the same week,
    /// regardless of how high its visit standard or distribution-day count is. With distribution days on
    /// file, each visit targets 2 days before that week's earliest occurrence (falling back to 1 day
    /// before, then the nearest earlier day, if that's a zero-capacity day); a week with more than one
    /// distribution day still yields only one target, from its earliest occurrence. Without distribution
    /// days, dates are spread evenly across the month as loose anchors, one per week (actual
    /// placement/capacity is resolved later); a single request gets no preference at all and is placed
    /// wherever capacity allows.</summary>
    internal static List<DateTime?> PreferredDates(List<DayOfWeek> distDays, int count, DateTime monthStart, int daysInMonth, Func<DateTime, WorkDayType> dayType)
    {
        var monthEnd = monthStart.AddDays(daysInMonth - 1);
        if (distDays.Count > 0)
        {
            var occurrences = WeeklyOccurrences(distDays, monthStart, daysInMonth);

            // Capped to the number of weeks that actually have an occurrence - reusing the last
            // occurrence for any extra requested visits (rather than capping) would land two visits to
            // the same customer in the same week, which the business rule above forbids.
            var effectiveCount = occurrences.Count > 0 ? Math.Min(count, occurrences.Count) : count;
            var targets = new List<DateTime?>();
            for (var i = 0; i < effectiveCount; i++)
            {
                if (occurrences.Count == 0) { targets.Add(null); continue; }
                targets.Add(CandidatesNearOccurrence(occurrences[i], monthStart, dayType).First());
            }
            return targets;
        }

        if (count <= 0) return new List<DateTime?>(); // not due this month (e.g. a low-frequency standard) - no request at all
        if (count == 1) return new List<DateTime?> { null };
        // No distribution days on file: spread anchors evenly across the month, capped to at most one
        // per week for the same "never twice in a week" rule.
        var weekCount = 0;
        var lastWeek = DateTime.MinValue;
        for (var d = monthStart; d <= monthEnd; d = d.AddDays(1))
        {
            var week = WeekStartOf(d);
            if (week != lastWeek) { weekCount++; lastWeek = week; }
        }
        var effCount = Math.Min(count, weekCount);
        var spread = new List<DateTime?>();
        for (var i = 0; i < effCount; i++)
        {
            var anchorDay = 1 + (int)Math.Round((decimal)i * (daysInMonth - 1) / Math.Max(1, effCount - 1));
            spread.Add(monthStart.AddDays(Math.Clamp(anchorDay - 1, 0, daysInMonth - 1)));
        }
        return spread;
    }
}
