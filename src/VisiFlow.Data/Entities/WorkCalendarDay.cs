using VisiFlow.Data.Enums;

namespace VisiFlow.Data.Entities;

/// <summary>Table WorkCalendarDays. Explicit day-type overrides for the company's work calendar - only
/// days an admin actually clicked are stored; any date with no row here falls back to the natural
/// default (Sunday-Thursday = Full, Friday/Saturday = Off), computed client-side.</summary>
public class WorkCalendarDay
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public Company Company { get; set; } = null!;
    /// <summary>התאריך (ללא רכיב שעה).</summary>
    public DateTime Date { get; set; }
    public WorkDayType DayType { get; set; }
}
