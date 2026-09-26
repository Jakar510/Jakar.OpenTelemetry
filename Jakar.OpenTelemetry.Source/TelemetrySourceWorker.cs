using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jakar.OpenTelemetry.Source;

public sealed class TelemetrySourceWorker( IHttpClientFactory httpClientFactory, ILogger<TelemetrySourceWorker> logger, IOptions<SampleSourceOptions> options ) : BackgroundService
{
    public const string ACTIVITY_SOURCE_NAME = "Jakar.OpenTelemetry.Source.Http";
    public const string CLIENT_NAME          = "telemetry-source";
    public const string METER_NAME           = "Jakar.OpenTelemetry.Source";

    private static readonly ActivitySource    ActivitySource    = new(ACTIVITY_SOURCE_NAME);
    private static readonly Meter             Meter             = new(METER_NAME);
    private static readonly Counter<long>     RequestCounter    = Meter.CreateCounter<long>( "sample_source.requests",         "{request}", "Total outbound sample requests." );
    private static readonly Counter<long>     FailureCounter    = Meter.CreateCounter<long>( "sample_source.request_failures", "{request}", "Failed outbound sample requests." );
    private static readonly Histogram<double> DurationHistogram = Meter.CreateHistogram<double>( "sample_source.request.duration", "ms", "Latency for outbound sample requests." );

    protected override async Task ExecuteAsync( CancellationToken stoppingToken )
    {
        SampleSourceOptions sourceOptions = options.Value;
        TimeSpan            interval      = TimeSpan.FromSeconds( Math.Max( 1, sourceOptions.IntervalSeconds ) );
        using PeriodicTimer timer         = new(interval);

        logger.LogInformation( "Sample source started: OTLP endpoint {OtlpEndpoint}, target URL {TargetUrl}, interval {IntervalSeconds}s", sourceOptions.OtlpEndpoint, sourceOptions.TargetUrl, sourceOptions.IntervalSeconds );
    
        await SendRequestAsync( sourceOptions, stoppingToken );
        while ( await timer.WaitForNextTickAsync( stoppingToken ) ) { await SendRequestAsync( sourceOptions, stoppingToken ); }
    }

    private async Task SendRequestAsync( SampleSourceOptions sourceOptions, CancellationToken token )
    {
        HttpClient client = httpClientFactory.CreateClient( CLIENT_NAME );

        KeyValuePair<string, object?>[] tags =
            [
                new("http.method", "GET"),
                new("server.address", new Uri( sourceOptions.TargetUrl ).Host),
                new("url.full", sourceOptions.TargetUrl)
            ];

        using IDisposable? scope = logger.BeginScope( new Dictionary<string, object?> { ["target.url"] = sourceOptions.TargetUrl, ["source.kind"] = "sample" } );

        using Activity? activity = ActivitySource.StartActivity( "sample.http.get", ActivityKind.Client );
        activity?.SetTag( "sample.target_url", sourceOptions.TargetUrl );
        activity?.SetTag( "sample.source",     "Jakar.OpenTelemetry.Source" );

        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            logger.LogInformation( "Sending GET request to {TargetUrl}", sourceOptions.TargetUrl );

            using HttpResponseMessage response = await client.GetAsync( sourceOptions.TargetUrl, token );
            stopwatch.Stop();

            activity?.SetTag( "http.response.status_code", (int)response.StatusCode );
            activity?.SetStatus( response.IsSuccessStatusCode
                                     ? ActivityStatusCode.Ok
                                     : ActivityStatusCode.Error );

            DurationHistogram.Record( stopwatch.Elapsed.TotalMilliseconds, tags.Append( new KeyValuePair<string, object?>( "http.response.status_code", (int)response.StatusCode ) ).ToArray() );

            RequestCounter.Add( 1, tags );

            logger.LogInformation( "Received {StatusCode} from {TargetUrl} in {ElapsedMilliseconds} ms", (int)response.StatusCode, sourceOptions.TargetUrl, stopwatch.Elapsed.TotalMilliseconds );
        }
        catch ( OperationCanceledException ) when ( token.IsCancellationRequested ) { logger.LogInformation( "Sample source stopping" ); }
        catch ( Exception e )
        {
            stopwatch.Stop();
            activity?.SetStatus( ActivityStatusCode.Error, e.Message );

            DurationHistogram.Record( stopwatch.Elapsed.TotalMilliseconds, tags );
            RequestCounter.Add( 1, tags );
            FailureCounter.Add( 1, tags );

            logger.LogError( e, "GET request to {TargetUrl} failed after {ElapsedMilliseconds} ms", sourceOptions.TargetUrl, stopwatch.Elapsed.TotalMilliseconds );
        }
    }
}
