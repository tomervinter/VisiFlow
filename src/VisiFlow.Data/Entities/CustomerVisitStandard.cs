namespace VisiFlow.Data.Entities;

/// <summary>Table CustomerVisitStandards. The manually-set required weekly visit count for a customer -
/// deliberately kept OUT of the monthly Customer snapshot (see Customer.cs) so it survives automatically
/// every time a new month's customer file is loaded, instead of needing to be re-entered each month.
/// One row per (company, customer number), independent of any particular month.</summary>
public class CustomerVisitStandard
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public Company Company { get; set; } = null!;
    public string CustomerNumber { get; set; } = null!;
    /// <summary>תקן ביקורים נדרש בשבוע (למשל 0.5 = פעם בשבועיים, 1 = שבועי, 2 = פעמיים בשבוע).</summary>
    public decimal? RequiredVisitsPerWeek { get; set; }
    public DateTime UpdatedAt { get; set; }
}
