using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SyncSentry.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<InstanceSetting> Settings => Set<InstanceSetting>();
    public DbSet<WatchedMount> WatchedMounts => Set<WatchedMount>();
    public DbSet<LogSource> LogSources => Set<LogSource>();
    public DbSet<SyncJobDefinition> Jobs => Set<SyncJobDefinition>();
    public DbSet<JobRun> JobRuns => Set<JobRun>();
    public DbSet<OutboxPair> OutboxPairs => Set<OutboxPair>();
    public DbSet<AlertEvent> AlertEvents => Set<AlertEvent>();
    public DbSet<MountStateRow> MountStates => Set<MountStateRow>();
    public DbSet<RouteStateRow> RouteStates => Set<RouteStateRow>();
    public DbSet<TransferEvent> TransferEvents => Set<TransferEvent>();
    public DbSet<UserPreference> UserPreferences => Set<UserPreference>();
    public DbSet<SetupState> SetupStates => Set<SetupState>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<InstanceSetting>(entity =>
        {
            entity.ToTable("Settings");
            entity.Property(item => item.RcUrl).HasMaxLength(300);
            entity.Property(item => item.RcUser).HasMaxLength(120);
            entity.Property(item => item.NtfyServerUrl).HasMaxLength(300);
            entity.Property(item => item.NtfyTopic).HasMaxLength(80);
            entity.Property(item => item.TailscaleStatusPath).HasMaxLength(500);
            entity.Property(item => item.TailscaleSocketPath).HasMaxLength(500);
            entity.Property(item => item.TriggerDirectory).HasMaxLength(500);
            entity.Property(item => item.SonarrUrl).HasMaxLength(300);
            entity.Property(item => item.RadarrUrl).HasMaxLength(300);
            entity.Property(item => item.LidarrUrl).HasMaxLength(300);
            entity.Property(item => item.PlexUrl).HasMaxLength(300);
        });

        builder.Entity<WatchedMount>(entity =>
        {
            entity.ToTable("WatchedMounts");
            entity.Property(item => item.Name).HasMaxLength(120).IsRequired();
            entity.Property(item => item.Path).HasMaxLength(500).IsRequired();
            entity.HasIndex(item => item.Path).IsUnique();
        });

        builder.Entity<LogSource>(entity =>
        {
            entity.ToTable("LogSources");
            entity.Property(item => item.Name).HasMaxLength(120).IsRequired();
            entity.Property(item => item.Path).HasMaxLength(500).IsRequired();
        });

        builder.Entity<SyncJobDefinition>(entity =>
        {
            entity.ToTable("Jobs");
            entity.Property(item => item.Name).HasMaxLength(120).IsRequired();
            entity.Property(item => item.StartMode).HasMaxLength(20).IsRequired();
            entity.Property(item => item.Operation).HasMaxLength(20).IsRequired();
            entity.Property(item => item.Source).HasMaxLength(500);
            entity.Property(item => item.Destination).HasMaxLength(500);
            entity.Property(item => item.ScriptKey).HasMaxLength(48);
            entity.HasOne(item => item.LogSource)
                .WithMany()
                .HasForeignKey(item => item.LogSourceId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<JobRun>(entity =>
        {
            entity.ToTable("JobRuns");
            entity.Property(item => item.Name).HasMaxLength(120).IsRequired();
            entity.Property(item => item.Origin).HasMaxLength(20).IsRequired();
            entity.Property(item => item.ExternalId).HasMaxLength(80).IsRequired();
            entity.Property(item => item.Status).HasMaxLength(20).IsRequired();
            entity.Property(item => item.LastError).HasMaxLength(300);
            entity.HasIndex(item => new { item.ExternalId, item.Status });
            entity.HasIndex(item => item.UpdatedUtc);
        });

        builder.Entity<OutboxPair>(entity =>
        {
            entity.ToTable("OutboxPairs");
            entity.Property(item => item.Name).HasMaxLength(120).IsRequired();
            entity.Property(item => item.SourcePath).HasMaxLength(500).IsRequired();
            entity.Property(item => item.Destination).HasMaxLength(500).IsRequired();
            entity.Property(item => item.DestinationKind).HasMaxLength(20).IsRequired();
            entity.Property(item => item.Status).HasMaxLength(20).IsRequired();
            entity.Property(item => item.Error).HasMaxLength(300);
            entity.Property(item => item.SampleJson).IsRequired();
        });

        builder.Entity<AlertEvent>(entity =>
        {
            entity.ToTable("AlertEvents");
            entity.Property(item => item.Key).HasMaxLength(240).IsRequired();
            entity.Property(item => item.Kind).HasMaxLength(40).IsRequired();
            entity.Property(item => item.Title).HasMaxLength(180).IsRequired();
            entity.Property(item => item.Detail).HasMaxLength(200);
            entity.HasIndex(item => item.Key);
        });

        builder.Entity<MountStateRow>(entity =>
        {
            entity.ToTable("MountStates");
            entity.Property(item => item.Path).HasMaxLength(500).IsRequired();
            entity.Property(item => item.State).HasMaxLength(20).IsRequired();
            entity.HasIndex(item => item.Path).IsUnique();
        });

        builder.Entity<RouteStateRow>(entity => entity.ToTable("RouteState"));

        builder.Entity<TransferEvent>(entity =>
        {
            entity.ToTable("TransferEvents");
            entity.Property(item => item.DedupeKey).HasMaxLength(240).IsRequired();
            entity.Property(item => item.Name).HasMaxLength(500).IsRequired();
            entity.Property(item => item.SourcePath).HasMaxLength(500);
            entity.Property(item => item.DestinationPath).HasMaxLength(500);
            entity.Property(item => item.Error).HasMaxLength(300);
            entity.HasIndex(item => item.DedupeKey).IsUnique();
            entity.HasIndex(item => item.CompletedUtc);
        });

        builder.Entity<UserPreference>(entity =>
        {
            entity.ToTable("UserPreferences");
            entity.Property(item => item.UserId).HasMaxLength(450).IsRequired();
            entity.HasIndex(item => item.UserId).IsUnique();
        });

        builder.Entity<SetupState>(entity =>
        {
            entity.ToTable("SetupState");
            entity.Property(item => item.AppdataHostPath).HasMaxLength(500);
        });
    }
}

public sealed class DesignTimeDb : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>();
        options.UseSqlite("Data Source=/tmp/syncsentry-design.db");
        return new ApplicationDbContext(options.Options);
    }
}
