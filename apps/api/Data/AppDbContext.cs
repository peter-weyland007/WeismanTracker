using api.Models;
using Microsoft.EntityFrameworkCore;

namespace api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<TrackedPerson> TrackedPeople => Set<TrackedPerson>();
    public DbSet<TrackedComputer> TrackedComputers => Set<TrackedComputer>();
    public DbSet<CellPhoneAllowance> CellPhoneAllowances => Set<CellPhoneAllowance>();
    public DbSet<CatEtLicense> CatEtLicenses => Set<CatEtLicense>();
    public DbSet<CatEtActivationEvent> CatEtActivationEvents => Set<CatEtActivationEvent>();
    public DbSet<ResourceDefinition> ResourceDefinitions => Set<ResourceDefinition>();
    public DbSet<EntityReference> EntityReferences => Set<EntityReference>();
    public DbSet<IntegrationProviderConfig> IntegrationProviderConfigs => Set<IntegrationProviderConfig>();
    public DbSet<IntegrationSyncStatus> IntegrationSyncStatuses => Set<IntegrationSyncStatus>();
    public DbSet<PrinterTelemetryRecord> PrinterTelemetryRecords => Set<PrinterTelemetryRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AppUser>()
            .HasIndex(u => u.Username)
            .IsUnique();

        modelBuilder.Entity<TrackedPerson>()
            .HasIndex(p => p.Email)
            .IsUnique();

        modelBuilder.Entity<TrackedComputer>()
            .HasIndex(c => c.AssetTag)
            .IsUnique();

        modelBuilder.Entity<CatEtLicense>()
            .HasIndex(l => l.SerialNumber)
            .IsUnique();

        modelBuilder.Entity<CellPhoneAllowance>()
            .HasOne(a => a.TrackedPerson)
            .WithMany(p => p.CellPhoneAllowances)
            .HasForeignKey(a => a.TrackedPersonId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CellPhoneAllowance>()
            .HasIndex(a => a.TrackedPersonId);

        modelBuilder.Entity<TrackedComputer>()
            .HasOne(c => c.TrackedPerson)
            .WithMany(p => p.Computers)
            .HasForeignKey(c => c.TrackedPersonId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);

        modelBuilder.Entity<CatEtLicense>()
            .HasOne(l => l.TrackedComputer)
            .WithMany(c => c.CatEtLicenses)
            .HasForeignKey(l => l.TrackedComputerId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);

        modelBuilder.Entity<CatEtActivationEvent>()
            .HasOne(e => e.CatEtLicense)
            .WithMany(l => l.ActivationEvents)
            .HasForeignKey(e => e.CatEtLicenseId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ResourceDefinition>()
            .HasIndex(x => new { x.EntityType, x.Provider, x.ResourceType })
            .IsUnique();

        modelBuilder.Entity<EntityReference>()
            .HasOne(x => x.ResourceDefinition)
            .WithMany(x => x.EntityReferences)
            .HasForeignKey(x => x.ResourceDefinitionId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<EntityReference>()
            .HasIndex(x => new { x.EntityType, x.EntityId });

        modelBuilder.Entity<EntityReference>()
            .HasIndex(x => new { x.ResourceDefinitionId, x.ExternalKey });

        modelBuilder.Entity<EntityReference>()
            .HasIndex(x => new { x.EntityType, x.EntityId, x.ResourceDefinitionId, x.ExternalId })
            .IsUnique();

        modelBuilder.Entity<IntegrationProviderConfig>()
            .HasIndex(x => x.Provider)
            .IsUnique();

        modelBuilder.Entity<IntegrationSyncStatus>()
            .HasIndex(x => x.SyncTarget)
            .IsUnique();
    }
}
