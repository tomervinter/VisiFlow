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
    /// <summary>מזהה הלקוח המקביל ב-Stripe - null עד שההרשמה יצרה אותו (או שאין מפתח Stripe מוגדר בכלל,
    /// ראו StripeService), וגם אז רק לחברות שנוצרו דרך ה-signup הציבורי, לא חברות שנוצרו ע"י מנהל-על
    /// דרך "ניהול". המפתח שדרכו webhook.cs מזהה איזו חברה להקפיא כשמנוי מבוטל/תשלום נכשל.</summary>
    public string? StripeCustomerId { get; set; }
}
