using System.Collections.ObjectModel;
using Jakar.OpenTelemetry.Api.Components.Dashboard;
using Jakar.OpenTelemetry.Api.Services;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.AspNetCore.Components.Web;

namespace Jakar.OpenTelemetry.Api.Tests;

[TestFixture]
public sealed class DashboardFormatterTests
{
	private static ReadOnlyDictionary<string, string?> Map( params (string Key, string? Value)[] pairs ) => new(pairs.ToDictionary( static x => x.Key, static x => x.Value ));

	internal static TelemetryMetricRecordDto Metric( double value = 1.5, long? intValue = null, ReadOnlyDictionary<string, string?>? attributes = null, ReadOnlyDictionary<string, string?>? metadata = null ) => new(Guid.NewGuid(),
																																																					  DateTimeOffset.UnixEpoch,
																																																					  null,
																																																					  null,
																																																					  "svc",
																																																					  "m",
																																																					  null,
																																																					  null,
																																																					  "Gauge",
																																																					  null,
																																																					  null,
																																																					  value,
																																																					  null,
																																																					  null,
																																																					  null,
																																																					  null,
																																																					  null,
																																																					  null,
																																																					  TelemetryJson.Empty,
																																																					  TelemetryJson.Empty,
																																																					  attributes ?? TelemetryJson.Empty,
																																																					  metadata   ?? TelemetryJson.Empty,
																																																					  null,
																																																					  null,
																																																					  intValue);


	[Test] public void FormatCountAndDates_AreInvariant()
	{
		DateTimeOffset value = new DateTimeOffset( 2026, 9, 26, 10, 11, 12, 345, TimeSpan.FromHours( 2 ) ).AddTicks( 6789 );

		Assert.Multiple( () =>
						 {
							 Assert.That( TelemetryDashboardFormatter.FormatCount( 1234567 ),     Is.EqualTo( "1,234,567" ) );
							 Assert.That( TelemetryDashboardFormatter.FormatDate( value ),        Is.EqualTo( "2026-09-26 08:11:12Z" ) );
							 Assert.That( TelemetryDashboardFormatter.FormatDatePrecise( value ), Is.EqualTo( "2026-09-26 08:11:12.345678Z" ) );
							 Assert.That( TelemetryDashboardFormatter.FormatDatePrecise( null ),  Is.EqualTo( "Z" ) );
						 } );
	}

	[Test] public void FormatMap_JoinsCleanedValues()
	{
		Assert.Multiple( () =>
						 {
							 Assert.That( TelemetryDashboardFormatter.FormatMap( TelemetryJson.Empty ),                                       Is.EqualTo( "none" ) );
							 Assert.That( TelemetryDashboardFormatter.FormatMap( Map( ( "a", "\"quoted\"" ), ( "b", null ), ( "c", "x" ) ) ), Is.EqualTo( "a=quoted, b=, c=x" ) );
						 } );
	}

	[TestCase( 120_000, "2 min" )] [TestCase( 90_500, "1.51 min" )] [TestCase( 1_500, "1.5 s" )] [TestCase( 1_000, "1 s" )] [TestCase( 42.1234, "42.123 ms" )] [TestCase( 1, "1 ms" )] [TestCase( 0.25, "250 µs" )] [TestCase( 0, "0 µs" )]
	public void FormatDuration_PicksUnit( double milliseconds, string expected ) => Assert.That( TelemetryDashboardFormatter.FormatDuration( milliseconds ), Is.EqualTo( expected ) );

	[Test] public void FormatMetricValue_PrefersExactInteger()
	{
		Assert.Multiple( () =>
						 {
							 Assert.That( TelemetryDashboardFormatter.FormatMetricValue( Metric( 9.007199254740992E15, 9_007_199_254_740_993 ) ), Is.EqualTo( "9,007,199,254,740,993" ) );
							 Assert.That( TelemetryDashboardFormatter.FormatMetricValue( Metric( 1.1234567 ) ),                                   Is.EqualTo( "1.123457" ) );
						 } );
	}

	[Test] public void FormatMetricMap_PrefixesMetadata() => Assert.That( TelemetryDashboardFormatter.FormatMetricMap( Metric( attributes: Map( ( "k", "v" ) ), metadata: Map( ( "m", "x" ) ) ) ), Is.EqualTo( "k=v, meta.m=x" ) );

	[TestCase( null, 10, "-" )] [TestCase( "  ", 10, "-" )] [TestCase( "short", 10, "short" )] [TestCase( "exactly10!", 10, "exactly10!" )] [TestCase( "much longer text", 4, "much..." )] [TestCase( "\"json\\\"text\\n\"", 20, "json\"text " )]
	public void Trim_CleansAndTruncates( string? value, int max, string expected ) => Assert.That( TelemetryDashboardFormatter.Trim( value, max ), Is.EqualTo( expected ) );

	[TestCase( null, "" )] [TestCase( "", "" )] [TestCase( "{}", "{}" )] [TestCase( "not json", "not json" )]
	public void PrettyJson_NonObjectsAndInvalid( string? json, string expected ) => Assert.That( TelemetryDashboardFormatter.PrettyJson( json ), Is.EqualTo( expected ) );

	[Test] public void PrettyJson_IndentsWithoutEscaping() => Assert.That( TelemetryDashboardFormatter.PrettyJson( """{"a":[1,"<é>"]}""" ).ReplaceLineEndings( "\n" ), Is.EqualTo( "{\n  \"a\": [\n    1,\n    \"<é>\"\n  ]\n}" ) );

	[Test] public void GetImageTags_ReadsLogTagsAttribute()
	{
		Guid                  id       = Guid.CreateVersion7();
		TelemetryLogRecordDto tagged   = Log( Map( ( LogTags.ATTRIBUTE_KEY, $"""["image:a.png:{id}"]""" ) ) );
		TelemetryLogRecordDto untagged = Log( TelemetryJson.Empty );

		Assert.Multiple( () =>
						 {
							 Assert.That( TelemetryDashboardFormatter.GetImageTags( tagged ),
										  Is.EqualTo( new[]
														  {
															  new ImageTag( id, "a.png" )
														  } ) );
							 Assert.That( TelemetryDashboardFormatter.GetImageTags( untagged ), Is.Empty );
						 } );

		static TelemetryLogRecordDto Log( ReadOnlyDictionary<string, string?> attributes ) => new(Guid.NewGuid(),
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
																								  TelemetryJson.Empty,
																								  TelemetryJson.Empty,
																								  attributes);
	}

	[TestCase( 0, "" )] [TestCase( 1, "sev-debug" )] [TestCase( 8, "sev-debug" )] [TestCase( 9, "sev-info" )] [TestCase( 13, "sev-warn" )] [TestCase( 17, "sev-error" )] [TestCase( 21, "sev-fatal" )] [TestCase( 24, "sev-fatal" )]
	public void SeverityClass( int severity, string expected ) => Assert.That( TelemetryDashboardFormatter.SeverityClass( severity ), Is.EqualTo( expected ) );

	[TestCase( "Error", "sev-error" )] [TestCase( "Ok", "sev-ok" )] [TestCase( "Unset", "" )] [TestCase( null, "" )]
	public void StatusClass( string? status, string expected ) => Assert.That( TelemetryDashboardFormatter.StatusClass( status ), Is.EqualTo( expected ) );

	[TestCase( 0U, "0" )] [TestCase( 0x01U, "0x1, sampled" )] [TestCase( 0x100U, "0x100, local parent" )] [TestCase( 0x301U, "0x301, sampled, remote parent" )] [TestCase( 0x02U, "0x2" )]
	public void FormatSpanFlags( uint flags, string expected ) => Assert.That( TelemetryDashboardFormatter.FormatSpanFlags( flags ), Is.EqualTo( expected ) );

	[Test] public void RowClassAndActivationKeys()
	{
		Assert.Multiple( () =>
						 {
							 Assert.That( TelemetryDashboardFormatter.RowClass( true ),                                           Is.EqualTo( "selected" ) );
							 Assert.That( TelemetryDashboardFormatter.RowClass( false ),                                          Is.Empty );
							 Assert.That( TelemetryDashboardFormatter.IsActivationKey( new KeyboardEventArgs { Key = "Enter" } ), Is.True );
							 Assert.That( TelemetryDashboardFormatter.IsActivationKey( new KeyboardEventArgs { Key = " " } ),     Is.True );
							 Assert.That( TelemetryDashboardFormatter.IsActivationKey( new KeyboardEventArgs { Key = "Tab" } ),   Is.False );
						 } );
	}
}
