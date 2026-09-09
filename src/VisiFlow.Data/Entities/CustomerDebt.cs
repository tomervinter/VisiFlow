namespace VisiFlow.Data.Entities;

/// <summary>Table CustomerDebts. The current collection/debt snapshot for a customer - deliberately
/// kept OUT of the monthly Customer snapshot (see Customer.cs) and NOT month-scoped at all, unlike it:
/// debt data always represents "as of the last upload", not a specific month's picture worth preserving
/// historically, so a fresh upload for a customer number OVERWRITES that customer's existing row in
/// place rather than adding a new dated one. One row per (company, customer number), same pattern as
/// CustomerVisitStandard.</summary>
public class CustomerDebt
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public Company Company { get; set; } = null!;
    public string CustomerNumber { get; set; } = null!;
    /// <summary>חוב לגביה - הסכום הכולל שיש לגבות מהלקוח.</summary>
    public decimal? DebtToCollect { get; set; }
    /// <summary>חוב בפיגור - מתוך חוב הגביה, הסכום שכבר חרג ממועד התשלום.</summary>
    public decimal? OverdueDebt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
