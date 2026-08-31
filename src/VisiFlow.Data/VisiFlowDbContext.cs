using VisiFlow.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace VisiFlow.Data;

public class VisiFlowDbContext : DbContext
{
    public VisiFlowDbContext(DbContextOptions<VisiFlowDbContext> options) : base(options)
    {
    }

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CustomerVisitStandard> CustomerVisitStandards => Set<CustomerVisitStandard>();
    public DbSet<NonVisitReason> NonVisitReasons => Set<NonVisitReason>();
    public DbSet<CustomerDistributionDay> CustomerDistributionDays => Set<CustomerDistributionDay>();
    public DbSet<WorkCalendarDay> WorkCalendarDays => Set<WorkCalendarDay>();
    public DbSet<CustomerVisit> CustomerVisits => Set<CustomerVisit>();
    public DbSet<VisitPlanWeights> VisitPlanWeights => Set<VisitPlanWeights>();
    public DbSet<VisitPlanEntry> VisitPlanEntries => Set<VisitPlanEntry>();
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Company>(entity =>
        {
            entity.ToTable("Companies");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Name).IsRequired().HasMaxLength(200);
        });

        modelBuilder.Entity<Customer>(entity =>
        {
            entity.ToTable("Customers");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.CustomerNumber).IsRequired().HasMaxLength(30);
            entity.Property(c => c.CustomerName).IsRequired().HasMaxLength(200);
            entity.Property(c => c.AgentName).HasMaxLength(100);
            entity.Property(c => c.AgentIdNumber).HasMaxLength(20);
            entity.Property(c => c.Channel).HasMaxLength(100);
            entity.Property(c => c.CustomerSize).HasMaxLength(50);
            entity.Property(c => c.Phone).HasMaxLength(50);
            entity.Property(c => c.Address).HasMaxLength(300);
            entity.Property(c => c.City).HasMaxLength(100);
            entity.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
            // A customer number is only unique within its own company (two different companies using
            // VisiFlow may both have a "1001" in their own source ERP) AND within its own (year, month)
            // snapshot - the same real customer gets a fresh row every month it's re-uploaded, rather
            // than one row that's overwritten in place.
            entity.HasIndex(c => new { c.CompanyId, c.CustomerNumber, c.Year, c.Month }).IsUnique();
            // Not unique - AgentIdNumber is a plain string until real agent accounts exist, and
            // every customer belonging to the same agent shares the same value. Indexed since the
            // agent-facing page's whole query is "find my customers by this number".
            entity.HasIndex(c => c.AgentIdNumber);

            entity.HasOne(c => c.Company)
                .WithMany()
                .HasForeignKey(c => c.CompanyId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CustomerVisitStandard>(entity =>
        {
            entity.ToTable("CustomerVisitStandards");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.CustomerNumber).IsRequired().HasMaxLength(30);
            // One durable standard per real customer (company + number), independent of any month's
            // Customer snapshot - this is what lets it survive automatically across monthly reloads.
            entity.HasIndex(s => new { s.CompanyId, s.CustomerNumber }).IsUnique();
            entity.HasOne(s => s.Company).WithMany().HasForeignKey(s => s.CompanyId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<NonVisitReason>(entity =>
        {
            entity.ToTable("NonVisitReasons");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Text).IsRequired().HasMaxLength(300);
            entity.HasOne(r => r.Company).WithMany().HasForeignKey(r => r.CompanyId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CustomerDistributionDay>(entity =>
        {
            entity.ToTable("CustomerDistributionDays");
            entity.HasKey(d => d.Id);
            entity.Property(d => d.CustomerNumber).IsRequired().HasMaxLength(30);
            entity.HasIndex(d => new { d.CompanyId, d.CustomerNumber, d.Year, d.Month }).IsUnique();
            entity.HasOne(d => d.Company).WithMany().HasForeignKey(d => d.CompanyId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkCalendarDay>(entity =>
        {
            entity.ToTable("WorkCalendarDays");
            entity.HasKey(d => d.Id);
            entity.Property(d => d.DayType).HasConversion<string>().HasMaxLength(10);
            entity.HasIndex(d => new { d.CompanyId, d.Date }).IsUnique();
            entity.HasOne(d => d.Company).WithMany().HasForeignKey(d => d.CompanyId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CustomerVisit>(entity =>
        {
            entity.ToTable("CustomerVisits");
            entity.HasKey(v => v.Id);
            entity.Property(v => v.CustomerNumber).IsRequired().HasMaxLength(30);
            entity.Property(v => v.AgentName).HasMaxLength(100);
            entity.Property(v => v.Outcome).HasConversion<string>().HasMaxLength(20);
            entity.Property(v => v.Notes).HasMaxLength(500);
            entity.HasIndex(v => new { v.CompanyId, v.CustomerNumber, v.VisitDate });
            entity.HasOne(v => v.Company).WithMany().HasForeignKey(v => v.CompanyId).OnDelete(DeleteBehavior.Restrict);
            // A reason can't be deleted out from under a logged visit that cites it - Restrict makes
            // that an explicit error rather than silently orphaning the reference.
            entity.HasOne(v => v.NonVisitReason).WithMany().HasForeignKey(v => v.NonVisitReasonId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<VisitPlanWeights>(entity =>
        {
            entity.ToTable("VisitPlanWeights");
            entity.HasKey(w => w.Id);
            entity.HasIndex(w => w.CompanyId).IsUnique();
            entity.HasOne(w => w.Company).WithMany().HasForeignKey(w => w.CompanyId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<VisitPlanEntry>(entity =>
        {
            entity.ToTable("VisitPlanEntries");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CustomerNumber).IsRequired().HasMaxLength(30);
            entity.Property(e => e.AgentName).HasMaxLength(100);
            entity.Property(e => e.ManuallyModifiedNote).HasMaxLength(300);
            entity.Property(e => e.CityOptimizedNote).HasMaxLength(300);
            entity.HasIndex(e => new { e.CompanyId, e.PlanYear, e.PlanMonth });
            entity.HasOne(e => e.Company).WithMany().HasForeignKey(e => e.CompanyId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Username).IsRequired().HasMaxLength(100);
            entity.Property(u => u.PasswordHash).IsRequired();
            entity.Property(u => u.DisplayName).IsRequired().HasMaxLength(200);
            entity.Property(u => u.AllowedChannels).HasMaxLength(2000);
            entity.HasIndex(u => u.Username).IsUnique();
            entity.HasOne(u => u.Company).WithMany().HasForeignKey(u => u.CompanyId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
