using System.Collections;
using Jakar.OpenTelemetry.Api.Client.Logging;
using Jakar.OpenTelemetry.Contracts;

namespace Jakar.OpenTelemetry.Api.Client.Tests;

[TestFixture]
public sealed class JakarLoggerExtensionsTests
{
	private static readonly ImageTag _s_first  = new(Guid.Parse( "0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b" ), "a.png");
	private static readonly ImageTag _s_second = new(Guid.Parse( "0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5c" ), "b.png");

	private FakeLogger _logger = null!;


	[SetUp] public void SetUp() => _logger = new FakeLogger();


	private static IReadOnlyList<KeyValuePair<string, object?>> TagScope( FakeLogRecord record ) => record.Scopes.OfType<IReadOnlyList<KeyValuePair<string, object?>>>().Single( static x => x.Any( static p => p.Key == LogTags.ATTRIBUTE_KEY ) );


	[Test] public void BeginImageScope_NoImages_ReturnsNull() => Assert.That( _logger.BeginImageScope(), Is.Null );

	[Test] public void BeginImageScope_ExposesDistinctTagsAsStringArray()
	{
		using ( _logger.BeginImageScope( _s_first, _s_second, _s_first ) ) { _logger.LogInformation( "inside" ); }

		_logger.LogInformation( "outside" );

		IReadOnlyList<FakeLogRecord>                 records = _logger.Collector.GetSnapshot();
		IReadOnlyList<KeyValuePair<string, object?>> scope   = TagScope( records[0] );

		Assert.Multiple( () =>
						 {
							 Assert.That( scope.Count,  Is.EqualTo( 1 ) );
							 Assert.That( scope[0].Key, Is.EqualTo( LogTags.ATTRIBUTE_KEY ) );
							 Assert.That( scope[0].Value,
										  Is.EqualTo( new[]
														  {
															  _s_first.ToString(),
															  _s_second.ToString()
														  } ) );
							 Assert.That( scope.ToString(),                              Is.EqualTo( $"log.tags={_s_first},{_s_second}" ) );
							 Assert.That( scope.ToArray(),                               Has.Length.EqualTo( 1 ) );
							 Assert.That( ( (IEnumerable)scope ).Cast<object>().Count(), Is.EqualTo( 1 ) );
							 Assert.That( () => scope[1],                                Throws.TypeOf<ArgumentOutOfRangeException>() );
							 Assert.That( records[1].Scopes,                             Is.Empty );
						 } );
	}

	[Test] public void LogWithImages_LogsAtLevelWithTags()
	{
		InvalidOperationException exception = new("x");

		_logger.LogWithImages( LogLevel.Warning, exception, [ _s_first ], "Value {Value}", 42 );

		FakeLogRecord record = _logger.LatestRecord;

		Assert.Multiple( () =>
						 {
							 Assert.That( record.Level,     Is.EqualTo( LogLevel.Warning ) );
							 Assert.That( record.Exception, Is.SameAs( exception ) );
							 Assert.That( record.Message,   Is.EqualTo( "Value 42" ) );
							 Assert.That( TagScope( record )[0].Value,
										  Is.EqualTo( new[]
														  {
															  _s_first.ToString()
														  } ) );
						 } );
	}

	[Test] public void LogErrorWithImages_LogsError()
	{
		_logger.LogErrorWithImages( null, [ _s_first ], "failed" );

		Assert.That( _logger.LatestRecord.Level, Is.EqualTo( LogLevel.Error ) );
	}

	[Test] public void LogWithImages_NoImages_LogsWithoutScope()
	{
		_logger.LogWithImages( LogLevel.Information, null, [ ], "plain" );

		Assert.That( _logger.LatestRecord.Scopes, Is.Empty );
	}

	[Test] public async Task LogErrorWithScreenshot_AttachesCapturedScreenshot()
	{
		Mock<ITelemetryClient> telemetry = new();
		telemetry.Setup( static x => x.CaptureScreenshotAsync( null, It.IsAny<CancellationToken>() ) ).ReturnsAsync( _s_first );

		ImageTag? tag = await _logger.LogErrorWithScreenshotAsync( telemetry.Object, new Exception( "boom" ), "Checkout failed for {OrderId}", 7 );

		Assert.Multiple( () =>
						 {
							 Assert.That( tag,                          Is.EqualTo( _s_first ) );
							 Assert.That( _logger.LatestRecord.Level,   Is.EqualTo( LogLevel.Error ) );
							 Assert.That( _logger.LatestRecord.Message, Is.EqualTo( "Checkout failed for 7" ) );
							 Assert.That( TagScope( _logger.LatestRecord )[0].Value,
										  Is.EqualTo( new[]
														  {
															  _s_first.ToString()
														  } ) );
						 } );
	}

	[Test] public async Task LogErrorWithScreenshot_NoScreenshot_StillLogs()
	{
		Mock<ITelemetryClient> telemetry = new();
		telemetry.Setup( static x => x.CaptureScreenshotAsync( null, It.IsAny<CancellationToken>() ) ).ReturnsAsync( (ImageTag?)null );

		Assert.That( await _logger.LogErrorWithScreenshotAsync( telemetry.Object, null, "failed" ), Is.Null );
		Assert.That( _logger.LatestRecord.Scopes,                                                   Is.Empty );
	}

	[Test] public async Task LogErrorWithScreenshot_CaptureThrows_LogsDebugAndError()
	{
		Mock<ITelemetryClient> telemetry = new();
		telemetry.Setup( static x => x.CaptureScreenshotAsync( null, It.IsAny<CancellationToken>() ) ).ThrowsAsync( new InvalidOperationException( "no window" ) );

		Assert.That( await _logger.LogErrorWithScreenshotAsync( telemetry.Object, null, "failed" ), Is.Null );
		Assert.That( _logger.Collector.GetSnapshot().Select( static x => x.Level ),
					 Is.EqualTo( new[]
									 {
										 LogLevel.Debug,
										 LogLevel.Error
									 } ) );
	}
}
