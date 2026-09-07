namespace VisiFlow.Data.Entities;

/// <summary>Table AuditLogEntries. An append-only record of destructive/sensitive admin actions
/// (freezing/deleting a company, deleting a user, wiping a month of customers, wiping a visit plan) -
/// written by the endpoints that perform those actions, never edited or deleted through the app itself.
/// Surfaced to super-admins only, on the "ניהול" screen.</summary>
public class AuditLogEntry
{
    public int Id { get; set; }
    /// <summary>החברה שהפעולה בוצעה עליה/בתוכה (לא בהכרח החברה של המבצע - למשל מנהל-על שמקפיא חברה אחרת).</summary>
    public int CompanyId { get; set; }
    public Company Company { get; set; } = null!;
    public int ActorUserId { get; set; }
    /// <summary>שם המשתמש המבצע, נשמר כטקסט קבוע בזמן הפעולה - כך שהרשומה נשארת קריאה גם אם המשתמש נמחק מאוחר יותר.</summary>
    public string ActorUsername { get; set; } = null!;
    /// <summary>תיאור קצר וקבוע-מראש של סוג הפעולה, למשל "הקפאת חברה" / "מחיקת משתמש" / "מחיקת חודש לקוחות".</summary>
    public string Action { get; set; } = null!;
    /// <summary>תיאור חופשי וקריא של המושא הספציפי, למשל "חברה #3 (בית קפה השכונה)" או "משתמש rachel (רחל כהן)".</summary>
    public string TargetDescription { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}
