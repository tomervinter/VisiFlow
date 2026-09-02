namespace VisiFlow.Data.Entities;

/// <summary>Table Companies. A tenant - VisiFlow serves multiple companies, each with its own customers/agents/users.</summary>
public class Company
{
    public int Id { get; set; }
    /// <summary>שם חברה</summary>
    public string Name { get; set; } = null!;
    /// <summary>הקפאה הפיכה (לא מחיקה) - חברה קפואה חוסמת התחברות לכל המשתמשים שלה, אבל שומרת את כל
    /// הנתונים שלה שלמים. ברירת מחדל true כדי שחברות קיימות לא ייחסמו כתוצאה מהמיגרציה שמוסיפה שדה זה.</summary>
    public bool IsActive { get; set; } = true;
}
