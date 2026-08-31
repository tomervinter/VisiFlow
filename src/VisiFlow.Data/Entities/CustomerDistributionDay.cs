namespace VisiFlow.Data.Entities;

/// <summary>Table CustomerDistributionDays. Which weekdays a customer normally receives deliveries -
/// one row per (customer, year, month), reloaded from Excel only for months where something changed
/// (the schedule is otherwise treated as fixed).</summary>
public class CustomerDistributionDay
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public Company Company { get; set; } = null!;
    /// <summary>מספר לקוח (לא FK מחייב - כך שניתן לטעון ימי הפצה גם אם סדר הטעינה שונה מטעינת הלקוחות).</summary>
    public string CustomerNumber { get; set; } = null!;
    public int Year { get; set; }
    /// <summary>1-12</summary>
    public int Month { get; set; }
    public bool Sunday { get; set; }
    public bool Monday { get; set; }
    public bool Tuesday { get; set; }
    public bool Wednesday { get; set; }
    public bool Thursday { get; set; }
    public bool Friday { get; set; }
    public bool Saturday { get; set; }
}
