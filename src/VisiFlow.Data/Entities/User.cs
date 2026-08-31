namespace VisiFlow.Data.Entities;

/// <summary>Table Users. A real login account for the ADMIN interface (home.html) only - the agent
/// interface (agent.html) keeps its own separate, simpler identify-by-ID-number flow and is not
/// affected by this. Every user belongs to one company; there is no role/permission tiering yet -
/// any logged-in user can do everything, including managing other users.</summary>
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
}
