using VisiFlow.Data.Enums;

// Deliberately no namespace - matches VisitPlanGenerator.cs/VisitPlanCityOptimizer.cs/Program.cs's
// implicit global namespace.

/// <summary>
/// Israeli (Jewish) holidays that close stores, used as an automatic work-calendar default the same
/// way Friday/Saturday already are: the holiday day itself defaults to Off and its eve to Half,
/// UNLESS a company has an explicit WorkCalendarDays override for that date (checked first by every
/// caller here, same precedence as the existing Fri/Sat default). Kept as one shared source of dates
/// so the visit-plan generator, the city optimizer, and the admin's "יומן ימי עבודה" screen (see the
/// mirrored ISRAELI_HOLIDAYS table in home.html) all agree on which days are holidays.
///
/// Dates come from the Hebrew calendar (lunar, shifts every Gregorian year) and are listed explicitly
/// per year rather than computed live, to avoid re-deriving Hebrew calendar math (molad + postponement
/// rules) here. Covers 2024-2028; like the Fri/Sat default, any single date is one admin click away
/// from being overridden on the "יומן ימי עבודה" screen, so a rare off-by-one on the exact Gregorian
/// date is trivially fixable per-date rather than a scheduling failure.
/// </summary>
public static class IsraeliHolidays
{
    private static readonly Dictionary<string, WorkDayType> Lookup = Build();

    public static WorkDayType? TypeFor(DateTime date) =>
        Lookup.TryGetValue(date.ToString("yyyy-MM-dd"), out var t) ? t : null;

    private static Dictionary<string, WorkDayType> Build()
    {
        // [holidayName] = (eve date-or-null, Off-day dates)
        var years = new (string Name, string? Eve, string[] Days)[]
        {
            // 2024
            ("ראש השנה 2024", "2024-10-02", new[] { "2024-10-03", "2024-10-04" }),
            ("יום כיפור 2024", "2024-10-11", new[] { "2024-10-12" }),
            ("סוכות 2024", "2024-10-16", new[] { "2024-10-17" }),
            ("שמחת תורה 2024", null, new[] { "2024-10-24" }),
            ("פסח 2024", "2024-04-22", new[] { "2024-04-23" }),
            ("שביעי של פסח 2024", null, new[] { "2024-04-29" }),
            ("שבועות 2024", "2024-06-11", new[] { "2024-06-12" }),
            // 2025
            ("ראש השנה 2025", "2025-09-22", new[] { "2025-09-23", "2025-09-24" }),
            ("יום כיפור 2025", "2025-10-01", new[] { "2025-10-02" }),
            ("סוכות 2025", "2025-10-06", new[] { "2025-10-07" }),
            ("שמחת תורה 2025", null, new[] { "2025-10-14" }),
            ("פסח 2025", "2025-04-12", new[] { "2025-04-13" }),
            ("שביעי של פסח 2025", null, new[] { "2025-04-19" }),
            ("שבועות 2025", "2025-06-01", new[] { "2025-06-02" }),
            // 2026
            ("ראש השנה 2026", "2026-09-11", new[] { "2026-09-12", "2026-09-13" }),
            ("יום כיפור 2026", "2026-09-20", new[] { "2026-09-21" }),
            ("סוכות 2026", "2026-09-25", new[] { "2026-09-26" }),
            ("שמחת תורה 2026", null, new[] { "2026-10-03" }),
            ("פסח 2026", "2026-04-01", new[] { "2026-04-02" }),
            ("שביעי של פסח 2026", null, new[] { "2026-04-08" }),
            ("שבועות 2026", "2026-05-21", new[] { "2026-05-22" }),
            // 2027
            ("ראש השנה 2027", "2027-10-01", new[] { "2027-10-02", "2027-10-03" }),
            ("יום כיפור 2027", "2027-10-10", new[] { "2027-10-11" }),
            ("סוכות 2027", "2027-10-15", new[] { "2027-10-16" }),
            ("שמחת תורה 2027", null, new[] { "2027-10-23" }),
            ("פסח 2027", "2027-04-21", new[] { "2027-04-22" }),
            ("שביעי של פסח 2027", null, new[] { "2027-04-28" }),
            ("שבועות 2027", "2027-06-10", new[] { "2027-06-11" }),
            // 2028
            ("ראש השנה 2028", "2028-09-20", new[] { "2028-09-21", "2028-09-22" }),
            ("יום כיפור 2028", "2028-09-29", new[] { "2028-09-30" }),
            ("סוכות 2028", "2028-10-04", new[] { "2028-10-05" }),
            ("שמחת תורה 2028", null, new[] { "2028-10-12" }),
            ("פסח 2028", "2028-04-10", new[] { "2028-04-11" }),
            ("שביעי של פסח 2028", null, new[] { "2028-04-17" }),
            ("שבועות 2028", "2028-05-30", new[] { "2028-05-31" })
        };

        var map = new Dictionary<string, WorkDayType>();
        foreach (var (_, eve, days) in years)
        {
            foreach (var d in days) map[d] = WorkDayType.Off;
            if (eve != null) map[eve] = WorkDayType.Half;
        }
        return map;
    }
}
