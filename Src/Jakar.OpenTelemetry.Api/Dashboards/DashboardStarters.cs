namespace Jakar.OpenTelemetry.Api.Dashboards;

/// <summary>
///     The dashboards every installation starts with, shared with everyone. They are added once (by fixed id): an admin can edit, re-share or delete them,
///     and that sticks.
/// </summary>
public static class DashboardStarters
{
	public static readonly Guid s_ServiceOverview = Guid.Parse( "0199f000-0000-7000-8000-000000000001" );
	public static readonly Guid s_DotNetRuntime   = Guid.Parse( "0199f000-0000-7000-8000-000000000002" );
	public static readonly Guid s_Traces          = Guid.Parse( "0199f000-0000-7000-8000-000000000003" );


	public static IReadOnlyList<DashboardRecord> All( DateTimeOffset now ) =>
	[
		Starter( s_ServiceOverview, "Service overview", "Requests, errors and latency of ASP.NET Core services, with their logs and traces.", ServiceOverview(), now ),
		Starter( s_DotNetRuntime,   ".NET runtime",     "Memory, garbage collection, CPU, thread pool, JIT and exceptions per service.",       DotNetRuntime(),   now ),
		Starter( s_Traces,          "Traces",           "Span rates and durations by service and operation, and the latest traces.",           Traces(),          now )
	];


	private static DashboardRecord Starter( Guid id, string name, string description, DashboardDefinition definition, DateTimeOffset now ) => new(id, name, description, string.Empty, DashboardVisibility.Everyone, [ ], definition.Normalize(), true, now, now, 1);

	private static DashboardDefinition ServiceOverview()
	{
		const string REQUESTS = "http.server.request.duration";

		return new DashboardDefinition
			   {
				   TimeRange = "1h",
				   Variables = [ new DashboardVariable( "service", "Service" ), new DashboardVariable( "http.route", "Route" ) ],
				   Panels =
				   [
					   Metric( "Requests",            PanelTypeIds.STAT, REQUESTS, PanelAggregation.Increase, 3, 1 ),
					   Metric( "Request rate",        PanelTypeIds.STAT, REQUESTS, PanelAggregation.Rate,     3, 1 ),
					   Metric( "Server errors (5xx)", PanelTypeIds.STAT, REQUESTS, PanelAggregation.Increase, 3, 1, filters: [ new PanelFilter( "http.response.status_code", FilterOperator.GreaterOrEqual, "500" ) ], thresholds: Bad( 1 ) ),
					   Metric( "p95 latency",         PanelTypeIds.STAT, REQUESTS, PanelAggregation.P95,      3, 1 ),

					   Metric( "Request rate by route", PanelTypeIds.TIME_SERIES, REQUESTS, PanelAggregation.Rate, 6, 2, "http.route", limit: 10 ),
					   Metric( "p95 latency by route",  PanelTypeIds.TIME_SERIES, REQUESTS, PanelAggregation.P95,  6, 2, "http.route", limit: 10 ),

					   Metric( "Requests by status code",       PanelTypeIds.BAR,         REQUESTS,                      PanelAggregation.Increase, 4, 2, "http.response.status_code" ),
					   Metric( "Slowest routes (p95)",          PanelTypeIds.TABLE,       REQUESTS,                      PanelAggregation.P95,      4, 2, "http.route" ),
					   Metric( "Outgoing HTTP p95 by host",     PanelTypeIds.TIME_SERIES, "http.client.request.duration", PanelAggregation.P95,      4, 2, "server.address", limit: 10 ),

					   Records( "Warnings and errors", PanelTypeIds.LOGS,   TelemetrySignal.Logs,  6, 3, [ new PanelFilter( "severity", FilterOperator.GreaterOrEqual, "Warning" ) ] ),
					   Records( "Recent traces",       PanelTypeIds.TRACES, TelemetrySignal.Spans, 6, 3 )
				   ]
			   };
	}

	private static DashboardDefinition DotNetRuntime() => new()
														  {
															  TimeRange = "1h",
															  Variables = [ new DashboardVariable( "service", "Service" ) ],
															  Panels =
															  [
																  Metric( "Working set",       PanelTypeIds.STAT, "dotnet.process.memory.working_set",     PanelAggregation.Last,     3, 1 ),
																  Metric( "GC heap",           PanelTypeIds.STAT, "dotnet.gc.last_collection.heap.size",   PanelAggregation.Last,     3, 1 ),
																  Metric( "Assemblies loaded", PanelTypeIds.STAT, "dotnet.assembly.count",                 PanelAggregation.Last,     3, 1 ),
																  Metric( "Exceptions",        PanelTypeIds.STAT, "dotnet.exceptions",                     PanelAggregation.Increase, 3, 1, thresholds: Bad( 1 ) ),

																  Metric( "CPU time per second",     PanelTypeIds.TIME_SERIES, "dotnet.process.cpu.time",             PanelAggregation.Rate,     6, 2, "cpu.mode" ),
																  Metric( "Allocation rate",         PanelTypeIds.TIME_SERIES, "dotnet.gc.heap.total_allocated",      PanelAggregation.Rate,     6, 2 ),
																  Metric( "GC collections",          PanelTypeIds.TIME_SERIES, "dotnet.gc.collections",               PanelAggregation.Increase, 6, 2, "gc.heap.generation" ),
																  Metric( "GC pause time per second", PanelTypeIds.TIME_SERIES, "dotnet.gc.pause.time",               PanelAggregation.Rate,     6, 2 ),
																  Metric( "Heap size by generation", PanelTypeIds.TIME_SERIES, "dotnet.gc.last_collection.heap.size", PanelAggregation.Avg,      6, 2, "gc.heap.generation" ),
																  Metric( "Thread pool threads",     PanelTypeIds.TIME_SERIES, "dotnet.thread_pool.thread.count",     PanelAggregation.Avg,      6, 2 ),
																  Metric( "Thread pool queue",       PanelTypeIds.TIME_SERIES, "dotnet.thread_pool.queue.length",     PanelAggregation.Avg,      6, 2 ),
																  Metric( "Exceptions by type",      PanelTypeIds.BAR,         "dotnet.exceptions",                   PanelAggregation.Increase, 6, 2, "error.type" ),
																  Metric( "JIT compiled methods",    PanelTypeIds.TIME_SERIES, "dotnet.jit.compiled_methods",         PanelAggregation.Increase, 6, 2 ),
																  Metric( "Lock contentions",        PanelTypeIds.TIME_SERIES, "dotnet.monitor.lock_contentions",     PanelAggregation.Increase, 6, 2 )
															  ]
														  };

	private static DashboardDefinition Traces() => new()
												   {
													   TimeRange = "1h",
													   Variables = [ new DashboardVariable( "service", "Service" ) ],
													   Panels =
													   [
														   Counts( "Spans",             PanelTypeIds.STAT, TelemetrySignal.Spans, PanelAggregation.Count, 3, 1 ),
														   Counts( "Error spans",       PanelTypeIds.STAT, TelemetrySignal.Spans, PanelAggregation.Count, 3, 1, filters: [ new PanelFilter( "status", FilterOperator.Equals, "Error" ) ], thresholds: Bad( 1 ) ),
														   Counts( "p95 span duration", PanelTypeIds.STAT, TelemetrySignal.Spans, PanelAggregation.P95,   3, 1 ),
														   Counts( "Logs",              PanelTypeIds.STAT, TelemetrySignal.Logs,  PanelAggregation.Count, 3, 1 ),

														   Counts( "Spans per second by service", PanelTypeIds.TIME_SERIES, TelemetrySignal.Spans, PanelAggregation.Rate,  6, 2, "service" ),
														   Counts( "p95 duration by operation",   PanelTypeIds.TIME_SERIES, TelemetrySignal.Spans, PanelAggregation.P95,   6, 2, "name", limit: 10 ),
														   Counts( "Logs by severity",            PanelTypeIds.BAR,         TelemetrySignal.Logs,  PanelAggregation.Count, 4, 2, "severity" ),
														   Counts( "Slowest operations (p95)",    PanelTypeIds.TABLE,       TelemetrySignal.Spans, PanelAggregation.P95,   8, 2, "name" ),

														   Records( "Recent traces", PanelTypeIds.TRACES, TelemetrySignal.Spans, 12, 3 )
													   ]
												   };


	private static DashboardPanel Metric( string title, string type, string metric, PanelAggregation aggregation, int width, int height, string? groupBy = null, int limit = 20, List<PanelFilter>? filters = null, List<PanelThreshold>? thresholds = null ) =>
		new()
		{
			ID      = Slug( title ),
			Title   = title,
			Type    = type,
			Width   = width,
			Height  = height,
			Query   = new PanelQuery { Signal = TelemetrySignal.Metrics, Metric = metric, Aggregation = aggregation, GroupBy = groupBy, Limit = limit, Filters = filters ?? [ ] },
			Options = new PanelOptions { Thresholds = thresholds ?? [ ] }
		};

	private static DashboardPanel Counts( string title, string type, TelemetrySignal signal, PanelAggregation aggregation, int width, int height, string? groupBy = null, int limit = 20, List<PanelFilter>? filters = null, List<PanelThreshold>? thresholds = null ) =>
		new()
		{
			ID      = Slug( title ),
			Title   = title,
			Type    = type,
			Width   = width,
			Height  = height,
			Query   = new PanelQuery { Signal = signal, Aggregation = aggregation, GroupBy = groupBy, Limit = limit, Filters = filters ?? [ ] },
			Options = new PanelOptions { Thresholds = thresholds ?? [ ] }
		};

	private static DashboardPanel Records( string title, string type, TelemetrySignal signal, int width, int height, List<PanelFilter>? filters = null ) =>
		new()
		{
			ID     = Slug( title ),
			Title  = title,
			Type   = type,
			Width  = width,
			Height = height,
			Query  = new PanelQuery { Signal = signal, Limit = 50, Filters = filters ?? [ ] }
		};

	/// <summary> Green from zero, red from <paramref name="from"/>. </summary>
	private static List<PanelThreshold> Bad( double from ) => [ new PanelThreshold( 0, "green" ), new PanelThreshold( from, "red" ) ];

	private static string Slug( string title ) => new(title.ToLowerInvariant().Select( static c => char.IsLetterOrDigit( c ) ? c : '-' ).ToArray());
}
