using System.Linq.Expressions;
using System.Text.Json;
using Jakar.OpenTelemetry.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Jakar.OpenTelemetry.Api.Data;

public sealed class TelemetryDbContext( DbContextOptions<TelemetryDbContext> options, IConfiguration configuration ) : DbContext( options )
{
    public DbSet<TelemetryLogEntity>    Logs    => Set<TelemetryLogEntity>();
    public DbSet<TelemetrySpanEntity>   Spans   => Set<TelemetrySpanEntity>();
    public DbSet<TelemetryMetricEntity> Metrics => Set<TelemetryMetricEntity>();



    protected override void OnConfiguring( DbContextOptionsBuilder options )
    {
        string connectionString = configuration.GetConnectionString( "Telemetry" ) ?? "Host=localhost;Port=5432;Database=mydb;Username=postgres;Password=secret";
        options.UseNpgsql( connectionString );
        // options.UseMemoryCache();
    }


    protected override void OnModelCreating( ModelBuilder modelBuilder )
    {
        modelBuilder.Entity<TelemetryLogEntity>( static entity =>
                                                 {
                                                     entity.HasKey( x => x.ID );
                                                     entity.HasIndex( x => x.TimestampUtc );
                                                     entity.HasIndex( x => x.ServiceName );
                                                     entity.HasIndex( x => x.TraceId );
                                                     entity.HasIndex( x => x.SeverityText );
                                                 } );

        modelBuilder.Entity<TelemetrySpanEntity>( static entity =>
                                                  {
                                                      entity.HasKey( x => x.ID );
                                                      entity.HasIndex( x => x.StartTimeUtc );
                                                      entity.HasIndex( x => x.ServiceName );
                                                      entity.HasIndex( x => x.TraceId );
                                                      entity.HasIndex( x => x.Name );
                                                      entity.Property( x => x.ResourceAttributesJson ).HasColumnType( "json" ).HasConversion( TelemetryJson.Converter ).Metadata.SetValueComparer( TelemetryJson.Comparer );
                                                      entity.Property( x => x.ScopeAttributesJson ).HasColumnType( "json" ).HasConversion( TelemetryJson.Converter ).Metadata.SetValueComparer( TelemetryJson.Comparer );
                                                      entity.Property( x => x.AttributesJson ).HasColumnType( "json" ).HasConversion( TelemetryJson.Converter ).Metadata.SetValueComparer( TelemetryJson.Comparer );
                                                  } );

        modelBuilder.Entity<TelemetryMetricEntity>( static entity =>
                                                    {
                                                        entity.HasKey( x => x.ID );
                                                        entity.HasIndex( x => x.TimestampUtc );
                                                        entity.HasIndex( x => x.ServiceName );
                                                        entity.HasIndex( x => x.Name );
                                                        entity.HasIndex( x => x.MetricType );
                                                        entity.Property( x => x.MetadataAttributesJson ).HasColumnType( "json" ).HasConversion( TelemetryJson.Converter ).Metadata.SetValueComparer( TelemetryJson.Comparer );
                                                        entity.Property( x => x.ResourceAttributesJson ).HasColumnType( "json" ).HasConversion( TelemetryJson.Converter ).Metadata.SetValueComparer( TelemetryJson.Comparer );
                                                        entity.Property( x => x.ScopeAttributesJson ).HasColumnType( "json" ).HasConversion( TelemetryJson.Converter ).Metadata.SetValueComparer( TelemetryJson.Comparer );
                                                        entity.Property( x => x.AttributesJson ).HasColumnType( "json" ).HasConversion( TelemetryJson.Converter ).Metadata.SetValueComparer( TelemetryJson.Comparer );
                                                    } );
    }
}
