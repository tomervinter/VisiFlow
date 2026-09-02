namespace VisiFlow.Data.Entities;

/// <summary>Table Users. A real login account for the ADMIN interface (home.html) only - the agent
/// interface (agent.html) keeps its own separate, simpler identify-by-ID-number flow and is not
/// affected by this. Every user belongs to one company and (see IsSuperAdmin) is normally confined to
/// it - there is no other role/permission tiering beyond AllowedChannels below.</summary>
public class User
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public Company Company { get; set; } = null!;
    public string Username { get; set; } = null!;
    /// <summary>PBKDF2 hash produced by Microsoft.AspNetCore.Identity.PasswordHasher&lt;User&gt; - never a plain password.</summary>
    public string PasswordHash { get; set; } = null!;
    public string DisplayName { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    /// <summary>Comma-separated list of Customer.Channel values this user may see on READ/display
    /// endpoints - write actions (plan generation, city optimization, Excel import) stay whole-company
    /// regardless. Null/empty = unrestricted, sees every channel (e.g. management users).</summary>
    public string? AllowedChannels { get; set; }
    /// <summary>Crosses the normal one-company boundary: can see/manage every company (not just their
    /// own CompanyId above), and is the only role allowed to create new companies (see POST
    /// /api/companies). Defaults false - never settable through the "add user" screen, only ever
    /// granted directly against the database, to keep it from being self-escalatable.</summary>
    public bool IsSuperAdmin { get; set; }
}
