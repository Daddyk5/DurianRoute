using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DurianRoute.Api.Data;

public class DurianDbContext(DbContextOptions<DurianDbContext> options) : DbContext(options)
{
    public DbSet<BusRoute> Routes => Set<BusRoute>();
    public DbSet<Stop> Stops => Set<Stop>();
    public DbSet<Bus> Buses => Set<Bus>();
    public DbSet<ChokePoint> ChokePoints => Set<ChokePoint>();
    public DbSet<TrafficObservation> TrafficObservations => Set<TrafficObservation>();
    public DbSet<TrafficForecast> Forecasts => Set<TrafficForecast>();
    public DbSet<ModelMetrics> ModelMetrics => Set<ModelMetrics>();
    public DbSet<LaneRecommendation> LaneRecommendations => Set<LaneRecommendation>();
    public DbSet<LaneChangeAudit> LaneChangeAudits => Set<LaneChangeAudit>();
    public DbSet<ScheduleDeviation> ScheduleDeviations => Set<ScheduleDeviation>();
    public DbSet<AppUser> Users => Set<AppUser>();

    /// <summary>All timestamps are stored in UTC; make sure they come back marked as UTC.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        builder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()) : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Explicit lengths keep SQL Server from using nvarchar(max), which cannot be indexed.
        b.Entity<BusRoute>(e =>
        {
            e.Property(r => r.Code).HasMaxLength(16);
            e.Property(r => r.Name).HasMaxLength(200);
            e.Property(r => r.Color).HasMaxLength(16);
            e.HasIndex(r => r.Code).IsUnique();
        });
        b.Entity<Stop>().Property(s => s.Name).HasMaxLength(200);
        b.Entity<Bus>(e =>
        {
            e.Property(x => x.PlateNumber).HasMaxLength(32);
            e.HasIndex(x => x.PlateNumber).IsUnique();
        });
        b.Entity<ChokePoint>().Property(c => c.Name).HasMaxLength(200);
        b.Entity<AppUser>(e =>
        {
            e.Property(u => u.UserName).HasMaxLength(64);
            e.Property(u => u.Role).HasMaxLength(32);
            e.HasIndex(u => u.UserName).IsUnique();
        });
        b.Entity<LaneRecommendation>().Property(r => r.DecidedBy).HasMaxLength(64);
        b.Entity<LaneRecommendation>().Property(r => r.RequestedBy).HasMaxLength(64);
        b.Entity<LaneChangeAudit>().Property(a => a.Actor).HasMaxLength(64);
        b.Entity<ScheduleDeviation>().Property(d => d.NearStop).HasMaxLength(200);

        b.Entity<TrafficObservation>()
            .HasIndex(o => new { o.ChokePointId, o.HourStartUtc, o.Direction })
            .IsUnique();

        b.Entity<TrafficForecast>()
            .HasIndex(f => new { f.ChokePointId, f.HourStartUtc, f.Direction })
            .IsUnique();

        b.Entity<LaneRecommendation>().HasIndex(r => new { r.ChokePointId, r.SlotStartUtc });
        b.Entity<ScheduleDeviation>().HasIndex(d => d.TimestampUtc);
        b.Entity<LaneChangeAudit>().HasIndex(a => a.TimestampUtc);
    }
}
