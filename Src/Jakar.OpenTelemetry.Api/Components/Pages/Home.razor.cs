using System.Globalization;
using System.Runtime.CompilerServices;
using Jakar.OpenTelemetry.Api.Components.Charts;
using Jakar.OpenTelemetry.Api.Components.Dashboard;
using Jakar.OpenTelemetry.Api.Services;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;

namespace Jakar.OpenTelemetry.Api.Components.Pages;

public sealed partial class Home : ComponentBase, IDisposable
{
	[Inject] public required TelemetryQueryService         Telemetry     { get; init; }
	[Inject] public required TelemetryLiveFeed             LiveFeed      { get; init; }
	[Inject] public required IOptions<PortalConfiguration> Configuration { get; init; }

	private readonly CancellationTokenSource _disposed = new();
	private          IDisposable?            _subscription;
	private          int                     _refreshing;
	private          int                     _refreshPending;
	private          bool                    _resumeAfterDetails; // the details dialog paused live updates, so closing it resumes them
	private          ExplorerOptions?        _options;

	/// <summary> Each record's searchable text, joined once per record (records are immutable; a new snapshot brings new ones). </summary>
	private static readonly ConditionalWeakTable<object, string> _s_searchText = new();

	private TelemetrySnapshotDto?          Snapshot              { get;                                set; }
	private TelemetryTab                   ActiveTab             { get;                                set; } = TelemetryTab.Logs;
	private bool                           IsLoading             { get;                                set; } = true;
	private bool                           LiveUpdates           { get;                                set; } = true;
	private string?                        ErrorMessage          { get;                                set; }
	private string                         SearchText            { get;                                set; } = string.Empty;
	private string                         SelectedService       { get;                                set; } = "all";
	private FilterKey                      SelectedCategory      { get;                                set; } = new(FilterField.None);
	private string                         CategoryValue         { get;                                set; } = string.Empty;
	private FilterKey                      SelectedSort          { get;                                set; } = new(FilterField.Timestamp);
	private bool                           SortDescending        { get;                                set; } = true;
	private string                         SelectedSeverity      { get;                                set; } = "all";
	private string                         SelectedSpanKind      { get;                                set; } = "all";
	private string                         SelectedMetricName    { get;                                set; } = "all";
	private TelemetryRealtimeEventDto?     LastEvent             { get;                                set; }
	private Guid?                          SelectedRecordId      { get;                                set; }
	private List<TelemetryLogRecordDto>    VisibleLogs           { get;                                set; } = [ ];
	private List<TelemetrySpanRecordDto>   VisibleSpans          { get;                                set; } = [ ];
	private List<TelemetryMetricRecordDto> VisibleMetrics        { get;                                set; } = [ ];
	private string                         SelectedCategoryToken { get => ToToken( SelectedCategory ); set => SelectedCategory = ParseToken( value ); }
	private string                         SelectedSortToken     { get => ToToken( SelectedSort );     set => SelectedSort = ParseToken( value ); }

	private TelemetryLogRecordDto? SelectedLog => ActiveTab == TelemetryTab.Logs && SelectedRecordId is { } id
													  ? Snapshot?.Logs.FirstOrDefault( x => x.ID == id )
													  : null;
	private TelemetrySpanRecordDto? SelectedSpan => ActiveTab == TelemetryTab.Spans && SelectedRecordId is { } id
														? Snapshot?.Spans.FirstOrDefault( x => x.ID == id )
														: null;
	private TelemetryMetricRecordDto? SelectedMetric => ActiveTab == TelemetryTab.Metrics && SelectedRecordId is { } id
															? Snapshot?.Metrics.FirstOrDefault( x => x.ID == id )
															: null;

	/// <summary> The filter and sort choices, built once per snapshot and tab (they scan every loaded record), not on every render. </summary>
	private ExplorerOptions Options
	{
		get
		{
			if ( _options is null || !ReferenceEquals( _options.Snapshot, Snapshot ) || _options.Tab != ActiveTab )
			{
				IReadOnlyList<FilterOption> categories = BuildCategoryOptions();
				_options = new ExplorerOptions( Snapshot, ActiveTab, BuildServiceOptions(), BuildSeverityOptions(), BuildSpanKindOptions(), BuildMetricNameOptions(), categories, BuildSortOptions( categories ) );
			}

			return _options;
		}
	}

	private IReadOnlyList<string>       ServiceOptions    => Options.Services;
	private IReadOnlyList<string>       SeverityOptions   => Options.Severities;
	private IReadOnlyList<string>       SpanKindOptions   => Options.SpanKinds;
	private IReadOnlyList<string>       MetricNameOptions => Options.MetricNames;
	private IReadOnlyList<FilterOption> CategoryOptions   => Options.Categories;
	private IReadOnlyList<FilterOption> SortOptions       => Options.Sorts;

	private IReadOnlyList<string> BuildServiceOptions() => Snapshot?.Logs.Select( x => x.ServiceName ).Concat( Snapshot.Spans.Select( x => x.ServiceName ) ).Concat( Snapshot.Metrics.Select( x => x.ServiceName ) ).Where( x => !string.IsNullOrWhiteSpace( x ) ).Distinct( StringComparer.OrdinalIgnoreCase ).OrderBy( x => x ).Cast<string>().ToArray() ?? [ ];

	private IReadOnlyList<string> BuildSeverityOptions() => Snapshot?.Logs.Select( static x => x.SeverityText ).Where( static x => !string.IsNullOrWhiteSpace( x ) ).Distinct( StringComparer.OrdinalIgnoreCase ).OrderBy( static x => x ).Cast<string>().ToArray() ?? [ ];

	private IReadOnlyList<string> BuildSpanKindOptions() => Snapshot?.Spans.Select( static x => x.Kind ).Where( static x => !string.IsNullOrWhiteSpace( x ) ).Distinct( StringComparer.OrdinalIgnoreCase ).OrderBy( static x => x ).Cast<string>().ToArray() ?? [ ];

	private IReadOnlyList<string> BuildMetricNameOptions() => Snapshot?.Metrics.Select( static x => x.Name ).Where( static x => !string.IsNullOrWhiteSpace( x ) ).Distinct( StringComparer.OrdinalIgnoreCase ).OrderBy( static x => x ).Cast<string>().ToArray() ?? [ ];

	private IReadOnlyList<FilterOption> BuildCategoryOptions()
	{
		IEnumerable<string> keys = ActiveTab switch
									   {
										   TelemetryTab.Logs    => Snapshot?.Logs.SelectMany( GetLogDynamicKeys )       ?? [ ],
										   TelemetryTab.Spans   => Snapshot?.Spans.SelectMany( GetSpanDynamicKeys )     ?? [ ],
										   TelemetryTab.Metrics => Snapshot?.Metrics.SelectMany( GetMetricDynamicKeys ) ?? [ ],
										   _                    => [ ]
									   };

		return
			[
				new FilterOption( ToToken( new FilterKey( FilterField.None ) ), "Any category" ),
				.. keys.Where( static x => !x.EndsWith( "{OriginalFormat}", StringComparison.Ordinal ) ).Distinct( StringComparer.OrdinalIgnoreCase ).OrderBy( static x => x ).Select( key => new FilterOption( ToToken( new FilterKey( FilterField.None, key ) ), key ) )
			];
	}

	private IReadOnlyList<FilterOption> BuildSortOptions( IReadOnlyList<FilterOption> categories ) => ActiveTab switch
														   {
															   TelemetryTab.Logs =>
																   [
																	   new FilterOption( ToToken( new FilterKey( FilterField.Timestamp ) ), "Timestamp" ),
																	   new FilterOption( ToToken( new FilterKey( FilterField.Service ) ),   "Service" ),
																	   new FilterOption( ToToken( new FilterKey( FilterField.Severity ) ),  "Severity" ),
																	   new FilterOption( ToToken( new FilterKey( FilterField.Body ) ),      "Body" ),
																	   .. categories.Where( static option => option.Token.StartsWith( "custom:", StringComparison.Ordinal ) )
																   ],
															   TelemetryTab.Spans =>
																   [
																	   new FilterOption( ToToken( new FilterKey( FilterField.Start ) ),    "Start time" ),
																	   new FilterOption( ToToken( new FilterKey( FilterField.Service ) ),  "Service" ),
																	   new FilterOption( ToToken( new FilterKey( FilterField.Name ) ),     "Span name" ),
																	   new FilterOption( ToToken( new FilterKey( FilterField.Kind ) ),     "Kind" ),
																	   new FilterOption( ToToken( new FilterKey( FilterField.Duration ) ), "Duration" ),
																	   .. categories.Where( static option => option.Token.StartsWith( "custom:", StringComparison.Ordinal ) )
																   ],
															   TelemetryTab.Metrics =>
																   [
																	   new FilterOption( ToToken( new FilterKey( FilterField.Timestamp ) ), "Timestamp" ),
																	   new FilterOption( ToToken( new FilterKey( FilterField.Service ) ),   "Service" ),
																	   new FilterOption( ToToken( new FilterKey( FilterField.Name ) ),      "Metric name" ),
																	   new FilterOption( ToToken( new FilterKey( FilterField.Value ) ),     "Value" ),
																	   new FilterOption( ToToken( new FilterKey( FilterField.Type ) ),      "Metric type" ),
																	   .. categories.Where( static option => option.Token.StartsWith( "custom:", StringComparison.Ordinal ) )
																   ],
															   _ => [ ]
														   };

	/// <summary> The activity chart's series: when the most recently loaded records of each signal arrived (10 minute buckets). </summary>
	private IReadOnlyList<ActivitySeries> ActivitySeries =>
	[
		new("Logs",    "activity-logs",    Snapshot?.Overview.LogTimeline    ?? [ ]),
		new("Spans",   "activity-spans",   Snapshot?.Overview.SpanTimeline   ?? [ ]),
		new("Metrics", "activity-metrics", Snapshot?.Overview.MetricTimeline ?? [ ])
	];

	private string ActivityMeta => Snapshot is null
									   ? "10 minute buckets"
									   : $"Most recent {FormatCount( Math.Max( Snapshot.Logs.Count, Math.Max( Snapshot.Spans.Count, Snapshot.Metrics.Count ) ) )} of each | 10 minute buckets | log scale";

	private string ConnectionStateClass => LiveUpdates
											   ? "online"
											   : "warming";
	private string ConnectionStateLabel => LiveUpdates
											   ? "Live"
											   : "Paused";
	private string LastEventLabel => LastEvent is null
										 ? "Waiting for live OTLP traffic"
										 : $"{LastEvent.TimestampUtc:HH:mm:ss}Z | {LastEvent.Summary}";
	private string SnapshotAgeLabel => Snapshot is null
										   ? "No data"
										   : $"{Math.Max( 0, ( DateTimeOffset.UtcNow - Snapshot.Overview.GeneratedAtUtc ).TotalSeconds ):0}s";
	private string GeneratedAtLabel => Snapshot is null
										   ? string.Empty
										   : $"Generated {Snapshot.Overview.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}Z";
	private int Take => Math.Clamp( Configuration.Value.DefaultTake, 25, 1000 );

	public void Dispose()
	{
		_subscription?.Dispose();
		_disposed.Cancel();
		_disposed.Dispose();
	}

	protected override async Task OnInitializedAsync()
	{
		LastEvent     = LiveFeed.Last;
		_subscription = LiveFeed.Subscribe( OnTelemetryUpdatedAsync );
		await RefreshAsync();
	}

	private async Task OnTelemetryUpdatedAsync( TelemetryRealtimeEventDto update )
	{
		LastEvent = update;

		if ( LiveUpdates ) { await RefreshAsync( showLoading: false ); }
		else { await InvokeAsync( StateHasChanged ); }
	}

	private Task RefreshAsync() => RefreshAsync( showLoading: true );

	/// <summary>
	///     Coalesces overlapping refresh requests (live events arrive at most once per second) into at most one follow-up refresh. Live refreshes don't render a
	///     "loading" state first (<paramref name="showLoading"/> false): that would re-render the whole page twice per second while data streams in.
	/// </summary>
	private async Task RefreshAsync( bool showLoading )
	{
		if ( Interlocked.Exchange( ref _refreshing, 1 ) == 1 )
		{
			Interlocked.Exchange( ref _refreshPending, 1 );
			return;
		}

		try
		{
			do
			{
				Interlocked.Exchange( ref _refreshPending, 0 );

				try
				{
					ErrorMessage = null;

					if ( showLoading || Snapshot is null )
					{
						IsLoading = true;
						await InvokeAsync( StateHasChanged );
					}

					TelemetrySnapshotDto snapshot = await Telemetry.GetSnapshotAsync( Take, _disposed.Token );

					await InvokeAsync( () =>
									   {
										   Snapshot = snapshot;
										   EnsureValidSort();
										   ApplyFilters();
									   } );
				}
				catch ( OperationCanceledException ) when ( _disposed.IsCancellationRequested ) { return; }
				catch ( Exception ex ) { ErrorMessage = $"Failed to load telemetry: {ex.Message.TrimEnd( '.' )}"; }
				finally
				{
					IsLoading = false;
					if ( !_disposed.IsCancellationRequested ) { await InvokeAsync( StateHasChanged ); }
				}
			} while ( Volatile.Read( ref _refreshPending ) == 1 && !_disposed.IsCancellationRequested );
		}
		finally { Interlocked.Exchange( ref _refreshing, 0 ); }
	}

	private Task ToggleLiveUpdatesAsync()
	{
		LiveUpdates         = !LiveUpdates;
		_resumeAfterDetails = false; // the user's choice stands when the dialog closes
		return LiveUpdates
				   ? RefreshAsync()
				   : Task.CompletedTask;
	}

	private void ApplyFilters()
	{
		VisibleLogs    = ApplyLogSort( ( Snapshot?.Logs       ?? [ ] ).Where( MatchesLog ).ToList() );
		VisibleSpans   = ApplySpanSort( ( Snapshot?.Spans     ?? [ ] ).Where( MatchesSpan ).ToList() );
		VisibleMetrics = ApplyMetricSort( ( Snapshot?.Metrics ?? [ ] ).Where( MatchesMetric ).ToList() );
	}

	/// <summary> Opens the record in the details dialog and pauses live updates, so the list (and the record) hold still while it is read. </summary>
	private void SelectRecord( Guid id )
	{
		SelectedRecordId = id;
		if ( !LiveUpdates ) { return; }

		LiveUpdates         = false;
		_resumeAfterDetails = true;
	}

	private Task CloseDetailsAsync()
	{
		SelectedRecordId = null;
		if ( !_resumeAfterDetails ) { return Task.CompletedTask; }

		_resumeAfterDetails = false;
		LiveUpdates         = true;
		return RefreshAsync(); // catch up on what arrived while it was open
	}

	/// <summary> Closes the dialog and pivots the list to everything recorded for the same trace. </summary>
	private Task ShowTraceAsync( string traceId, TelemetryTab tab )
	{
		ActiveTab        = tab;
		SearchText       = traceId;
		SelectedService  = "all";
		SelectedCategory = new FilterKey( FilterField.None );
		CategoryValue    = string.Empty;
		EnsureValidSort();
		ApplyFilters();
		return CloseDetailsAsync();
	}

	/// <summary> From the breakdown: show the logs of one severity ("all" clears it). </summary>
	private Task FilterSeverityAsync( string severity )
	{
		SwitchTab( TelemetryTab.Logs );
		SelectedSeverity = severity;
		return FiltersChangedAsync();
	}

	/// <summary> From the breakdown: one service, on whichever tab is open ("all" clears it). </summary>
	private Task FilterServiceAsync( string service )
	{
		SelectedService = service;
		return FiltersChangedAsync();
	}

	/// <summary> From the breakdown: the points of one metric ("all" clears it). </summary>
	private Task FilterMetricAsync( string metric )
	{
		SwitchTab( TelemetryTab.Metrics );
		SelectedMetricName = metric;
		return FiltersChangedAsync();
	}

	private void SwitchTab( TelemetryTab tab )
	{
		if ( ActiveTab == tab ) { return; }

		ActiveTab        = tab;
		SelectedRecordId = null;
		SelectedCategory = new FilterKey( FilterField.None );
		CategoryValue    = string.Empty;
		EnsureValidSort();
	}

	private Task SetActiveTabAsync( TelemetryTab tab )
	{
		ActiveTab        = tab;
		SelectedRecordId = null;
		SelectedCategory = new FilterKey( FilterField.None );
		CategoryValue    = string.Empty;
		EnsureValidSort();
		return FiltersChangedAsync();
	}

	private Task OnSearchTextChanged( string value )
	{
		SearchText = value;
		return FiltersChangedAsync();
	}
	private Task OnSelectedServiceChanged( string value )
	{
		SelectedService = value;
		return FiltersChangedAsync();
	}
	private Task OnSelectedCategoryTokenChanged( string value )
	{
		SelectedCategoryToken = value;
		return FiltersChangedAsync();
	}
	private Task OnCategoryValueChanged( string value )
	{
		CategoryValue = value;
		return FiltersChangedAsync();
	}
	private Task OnSelectedSortTokenChanged( string value )
	{
		SelectedSortToken = value;
		return FiltersChangedAsync();
	}
	private Task OnSortDescendingChanged( bool value )
	{
		SortDescending = value;
		return FiltersChangedAsync();
	}
	private Task OnSelectedSeverityChanged( string value )
	{
		SelectedSeverity = value;
		return FiltersChangedAsync();
	}
	private Task OnSelectedSpanKindChanged( string value )
	{
		SelectedSpanKind = value;
		return FiltersChangedAsync();
	}
	private Task OnSelectedMetricNameChanged( string value )
	{
		SelectedMetricName = value;
		return FiltersChangedAsync();
	}

	private Task FiltersChangedAsync()
	{
		ApplyFilters();
		return InvokeAsync( StateHasChanged );
	}

	private void EnsureValidSort()
	{
		if ( SortOptions.All( option => option.Token != SelectedSortToken ) )
		{
			SelectedSort = ActiveTab switch
							   {
								   TelemetryTab.Spans => new FilterKey( FilterField.Start ),
								   _                  => new FilterKey( FilterField.Timestamp )
							   };
		}
	}

	private bool MatchesLog( TelemetryLogRecordDto log ) { return MatchesService( log.ServiceName ) && MatchesSearch( log ) && MatchesCategory( log ) && ( SelectedSeverity == "all" || string.Equals( log.SeverityText, SelectedSeverity, StringComparison.OrdinalIgnoreCase ) ); }

	private bool MatchesSpan( TelemetrySpanRecordDto span ) { return MatchesService( span.ServiceName ) && MatchesSearch( span ) && MatchesCategory( span ) && ( SelectedSpanKind == "all" || string.Equals( span.Kind, SelectedSpanKind, StringComparison.OrdinalIgnoreCase ) ); }

	private bool MatchesMetric( TelemetryMetricRecordDto metric ) { return MatchesService( metric.ServiceName ) && MatchesSearch( metric ) && MatchesCategory( metric ) && ( SelectedMetricName == "all" || string.Equals( metric.Name, SelectedMetricName, StringComparison.OrdinalIgnoreCase ) ); }

	private bool MatchesService( string? serviceName ) => SelectedService == "all" || string.Equals( serviceName, SelectedService, StringComparison.OrdinalIgnoreCase );

	private bool MatchesSearch( TelemetryLogRecordDto log ) => string.IsNullOrWhiteSpace( SearchText ) ||
															   SearchContains( _s_searchText.GetValue( log,
																									   static x => x is TelemetryLogRecordDto record
																													   ? JoinSearchFields( record.Body, record.ServiceName, record.TraceId, record.SpanId, record.ScopeName, record.SeverityText, FormatMap( record.Attributes ), FormatMap( record.ResourceAttributes ), FormatMap( record.ScopeAttributes ) )
																													   : string.Empty ) );

	private bool MatchesSearch( TelemetrySpanRecordDto span ) => string.IsNullOrWhiteSpace( SearchText ) ||
																 SearchContains( _s_searchText.GetValue( span,
																										 static x => x is TelemetrySpanRecordDto record
																														 ? JoinSearchFields( record.Name, record.ServiceName, record.TraceId, record.SpanId, record.ParentSpanId, record.Kind, record.StatusCode, record.StatusMessage, FormatMap( record.Attributes ), FormatMap( record.ResourceAttributes ), FormatMap( record.ScopeAttributes ) )
																														 : string.Empty ) );

	private bool MatchesSearch( TelemetryMetricRecordDto metric ) => string.IsNullOrWhiteSpace( SearchText ) ||
																	 SearchContains( _s_searchText.GetValue( metric,
																											 static x => x is TelemetryMetricRecordDto record
																															 ? JoinSearchFields( record.Name, record.Description, record.ServiceName, record.Unit, record.MetricType, record.AggregationTemporality, FormatMetricMap( record ) )
																															 : string.Empty ) );

	/// <summary> The fields joined by line breaks, which a typed query can't contain, so a match never spans two fields. </summary>
	private static string JoinSearchFields( params string?[] fields ) => string.Join( '\n', fields );

	private bool MatchesCategory( TelemetryLogRecordDto log )
	{
		if ( SelectedCategory.IsAny ||
			 string.IsNullOrWhiteSpace( CategoryValue ) ) { return true; }

		return SearchContains( GetLogCategoryValue( log, SelectedCategory ), CategoryValue );
	}

	private bool MatchesCategory( TelemetrySpanRecordDto span )
	{
		if ( SelectedCategory.IsAny ||
			 string.IsNullOrWhiteSpace( CategoryValue ) ) { return true; }

		return SearchContains( GetSpanCategoryValue( span, SelectedCategory ), CategoryValue );
	}

	private bool MatchesCategory( TelemetryMetricRecordDto metric )
	{
		if ( SelectedCategory.IsAny ||
			 string.IsNullOrWhiteSpace( CategoryValue ) ) { return true; }

		return SearchContains( GetMetricCategoryValue( metric, SelectedCategory ), CategoryValue );
	}

	private List<TelemetryLogRecordDto> ApplyLogSort( List<TelemetryLogRecordDto> logs )
	{
		Func<TelemetryLogRecordDto, IComparable?> selector = SelectedSort.Field switch
																 {
																	 FilterField.Service   => log => log.ServiceName,
																	 FilterField.Severity  => log => log.SeverityNumber,
																	 FilterField.Body      => log => log.Body,
																	 FilterField.Timestamp => log => log.TimestampUtc ?? log.ObservedTimestampUtc ?? log.ReceivedAtUtc,
																	 _                     => log => GetLogCategoryValue( log, SelectedSort )
																 };

		return ApplySort( logs, selector );
	}

	private List<TelemetrySpanRecordDto> ApplySpanSort( List<TelemetrySpanRecordDto> spans )
	{
		Func<TelemetrySpanRecordDto, IComparable?> selector = SelectedSort.Field switch
																  {
																	  FilterField.Service  => span => span.ServiceName,
																	  FilterField.Name     => span => span.Name,
																	  FilterField.Kind     => span => span.Kind,
																	  FilterField.Duration => span => span.DurationMilliseconds,
																	  FilterField.Start    => span => span.StartTimeUtc ?? span.ReceivedAtUtc,
																	  _                    => span => GetSpanCategoryValue( span, SelectedSort )
																  };

		return ApplySort( spans, selector );
	}

	private List<TelemetryMetricRecordDto> ApplyMetricSort( List<TelemetryMetricRecordDto> metrics )
	{
		Func<TelemetryMetricRecordDto, IComparable?> selector = SelectedSort.Field switch
																	{
																		FilterField.Service   => metric => metric.ServiceName,
																		FilterField.Name      => metric => metric.Name,
																		FilterField.Value     => metric => metric.NumericValue,
																		FilterField.Type      => metric => metric.MetricType,
																		FilterField.Timestamp => metric => metric.TimestampUtc ?? metric.ReceivedAtUtc,
																		_                     => metric => GetMetricCategoryValue( metric, SelectedSort )
																	};

		return ApplySort( metrics, selector );
	}

	private List<T> ApplySort<T>( List<T> source, Func<T, IComparable?> selector )
	{
		return SortDescending
				   ? source.OrderByDescending( selector ).ToList()
				   : source.OrderBy( selector ).ToList();
	}

	private static IEnumerable<string> GetLogDynamicKeys( TelemetryLogRecordDto log ) => log.ResourceAttributes.Keys.Select( key => $"resource:{key}" ).Concat( log.ScopeAttributes.Keys.Select( key => $"scope:{key}" ) ).Concat( log.Attributes.Keys.Select( key => $"attr:{key}" ) );

	private static IEnumerable<string> GetSpanDynamicKeys( TelemetrySpanRecordDto span ) => span.ResourceAttributes.Keys.Select( key => $"resource:{key}" ).Concat( span.ScopeAttributes.Keys.Select( key => $"scope:{key}" ) ).Concat( span.Attributes.Keys.Select( key => $"attr:{key}" ) );

	private static IEnumerable<string> GetMetricDynamicKeys( TelemetryMetricRecordDto metric ) => metric.ResourceAttributes.Keys.Select( key => $"resource:{key}" ).Concat( metric.ScopeAttributes.Keys.Select( key => $"scope:{key}" ) ).Concat( metric.Attributes.Keys.Select( key => $"attr:{key}" ) ).Concat( metric.MetadataAttributes.Keys.Select( key => $"meta:{key}" ) );

	private static string? GetLogCategoryValue( TelemetryLogRecordDto log, FilterKey key )
	{
		if ( key.IsCustom ) { return GetPrefixedValue( key.CustomKey!, log.ResourceAttributes, log.ScopeAttributes, log.Attributes ); }

		return key.Field switch
				   {
					   FilterField.Timestamp => FormatDate( log.TimestampUtc ?? log.ObservedTimestampUtc ?? log.ReceivedAtUtc ),
					   FilterField.Service   => log.ServiceName,
					   FilterField.Severity  => log.SeverityText,
					   FilterField.Body      => log.Body,
					   _                     => null
				   };
	}

	private static string? GetSpanCategoryValue( TelemetrySpanRecordDto span, FilterKey key )
	{
		if ( key.IsCustom ) { return GetPrefixedValue( key.CustomKey!, span.ResourceAttributes, span.ScopeAttributes, span.Attributes ); }

		return key.Field switch
				   {
					   FilterField.Start    => FormatDate( span.StartTimeUtc ?? span.ReceivedAtUtc ),
					   FilterField.Service  => span.ServiceName,
					   FilterField.Name     => span.Name,
					   FilterField.Kind     => span.Kind,
					   FilterField.Duration => span.DurationMilliseconds.ToString( CultureInfo.InvariantCulture ),
					   _                    => null
				   };
	}

	private static string? GetMetricCategoryValue( TelemetryMetricRecordDto metric, FilterKey key )
	{
		if ( key.IsCustom ) { return GetPrefixedValue( key.CustomKey!, metric.ResourceAttributes, metric.ScopeAttributes, metric.Attributes, metric.MetadataAttributes ); }

		return key.Field switch
				   {
					   FilterField.Timestamp => FormatDate( metric.TimestampUtc ?? metric.ReceivedAtUtc ),
					   FilterField.Service   => metric.ServiceName,
					   FilterField.Name      => metric.Name,
					   FilterField.Value     => metric.NumericValue.ToString( CultureInfo.InvariantCulture ),
					   FilterField.Type      => metric.MetricType,
					   _                     => null
				   };
	}

	private static string? GetPrefixedValue<T>( string key, T resource, T scope, T attributes, T? metadata = null ) where T : class, IReadOnlyDictionary<string, string?>
	{
		if ( key.StartsWith( "resource:", StringComparison.OrdinalIgnoreCase ) &&
			 resource.TryGetValue( key["resource:".Length..], out string? resourceValue ) ) { return resourceValue; }

		if ( key.StartsWith( "scope:", StringComparison.OrdinalIgnoreCase ) &&
			 scope.TryGetValue( key["scope:".Length..], out string? scopeValue ) ) { return scopeValue; }

		if ( key.StartsWith( "attr:", StringComparison.OrdinalIgnoreCase ) &&
			 attributes.TryGetValue( key["attr:".Length..], out string? attributeValue ) ) { return attributeValue; }

		if ( metadata is not null                                          &&
			 key.StartsWith( "meta:", StringComparison.OrdinalIgnoreCase ) &&
			 metadata.TryGetValue( key["meta:".Length..], out string? metadataValue ) ) { return metadataValue; }

		return null;
	}

	private static string ToToken( FilterKey key )
	{
		return key.IsCustom
				   ? $"custom:{key.CustomKey}"
				   : $"enum:{key.Field}";
	}

	private static FilterKey ParseToken( string? token )
	{
		if ( string.IsNullOrWhiteSpace( token ) ) { return new FilterKey( FilterField.None ); }

		if ( token.StartsWith( "custom:", StringComparison.Ordinal ) )
		{
			string customKey = token["custom:".Length..];
			return string.IsNullOrWhiteSpace( customKey )
					   ? new FilterKey( FilterField.None )
					   : new FilterKey( FilterField.None, customKey );
		}

		if ( token.StartsWith( "enum:", StringComparison.Ordinal ) &&
			 Enum.TryParse( token["enum:".Length..], ignoreCase: true, out FilterField field ) ) { return new FilterKey( field ); }

		return new FilterKey( FilterField.None );
	}

	private bool SearchContains( string? value ) => SearchContains( value, SearchText );

	private static bool   SearchContains( string?    value, string query ) { return !string.IsNullOrWhiteSpace( value ) && !string.IsNullOrWhiteSpace( query ) && value.Contains( query, StringComparison.OrdinalIgnoreCase ); }
	private static string FormatCount( long          value ) => value.ToString( "N0", CultureInfo.InvariantCulture );
	private static string FormatDate( DateTimeOffset value ) => value.ToUniversalTime().ToString( "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture ) + "Z";
	private static string FormatMap<T>( T map ) where T : IReadOnlyDictionary<string, string?> => map.Count == 0
																									  ? "none"
																									  : string.Join( ", ", map.Select( pair => $"{pair.Key}={CleanJson( pair.Value )}" ) );

	private static string FormatMetricMap( TelemetryMetricRecordDto metric )
	{
		Dictionary<string, string?> merged = new(StringComparer.OrdinalIgnoreCase);

		foreach ( ( string key, string? value ) in metric.Attributes ) { merged[key] = value; }

		foreach ( ( string key, string? value ) in metric.MetadataAttributes ) { merged[$"meta.{key}"] = value; }

		return FormatMap( merged );
	}

	private static string CleanJson( string? value )
	{
		if ( string.IsNullOrWhiteSpace( value ) ) { return string.Empty; }

		return value.Trim().Trim( '"' ).Replace( "\\\"", "\"", StringComparison.Ordinal ).Replace( "\\n", " ", StringComparison.Ordinal );
	}

	private sealed record ExplorerOptions( TelemetrySnapshotDto?       Snapshot,
										   TelemetryTab                Tab,
										   IReadOnlyList<string>       Services,
										   IReadOnlyList<string>       Severities,
										   IReadOnlyList<string>       SpanKinds,
										   IReadOnlyList<string>       MetricNames,
										   IReadOnlyList<FilterOption> Categories,
										   IReadOnlyList<FilterOption> Sorts );

	private enum FilterField
	{
		None,
		Timestamp,
		Service,
		Severity,
		Body,
		Start,
		Name,
		Kind,
		Duration,
		Value,
		Type
	}

	private readonly record struct FilterKey( FilterField Field,
											  string?     CustomKey = null )
	{
		public bool IsCustom => !string.IsNullOrWhiteSpace( CustomKey );
		public bool IsAny    => Field is FilterField.None && string.IsNullOrWhiteSpace( CustomKey );
	}
}
