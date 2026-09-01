using VisiFlow.Data;
using VisiFlow.Data.Entities;
using VisiFlow.Data.Enums;
using Microsoft.EntityFrameworkCore;

// No namespace - matches VisitPlanGenerator.cs and Program.cs's implicit global namespace.

public record CityOptimizationResult(int SwapsApplied, int FragmentationBefore, int FragmentationAfter, int AgentsAffected, int NotConsolidated);

/// <summary>
/// Post-process pass over an ALREADY-GENERATED month's visit plan: reorders which day each visit
/// lands on (within that customer's own already-valid date options) so an agent's same-city
/// customers land on the same day as much as possible, reducing daily driving. Never touches
/// PriorityScore, never adds/removes entries, never moves a visit between agents, and never moves
/// an entry that was already placed by a human (ManuallyModifiedAt set).
///
/// Two passes, run in order:
/// 1. Direct consolidation - for each agent+city-cluster+week that spans more than one day, moves
///    every eligible entry directly onto whichever day already has the most of that cluster's visits
///    that week, as long as the move is legal for that customer (see below) and the target day still
///    has spare daily capacity (VisitPlanWeights). Unlike a swap, this needs no "trade partner" - it's
///    what actually satisfies "put every visit to the same city that week on the same day", which a
///    pairwise swap alone can't guarantee (nothing to swap with = nothing moves, even with room to spare).
/// 2. Pairwise swap local-search (same agent, different cities, each side's new date legal for the
///    other's customer) - repeatedly finds a swap that strictly reduces the total count of
///    distinct-city-per-agent-per-day pairs, until no improving swap remains or a pass cap is hit.
///    Capacity-neutral by construction (one entry out, one in), so needs no capacity check itself.
///
/// "Legal date" for a candidate target is deliberately defined the same way the generator itself
/// would have placed that customer: for a customer with distribution days on file, the exact set
/// VisitPlanGenerator.PreferredDates would propose for their current visit count this month (so the
/// distribution-day-minus-1/2 placement rule is never violated); for a customer with no distribution
/// days, any working day in the month not already used by another of their own entries that same
/// (Sun-Sat) week (the one hard rule that actually applied to them).
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
        if (entries.Count == 0) return new CityOptimizationResult(0, 0, 0, 0, 0);

        var customerNumbers = entries.Select(e => e.CustomerNumber).Distinct().ToList();
        // The plan's own (year, month) is exactly the customer snapshot it was built from - entries
        // never span months (see VisitPlanGenerator), so filtering to this one snapshot is correct.
        var customers = await db.Customers
            .Where(c => c.CompanyId == companyId && c.Year == year && c.Month == month && customerNumbers.Contains(c.CustomerNumber))
            .ToDictionaryAsync(c => c.CustomerNumber);

        var distByCustomer = await VisitPlanGenerator.LoadDistributionDaysWithFallback(db, companyId, year, month, customerNumbers);

        // Admin-defined "nearby cities" clusters (see CityGroup.cs) - maps each grouped city to a
        // shared cluster key so the fragmentation/swap logic below treats them as one location. A city
        // not in any group maps to itself (its own singleton cluster) via ClusterOf's fallback below.
        var cityToCluster = new Dictionary<string, string>();
        foreach (var group in await db.CityGroups.Where(g => g.CompanyId == companyId).ToListAsync())
        {
            var clusterKey = $"__group_{group.Id}";
            foreach (var city in group.Cities.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                cityToCluster[city] = clusterKey;
        }

        // Company-wide daily capacity (see VisitPlanWeights) - same numbers GenerateAsync placed
        // entries against. Not persisted here if missing (this is a read for an existing plan, not
        // plan creation) - just falls back to the same defaults VisitPlanWeights itself defaults to.
        var weights = await db.VisitPlanWeights.FirstOrDefaultAsync(w => w.CompanyId == companyId)
            ?? new VisitPlanWeights { FullDayCapacity = 8, HalfDayCapacity = 4 };

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
        // The grouping key actually used for fragmentation/swap decisions below - a grouped city's
        // cluster key, or the raw city itself if it's in no group. CityOf stays the raw city for
        // display (CityOptimizedNote), ClusterOf is what "same location" means for this optimizer.
        string? ClusterOf(VisitPlanEntry e) { var city = CityOf(e); return city == null ? null : cityToCluster.GetValueOrDefault(city, city); }
        // AgentIdNumber, not AgentName - two different real agents can share a display name, and
        // grouping by name would let this optimizer "consolidate" one agent's city visits onto a
        // day that actually belongs to a different agent who happens to share their name.
        string? AgentKeyOf(VisitPlanEntry e) => customers.TryGetValue(e.CustomerNumber, out var c) ? VisitPlanGenerator.AgentKey(c) : e.AgentName;

        var fragmentationBefore = TotalFragmentation(entries, ClusterOf, AgentKeyOf);

        var swapsApplied = 0;
        var notConsolidated = 0;
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

            // ---- Pass 1: direct same-day consolidation within an (city-cluster, week) group ----
            // Tracks each day's live occupancy (ALL of the agent's entries, including manually-moved
            // ones - they still take up a capacity slot even though they can't themselves be moved) so
            // a move here never pushes a day over its daily capacity.
            var occupancy = agentEntries.Where(e => e.PlannedDate.HasValue)
                .GroupBy(e => e.PlannedDate!.Value.Date).ToDictionary(g => g.Key, g => g.Count());
            var eligibleIds = eligible.Select(e => e.Id).ToHashSet();
            // Grouped from ALL of the agent's entries (not just eligible ones) - a manually-moved entry
            // still legitimately anchors which day is "the" day for its cluster/week, even though it
            // can't be moved itself. Biggest groups first, so they get first claim on shared capacity
            // when two clusters in the same week are both competing for room on the same target day.
            var consolidationGroups = agentEntries
                .Where(e => e.PlannedDate.HasValue && !string.IsNullOrWhiteSpace(CityOf(e)))
                .GroupBy(e => (Cluster: ClusterOf(e), Week: VisitPlanGenerator.WeekStartOf(e.PlannedDate!.Value)))
                .Where(g => g.Select(e => e.PlannedDate!.Value.Date).Distinct().Count() > 1)
                .OrderByDescending(g => g.Count())
                .ToList();

            foreach (var group in consolidationGroups)
            {
                // Target = the day already used by the most of this cluster's visits this week (ties
                // -> earliest) - minimizes how many entries actually need to move.
                var targetDay = group.GroupBy(e => e.PlannedDate!.Value.Date)
                    .OrderByDescending(dg => dg.Count()).ThenBy(dg => dg.Key)
                    .First().Key;
                var targetCapacity = VisitPlanGenerator.CapacityFor(DayTypeOf(targetDay), weights);

                foreach (var entry in group.Where(e => e.PlannedDate!.Value.Date != targetDay).OrderBy(e => e.PlannedDate))
                {
                    if (!eligibleIds.Contains(entry.Id)) continue; // manually-moved - anchor only, never itself moved
                    if (occupancy.GetValueOrDefault(targetDay) >= targetCapacity) { notConsolidated++; continue; }
                    if (!LegalDatesFor(entry).Contains(targetDay)) { notConsolidated++; continue; }

                    var oldDate = entry.PlannedDate!.Value.Date;
                    var city = CityOf(entry)!;
                    occupancy[oldDate] = occupancy.GetValueOrDefault(oldDate) - 1;
                    occupancy[targetDay] = occupancy.GetValueOrDefault(targetDay) + 1;
                    entry.PlannedDate = targetDay;
                    entry.CityOptimizedAt = DateTime.UtcNow;
                    entry.CityOptimizedNote = $"רוכז לפי עיר ({city}) - הוזז מ-{oldDate:dd/MM} ל-{targetDay:dd/MM}";
                    swapsApplied++;
                    agentsAffected.Add(agentGroup.Key);
                }
            }

            // ---- Pass 2: pairwise swap local-search (unchanged) ----
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
                        if (ClusterOf(a) == ClusterOf(b)) continue; // already the same location (or same group) - nothing to gain
                        var dateA = a.PlannedDate!.Value;
                        var dateB = b.PlannedDate!.Value;
                        if (dateA == dateB) continue;

                        if (!LegalDatesFor(a).Contains(dateB)) continue;
                        if (!LegalDatesFor(b).Contains(dateA)) continue;

                        var delta = SwapDelta(agentEntries, ClusterOf, a, b, dateA, dateB);
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
        var fragmentationAfter = TotalFragmentation(entries, ClusterOf, AgentKeyOf);
        return new CityOptimizationResult(swapsApplied, fragmentationBefore, fragmentationAfter, agentsAffected.Count, notConsolidated);
    }

    /// <summary>Sum, across every (agent, day), of the number of DISTINCT location clusters visited
    /// that day - the quantity the optimizer minimizes (a "cluster" is a city-group's shared key, or a
    /// plain city for one in no group - see ClusterOf). Entries with no city on file don't participate.</summary>
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
