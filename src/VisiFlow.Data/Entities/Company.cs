namespace VisiFlow.Data.Entities;

/// <summary>Table Companies. A tenant - VisiFlow serves multiple companies, each with its own customers/agents/users.</summary>
public class Company
{
    public int Id { get; set; }
    /// <summary>שם חברה</summary>
    public string Name { get; set; } = null!;
}
