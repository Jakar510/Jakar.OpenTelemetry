using System.Globalization;
using Jakar.OpenTelemetry.Api.Data;
using Jakar.OpenTelemetry.Api.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Jakar.OpenTelemetry.Api.Dashboards;

/// <summary> Runs panel queries: metrics are aggregated by <see cref="MetricAggregator"/>, logs and spans in SQL, records through EF. </summary>
public sealed class PanelQueryService( NpgsqlDataSource dataSource, IDbContextFactory<TelemetryDbContext> contextFactory, ILogger<PanelQueryService> logger )
{
	/// <summary> The most metric points one panel reads; beyond it the panel says it is partial. </summary>
	public const int MAX_METRIC_POINTS = 200_000;

	private const int MAX_VARIABLE_VALUES = 200;


	/// <summary> Runs <paramref name="panel"/>'s query over <paramref name="window"/>, with the dashboard's variable selections as extra filters. </summary>
	public async Task<PanelResult> RunAsync( DashboardPanel panel, PanelShape shape, PanelWindow window, IReadOnlyList<PanelFilter> variables, CancellationToken token )
	{
		PanelQuery        query   = panel.Query;
		List<PanelFilter> filters = [ .. query.Filters, .. variables ];

		try
		{
			return shape == PanelShape.Records
					   ? await RecordsAsync( query, filters, window, token )
					   : query.Signal == TelemetrySignal.Metrics
						   ? await MetricsAsync( query, filters, window, shape, token )
						   : await CountsAsync( query, filters, window, shape, token );
		}
		catch ( OperationCanceledException ) when ( token.IsCancellationRequested ) { throw; }
		catch ( Exception e ) when ( e is NpgsqlException or InvalidOperationException or InvalidCastException or TimeoutException )
		{
			logger.LogWarning( e, "Panel {PanelId} query failed", panel.ID );
			return PanelResult.Failed( $"Query failed: {e.Message.TrimEnd( '.' )}" );
		}
	}

	/// <summary> Values seen for a variable's key in the time range (services, routes, ...), for its selector. </summary>
	public async Task<IReadOnlyList<string>> ValuesAsync( string key, PanelWindow window, CancellationToken token )
	{
		SortedSet<string> values = new(StringComparer.OrdinalIgnoreCase);

		foreach ( TelemetrySignal signal in Enum.GetValues<TelemetrySignal>() )
		{
			PanelSqlBuilder sql        = new(signal);
			string          expression = sql.Key( key );
			string          where      = $"\"SortTimeUtc\" >= {sql.Parameter( window.From.ToUniversalTime(), NpgsqlDbType.TimestampTz )} AND \"SortTimeUtc\" <= {sql.Parameter( window.To.ToUniversalTime(), NpgsqlDbType.TimestampTz )}";

			await using NpgsqlCommand command = Command( $"SELECT DISTINCT {expression} AS value FROM {sql.Table} WHERE {where} AND {expression} IS NOT NULL LIMIT {MAX_VARIABLE_VALUES}", sql );

			try
			{
				await using NpgsqlDataReader reader = await command.ExecuteReaderAsync( token );
				while ( await reader.ReadAsync( token ) ) { values.Add( reader.GetString( 0 ) ); }
			}
			catch ( NpgsqlException e ) { logger.LogWarning( e, "Values of {Key} could not be read", key ); }
		}

		return values.Take( MAX_VARIABLE_VALUES ).ToArray();
	}

	/// <summary> Metric names seen in the time range, for the panel editor. </summary>
	public async Task<IReadOnlyList<string>> MetricNamesAsync( PanelWindow window, CancellationToken token )
	{
		await using NpgsqlCommand command = dataSource.CreateCommand( """SELECT DISTINCT "Name" FROM "Metrics" WHERE "SortTimeUtc" >= @from AND "Name" IS NOT NULL ORDER BY 1 LIMIT 1000""" );
		command.Parameters.AddWithValue( "from", window.From.ToUniversalTime() );

		List<string>                 names  = [ ];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync( token );
		while ( await reader.ReadAsync( token ) ) { names.Add( reader.GetString( 0 ) ); }

		return names;
	}


	private async Task<PanelResult> MetricsAsync( PanelQuery query, List<PanelFilter> filters, PanelWindow window, PanelShape shape, CancellationToken token )
	{
		if ( string.IsNullOrWhiteSpace( query.Metric ) ) { return PanelResult.Failed( "Choose a metric" ); }

		// One step (at least two minutes) before the window, so counters have a baseline for their first change inside it.
		TimeSpan        lookback = window.Step > TimeSpan.FromMinutes( 2 ) ? window.Step : TimeSpan.FromMinutes( 2 );
		PanelSqlBuilder sql      = new(TelemetrySignal.Metrics);
		string          where    = sql.Where( window.From - lookback, window.To, query.Metric, filters );
		string          group    = string.IsNullOrWhiteSpace( query.GroupBy ) ? "''" : $"COALESCE({sql.Key( query.GroupBy )}, '')";

		await using NpgsqlCommand command = Command( $"""
													  SELECT "SortTimeUtc", "StartTimeUtc", COALESCE("ServiceName", ''), "AttributesJson"::text, COALESCE("ResourceAttributesJson" ->> 'service.instance.id', ''),
													         "MetricType", "AggregationTemporality", "IsMonotonic", "NumericValue", "Count", "Sum", "Min", "Max", "DistributionJson"::text, "Unit", {group}
													  FROM "Metrics"
													  WHERE {where}
													  ORDER BY "SortTimeUtc"
													  LIMIT {MAX_METRIC_POINTS + 1}
													  """,
													  sql );

		List<MetricSample> samples = [ ];
		string?            unit    = null;
		string?            type    = null;

		await using ( NpgsqlDataReader reader = await command.ExecuteReaderAsync( token ) )
		{
			while ( await reader.ReadAsync( token ) )
			{
				type ??= reader.IsDBNull( 5 ) ? null : reader.GetString( 5 );
				unit ??= reader.IsDBNull( 14 ) ? null : reader.GetString( 14 );

				// A series is one instance's attribute set: cumulative totals are only comparable within it.
				string series = $"{reader.GetString( 2 )}\u001f{reader.GetString( 4 )}\u001f{reader.GetString( 3 )}";

				samples.Add( new MetricSample( reader.GetFieldValue<DateTimeOffset>( 0 ),
											   reader.IsDBNull( 1 ) ? null : reader.GetFieldValue<DateTimeOffset>( 1 ),
											   series,
											   reader.GetString( 15 ),
											   reader.IsDBNull( 5 ) ? null : reader.GetString( 5 ),
											   reader.IsDBNull( 6 ) ? null : reader.GetString( 6 ),
											   reader.IsDBNull( 7 ) ? null : reader.GetBoolean( 7 ),
											   reader.GetDouble( 8 ),
											   reader.IsDBNull( 9 ) ? null : reader.GetInt64( 9 ),
											   reader.IsDBNull( 10 ) ? null : reader.GetDouble( 10 ),
											   reader.IsDBNull( 11 ) ? null : reader.GetDouble( 11 ),
											   reader.IsDBNull( 12 ) ? null : reader.GetDouble( 12 ),
											   HistogramBuckets.Parse( reader.IsDBNull( 13 ) ? null : reader.GetString( 13 ) ) ) );
			}
		}

		bool truncated = samples.Count > MAX_METRIC_POINTS;
		if ( truncated ) { samples.RemoveAt( samples.Count - 1 ); }

		return new PanelResult( MetricAggregator.Aggregate( samples, window, query.Aggregation, shape, query.Limit ), MetricAggregator.ResultUnit( unit, type, query.Aggregation ) ) { Truncated = truncated };
	}

	/// <summary> Logs and spans: counts and rates (and, for spans, durations) per bucket and group, computed by PostgreSQL. </summary>
	private async Task<PanelResult> CountsAsync( PanelQuery query, List<PanelFilter> filters, PanelWindow window, PanelShape shape, CancellationToken token )
	{
		PanelSqlBuilder sql      = new(query.Signal);
		string          where    = sql.Where( window.From, window.To, null, filters );
		string          group    = string.IsNullOrWhiteSpace( query.GroupBy ) ? "''" : $"COALESCE({sql.Key( query.GroupBy )}, '')";
		bool            overTime = shape == PanelShape.TimeSeries;
		string bucket = overTime
							? $"date_bin({sql.Parameter( window.Step, NpgsqlDbType.Interval )}, \"SortTimeUtc\", {sql.Parameter( window.From.ToUniversalTime(), NpgsqlDbType.TimestampTz )})"
							: sql.Parameter( window.To.ToUniversalTime(), NpgsqlDbType.TimestampTz );
		string duration = query.Signal == TelemetrySignal.Spans
							  ? DurationExpression( query.Aggregation )
							  : "NULL::double precision";

		await using NpgsqlCommand command = Command( $"""
													  SELECT {bucket} AS bucket, {group} AS grp, count(*) AS n, {duration} AS value
													  FROM {sql.Table}
													  WHERE {where}
													  GROUP BY 1, 2
													  ORDER BY 1
													  """,
													  sql );

		double                                          seconds = overTime ? window.Step.TotalSeconds : window.Range.TotalSeconds;
		Dictionary<string, List<PanelPoint>>            series  = new(StringComparer.Ordinal);
		await using ( NpgsqlDataReader reader = await command.ExecuteReaderAsync( token ) )
		{
			while ( await reader.ReadAsync( token ) )
			{
				long    count = reader.GetInt64( 2 );
				double? value = CountValue( query.Signal, query.Aggregation, count, reader.IsDBNull( 3 ) ? null : reader.GetDouble( 3 ), seconds );
				if ( value is null ) { continue; }

				string name = reader.GetString( 1 );
				if ( !series.TryGetValue( name, out List<PanelPoint>? points ) ) { series[name] = points = [ ]; }

				points.Add( new PanelPoint( reader.GetFieldValue<DateTimeOffset>( 0 ), value.Value ) );
			}
		}

		PanelSeries[] result = series.Select( static x => new PanelSeries( x.Key, x.Value ) )
									 .OrderByDescending( static x => x.Points.Sum( static p => Math.Abs( p.Value ) ) )
									 .ThenBy( static x => x.Name, StringComparer.Ordinal )
									 .Take( Math.Max( 1, query.Limit ) )
									 .ToArray();

		return new PanelResult( result, CountUnit( query.Signal, query.Aggregation ) );
	}

	private async Task<PanelResult> RecordsAsync( PanelQuery query, List<PanelFilter> filters, PanelWindow window, CancellationToken token )
	{
		PanelSqlBuilder sql   = new(query.Signal == TelemetrySignal.Spans ? TelemetrySignal.Spans : TelemetrySignal.Logs);
		string          where = sql.Where( window.From, window.To, null, filters );
		int             limit = Math.Clamp( query.Limit, 1, 500 );

		await using TelemetryDbContext dbContext = await contextFactory.CreateDbContextAsync( token );

		if ( sql.Signal == TelemetrySignal.Spans )
		{
			// A trace list: the root span of each trace (spans whose parent isn't recorded are roots too).
			List<TelemetrySpanEntity> spans = await dbContext.Spans.FromSqlRaw( $"""SELECT * FROM "Spans" WHERE {where} AND "ParentSpanId" IS NULL ORDER BY "SortTimeUtc" DESC LIMIT {limit}""", [ .. sql.Parameters ] ).AsNoTracking().ToListAsync( token );
			return PanelResult.s_Empty with { Spans = spans.Select( TelemetryQueryService.MapSpan ).ToArray() };
		}

		List<TelemetryLogEntity> logs = await dbContext.Logs.FromSqlRaw( $"""SELECT * FROM "Logs" WHERE {where} ORDER BY "SortTimeUtc" DESC LIMIT {limit}""", [ .. sql.Parameters ] ).AsNoTracking().ToListAsync( token );
		return PanelResult.s_Empty with { Logs = logs.Select( TelemetryQueryService.MapLog ).ToArray() };
	}


	/// <summary> The SQL aggregate over span durations the aggregation asks for (null when it only counts). </summary>
	internal static string DurationExpression( PanelAggregation aggregation ) => aggregation switch
																				  {
																					  PanelAggregation.Avg or PanelAggregation.Last => "avg(\"DurationMilliseconds\")",
																					  PanelAggregation.Sum                          => "sum(\"DurationMilliseconds\")",
																					  PanelAggregation.Min                          => "min(\"DurationMilliseconds\")",
																					  PanelAggregation.Max                          => "max(\"DurationMilliseconds\")",
																					  PanelAggregation.P50                          => "percentile_cont(0.5) WITHIN GROUP (ORDER BY \"DurationMilliseconds\")",
																					  PanelAggregation.P90                          => "percentile_cont(0.9) WITHIN GROUP (ORDER BY \"DurationMilliseconds\")",
																					  PanelAggregation.P95                          => "percentile_cont(0.95) WITHIN GROUP (ORDER BY \"DurationMilliseconds\")",
																					  PanelAggregation.P99                          => "percentile_cont(0.99) WITHIN GROUP (ORDER BY \"DurationMilliseconds\")",
																					  _                                             => "NULL::double precision"
																				  };

	/// <summary> Logs only count; spans count, or use the duration aggregate. </summary>
	internal static double? CountValue( TelemetrySignal signal, PanelAggregation aggregation, long count, double? duration, double seconds ) => aggregation switch
		{
			PanelAggregation.Rate                                                   => count / seconds,
			PanelAggregation.Count or PanelAggregation.Increase                     => count,
			_ when signal == TelemetrySignal.Logs                                   => count,
			_                                                                       => duration
		};

	internal static string? CountUnit( TelemetrySignal signal, PanelAggregation aggregation ) => aggregation switch
																								  {
																									  PanelAggregation.Rate                                            => "/s",
																									  PanelAggregation.Count or PanelAggregation.Increase              => null,
																									  _ when signal == TelemetrySignal.Spans                           => "ms",
																									  _                                                                => null
																								  };

	private NpgsqlCommand Command( string text, PanelSqlBuilder sql )
	{
		NpgsqlCommand command = dataSource.CreateCommand( text );
		command.Parameters.AddRange( sql.Parameters.ToArray() );
		return command;
	}
}
