using VisiFlow.Data.Enums;

namespace VisiFlow.Data.Entities;

/// <summary>Table CustomerVisits. Log of actual agent-customer visits (or missed visits) - the data
/// source for "days since last visit" in the visit-planning algorithm. Recorded by an admin today;
/// eventually this is what the agent's tablet/phone app would write directly.</summary>
public class CustomerVisit
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public Company Company { get; set; } = null!;
    public string CustomerNumber { get; set; } = null!;
    /// <summary>תאריך הביקור (המתוכנן/בפועל).</summary>
    public DateTime VisitDate { get; set; }
    /// <summary>הסוכן שביקר (או שהיה אמור לבקר) - טקסט חופשי, כמו AgentName בטבלת הלקוחות.</summary>
    public string? AgentName { get; set; }
    public VisitOutcome Outcome { get; set; } = VisitOutcome.Visited;
    /// <summary>כאשר Outcome=NotVisited - הסיבה שנבחרה מתוך קטלוג "סיבות אי ביקור".</summary>
    public int? NonVisitReasonId { get; set; }
    public NonVisitReason? NonVisitReason { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
}
