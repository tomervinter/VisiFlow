namespace VisiFlow.Data.Entities;

/// <summary>Table VisitFrequencyPresets. Editable catalog of named visit-frequency shortcuts an admin
/// picks from in "תקן ביקורים ללקוח" (e.g. "פעם בשבוע" = 1) - per company, since different companies
/// can want different named cadences. Seeded with the app's original 11 built-in presets the first time
/// a company's list is requested (see GET /api/visitfrequencypresets), then freely editable from there.
/// Purely a UI convenience: the value it fills in is the same plain RequiredVisitsPerWeek decimal used
/// everywhere else (see CustomerVisitStandard.cs) - deleting a preset never touches any customer's
/// already-set standard, it just stops offering that value as a named shortcut going forward.</summary>
public class VisitFrequencyPreset
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public Company Company { get; set; } = null!;
    /// <summary>שם התדירות כפי שיוצג ברשימת הבחירה (למשל "פעם בשבועיים").</summary>
    public string Label { get; set; } = null!;
    /// <summary>כמות ביקורים בשבוע (0 = אין צורך בביקורים כלל ללקוח עם התדירות הזו).</summary>
    public decimal VisitsPerWeek { get; set; }
    /// <summary>סדר הצגה ברשימה.</summary>
    public int SortOrder { get; set; }
}
