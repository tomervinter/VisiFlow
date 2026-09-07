using Microsoft.AspNetCore.Identity;
using VisiFlow.Data.Entities;

namespace VisiFlow.Tests;

internal static class TestHelpers
{
    /// <summary>Sunday of the (Sun-Sat) week containing <paramref name="date"/> - deliberately
    /// duplicated here (rather than referencing VisitPlanGenerator.WeekStartOf, which is internal to
    /// VisiFlow.Api) so tests don't require touching production source to add InternalsVisibleTo.</summary>
    public static DateTime WeekStart(DateTime date) => date.Date.AddDays(-(int)date.DayOfWeek);

    /// <summary>Hashes a password exactly the way Program.cs does (the same PasswordHasher&lt;User&gt;
    /// type, registered as a singleton in Program.cs's DI container) so directly-seeded users can log
    /// in through the real /api/auth/login endpoint.</summary>
    public static string HashPassword(User user, string password) => new PasswordHasher<User>().HashPassword(user, password);
}
