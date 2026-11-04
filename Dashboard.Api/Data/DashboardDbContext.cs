using Dashboard.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace Dashboard.Api.Data;

public class DashboardDbContext : DbContext
{
    private readonly DescriptionEncryptor _encryptor;

    public DashboardDbContext(DbContextOptions<DashboardDbContext> options, DescriptionEncryptor encryptor)
        : base(options)
    {
        _encryptor = encryptor;
    }

    public DbSet<Transaction> Transactions => Set<Transaction>();

    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<InboxEntry> InboxEntries => Set<InboxEntry>();

    public DbSet<BudgetSettings> BudgetSettings => Set<BudgetSettings>();

    public DbSet<BudgetCategoryCap> BudgetCategoryCaps => Set<BudgetCategoryCap>();

    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();

    public DbSet<OpScenario> OpScenarios => Set<OpScenario>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasIndex(account => account.Username).IsUnique();
            entity.Property(account => account.Username).HasMaxLength(32);
        });

        modelBuilder.Entity<Transaction>(entity =>
        {
            entity.Property(transaction => transaction.Amount).HasPrecision(19, 2);
            entity.HasIndex(transaction => transaction.AccountId);
            entity.Property(transaction => transaction.Description)
                .HasConversion(value => _encryptor.Protect(value), value => _encryptor.Unprotect(value));
            entity.Property(transaction => transaction.Why)
                .HasConversion(value => _encryptor.Protect(value), value => _encryptor.Unprotect(value));
        });

        modelBuilder.Entity<InboxEntry>(entity =>
        {
            entity.Property(entry => entry.Amount).HasPrecision(19, 2);
            entity.Property(entry => entry.RawText).HasMaxLength(1000);
            entity.HasIndex(entry => entry.AccountId);
            entity.Property(entry => entry.Description)
                .HasConversion(value => _encryptor.Protect(value), value => _encryptor.Unprotect(value));
            entity.Property(entry => entry.Why)
                .HasConversion(value => _encryptor.Protect(value), value => _encryptor.Unprotect(value));
        });

        modelBuilder.Entity<BudgetSettings>(entity =>
        {
            entity.Property(settings => settings.MonthlyIncome).HasPrecision(19, 2);
            entity.Property(settings => settings.MonthlyLimit).HasPrecision(19, 2);
            entity.Property(settings => settings.SavingsTarget).HasPrecision(19, 2);
            entity.Property(settings => settings.YearGoal).HasPrecision(19, 2);
            entity.Property(settings => settings.YearGoalName).HasMaxLength(120);
            entity.HasIndex(settings => settings.AccountId).IsUnique();
        });

        modelBuilder.Entity<BudgetCategoryCap>(entity =>
        {
            entity.Property(cap => cap.Category).HasMaxLength(100);
            entity.Property(cap => cap.MonthlyCap).HasPrecision(19, 2);
            entity.HasIndex(cap => new { cap.AccountId, cap.Category }).IsUnique();
        });

        modelBuilder.Entity<ImportBatch>(entity =>
        {
            entity.Property(batch => batch.FileName).HasMaxLength(260);
            entity.HasIndex(batch => batch.AccountId);
        });

        modelBuilder.Entity<OpScenario>(entity =>
        {
            entity.Property(scenario => scenario.Slot).HasMaxLength(1);
            entity.Property(scenario => scenario.Template).HasMaxLength(64);
            entity.Property(scenario => scenario.Title).HasMaxLength(200);
            entity.HasIndex(scenario => new { scenario.AccountId, scenario.Slot }).IsUnique();
        });
    }
}
