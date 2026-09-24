namespace VisiFlow.Data.Entities;

/// <summary>Table VisitPlanWeights. Per-company relative importance (%) of each visit-plan scoring
/// factor - must sum to 100. One row per company, created with equal defaults (20 each) on first use.</summary>
public class VisitPlanWeights
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public Company Company { get; set; } = null!;
    /// <summary>ירידה במכר (הפרש כספי מוחלט בין השנה לאשתקד).</summary>
    public decimal SalesDropWeight { get; set; } = 20;
    /// <summary>קיום יום הפצה החודש - חשיבות תזמון מדוייק ללקוח.</summary>
    public decimal DistributionWeight { get; set; } = 20;
    /// <summary>תדירות רכישה בפועל (ממוצע הזמנות חודשי).</summary>
    public decimal FrequencyWeight { get; set; } = 20;
    /// <summary>תקן ביקורים שהוגדר ללקוח.</summary>
    public decimal VisitStandardWeight { get; set; } = 20;
    /// <summary>ימים שעברו מאז הביקור האחרון.</summary>
    public decimal DaysSinceVisitWeight { get; set; } = 20;
    /// <summary>קיבולת יומית מקסימלית של סוכן ביום עבודה מלא (כמות פגישות).</summary>
    public int FullDayCapacity { get; set; } = 8;
    /// <summary>קיבולת יומית מקסימלית של סוכן בחצי יום עבודה (כמות פגישות).</summary>
    public int HalfDayCapacity { get; set; } = 4;
    /// <summary>true = הגנרטור משתמש בקיבולת לפי ערוץ מכר (ראו ChannelCapacity.cs), לפי הערוץ המשוייך של
    /// כל סוכן. false (ברירת מחדל) = הגנרטור תמיד משתמש ב-FullDayCapacity/HalfDayCapacity הכלליים כאן,
    /// גם אם יש שורות ChannelCapacity מוגדרות - הן פשוט לא בשימוש עד שהמנהל בוחר במפורש להפעיל את
    /// המצב הזה (ראו "הגדרת קיבולת" ב"יצירת תוכנית ביקורים").</summary>
    public bool UseChannelCapacity { get; set; } = false;
}
