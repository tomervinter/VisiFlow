using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace VisiFlow.Data;

/// <summary>Lets `dotnet ef migrations` generate/apply migrations without a running app.</summary>
public class VisiFlowDbContextFactory : IDesignTimeDbContextFactory<VisiFlowDbContext>
{
    public VisiFlowDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<VisiFlowDbContext>();
        optionsBuilder.UseSqlite("Data Source=design-time.db");
        return new VisiFlowDbContext(optionsBuilder.Options);
    }
}
