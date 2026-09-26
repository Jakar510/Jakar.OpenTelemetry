using System.Collections.ObjectModel;
using Jakar.OpenTelemetry.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jakar.OpenTelemetry.Api.Data;

/// <summary>
///     Read-side model. The schema is owned by <see cref="TelemetrySchema"/> (not EF) and rows are written by <see cref="TelemetryIngestService"/> with binary COPY,
///     so this context is configured for pooled, no-tracking queries only.
/// </summary>
public sealed class TelemetryDbContext( DbContextOptions<TelemetryDbContext> options ) : DbContext( options )
{
	public DbSet<TelemetryLogEntity>    Logs    => Set<TelemetryLogEntity>();
	public DbSet<TelemetrySpanEntity>   Spans   => Set<TelemetrySpanEntity>();
	public DbSet<TelemetryMetricEntity> Metrics => Set<TelemetryMetricEntity>();


	protected override void OnModelCreating( ModelBuilder modelBuilder )
	{
		modelBuilder.Entity<TelemetryLogEntity>( entity =>
												 {
													 entity.ToTable( "Logs" );
													 entity.HasKey( x => x.ID );
													 ConfigureSortTime( entity.Property( x => x.SortTimeUtc ), """COALESCE("TimestampUtc", "ObservedTimestampUtc", "ReceivedAtUtc")""" );
													 ConfigureStringDictionary( entity.Property( x => x.ResourceAttributesJson ) );
													 ConfigureStringDictionary( entity.Property( x => x.ScopeAttributesJson ) );
													 ConfigureStringDictionary( entity.Property( x => x.AttributesJson ) );
												 } );

		modelBuilder.Entity<TelemetrySpanEntity>( entity =>
												  {
													  entity.ToTable( "Spans" );
													  entity.HasKey( x => x.ID );
													  ConfigureSortTime( entity.Property( x => x.SortTimeUtc ), """COALESCE("StartTimeUtc", "ReceivedAtUtc")""" );
													  ConfigureStringDictionary( entity.Property( x => x.ResourceAttributesJson ) );
													  ConfigureStringDictionary( entity.Property( x => x.ScopeAttributesJson ) );
													  ConfigureStringDictionary( entity.Property( x => x.AttributesJson ) );

													  entity.Property( x => x.EventsJson ).HasColumnType( "jsonb" ).HasConversion( TelemetryJson.SpanEventArrayConverter ).Metadata.SetValueComparer( TelemetryJson.SpanEventArrayComparer );
													  entity.Property( x => x.LinksJson ).HasColumnType( "jsonb" ).HasConversion( TelemetryJson.SpanLinkArrayConverter ).Metadata.SetValueComparer( TelemetryJson.SpanLinkArrayComparer );
												  } );

		modelBuilder.Entity<TelemetryMetricEntity>( entity =>
													{
														entity.ToTable( "Metrics" );
														entity.HasKey( x => x.ID );
														ConfigureSortTime( entity.Property( x => x.SortTimeUtc ), """COALESCE("TimestampUtc", "ReceivedAtUtc")""" );
														ConfigureStringDictionary( entity.Property( x => x.MetadataAttributesJson ) );
														ConfigureStringDictionary( entity.Property( x => x.ResourceAttributesJson ) );
														ConfigureStringDictionary( entity.Property( x => x.ScopeAttributesJson ) );
														ConfigureStringDictionary( entity.Property( x => x.AttributesJson ) );
														entity.Property( x => x.DistributionJson ).HasColumnType( "jsonb" );
														entity.Property( x => x.ExemplarsJson ).HasColumnType( "jsonb" );
														entity.Property( x => x.QuantilesJson ).HasColumnType( "jsonb" ).HasConversion( TelemetryJson.DoubleDictionaryConverter ).Metadata.SetValueComparer( TelemetryJson.DoubleDictionaryComparer );
													} );
	}

	private static void ConfigureSortTime( PropertyBuilder<DateTimeOffset> property, string sql ) => property.HasComputedColumnSql( sql, stored: true );

	private static void ConfigureStringDictionary( PropertyBuilder<ReadOnlyDictionary<string, string?>> property )
	{
		property.HasColumnType( "jsonb" );
		property.HasConversion( TelemetryJson.StringDictionaryConverter );
		property.Metadata.SetValueComparer( TelemetryJson.StringDictionaryComparer );
	}
}
