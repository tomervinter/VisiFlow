namespace VisiFlow.Data.Entities;

/// <summary>Table CityGroups. Admin-defined cluster of Customer.City values that should be treated as
/// one location for the visit-plan city optimizer (VisitPlanCityOptimizer) - e.g. "רמת גן"+"גבעתיים"
/// close enough together that an agent's visits to both should still land on the same day, even
/// though they're not literally the same city string. Purely a display/optimization aid - never
/// changes a Customer's own City field.</summary>
public class CityGroup
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public Company Company { get; set; } = null!;
    public string Name { get; set; } = null!;
    /// <summary>Comma-separated Customer.City values belonging to this cluster - same convention as
    /// User.AllowedChannels. Always at least 2 (a 1-city "group" would be meaningless).</summary>
    public string Cities { get; set; } = null!;
}
