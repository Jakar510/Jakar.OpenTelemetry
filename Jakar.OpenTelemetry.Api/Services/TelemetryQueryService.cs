using Jakar.OpenTelemetry.Api.Data;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.EntityFrameworkCore;
using ZiggyCreatures.Caching.Fusion;

namespace Jakar.OpenTelemetry.Api.Services;

public sealed class TelemetryQueryService( TelemetryDbContext dbContext, IFusionCache cache )
{
    public async Task<TelemetrySnapshotDto> GetSnapshotAsync( int take, CancellationToken cancellationToken )
    {
        string cacheKey = TelemetryCacheKeys.Snapshot( take );
        return await cache.GetOrSetAsync( cacheKey, token => LoadSnapshotAsync( take, token ), options => options.Duration = TimeSpan.FromSeconds( 30 ), [ TelemetryCacheKeys.SNAPSHOT_TAG ], cancellationToken );
    }

    
    private async Task<TelemetrySnapshotDto> LoadSnapshotAsync( int take, CancellationToken cancellationToken )
    {
        List<TelemetryLogEntity> logEntities = await dbContext.Logs.AsNoTracking().OrderByDescending( x => x.TimestampUtc ?? x.ObservedTimestampUtc ?? x.ReceivedAtUtc ).Take( take ).ToListAsync( cancellationToken );

        List<TelemetrySpanEntity> spanEntities = await dbContext.Spans.AsNoTracking().OrderByDescending( x => x.StartTimeUtc ?? x.ReceivedAtUtc ).Take( take ).ToListAsync( cancellationToken );

        List<TelemetryMetricEntity> metricEntities = await dbContext.Metrics.AsNoTracking().OrderByDescending( x => x.TimestampUtc ?? x.ReceivedAtUtc ).Take( take ).ToListAsync( cancellationToken );

        TelemetryOverviewDto overview = new(GeneratedAtUtc: DateTimeOffset.UtcNow,
                                            TotalLogs: await dbContext.Logs.CountAsync( cancellationToken ),
                                            TotalSpans: await dbContext.Spans.CountAsync( cancellationToken ),
                                            TotalMetrics: await dbContext.Metrics.CountAsync( cancellationToken ),
                                            LogTimeline: BuildTimeline( logEntities.Select( x => x.TimestampUtc       ?? x.ObservedTimestampUtc ?? x.ReceivedAtUtc ) ),
                                            SpanTimeline: BuildTimeline( spanEntities.Select( x => x.StartTimeUtc     ?? x.ReceivedAtUtc ) ),
                                            MetricTimeline: BuildTimeline( metricEntities.Select( x => x.TimestampUtc ?? x.ReceivedAtUtc ) ),
                                            SeverityBreakdown: logEntities.GroupBy( x => x.SeverityText ?? "Unknown" ).OrderByDescending( group => group.Count() ).Take( 8 ).Select( group => new NamedValueDto( group.Key, group.Count() ) ).ToArray(),
                                            ServiceBreakdown: logEntities.Select( x => x.ServiceName )
                                                                         .Concat( spanEntities.Select( x => x.ServiceName ) )
                                                                         .Concat( metricEntities.Select( x => x.ServiceName ) )
                                                                         .Where( value => !string.IsNullOrWhiteSpace( value ) )
                                                                         .GroupBy( value => value! )
                                                                         .OrderByDescending( group => group.Count() )
                                                                         .Take( 8 )
                                                                         .Select( group => new NamedValueDto( group.Key, group.Count() ) )
                                                                         .ToArray(),
                                            MetricBreakdown: metricEntities.GroupBy( x => x.Name ?? "unnamed" ).OrderByDescending( group => group.Count() ).Take( 8 ).Select( group => new NamedValueDto( group.Key, group.Count() ) ).ToArray());

        return new TelemetrySnapshotDto( overview, logEntities.Select( MapLog ).ToArray(), spanEntities.Select( MapSpan ).ToArray(), metricEntities.Select( MapMetric ).ToArray() );
    }

    private static TelemetryLogRecordDto MapLog( TelemetryLogEntity entity ) => new(entity.ID,
                                                                                    entity.ReceivedAtUtc,
                                                                                    entity.TimestampUtc,
                                                                                    entity.ObservedTimestampUtc,
                                                                                    entity.ServiceName,
                                                                                    entity.SeverityText,
                                                                                    entity.SeverityNumber,
                                                                                    entity.Body,
                                                                                    entity.TraceId,
                                                                                    entity.SpanId,
                                                                                    entity.ScopeName,
                                                                                    entity.ScopeVersion,
                                                                                    entity.CategoryName,
                                                                                    entity.Flags,
                                                                                    entity.ResourceAttributesJson,
                                                                                    entity.ScopeAttributesJson,
                                                                                    entity.AttributesJson);

    private static TelemetrySpanRecordDto MapSpan( TelemetrySpanEntity entity ) => new(entity.ID,
                                                                                       entity.ReceivedAtUtc,
                                                                                       entity.StartTimeUtc,
                                                                                       entity.EndTimeUtc,
                                                                                       entity.ServiceName,
                                                                                       entity.TraceId,
                                                                                       entity.SpanId,
                                                                                       entity.ParentSpanId,
                                                                                       entity.Name,
                                                                                       entity.Kind,
                                                                                       entity.TraceState,
                                                                                       entity.StatusCode,
                                                                                       entity.StatusMessage,
                                                                                       entity.DurationMilliseconds,
                                                                                       entity.ScopeName,
                                                                                       entity.ScopeVersion,
                                                                                       entity.ResourceAttributesJson,
                                                                                       entity.ScopeAttributesJson,
                                                                                       entity.AttributesJson,
                                                                                       entity.EventsJson,
                                                                                       entity.LinksJson);

    private static TelemetryMetricRecordDto MapMetric( TelemetryMetricEntity entity ) => new(entity.ID,
                                                                                             entity.ReceivedAtUtc,
                                                                                             entity.StartTimeUtc,
                                                                                             entity.TimestampUtc,
                                                                                             entity.ServiceName,
                                                                                             entity.Name,
                                                                                             entity.Description,
                                                                                             entity.Unit,
                                                                                             entity.MetricType,
                                                                                             entity.AggregationTemporality,
                                                                                             entity.IsMonotonic,
                                                                                             entity.NumericValue,
                                                                                             entity.Sum,
                                                                                             entity.Count,
                                                                                             entity.Min,
                                                                                             entity.Max,
                                                                                             entity.ScopeName,
                                                                                             entity.ScopeVersion,
                                                                                             entity.ResourceAttributesJson,
                                                                                             entity.ScopeAttributesJson,
                                                                                             entity.AttributesJson,
                                                                                             entity.MetadataAttributesJson,
                                                                                             entity.DistributionJson,
                                                                                             entity.QuantilesJson);

    private static TelemetryTimeSeriesPointDto[] BuildTimeline( IEnumerable<DateTimeOffset> values ) =>
        values.GroupBy( Bucket ).OrderBy( static group => group.Key ).Select( static group => new TelemetryTimeSeriesPointDto( group.Key, group.Count() ) ).ToArray();

    private static DateTimeOffset Bucket( DateTimeOffset value )
    {
        DateTimeOffset utc          = value.ToUniversalTime();
        int            minuteBucket = utc.Minute - utc.Minute % 10;
        return new DateTimeOffset( utc.Year, utc.Month, utc.Day, utc.Hour, minuteBucket, 0, TimeSpan.Zero );
    }
}
