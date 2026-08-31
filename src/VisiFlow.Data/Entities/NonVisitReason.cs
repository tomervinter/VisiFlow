namespace VisiFlow.Data.Entities;

/// <summary>Table NonVisitReasons. Editable catalog of reasons an agent can pick when logging that they
/// didn't visit a customer they were scheduled to see (used from the agent's tablet/phone app - not built yet).</summary>
public class NonVisitReason
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public Company Company { get; set; } = null!;
    /// <summary>נוסח הסיבה כפי שהסוכן יראה אותה.</summary>
    public string Text { get; set; } = null!;
    /// <summary>סדר הצגה ברשימה.</summary>
    public int SortOrder { get; set; }
}
