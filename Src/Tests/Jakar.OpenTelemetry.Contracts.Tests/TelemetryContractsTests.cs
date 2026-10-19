using System.Collections.ObjectModel;

namespace Jakar.OpenTelemetry.Contracts.Tests;

[TestFixture]
public sealed class TelemetryContractsTests
{
	private static readonly ReadOnlyDictionary<string, string?> _s_noAttributes = new(new Dictionary<string, string?>());


	[Test] public void LogTags_ImagePath_UsesImageRouteAndDashedId()
	{
		Guid id = Guid.Parse( "0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b" );

		Assert.Multiple( () =>
						 {
							 Assert.That( LogTags.ImagePath( id ), Is.EqualTo( "/v1/images/0192a6f1-3c4d-7e5f-8a9b-0c1d2e3f4a5b" ) );
							 Assert.That( LogTags.ATTRIBUTE_KEY,   Is.EqualTo( "log.tags" ) );
						 } );
	}

	[Test] public void SpanEventAndLink_DefaultCounts_AreZero()
	{
		SpanEvent spanEvent = new(null, "event", _s_noAttributes);
		SpanLink  link      = new("trace", "span", string.Empty, _s_noAttributes);

		Assert.Multiple( () =>
						 {
							 Assert.That( spanEvent.DroppedAttributesCount, Is.Zero );
							 Assert.That( link.Flags,                       Is.Zero );
							 Assert.That( link.DroppedAttributesCount,      Is.Zero );
						 } );
	}

	[Test] public void RealtimeEvent_DefaultsToNoImages() => Assert.That( new TelemetryRealtimeEventDto( DateTimeOffset.UnixEpoch, 1, 2, 3, "summary" ).NewImages, Is.Zero );

	[Test] public void Records_HaveValueEquality()
	{
		TelemetryImageDto left  = new(Guid.Empty, "a.png", true, "image/png", 10, DateTimeOffset.UnixEpoch);
		TelemetryImageDto right = new(Guid.Empty, "a.png", true, "image/png", 10, DateTimeOffset.UnixEpoch);

		Assert.Multiple( () =>
						 {
							 Assert.That( left,                                                                  Is.EqualTo( right ) );
							 Assert.That( new ApiMetadataDto( "n", new ApiIngestEndpointsDto( "g", "s", "p" ) ), Is.EqualTo( new ApiMetadataDto( "n", new ApiIngestEndpointsDto( "g", "s", "p" ) ) ) );
							 Assert.That( new NamedValueDto( "a", 1 ),                                           Is.Not.EqualTo( new NamedValueDto( "a", 2 ) ) );
						 } );
	}

	[Test] public void LogRecordDto_OptionalMembers_DefaultToNullOrZero()
	{
		TelemetryLogRecordDto log = new(Guid.Empty,
										DateTimeOffset.UnixEpoch,
										null,
										null,
										null,
										null,
										0,
										null,
										null,
										null,
										null,
										null,
										null,
										0,
										_s_noAttributes,
										_s_noAttributes,
										_s_noAttributes);

		Assert.Multiple( () =>
						 {
							 Assert.That( log.EventName,              Is.Null );
							 Assert.That( log.DroppedAttributesCount, Is.Zero );
							 Assert.That( log.ResourceSchemaUrl,      Is.Null );
							 Assert.That( log.ScopeSchemaUrl,         Is.Null );
						 } );
	}
}
