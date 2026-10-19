namespace VisiFlow.Data.Entities;

/// <summary>Table PendingChecks. A customer with a check waiting to be picked up by their agent.
/// Presence of a row IS the flag - there's no amount/count. Like CustomerDebt this is deliberately NOT
/// month-scoped (always "as of the last upload"), but unlike it the upload is a FULL REPLACE, not an
/// upsert: a customer absent from the newest file is removed, since a collected check has to disappear
/// from the agent's view without anyone doing anything else. One row per (company, customer number).
/// UpdatedAt = the last upload that (re)confirmed this check is still pending, shown to the agent so
/// they can judge how fresh the information is.</summary>
public class PendingCheck
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public Company Company { get; set; } = null!;
    public string CustomerNumber { get; set; } = null!;
    public DateTime UpdatedAt { get; set; }
}
