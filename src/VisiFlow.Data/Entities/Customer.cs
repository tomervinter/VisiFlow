using VisiFlow.Data.Enums;

namespace VisiFlow.Data.Entities;

/// <summary>Table Customers. A monthly SNAPSHOT of a customer's master data, as loaded from that
/// month's Excel file - each (company, customer number, year, month) combination is its own row, never
/// overwritten by a later month's upload. This lets a visit plan for an old month be regenerated later
/// using the exact data it was originally built from, even after newer months have since been loaded.</summary>
public class Customer
{
    /// <summary>מזהה פנימי (מפתח ראשי).</summary>
    public int Id { get; set; }
    /// <summary>החברה (הטננט) שהלקוח שייך אליה - כל לקוח שייך לחברה אחת בלבד.</summary>
    public int CompanyId { get; set; }
    public Company Company { get; set; } = null!;
    /// <summary>מספר לקוח - ייחודי בתוך החברה (לא גלובלית, כדי שחברות שונות לא יתנגשו במספור לקוחות),
    /// ובתוך חודש/שנה נתונים (ראו Year/Month) - לא גלובלית לאורך זמן.</summary>
    public string CustomerNumber { get; set; } = null!;
    /// <summary>שנת התמונה - לאיזה חודש טעינה שייכת השורה הזו (לא חודש קלנדרי "נוכחי").</summary>
    public int Year { get; set; }
    /// <summary>חודש התמונה (1-12).</summary>
    public int Month { get; set; }
    /// <summary>שם לקוח</summary>
    public string CustomerName { get; set; } = null!;
    /// <summary>סוכן משוייך</summary>
    public string? AgentName { get; set; }
    /// <summary>תעודת זהות הסוכן - ישמש בהמשך כשם המשתמש שלו בכניסה למערכת. נטען מקובץ האקסל.</summary>
    public string? AgentIdNumber { get; set; }
    /// <summary>ערוץ מכר</summary>
    public string? Channel { get; set; }
    /// <summary>נתוני מכר מצטברים השנה</summary>
    public decimal? SalesYtdCurrentYear { get; set; }
    /// <summary>נתוני מכר מצטברים אשתקד</summary>
    public decimal? SalesYtdPreviousYear { get; set; }
    /// <summary>גודל לקוח</summary>
    public string? CustomerSize { get; set; }
    /// <summary>טלפון</summary>
    public string? Phone { get; set; }
    /// <summary>כתובת</summary>
    public string? Address { get; set; }
    /// <summary>עיר - נטענת כעמודה נפרדת מקובץ האקסל (לא מנותחת מתוך הכתובת), משמשת לקיבוץ ביקורים גיאוגרפית.</summary>
    public string? City { get; set; }
    /// <summary>כמות הזמנות ממוצעת בחודש</summary>
    public decimal? AvgMonthlyOrders { get; set; }
    /// <summary>לקוח היה פעיל בכל התקופה</summary>
    public bool WasActiveAllPeriod { get; set; }
    /// <summary>סטטוס לקוח נוכחי - פעיל / לא פעיל.</summary>
    public CustomerStatus Status { get; set; } = CustomerStatus.Active;
    /// <summary>מועד הטעינה/עדכון האחרון מקובץ האקסל.</summary>
    public DateTime UpdatedAt { get; set; }
}
