using Jakar.OpenTelemetry.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jakar.OpenTelemetry.Api.Data;

public sealed class TelemetryDbContext( DbContextOptions<TelemetryDbContext> options ) : DbContext( options )
{
    public DbSet<TelemetryLogEntity>    Logs    => Set<TelemetryLogEntity>();
    public DbSet<TelemetrySpanEntity>   Spans   => Set<TelemetrySpanEntity>();
    public DbSet<TelemetryMetricEntity> Metrics => Set<TelemetryMetricEntity>();


    protected override void OnModelCreating( ModelBuilder modelBuilder )
    {
        modelBuilder.Entity<TelemetryLogEntity>( entity =>
                                                 {
                                                     entity.HasKey( x => x.ID );
                                                     entity.HasIndex( x => x.TimestampUtc );
                                                     entity.HasIndex( x => x.ServiceName );
                                                     entity.HasIndex( x => x.TraceId );
                                                     entity.HasIndex( x => x.SeverityText );
                                                     ConfigureStringDictionary( entity.Property( x => x.ResourceAttributesJson ) );
                                                     ConfigureStringDictionary( entity.Property( x => x.ScopeAttributesJson ) );
                                                     ConfigureStringDictionary( entity.Property( x => x.AttributesJson ) );
                                                 } );

        modelBuilder.Entity<TelemetrySpanEntity>( entity =>
                                                  {
                                                      entity.HasKey( x => x.ID );
                                                      entity.HasIndex( x => x.StartTimeUtc );
                                                      entity.HasIndex( x => x.ServiceName );
                                                      entity.HasIndex( x => x.TraceId );
                                                      entity.HasIndex( x => x.Name );
                                                      ConfigureStringDictionary( entity.Property( x => x.ResourceAttributesJson ) );
                                                      ConfigureStringDictionary( entity.Property( x => x.ScopeAttributesJson ) );
                                                      ConfigureStringDictionary( entity.Property( x => x.AttributesJson ) );
                                                      ConfigureSpanEventArray( entity.Property( x => x.EventsJson ) );
                                                      ConfigureSpanLinkArray( entity.Property( x => x.LinksJson ) );
                                                  } );

        modelBuilder.Entity<TelemetryMetricEntity>( entity =>
                                                    {
                                                        entity.HasKey( x => x.ID );
                                                        entity.HasIndex( x => x.TimestampUtc );
                                                        entity.HasIndex( x => x.ServiceName );
                                                        entity.HasIndex( x => x.Name );
                                                        entity.HasIndex( x => x.MetricType );
                                                        ConfigureStringDictionary( entity.Property( x => x.MetadataAttributesJson ) );
                                                        ConfigureStringDictionary( entity.Property( x => x.ResourceAttributesJson ) );
                                                        ConfigureStringDictionary( entity.Property( x => x.ScopeAttributesJson ) );
                                                        ConfigureStringDictionary( entity.Property( x => x.AttributesJson ) );
                                                        ConfigureDoubleDictionary( entity.Property( x => x.QuantilesJson ) );
                                                    } );
    }

    private static void ConfigureStringDictionary( PropertyBuilder<System.Collections.ObjectModel.ReadOnlyDictionary<string, string?>> property )
    {
        property.HasColumnType( "jsonb" );
        property.HasConversion( TelemetryJson.StringDictionaryConverter );
        property.Metadata.SetValueComparer( TelemetryJson.StringDictionaryComparer );
    }

    private static void ConfigureDoubleDictionary( PropertyBuilder<System.Collections.ObjectModel.ReadOnlyDictionary<double, double>?> property )
    {
        property.HasColumnType( "jsonb" );
        property.HasConversion( TelemetryJson.DoubleDictionaryConverter );
        property.Metadata.SetValueComparer( TelemetryJson.DoubleDictionaryComparer );
    }

    private static void ConfigureSpanEventArray( PropertyBuilder<Jakar.OpenTelemetry.Contracts.SpanEvent[]?> property )
    {
        property.HasColumnType( "jsonb" );
        property.HasConversion( TelemetryJson.SpanEventArrayConverter );
        property.Metadata.SetValueComparer( TelemetryJson.SpanEventArrayComparer );
    }

    private static void ConfigureSpanLinkArray( PropertyBuilder<Jakar.OpenTelemetry.Contracts.SpanLink[]?> property )
    {
        property.HasColumnType( "jsonb" );
        property.HasConversion( TelemetryJson.SpanLinkArrayConverter );
        property.Metadata.SetValueComparer( TelemetryJson.SpanLinkArrayComparer );
    }
}
