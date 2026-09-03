using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Iolys.WebMetrics;

internal sealed class AnalyticsDbContext(DbContextOptions<AnalyticsDbContext> options) : DbContext(options)
{
    public DbSet<AnalyticsMetadataEntity> Metadata => Set<AnalyticsMetadataEntity>();
    public DbSet<AnalyticsEventEntity> Events => Set<AnalyticsEventEntity>();
    public DbSet<AnalyticsMonthSummaryEntity> MonthSummaries => Set<AnalyticsMonthSummaryEntity>();
    public DbSet<AnalyticsDailyRollupEntity> DailyRollups => Set<AnalyticsDailyRollupEntity>();
    public DbSet<AnalyticsPageRollupEntity> PageRollups => Set<AnalyticsPageRollupEntity>();
    public DbSet<AnalyticsSourceRollupEntity> SourceRollups => Set<AnalyticsSourceRollupEntity>();
    public DbSet<AnalyticsUtmSourceRollupEntity> UtmSourceRollups => Set<AnalyticsUtmSourceRollupEntity>();
    public DbSet<AnalyticsUtmMediumRollupEntity> UtmMediumRollups => Set<AnalyticsUtmMediumRollupEntity>();
    public DbSet<AnalyticsCampaignRollupEntity> CampaignRollups => Set<AnalyticsCampaignRollupEntity>();
    public DbSet<AnalyticsNotFoundRollupEntity> NotFoundRollups => Set<AnalyticsNotFoundRollupEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AnalyticsMetadataEntity>(entity =>
        {
            entity.ToTable("metadata");
            entity.HasKey(item => item.Key);
            entity.Property(item => item.Key).HasColumnName("key");
            entity.Property(item => item.Value).HasColumnName("value");
            entity.HasData(
                new AnalyticsMetadataEntity { Key = "schema_version", Value = "3" },
                new AnalyticsMetadataEntity { Key = "compacted", Value = "0" });
        });

        modelBuilder.Entity<AnalyticsEventEntity>(entity =>
        {
            entity.ToTable("events");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(item => item.OccurredAt).HasColumnName("occurred_at");
            entity.Property(item => item.Day).HasColumnName("day");
            entity.Property(item => item.VisitorId).HasColumnName("visitor_id");
            entity.Property(item => item.Kind).HasColumnName("kind");
            entity.Property(item => item.Path).HasColumnName("path");
            entity.Property(item => item.UtmSource).HasColumnName("utm_source");
            entity.Property(item => item.UtmMedium).HasColumnName("utm_medium");
            entity.Property(item => item.UtmCampaign).HasColumnName("utm_campaign");
            entity.Property(item => item.ReferrerHost).HasColumnName("referrer_host");
            entity.HasIndex(item => new { item.Day, item.Kind }).HasDatabaseName("ix_events_day_kind");
            entity.HasIndex(item => item.VisitorId).HasDatabaseName("ix_events_visitor");
            entity.HasIndex(item => item.Path).HasDatabaseName("ix_events_path");
            entity.HasIndex(item => new { item.UtmSource, item.UtmMedium, item.UtmCampaign })
                .HasDatabaseName("ix_events_utm");
        });

        modelBuilder.Entity<AnalyticsMonthSummaryEntity>(entity =>
        {
            entity.ToTable("month_summary");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(item => item.Month).HasColumnName("month");
            entity.Property(item => item.Views).HasColumnName("views");
            entity.Property(item => item.Visitors).HasColumnName("visitors");
            entity.Property(item => item.NotFoundHits).HasColumnName("not_found_hits");
        });

        modelBuilder.Entity<AnalyticsDailyRollupEntity>(entity =>
        {
            entity.ToTable("daily_rollup");
            entity.HasKey(item => item.Day);
            entity.Property(item => item.Day).HasColumnName("day");
            entity.Property(item => item.Views).HasColumnName("views");
            entity.Property(item => item.Visitors).HasColumnName("visitors");
            entity.Property(item => item.NotFoundHits).HasColumnName("not_found_hits");
        });

        modelBuilder.Entity<AnalyticsPageRollupEntity>(entity =>
        {
            entity.ToTable("page_rollup");
            entity.HasKey(item => new { item.Day, item.Path });
            entity.Property(item => item.Day).HasColumnName("day");
            entity.Property(item => item.Path).HasColumnName("path");
            entity.Property(item => item.Views).HasColumnName("views");
            entity.Property(item => item.Visitors).HasColumnName("visitors");
        });

        modelBuilder.Entity<AnalyticsSourceRollupEntity>(entity =>
        {
            entity.ToTable("source_rollup");
            entity.HasKey(item => new { item.Day, item.Source, item.Medium });
            entity.Property(item => item.Day).HasColumnName("day");
            entity.Property(item => item.Source).HasColumnName("source");
            entity.Property(item => item.Medium).HasColumnName("medium");
            entity.Property(item => item.Views).HasColumnName("views");
            entity.Property(item => item.Visitors).HasColumnName("visitors");
        });

        modelBuilder.Entity<AnalyticsUtmSourceRollupEntity>(entity =>
        {
            entity.ToTable("utm_source_rollup");
            entity.HasKey(item => new { item.Day, item.Source });
            entity.Property(item => item.Day).HasColumnName("day");
            entity.Property(item => item.Source).HasColumnName("source");
            entity.Property(item => item.Views).HasColumnName("views");
            entity.Property(item => item.Visitors).HasColumnName("visitors");
        });

        modelBuilder.Entity<AnalyticsUtmMediumRollupEntity>(entity =>
        {
            entity.ToTable("utm_medium_rollup");
            entity.HasKey(item => new { item.Day, item.Medium });
            entity.Property(item => item.Day).HasColumnName("day");
            entity.Property(item => item.Medium).HasColumnName("medium");
            entity.Property(item => item.Views).HasColumnName("views");
            entity.Property(item => item.Visitors).HasColumnName("visitors");
        });

        modelBuilder.Entity<AnalyticsCampaignRollupEntity>(entity =>
        {
            entity.ToTable("campaign_rollup");
            entity.HasKey(item => new { item.Day, item.Source, item.Medium, item.Campaign });
            entity.Property(item => item.Day).HasColumnName("day");
            entity.Property(item => item.Source).HasColumnName("source");
            entity.Property(item => item.Medium).HasColumnName("medium");
            entity.Property(item => item.Campaign).HasColumnName("campaign");
            entity.Property(item => item.Views).HasColumnName("views");
            entity.Property(item => item.Visitors).HasColumnName("visitors");
        });

        modelBuilder.Entity<AnalyticsNotFoundRollupEntity>(entity =>
        {
            entity.ToTable("not_found_rollup_v2");
            entity.HasKey(item => new { item.Day, item.Path });
            entity.Property(item => item.Day).HasColumnName("day");
            entity.Property(item => item.Path).HasColumnName("path");
            entity.Property(item => item.Hits).HasColumnName("hits");
            entity.Property(item => item.Visitors).HasColumnName("visitors");
        });
    }
}

internal sealed class AnalyticsDbContextFactory(ILoggerFactory loggerFactory)
{
    public AnalyticsDbContext Create(string path, bool readOnly = false)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();

        var options = new DbContextOptionsBuilder<AnalyticsDbContext>()
            .UseSqlite(connectionString)
            .UseLoggerFactory(loggerFactory)
            .Options;
        return new AnalyticsDbContext(options);
    }
}

internal sealed class AnalyticsMetadataEntity
{
    public required string Key { get; set; }
    public required string Value { get; set; }
}

internal sealed class AnalyticsEventEntity
{
    public long Id { get; set; }
    public required string OccurredAt { get; set; }
    public required string Day { get; set; }
    public required string VisitorId { get; set; }
    public AnalyticsEventKind Kind { get; set; }
    public required string Path { get; set; }
    public required string UtmSource { get; set; }
    public required string UtmMedium { get; set; }
    public required string UtmCampaign { get; set; }
    public required string ReferrerHost { get; set; }
}

internal sealed class AnalyticsMonthSummaryEntity
{
    public int Id { get; set; }
    public required string Month { get; set; }
    public long Views { get; set; }
    public long Visitors { get; set; }
    public long NotFoundHits { get; set; }
}

internal sealed class AnalyticsDailyRollupEntity
{
    public required string Day { get; set; }
    public long Views { get; set; }
    public long Visitors { get; set; }
    public long NotFoundHits { get; set; }
}

internal sealed class AnalyticsPageRollupEntity
{
    public required string Day { get; set; }
    public required string Path { get; set; }
    public long Views { get; set; }
    public long Visitors { get; set; }
}

internal sealed class AnalyticsSourceRollupEntity
{
    public required string Day { get; set; }
    public required string Source { get; set; }
    public required string Medium { get; set; }
    public long Views { get; set; }
    public long Visitors { get; set; }
}

internal sealed class AnalyticsUtmSourceRollupEntity
{
    public required string Day { get; set; }
    public required string Source { get; set; }
    public long Views { get; set; }
    public long Visitors { get; set; }
}

internal sealed class AnalyticsUtmMediumRollupEntity
{
    public required string Day { get; set; }
    public required string Medium { get; set; }
    public long Views { get; set; }
    public long Visitors { get; set; }
}

internal sealed class AnalyticsCampaignRollupEntity
{
    public required string Day { get; set; }
    public required string Source { get; set; }
    public required string Medium { get; set; }
    public required string Campaign { get; set; }
    public long Views { get; set; }
    public long Visitors { get; set; }
}

internal sealed class AnalyticsNotFoundRollupEntity
{
    public required string Day { get; set; }
    public required string Path { get; set; }
    public long Hits { get; set; }
    public long Visitors { get; set; }
}
