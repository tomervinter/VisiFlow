using VisiFlow.Data;
using VisiFlow.Data.Entities;
using VisiFlow.Data.Enums;
using Microsoft.EntityFrameworkCore;

// No namespace - matches VisitPlanGenerator.cs and Program.cs's implicit global namespace.

public record CityOptimizationResult(int SwapsApplied, int FragmentationBefore, int FragmentationAfter, int AgentsAffected);

/// <summary>
/// Post-process pass over an ALREADY-GENERATED month's visit plan: reorders which day each visit
/// lands on (within that customer's own already-valid date options) so an agent's same-city
/// customers land on the same day as much as possible, reducing daily driving. Never touches
/// PriorityScore, never adds/removes entries, never moves a visit between agents, and never moves
/// an entry that was already placed by a human (ManuallyModifiedAt set) - it only swaps dates
/// between pairs of entries belonging to the SAME agent.
///
/// "Legal date" for a candidate swap target is deliberately defined the same way the generator
/// itself would have placed that customer: for a customer with distribution days on file, the exact
/// set VisitPlanGenerator.PreferredDates would propose for their current visit count this month
/// (so the distribution-day-minus-1/2 placement rule is never violated); for a customer with no
/// distribution days, any working day in the month not already used by another of their own entries
/// that same (Sun-Sat) week (the one hard rule that actually applied to them).
///
/// Optimizes via local-search: repeatedly finds a pairwise swap (same agent, different cities, each
/// side's new date legal for the other's customer) that strictly reduces the total count of
/// distinct-city-per-agent-per-day pairs, until no improving swap remains or a pass cap is hit.
/// </summary>
public static class VisitPlanCityOptimizer
{
    public static async Task<CityOptimizationResult> OptimizeAsync(VisiFlowDbContext db, int companyId, int year, int month)
    {
        var monthStart = new DateTime(year, month, 1);
        var daysInMonth = DateTime.DaysInMonth(year, month);
        var monthEnd = monthStart.AddDays(daysInMonth - 1);

        var entries = await db.VisitPlanEntries
            .Where(e => e.CompanyId == companyId && e.PlanYear == year && e.PlanMonth == month && e.PlannedDate != null)
            .ToListAsync();
        if (entries.Count == 0) return new CityOptimizationResult(0, 0, 0, 0);

        var customerNumbers = entries.Select(e => e.CustomerNumber).Distinct().ToList();
        // The plan's own (year, month) is exactly the customer snapshot it was built from - entries
        // never span months (see VisitPlanGenerator), so filtering to this one snapshot is correct.
        var customers = await db.Customers
            .Where(c => c.CompanyId == companyId && c.Year == year && c.Month == month && customerNumbers.Contains(c.CustomerNumber))
            .ToDictionaryAsync(c => c.CustomerNumber);

        var distByCustomer = await VisitPlanGenerator.LoadDistributionDaysWithFallback(db, companyId, year, month, customerNumbers);

        var overrides = await db.WorkCalendarDays
            .Where(d => d.CompanyId == companyId && d.Date >= monthStart && d.Date <= monthEnd)
            .ToDictionaryAsync(d => d.Date.Date, d => d.DayType);
        WorkDayType DayTypeOf(DateTime date) => overrides.TryGetValue(date.Date, out var t) ? t : IsraeliHolidays.TypeFor(date) ?? date.DayOfWeek switch
        {
            DayOfWeek.Saturday => WorkDayType.Off,
            DayOfWeek.Friday => WorkDayType.Half,
            _ => WorkDayType.Full
        };

        string? CityOf(VisitPlanEntry e) => customers.TryGetValue(e.CustomerNumber, out var c) ? c.City : null;
        // AgentIdNumber, not AgentName - two different real agents can share a display name, and
        // grouping by name would let this optimizer "consolidate" one agent's city visits onto a
        // day that actually belongs to a different agent who happens to share their name.
        string? AgentKeyOf(VisitPlanEntry e) => customers.TryGetValue(e.CustomerNumber, out var c) ? VisitPlanGenerator.AgentKey(c) : e.AgentName;

        var fragmentationBefore = TotalFragmentation(entries, CityOf, AgentKeyOf);

        var swapsApplied = 0;
        var agentsAffected = new HashSet<string>();

        var byAgent = entries.Where(e => !string.IsNullOrWhiteSpace(AgentKeyOf(e))).GroupBy(e => AgentKeyOf(e)!).ToList();
        foreach (var agentGroup in byAgent)
        {
            var agentEntries = agentGroup.ToList();
            var eligible = agentEntries.Where(e => !string.IsNullOrWhiteSpace(CityOf(e)) && e.ManuallyModifiedAt == null).ToList();
            if (eligible.Count < 2) continue;

            var entriesByCustomer = agentEntries.GroupBy(e => e.CustomerNumber).ToDictionary(g => g.Key, g => g.ToList());

            HashSet<DateTime> LegalDatesFor(VisitPlanEntry entry)
            {
                distByCustomer.TryGetValue(entry.CustomerNumber, out var dd);
                var distDays = dd == null ? new List<DayOfWeek>() : VisitPlanGenerator.ActiveWeekdays(dd);
                var myEntries = entriesByCustomer[entry.CustomerNumber];

                if (distDays.Count > 0)
                {
                    var occurrences = VisitPlanGenerator.WeeklyOccurrences(distDays, monthStart, daysInMonth);
                    var effectiveCount = occurrences.Count > 0 ? Math.Min(myEntries.Count, occurrences.Count) : 0;
                    var canonical = new HashSet<DateTime>();
                    for (var i = 0; i < effectiveCount; i++)
                        canonical.Add(VisitPlanGenerator.CandidatesNearOccurrence(occurrences[i], monthStart, DayTypeOf).First());

                    // On its exact canonical (2-days-before-delivery, or fallback) target: stay strict,
                    // never move it off the one date the generator itself would have chosen.
                    if (entry.PlannedDate.HasValue && canonical.Contains(entry.PlannedDate.Value)) return canonical;

                    // Already off-canonical - this entry was itself capacity-bumped during generation
                    // (the agent's ideal day was full when it was placed). Widen to every candidate the
                    // generator's own fallback chain considers acceptable across ALL of this customer's
                    // occurrences this month, not just the single deterministic pick - the same
                    // tolerance the generator already used on this entry, just given more room to use it.
                    var widened = new HashSet<DateTime>();
                    foreach (var occ in occurrences)
                        foreach (var c in VisitPlanGenerator.CandidatesNearOccurrence(occ, monthStart, DayTypeOf))
                            widened.Add(c);
                    return widened;
                }

                var otherWeeks = myEntries.Where(x => x.Id != entry.Id).Select(x => VisitPlanGenerator.WeekStartOf(x.PlannedDate!.Value)).ToHashSet();
                var legal = new HashSet<DateTime>();
                for (var d = monthStart; d <= monthEnd; d = d.AddDays(1))
                {
                    if (DayTypeOf(d) == WorkDayType.Off) continue;
                    if (otherWeeks.Contains(VisitPlanGenerator.WeekStartOf(d))) continue;
                    legal.Add(d);
                }
                return legal;
            }

            var passes = 0;
            var improved = true;
            while (improved && passes < 20)
            {
                improved = false;
                passes++;
                for (var i = 0; i < eligible.Count; i++)
                {
                    for (var j = i + 1; j < eligible.Count; j++)
                    {
                        var a = eligible[i];
                        var b = eligible[j];
                        var cityA = CityOf(a)!;
                        var cityB = CityOf(b)!;
                        if (cityA == cityB) continue;
                        var dateA = a.PlannedDate!.Value;
                        var dateB = b.PlannedDate!.Value;
                        if (dateA == dateB) continue;

                        if (!LegalDatesFor(a).Contains(dateB)) continue;
                        if (!LegalDatesFor(b).Contains(dateA)) continue;

                        var delta = SwapDelta(agentEntries, CityOf, a, b, dateA, dateB);
                        if (delta < 0)
                        {
                            var now = DateTime.UtcNow;
                            a.PlannedDate = dateB;
                            a.CityOptimizedAt = now;
                            a.CityOptimizedNote = $"רוכז לפי עיר ({cityA}) - הוזז מ-{dateA:dd/MM} ל-{dateB:dd/MM}";
                            b.PlannedDate = dateA;
                            b.CityOptimizedAt = now;
                            b.CityOptimizedNote = $"רוכז לפי עיר ({cityB}) - הוזז מ-{dateB:dd/MM} ל-{dateA:dd/MM}";
                            swapsApplied++;
                            agentsAffected.Add(agentGroup.Key);
                            improved = true;
                        }
                    }
                }
            }
        }

        await db.SaveChangesAsync();
        var fragmentationAfter = TotalFragmentation(entries, CityOf, AgentKeyOf);
        return new CityOptimizationResult(swapsApplied, fragmentationBefore, fragmentationAfter, agentsAffected.Count);
    }

    /// <summary>Sum, across every (agent, day), of the number of DISTINCT cities visited that day -
    /// the quantity the optimizer minimizes. Entries with no city on file don't participate.</summary>
    private static int TotalFragmentation(List<VisitPlanEntry> entries, Func<VisitPlanEntry, string?> cityOf, Func<VisitPlanEntry, string?> agentKeyOf) =>
        entries
            .Where(e => e.PlannedDate.HasValue && !string.IsNullOrWhiteSpace(cityOf(e)) && !string.IsNullOrWhiteSpace(agentKeyOf(e)))
            .GroupBy(e => (Agent: agentKeyOf(e), Date: e.PlannedDate!.Value.Date))
            .Sum(g => g.Select(e => cityOf(e)).Distinct().Count());

    /// <summary>Change in total fragmentation (negative = improvement) from swapping a and b's dates,
    /// computed locally from just the two affected days rather than the whole plan.</summary>
    private static int SwapDelta(List<VisitPlanEntry> agentEntries, Func<VisitPlanEntry, string?> cityOf,
        VisitPlanEntry a, VisitPlanEntry b, DateTime dateA, DateTime dateB)
    {
        var cityA = cityOf(a)!;
        var cityB = cityOf(b)!;
        var dayAEntries = agentEntries.Where(e => e.PlannedDate == dateA && !string.IsNullOrWhiteSpace(cityOf(e))).ToList();
        var dayBEntries = agentEntries.Where(e => e.PlannedDate == dateB && !string.IsNullOrWhiteSpace(cityOf(e))).ToList();

        var before = dayAEntries.Select(cityOf).Distinct().Count() + dayBEntries.Select(cityOf).Distinct().Count();
        var afterA = dayAEntries.Where(e => e.Id != a.Id).Select(cityOf).Append(cityB).Distinct().Count();
        var afterB = dayBEntries.Where(e => e.Id != b.Id).Select(cityOf).Append(cityA).Distinct().Count();

        return (afterA + afterB) - before;
    }
}
