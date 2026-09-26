using System.Runtime.InteropServices;
using Jakar.OpenTelemetry.Api.Data;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.EntityFrameworkCore;
using ZiggyCreatures.Caching.Fusion;
using ZLinq;

namespace Jakar.OpenTelemetry.Api.Services;

public sealed class TelemetryQueryService( TelemetryDbContext dbContext, IFusionCache cache )
{
    /// <summary> Above this many rows (per <c>pg_class.reltuples</c>) the dashboard shows the planner's estimate instead of running a full-table <c>COUNT(*)</c>. </summary>
    private const long EXACT_COUNT_THRESHOLD = 1_000_000;

    private const int BREAKDOWN_SIZE = 8;

    private const string ESTIMATES_SQL = """
                                         SELECT c.relname AS "Name", c.reltuples::bigint AS "Estimate"
                                         FROM pg_class c
                                         WHERE c.oid IN (to_regclass('"Logs"'), to_regclass('"Spans"'), to_regclass('"Metrics"'))
                                         """;


    public async Task<TelemetrySnapshotDto> GetSnapshotAsync( int take, CancellationToken token )
    {
        string cacheKey = TelemetryCacheKeys.Snapshot( take );
        return await cache.GetOrSetAsync( cacheKey, token => LoadSnapshotAsync( take, token ), options => options.Duration = TimeSpan.FromSeconds( 30 ), [ TelemetryCacheKeys.SNAPSHOT_TAG ], token );
    }


    private async Task<TelemetrySnapshotDto> LoadSnapshotAsync( int take, CancellationToken token )
    {
        // "SortTimeUtc" is a stored generated column with a DESC index, so each of these is an index scan that stops after `take` rows.
        List<TelemetryLogEntity>    logEntities    = await dbContext.Logs.AsNoTracking().OrderByDescending( x => x.SortTimeUtc ).Take( take ).ToListAsync( token );
        List<TelemetrySpanEntity>   spanEntities   = await dbContext.Spans.AsNoTracking().OrderByDescending( x => x.SortTimeUtc ).Take( take ).ToListAsync( token );
        List<TelemetryMetricEntity> metricEntities = await dbContext.Metrics.AsNoTracking().OrderByDescending( x => x.SortTimeUtc ).Take( take ).ToListAsync( token );

        ( long totalLogs, long totalSpans, long totalMetrics ) = await CountAsync( token );

        Dictionary<string, int> services = new(StringComparer.Ordinal);
        foreach ( TelemetryLogEntity entity in logEntities ) { Tally( services, entity.ServiceName ); }

        foreach ( TelemetrySpanEntity entity in spanEntities ) { Tally( services, entity.ServiceName ); }

        foreach ( TelemetryMetricEntity entity in metricEntities ) { Tally( services, entity.ServiceName ); }

        Dictionary<string, int> severities = new(StringComparer.Ordinal);
        foreach ( TelemetryLogEntity entity in logEntities ) { Tally( severities, entity.SeverityText ?? "Unknown" ); }

        Dictionary<string, int> metricNames = new(StringComparer.Ordinal);
        foreach ( TelemetryMetricEntity entity in metricEntities ) { Tally( metricNames, entity.Name ?? "unnamed" ); }

        TelemetryOverviewDto overview = new(GeneratedAtUtc: DateTimeOffset.UtcNow,
                                            TotalLogs: totalLogs,
                                            TotalSpans: totalSpans,
                                            TotalMetrics: totalMetrics,
                                            LogTimeline: BuildTimeline( logEntities, static x => x.SortTimeUtc ),
                                            SpanTimeline: BuildTimeline( spanEntities, static x => x.SortTimeUtc ),
                                            MetricTimeline: BuildTimeline( metricEntities, static x => x.SortTimeUtc ),
                                            SeverityBreakdown: TopN( severities ),
                                            ServiceBreakdown: TopN( services ),
                                            MetricBreakdown: TopN( metricNames ));

        return new TelemetrySnapshotDto( overview,
                                         logEntities.AsValueEnumerable().Select( MapLog ).ToArray(),
                                         spanEntities.AsValueEnumerable().Select( MapSpan ).ToArray(),
                                         metricEntities.AsValueEnumerable().Select( MapMetric ).ToArray() );
    }

    private async Task<(long Logs, long Spans, long Metrics)> CountAsync( CancellationToken token )
    {
        List<TableEstimate> estimates = await dbContext.Database.SqlQueryRaw<TableEstimate>( ESTIMATES_SQL ).ToListAsync( token );

        long logs    = await CountAsync( dbContext.Logs,    Estimate( estimates, "Logs" ),    token );
        long spans   = await CountAsync( dbContext.Spans,   Estimate( estimates, "Spans" ),   token );
        long metrics = await CountAsync( dbContext.Metrics, Estimate( estimates, "Metrics" ), token );
        return ( logs, spans, metrics );

        static long Estimate( List<TableEstimate> estimates, string table ) => estimates.AsValueEnumerable().FirstOrDefault( x => x.Name == table )?.Estimate ?? -1;

        // reltuples is -1 until the table has been vacuumed/analyzed at least once.
        static async Task<long> CountAsync<T>( DbSet<T> set, long estimate, CancellationToken token )
            where T : class => estimate is >= 0 and >= EXACT_COUNT_THRESHOLD
                                   ? estimate
                                   : await set.LongCountAsync( token );
    }

    private static void Tally( Dictionary<string, int> counts, string? key )
    {
        if ( string.IsNullOrWhiteSpace( key ) ) { return; }

        CollectionsMarshal.GetValueRefOrAddDefault( counts, key, out _ )++;
    }

    private static NamedValueDto[] TopN( Dictionary<string, int> counts ) => counts.AsValueEnumerable()
                                                                                   .OrderByDescending( static pair => pair.Value )
                                                                                   .ThenBy( static pair => pair.Key, StringComparer.Ordinal )
                                                                                   .Take( BREAKDOWN_SIZE )
                                                                                   .Select( static pair => new NamedValueDto( pair.Key, pair.Value ) )
                                                                                   .ToArray();

    private static TelemetryTimeSeriesPointDto[] BuildTimeline<T>( List<T> items, Func<T, DateTimeOffset> selector )
    {
        Dictionary<DateTimeOffset, int> buckets = new();
        foreach ( T item in items ) { CollectionsMarshal.GetValueRefOrAddDefault( buckets, Bucket( selector( item ) ), out _ )++; }

        return buckets.AsValueEnumerable().OrderBy( static pair => pair.Key ).Select( static pair => new TelemetryTimeSeriesPointDto( pair.Key, pair.Value ) ).ToArray();
    }

    private static DateTimeOffset Bucket( DateTimeOffset value )
    {
        DateTimeOffset utc          = value.ToUniversalTime();
        int            minuteBucket = utc.Minute - utc.Minute % 10;
        return new DateTimeOffset( utc.Year, utc.Month, utc.Day, utc.Hour, minuteBucket, 0, TimeSpan.Zero );
    }


    private static TelemetryLogRecordDto MapLog( TelemetryLogEntity entity ) => new(entity.ID,
                                                                                    entity.ReceivedAtUtc,
                                                                                    entity.TimestampUtc,
                                                                                    entity.ObservedTimestampUtc,
                                                                                    entity.ServiceName,
                                                                                    entity.SeverityText,
                                                                                    entity.SeverityNumber,
                                                                                    entity.Body,
                                                                                    TelemetryJson.ToHex( entity.TraceId ),
                                                                                    TelemetryJson.ToHex( entity.SpanId ),
                                                                                    entity.ScopeName,
                                                                                    entity.ScopeVersion,
                                                                                    entity.CategoryName,
                                                                                    (uint)entity.Flags,
                                                                                    entity.ResourceAttributesJson,
                                                                                    entity.ScopeAttributesJson,
                                                                                    entity.AttributesJson,
                                                                                    entity.EventName,
                                                                                    (uint)entity.DroppedAttributesCount,
                                                                                    entity.ResourceSchemaUrl,
                                                                                    entity.ScopeSchemaUrl);

    private static TelemetrySpanRecordDto MapSpan( TelemetrySpanEntity entity ) => new(entity.ID,
                                                                                       entity.ReceivedAtUtc,
                                                                                       entity.StartTimeUtc,
                                                                                       entity.EndTimeUtc,
                                                                                       entity.ServiceName,
                                                                                       TelemetryJson.ToHex( entity.TraceId ),
                                                                                       TelemetryJson.ToHex( entity.SpanId ),
                                                                                       TelemetryJson.ToHex( entity.ParentSpanId ),
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
                                                                                       entity.LinksJson,
                                                                                       (uint)entity.Flags,
                                                                                       (uint)entity.DroppedAttributesCount,
                                                                                       (uint)entity.DroppedEventsCount,
                                                                                       (uint)entity.DroppedLinksCount,
                                                                                       entity.ResourceSchemaUrl,
                                                                                       entity.ScopeSchemaUrl);

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
                                                                                             entity.QuantilesJson,
                                                                                             entity.IntValue,
                                                                                             (uint)entity.Flags,
                                                                                             entity.ExemplarsJson,
                                                                                             entity.ResourceSchemaUrl,
                                                                                             entity.ScopeSchemaUrl);


    private sealed class TableEstimate
    {
        public string Name     { get; init; } = string.Empty;
        public long   Estimate { get; init; }
    }
}
