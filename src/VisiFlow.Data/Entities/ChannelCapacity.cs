namespace VisiFlow.Data.Entities;

/// <summary>Table ChannelCapacities. Per-company, per-sales-channel override of the daily agent
/// capacity used by VisitPlanGenerator - an agent whose customers are mostly in this channel (see
/// VisitPlanGenerator.PrimaryChannelOf) uses this channel's numbers instead of the company-wide default
/// (VisitPlanWeights.FullDayCapacity/HalfDayCapacity). One row per (company, channel), lazily seeded
/// from that same company-wide default the first time a company's list is requested (see
/// GET /api/visitplan/channelcapacity) for every channel currently present in its customer data.</summary>
public class ChannelCapacity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public Company Company { get; set; } = null!;
    /// <summary>שם ערוץ המכר (כפי שמופיע על הלקוחות עצמם).</summary>
    public string Channel { get; set; } = null!;
    /// <summary>קיבולת יומית מקסימלית של סוכן ביום עבודה מלא, לסוכנים ששייכים לערוץ הזה.</summary>
    public int FullDayCapacity { get; set; }
    /// <summary>קיבולת יומית מקסימלית של סוכן בחצי יום עבודה, לסוכנים ששייכים לערוץ הזה.</summary>
    public int HalfDayCapacity { get; set; }
}
