using System.Security.Cryptography;
using Jakar.OpenTelemetry.Api.Services;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.Extensions.DependencyInjection;
using global::OpenTelemetry.Proto.Logs.V1;

namespace Jakar.OpenTelemetry.Api.IntegrationTests;

[TestFixture]
public sealed class TelemetryQueryServiceTests : ApiFixture
{
	private TelemetryQueryService  Query  => Api.Services.GetRequiredService<TelemetryQueryService>();
	private TelemetryIngestService Ingest => Api.Services.GetRequiredService<TelemetryIngestService>();
	private TelemetryImageStore    Images => Api.Services.GetRequiredService<TelemetryImageStore>();


	[Test]
	public async Task Snapshot_MapsEveryRecordAndBuildsOverview()
	{
		await Ingest.IngestLogsAsync( Otlp.Logs( Otlp.Log( "error", SeverityNumber.Error, "Error" ), Otlp.Log( "info" ), Otlp.Log( "no severity", SeverityNumber.Unspecified, null ) ), CancellationToken.None );
		await Ingest.IngestSpansAsync( Otlp.Traces( Otlp.Span() ), CancellationToken.None );
		await Ingest.IngestMetricsAsync( Otlp.Metrics( Otlp.Gauge(), Otlp.Summary(), Otlp.Sum( "unnamed" ) ), CancellationToken.None );

		TelemetrySnapshotDto snapshot = await Query.GetSnapshotAsync( 100, CancellationToken.None );

		TelemetryLogRecordDto    error  = snapshot.Logs.Single( static x => x.Body == "error" );
		TelemetrySpanRecordDto   span   = snapshot.Spans.Single();
		TelemetryMetricRecordDto gauge  = snapshot.Metrics.Single( static x => x.Name == "test.gauge" );
		TelemetryMetricRecordDto summary = snapshot.Metrics.Single( static x => x.Name == "test.summary" );

		Assert.Multiple( () =>
						 {
							 Assert.That( snapshot.Overview.TotalLogs,    Is.EqualTo( 3 ) );
							 Assert.That( snapshot.Overview.TotalSpans,   Is.EqualTo( 1 ) );
							 Assert.That( snapshot.Overview.TotalMetrics, Is.EqualTo( 3 ) );
							 Assert.That( snapshot.Overview.ServiceBreakdown.Single(), Is.EqualTo( new NamedValueDto( Otlp.SERVICE_NAME, 7 ) ) );
							 Assert.That( snapshot.Overview.SeverityBreakdown.Select( static x => x.Name ), Is.EquivalentTo( new[] { "Error", "Information", "Unspecified" } ) );
							 Assert.That( snapshot.Overview.MetricBreakdown.Select( static x => x.Name ),   Is.EquivalentTo( new[] { "test.gauge", "test.summary", "unnamed" } ) );
							 Assert.That( snapshot.Overview.LogTimeline.Single().BucketUtc, Is.EqualTo( new DateTimeOffset( 2026, 9, 1, 12, 30, 0, TimeSpan.Zero ) ), "10 minute buckets" );
							 Assert.That( snapshot.Overview.LogTimeline.Single().Value,     Is.EqualTo( 3 ) );

							 Assert.That( error.TraceId,              Is.EqualTo( "0102030405060708090a0b0c0d0e0f10" ) );
							 Assert.That( error.SpanId,               Is.EqualTo( "a1a2a3a4a5a6a7a8" ) );
							 Assert.That( error.TimestampUtc,         Is.EqualTo( Otlp.Time ) );
							 Assert.That( error.ResourceAttributes["service.name"], Is.EqualTo( Otlp.SERVICE_NAME ) );
							 Assert.That( error.ScopeAttributes["scope.attr"],      Is.EqualTo( "scope-value" ) );
							 Assert.That( error.EventName,            Is.EqualTo( "test.event" ) );
							 Assert.That( error.Flags,                Is.EqualTo( 1 ) );
							 Assert.That( error.ResourceSchemaUrl,    Is.Not.Null );

							 Assert.That( span.ParentSpanId,          Is.EqualTo( "b1b2b3b4b5b6b7b8" ) );
							 Assert.That( span.EventsJson!.Single().Name, Is.EqualTo( "exception" ) );
							 Assert.That( span.EventsJson!.Single().DroppedAttributesCount, Is.EqualTo( 1 ) );
							 Assert.That( span.LinksJson!.Single().SpanId, Is.EqualTo( "b1b2b3b4b5b6b7b8" ) );
							 Assert.That( span.Attributes["http.status_code"], Is.EqualTo( "500" ) );
							 Assert.That( span.DroppedLinksCount,     Is.EqualTo( 5 ) );
							 Assert.That( span.Flags,                 Is.EqualTo( 0x301 ) );

							 Assert.That( gauge.MetadataAttributes["meta.key"], Is.EqualTo( "meta-value" ) );
							 Assert.That( gauge.ExemplarsJson,        Does.Contain( "filteredAttributes" ) );
							 Assert.That( summary.QuantilesJson![0.5],  Is.EqualTo( 5 ) );
							 Assert.That( summary.QuantilesJson[0.99], Is.EqualTo( double.PositiveInfinity ) );
						 } );
	}

	[Test]
	public async Task Snapshot_TakesNewestAndLimitsBreakdowns()
	{
		for ( int i = 0; i < 10; i++ ) { await Ingest.IngestLogsAsync( Otlp.Logs( $"svc-{i:00}", Otlp.Log( $"log {i}" ) ), CancellationToken.None ); }

		TelemetrySnapshotDto snapshot = await Query.GetSnapshotAsync( 5, CancellationToken.None );

		Assert.Multiple( () =>
						 {
							 Assert.That( snapshot.Logs,                        Has.Count.EqualTo( 5 ) );
							 Assert.That( snapshot.Overview.TotalLogs,          Is.EqualTo( 10 ) );
							 Assert.That( snapshot.Overview.ServiceBreakdown,   Has.Count.EqualTo( 5 ) );
							 Assert.That( snapshot.Overview.ServiceBreakdown.Select( static x => x.Name ), Is.Ordered, "ties are ordered by name" );
						 } );

		TelemetrySnapshotDto all = await Query.GetSnapshotAsync( 50, CancellationToken.None );
		Assert.That( all.Overview.ServiceBreakdown, Has.Count.EqualTo( 8 ), "breakdowns show the top 8" );
	}

	[Test]
	public async Task Snapshot_IsCachedUntilInvalidated()
	{
		await Ingest.IngestLogsAsync( Otlp.Logs( Otlp.Log( "first" ) ), CancellationToken.None );
		TelemetrySnapshotDto first = await Query.GetSnapshotAsync( 30, CancellationToken.None );

		await ExecuteAsync( """TRUNCATE "Logs" """ );
		TelemetrySnapshotDto cached = await Query.GetSnapshotAsync( 30, CancellationToken.None );

		Assert.That( cached, Is.SameAs( first ) );
	}

	[Test]
	public async Task Snapshot_LargeTablesUseThePlannerEstimate()
	{
		if ( !TestDatabase.IsContainer ) { Assert.Ignore( "Faking pg_class statistics needs superuser; only done on the disposable container" ); }

		await Ingest.IngestLogsAsync( Otlp.Logs( Otlp.Log() ), CancellationToken.None );
		await ExecuteAsync( """UPDATE pg_class SET reltuples = 5000000 WHERE oid = '"Logs"'::regclass""" );

		try
		{
			TelemetrySnapshotDto snapshot = await Query.GetSnapshotAsync( 25, CancellationToken.None );

			Assert.That( snapshot.Overview.TotalLogs, Is.EqualTo( 5_000_000 ) );
		}
		finally { await ExecuteAsync( """ANALYZE "Logs" """ ); }
	}

	[Test]
	public async Task ScreenshotLogs_FilterByErrorServiceAndLog_WithUploadState()
	{
		ImageTag uploaded = ImageTag.Create( "uploaded.png" );
		ImageTag pending  = ImageTag.Create( "pending.png" );
		ImageTag info     = ImageTag.Create( "info.png" );

		await Images.SaveAsync( uploaded.ID, uploaded.FileName, "image/png", TestImages.Png, SHA256.HashData( TestImages.Png ), CancellationToken.None );

		LogRecord error = Otlp.Log( "checkout failed", SeverityNumber.Error, "Error", Otlp.Kv( LogTags.ATTRIBUTE_KEY, Otlp.Array( Otlp.Str( uploaded.ToString() ), Otlp.Str( pending.ToString() ) ) ), Otlp.Kv( "exception.type", "System.Exception" ), Otlp.Kv( "exception.message", "boom" ), Otlp.Kv( "exception.stacktrace", "at X" ) );
		LogRecord textOnlyCritical = Otlp.Log( "fatal by text", SeverityNumber.Unspecified, "Critical", Otlp.Kv( LogTags.ATTRIBUTE_KEY, pending.ToString() ) );
		LogRecord infoLog = Otlp.Log( "info", SeverityNumber.Info, "Information", Otlp.Kv( LogTags.ATTRIBUTE_KEY, info.ToString() ) );
		LogRecord bogus   = Otlp.Log( "bogus tag", SeverityNumber.Error, "Error", Otlp.Kv( LogTags.ATTRIBUTE_KEY, "not an image tag" ) );
		LogRecord untagged = Otlp.Log( "untagged", SeverityNumber.Error, "Error" );

		await Ingest.IngestLogsAsync( Otlp.Logs( error, textOnlyCritical, infoLog, bogus, untagged ), CancellationToken.None );
		await Ingest.IngestLogsAsync( Otlp.Logs( "other-service", Otlp.Log( "other", SeverityNumber.Fatal, "Fatal", Otlp.Kv( LogTags.ATTRIBUTE_KEY, ImageTag.Create( "o.png" ).ToString() ) ) ), CancellationToken.None );

		IReadOnlyList<TelemetryScreenshotLogDto> errors  = await Query.GetScreenshotLogsAsync( 50, errorsOnly: true,  null,            null, CancellationToken.None );
		IReadOnlyList<TelemetryScreenshotLogDto> all     = await Query.GetScreenshotLogsAsync( 50, errorsOnly: false, null,            null, CancellationToken.None );
		IReadOnlyList<TelemetryScreenshotLogDto> service = await Query.GetScreenshotLogsAsync( 50, errorsOnly: true,  "other-service", null, CancellationToken.None );
		IReadOnlyList<TelemetryScreenshotLogDto> limited = await Query.GetScreenshotLogsAsync( 1,  errorsOnly: false, null,            null, CancellationToken.None );

		TelemetryScreenshotLogDto checkout = errors.Single( static x => x.Body == "checkout failed" );
		IReadOnlyList<TelemetryScreenshotLogDto> byId = await Query.GetScreenshotLogsAsync( 50, errorsOnly: true, null, all.Single( static x => x.Body == "info" ).LogID, CancellationToken.None );

		Assert.Multiple( () =>
						 {
							 Assert.That( errors.Select( static x => x.Body ),  Is.EquivalentTo( new[] { "checkout failed", "fatal by text", "other" } ), "severity number or text; bogus tags are skipped" );
							 Assert.That( all.Select( static x => x.Body ),     Is.EquivalentTo( new[] { "checkout failed", "fatal by text", "info", "other" } ) );
							 Assert.That( service.Single().Body,                Is.EqualTo( "other" ) );
							 Assert.That( limited,                              Has.Count.EqualTo( 1 ) );
							 Assert.That( byId.Single().Body,                   Is.EqualTo( "info" ), "a specific log is returned whatever its severity" );

							 Assert.That( checkout.ExceptionType,               Is.EqualTo( "System.Exception" ) );
							 Assert.That( checkout.ExceptionMessage,            Is.EqualTo( "boom" ) );
							 Assert.That( checkout.ExceptionStackTrace,         Is.EqualTo( "at X" ) );
							 Assert.That( checkout.TraceId,                     Is.EqualTo( "0102030405060708090a0b0c0d0e0f10" ) );
							 Assert.That( checkout.Images.Select( static x => ( x.FileName, x.IsUploaded ) ), Is.EqualTo( new[] { ( "uploaded.png", true ), ( "pending.png", false ) } ) );
							 Assert.That( checkout.Images[0].ContentType,       Is.EqualTo( "image/png" ) );
							 Assert.That( checkout.Images[0].Length,            Is.EqualTo( TestImages.Png.Length ) );
							 Assert.That( checkout.Images[0].ReceivedAtUtc,     Is.Not.Null );
							 Assert.That( checkout.Images[1].ContentType,       Is.Null );
						 } );
	}

	[Test]
	public async Task ImageStore_SaveIsIdempotentAndDetectsConflicts()
	{
		Guid   id   = Guid.CreateVersion7();
		byte[] hash = SHA256.HashData( TestImages.Png );

		ImageSaveResult created  = await Images.SaveAsync( id, "a.png", "image/png", TestImages.Png, hash, CancellationToken.None );
		ImageSaveResult again    = await Images.SaveAsync( id, "a.png", "image/png", TestImages.Png, hash, CancellationToken.None );
		ImageSaveResult conflict = await Images.SaveAsync( id, "a.png", "image/jpeg", TestImages.Jpeg, SHA256.HashData( TestImages.Jpeg ), CancellationToken.None );

		( string ContentType, byte[] Data )? stored = await Images.GetAsync( id, CancellationToken.None );

		await Assert.MultipleAsync( async () =>
						 {
							 Assert.That( created,                                                     Is.EqualTo( ImageSaveResult.Created ) );
							 Assert.That( again,                                                       Is.EqualTo( ImageSaveResult.AlreadyExists ) );
							 Assert.That( conflict,                                                    Is.EqualTo( ImageSaveResult.Conflict ) );
							 Assert.That( stored!.Value.ContentType,                                   Is.EqualTo( "image/png" ) );
							 Assert.That( stored.Value.Data,                                           Is.EqualTo( TestImages.Png ) );
							 Assert.That( await Images.ExistsAsync( id, CancellationToken.None ),      Is.True );
							 Assert.That( await Images.ExistsAsync( Guid.NewGuid(), CancellationToken.None ), Is.False );
							 Assert.That( await Images.GetAsync( Guid.NewGuid(), CancellationToken.None ),    Is.Null );
							 Assert.That( await Images.GetInfoAsync( [ ], CancellationToken.None ),           Is.Empty );
						 } );
	}
}
