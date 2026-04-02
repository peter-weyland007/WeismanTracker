using api.Models;
using Microsoft.EntityFrameworkCore;

namespace api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<TrackedPerson> TrackedPeople => Set<TrackedPerson>();
    public DbSet<TrackedComputer> TrackedComputers => Set<TrackedComputer>();
    public DbSet<CatEtLicense> CatEtLicenses => Set<CatEtLicense>();
    public DbSet<CatEtActivationEvent> CatEtActivationEvents => Set<CatEtActivationEvent>();

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
    }
}
