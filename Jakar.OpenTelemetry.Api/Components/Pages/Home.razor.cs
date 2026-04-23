using System.Globalization;
using Jakar.OpenTelemetry.Api.Components.Dashboard;
using Jakar.OpenTelemetry.Api.Services;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace Jakar.OpenTelemetry.Api.Components.Pages;

public sealed partial class Home : ComponentBase, IDisposable
{
    [Inject] public required TelemetryHubClient             PortalClient          { get; init; }
    private                  TelemetrySnapshotDto?          Snapshot              { get; set; }
    private                  TelemetryTab                   ActiveTab             { get; set; } = TelemetryTab.Logs;
    private                  bool                           IsLoading             { get; set; } = true;
    private                  string?                        ErrorMessage          { get; set; }
    private                  string                         SearchText            { get; set; } = string.Empty;
    private                  string                         SelectedService       { get; set; } = "all";
    private                  FilterKey                      SelectedCategory      { get; set; } = new( FilterField.None );
    private                  string                         CategoryValue         { get; set; } = string.Empty;
    private                  FilterKey                      SelectedSort          { get; set; } = new( FilterField.Timestamp );
    private                  bool                           SortDescending        { get; set; } = true;
    private                  string                         SelectedSeverity      { get; set; } = "all";
    private                  string?                        Status                { get; set; }
    private                  string                         SelectedSpanKind      { get; set; } = "all";
    private                  string                         SelectedMetricName    { get; set; } = "all";
    private                  TelemetryRealtimeEventDto?     LastEvent             { get; set; }
    private                  string                         ApiBaseUrl            => PortalClient.ApiBaseUrl;
    private                  string                         SelectedCategoryToken { get => ToToken( SelectedCategory ); set => SelectedCategory = ParseToken( value ); }
    private                  string                         SelectedSortToken     { get => ToToken( SelectedSort );     set => SelectedSort = ParseToken( value ); }
    private                  List<TelemetryLogRecordDto>    VisibleLogs           => ApplyLogSort( ( Snapshot?.Logs       ?? [ ] ).Where( MatchesLog ).ToList() );
    private                  List<TelemetrySpanRecordDto>   VisibleSpans          => ApplySpanSort( ( Snapshot?.Spans     ?? [ ] ).Where( MatchesSpan ).ToList() );
    private                  List<TelemetryMetricRecordDto> VisibleMetrics        => ApplyMetricSort( ( Snapshot?.Metrics ?? [ ] ).Where( MatchesMetric ).ToList() );

    private IReadOnlyList<string> ServiceOptions => Snapshot?.Logs.Select( x => x.ServiceName )
                                                             .Concat( Snapshot.Spans.Select( x => x.ServiceName ) )
                                                             .Concat( Snapshot.Metrics.Select( x => x.ServiceName ) )
                                                             .Where( x => !string.IsNullOrWhiteSpace( x ) )
                                                             .Distinct( StringComparer.OrdinalIgnoreCase )
                                                             .OrderBy( x => x )
                                                             .Cast<string>()
                                                             .ToArray() ??
                                                        [ ];

    private IReadOnlyList<string> SeverityOptions =>
        Snapshot?.Logs.Select( static x => x.SeverityText ).Where( static x => !string.IsNullOrWhiteSpace( x ) ).Distinct( StringComparer.OrdinalIgnoreCase ).OrderBy( static x => x ).Cast<string>().ToArray() ?? [ ];

    private IReadOnlyList<string> SpanKindOptions =>
        Snapshot?.Spans.Select( static x => x.Kind ).Where( static x => !string.IsNullOrWhiteSpace( x ) ).Distinct( StringComparer.OrdinalIgnoreCase ).OrderBy( static x => x ).Cast<string>().ToArray() ?? [ ];

    private IReadOnlyList<string> MetricNameOptions =>
        Snapshot?.Metrics.Select( static x => x.Name ).Where( static x => !string.IsNullOrWhiteSpace( x ) ).Distinct( StringComparer.OrdinalIgnoreCase ).OrderBy( static x => x ).Cast<string>().ToArray() ?? [ ];

    private IReadOnlyList<FilterOption> CategoryOptions
    {
        get
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
                .. keys.Distinct( StringComparer.OrdinalIgnoreCase ).OrderBy( static x => x ).Select( key => new FilterOption( ToToken( new FilterKey( FilterField.None, key ) ), key ) )
            ];
        }
    }

    private IReadOnlyList<FilterOption> SortOptions => ActiveTab switch
                                                           {
                                                               TelemetryTab.Logs =>
                                                               [
                                                                   new FilterOption( ToToken( new FilterKey( FilterField.Timestamp ) ), "Timestamp" ),
                                                                   new FilterOption( ToToken( new FilterKey( FilterField.Service ) ), "Service" ),
                                                                   new FilterOption( ToToken( new FilterKey( FilterField.Severity ) ), "Severity" ),
                                                                   new FilterOption( ToToken( new FilterKey( FilterField.Body ) ), "Body" ),
                                                                   .. CategoryOptions.Where( static option => option.Token.StartsWith( "custom:", StringComparison.Ordinal ) )
                                                               ],
                                                               TelemetryTab.Spans =>
                                                               [
                                                                   new FilterOption( ToToken( new FilterKey( FilterField.Start ) ), "Start time" ),
                                                                   new FilterOption( ToToken( new FilterKey( FilterField.Service ) ), "Service" ),
                                                                   new FilterOption( ToToken( new FilterKey( FilterField.Name ) ), "Span name" ),
                                                                   new FilterOption( ToToken( new FilterKey( FilterField.Kind ) ), "Kind" ),
                                                                   new FilterOption( ToToken( new FilterKey( FilterField.Duration ) ), "Duration" ),
                                                                   .. CategoryOptions.Where( static option => option.Token.StartsWith( "custom:", StringComparison.Ordinal ) )
                                                               ],
                                                               TelemetryTab.Metrics =>
                                                               [
                                                                   new FilterOption( ToToken( new FilterKey( FilterField.Timestamp ) ), "Timestamp" ),
                                                                   new FilterOption( ToToken( new FilterKey( FilterField.Service ) ), "Service" ),
                                                                   new FilterOption( ToToken( new FilterKey( FilterField.Name ) ), "Metric name" ),
                                                                   new FilterOption( ToToken( new FilterKey( FilterField.Value ) ), "Value" ),
                                                                   new FilterOption( ToToken( new FilterKey( FilterField.Type ) ), "Metric type" ),
                                                                   .. CategoryOptions.Where( static option => option.Token.StartsWith( "custom:", StringComparison.Ordinal ) )
                                                               ],
                                                               _ => [ ]
                                                           };

    private string ConnectionStateClass => PortalClient.ConnectionState switch
                                               {
                                                   HubConnectionState.Connected                                     => "online",
                                                   HubConnectionState.Connecting or HubConnectionState.Reconnecting => "warming",
                                                   _                                                                => "offline"
                                               };

    private string ConnectionStateLabel => $"SignalR {PortalClient.ConnectionState}";
    private string LastEventLabel       => LastEvent is null ? "Waiting for live OTLP traffic..." : $"{LastEvent.TimestampUtc:HH:mm:ss}Z | {LastEvent.Summary}";
    private string SnapshotAgeLabel     => Snapshot is null ? "No data" : $"{Math.Max( 0, ( DateTimeOffset.UtcNow - Snapshot.Overview.GeneratedAtUtc ).TotalSeconds ):0}s";
    private string GeneratedAtLabel     => Snapshot is null ? string.Empty : $"Generated {Snapshot.Overview.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}Z";

    public void Dispose()
    {
        PortalClient.TelemetryUpdated       -= OnTelemetryUpdatedAsync;
        PortalClient.ConnectionStateChanged -= OnConnectionChanged;
    }

    protected override async Task OnInitializedAsync()
    {
        PortalClient.TelemetryUpdated       += OnTelemetryUpdatedAsync;
        PortalClient.ConnectionStateChanged += OnConnectionChanged;

        await PortalClient.EnsureConnectedAsync();
        await RefreshAsync();
    }

    private void OnConnectionChanged( string status )
    {
        Status = status;
        _      = InvokeAsync( StateHasChanged );
    }

    private async Task OnTelemetryUpdatedAsync( TelemetryRealtimeEventDto update )
    {
        LastEvent = update;
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            IsLoading    = true;
            ErrorMessage = null;
            Snapshot     = await PortalClient.GetSnapshotAsync();
            EnsureValidSort();
        }
        catch ( Exception ex ) { ErrorMessage = ex.Message; }
        finally
        {
            IsLoading = false;
            await InvokeAsync( StateHasChanged );
        }
    }

    private Task SetActiveTabAsync( TelemetryTab tab )
    {
        ActiveTab        = tab;
        SelectedCategory = new FilterKey( FilterField.None );
        CategoryValue    = string.Empty;
        EnsureValidSort();
        return InvokeAsync( StateHasChanged );
    }

    private Task OnSearchTextChanged( string value ) { SearchText = value; return NotifyStateChangedAsync(); }
    private Task OnSelectedServiceChanged( string value ) { SelectedService = value; return NotifyStateChangedAsync(); }
    private Task OnSelectedCategoryTokenChanged( string value ) { SelectedCategoryToken = value; return NotifyStateChangedAsync(); }
    private Task OnCategoryValueChanged( string value ) { CategoryValue = value; return NotifyStateChangedAsync(); }
    private Task OnSelectedSortTokenChanged( string value ) { SelectedSortToken = value; return NotifyStateChangedAsync(); }
    private Task OnSortDescendingChanged( bool value ) { SortDescending = value; return NotifyStateChangedAsync(); }
    private Task OnSelectedSeverityChanged( string value ) { SelectedSeverity = value; return NotifyStateChangedAsync(); }
    private Task OnSelectedSpanKindChanged( string value ) { SelectedSpanKind = value; return NotifyStateChangedAsync(); }
    private Task OnSelectedMetricNameChanged( string value ) { SelectedMetricName = value; return NotifyStateChangedAsync(); }

    private Task NotifyStateChangedAsync() => InvokeAsync( StateHasChanged );

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

    private bool MatchesLog( TelemetryLogRecordDto log )
    {
        return MatchesService( log.ServiceName ) && MatchesSearch( log ) && MatchesCategory( log ) && ( SelectedSeverity == "all" || string.Equals( log.SeverityText, SelectedSeverity, StringComparison.OrdinalIgnoreCase ) );
    }

    private bool MatchesSpan( TelemetrySpanRecordDto span )
    {
        return MatchesService( span.ServiceName ) && MatchesSearch( span ) && MatchesCategory( span ) && ( SelectedSpanKind == "all" || string.Equals( span.Kind, SelectedSpanKind, StringComparison.OrdinalIgnoreCase ) );
    }

    private bool MatchesMetric( TelemetryMetricRecordDto metric )
    {
        return MatchesService( metric.ServiceName ) && MatchesSearch( metric ) && MatchesCategory( metric ) && ( SelectedMetricName == "all" || string.Equals( metric.Name, SelectedMetricName, StringComparison.OrdinalIgnoreCase ) );
    }

    private bool MatchesService( string? serviceName ) => SelectedService == "all" || string.Equals( serviceName, SelectedService, StringComparison.OrdinalIgnoreCase );

    private bool MatchesSearch( TelemetryLogRecordDto log )
    {
        if ( string.IsNullOrWhiteSpace( SearchText ) ) { return true; }

        return SearchContains( log.Body ) ||
               SearchContains( log.ServiceName ) ||
               SearchContains( log.TraceId ) ||
               SearchContains( log.SpanId ) ||
               SearchContains( log.ScopeName ) ||
               SearchContains( log.SeverityText ) ||
               SearchContains( FormatMap( log.Attributes ) ) ||
               SearchContains( FormatMap( log.ResourceAttributes ) ) ||
               SearchContains( FormatMap( log.ScopeAttributes ) );
    }

    private bool MatchesSearch( TelemetrySpanRecordDto span )
    {
        if ( string.IsNullOrWhiteSpace( SearchText ) ) { return true; }

        return SearchContains( span.Name ) ||
               SearchContains( span.ServiceName ) ||
               SearchContains( span.TraceId ) ||
               SearchContains( span.SpanId ) ||
               SearchContains( span.ParentSpanId ) ||
               SearchContains( span.Kind ) ||
               SearchContains( span.StatusCode ) ||
               SearchContains( span.StatusMessage ) ||
               SearchContains( FormatMap( span.Attributes ) ) ||
               SearchContains( FormatMap( span.ResourceAttributes ) ) ||
               SearchContains( FormatMap( span.ScopeAttributes ) );
    }

    private bool MatchesSearch( TelemetryMetricRecordDto metric )
    {
        if ( string.IsNullOrWhiteSpace( SearchText ) ) { return true; }

        return SearchContains( metric.Name ) ||
               SearchContains( metric.Description ) ||
               SearchContains( metric.ServiceName ) ||
               SearchContains( metric.Unit ) ||
               SearchContains( metric.MetricType ) ||
               SearchContains( metric.AggregationTemporality ) ||
               SearchContains( FormatMetricMap( metric ) );
    }

    private bool MatchesCategory( TelemetryLogRecordDto log )
    {
        if ( SelectedCategory.IsAny || string.IsNullOrWhiteSpace( CategoryValue ) ) { return true; }

        return SearchContains( GetLogCategoryValue( log, SelectedCategory ), CategoryValue );
    }

    private bool MatchesCategory( TelemetrySpanRecordDto span )
    {
        if ( SelectedCategory.IsAny || string.IsNullOrWhiteSpace( CategoryValue ) ) { return true; }

        return SearchContains( GetSpanCategoryValue( span, SelectedCategory ), CategoryValue );
    }

    private bool MatchesCategory( TelemetryMetricRecordDto metric )
    {
        if ( SelectedCategory.IsAny || string.IsNullOrWhiteSpace( CategoryValue ) ) { return true; }

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

    private List<T> ApplySort<T>( List<T> source, Func<T, IComparable?> selector ) { return SortDescending ? source.OrderByDescending( selector ).ToList() : source.OrderBy( selector ).ToList(); }

    private static IEnumerable<string> GetLogDynamicKeys( TelemetryLogRecordDto log ) =>
        log.ResourceAttributes.Keys.Select( key => $"resource:{key}" ).Concat( log.ScopeAttributes.Keys.Select( key => $"scope:{key}" ) ).Concat( log.Attributes.Keys.Select( key => $"attr:{key}" ) );

    private static IEnumerable<string> GetSpanDynamicKeys( TelemetrySpanRecordDto span ) =>
        span.ResourceAttributes.Keys.Select( key => $"resource:{key}" ).Concat( span.ScopeAttributes.Keys.Select( key => $"scope:{key}" ) ).Concat( span.Attributes.Keys.Select( key => $"attr:{key}" ) );

    private static IEnumerable<string> GetMetricDynamicKeys( TelemetryMetricRecordDto metric ) => metric.ResourceAttributes.Keys.Select( key => $"resource:{key}" )
                                                                                                        .Concat( metric.ScopeAttributes.Keys.Select( key => $"scope:{key}" ) )
                                                                                                        .Concat( metric.Attributes.Keys.Select( key => $"attr:{key}" ) )
                                                                                                        .Concat( metric.MetadataAttributes.Keys.Select( key => $"meta:{key}" ) );

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

        if ( metadata is not null &&
             key.StartsWith( "meta:", StringComparison.OrdinalIgnoreCase ) &&
             metadata.TryGetValue( key["meta:".Length..], out string? metadataValue ) ) { return metadataValue; }

        return null;
    }

    private static string ToToken( FilterKey key ) { return key.IsCustom ? $"custom:{key.CustomKey}" : $"enum:{key.Field}"; }

    private static FilterKey ParseToken( string? token )
    {
        if ( string.IsNullOrWhiteSpace( token ) ) { return new FilterKey( FilterField.None ); }

        if ( token.StartsWith( "custom:", StringComparison.Ordinal ) )
        {
            string customKey = token["custom:".Length..];
            return string.IsNullOrWhiteSpace( customKey ) ? new FilterKey( FilterField.None ) : new FilterKey( FilterField.None, customKey );
        }

        if ( token.StartsWith( "enum:", StringComparison.Ordinal ) &&
             Enum.TryParse( token["enum:".Length..], ignoreCase: true, out FilterField field ) ) { return new FilterKey( field ); }

        return new FilterKey( FilterField.None );
    }

    private bool SearchContains( string? value ) => SearchContains( value, SearchText );

    private static bool SearchContains( string? value, string query ) { return !string.IsNullOrWhiteSpace( value ) && !string.IsNullOrWhiteSpace( query ) && value.Contains( query, StringComparison.OrdinalIgnoreCase ); }
    private static string FormatCount( int value ) => value.ToString( "N0", CultureInfo.InvariantCulture );
    private static string FormatDate( DateTimeOffset value ) => value.ToUniversalTime().ToString( "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture ) + "Z";
    private static string FormatMap<T>( T map ) where T : IReadOnlyDictionary<string, string?> => map.Count == 0 ? "none" : string.Join( ", ", map.Select( pair => $"{pair.Key}={CleanJson( pair.Value )}" ) );

    private static string FormatMetricMap( TelemetryMetricRecordDto metric )
    {
        Dictionary<string, string?> merged = new( StringComparer.OrdinalIgnoreCase );

        foreach ( ( string key, string? value ) in metric.Attributes ) { merged[key] = value; }

        foreach ( ( string key, string? value ) in metric.MetadataAttributes ) { merged[$"meta.{key}"] = value; }

        return FormatMap( merged );
    }

    private static string Trim( string? value, int maxLength )
    {
        if ( string.IsNullOrWhiteSpace( value ) ) { return "-"; }

        string cleaned = CleanJson( value );
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength] + "...";
    }

    private static string CleanJson( string? value )
    {
        if ( string.IsNullOrWhiteSpace( value ) ) { return string.Empty; }

        return value.Trim().Trim( '"' ).Replace( "\\\"", "\"", StringComparison.Ordinal ).Replace( "\\n", " ", StringComparison.Ordinal );
    }

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

    private readonly record struct FilterKey( FilterField Field, string? CustomKey = null )
    {
        public bool IsCustom => !string.IsNullOrWhiteSpace( CustomKey );
        public bool IsAny    => Field is FilterField.None && string.IsNullOrWhiteSpace( CustomKey );

        public bool Equals( FilterKey other ) => Field == other.Field && string.Equals( CustomKey, other.CustomKey, StringComparison.InvariantCulture );

        public override int GetHashCode()
        {
            HashCode hashCode = new();
            hashCode.Add( (int)Field );
            hashCode.Add( CustomKey, StringComparer.InvariantCulture );
            return hashCode.ToHashCode();
        }
    }

}
