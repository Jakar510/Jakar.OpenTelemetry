using System.Collections.ObjectModel;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Jakar.OpenTelemetry.Api.Services;
using Jakar.OpenTelemetry.Contracts;
using global::OpenTelemetry.Proto.Common.V1;

namespace Jakar.OpenTelemetry.Api.Tests;

[TestFixture]
public sealed class TelemetryJsonTests
{
	private static ReadOnlyDictionary<string, string?> Map( params (string Key, string? Value)[] pairs ) => new(pairs.ToDictionary( static x => x.Key, static x => x.Value ));

	private static RepeatedField<KeyValue> Attributes( params KeyValue[] values )
	{
		RepeatedField<KeyValue> field = [ ];
		field.Add( values );
		return field;
	}


	#region Scalars

	[TestCase( "plain",      "plain" )]
	[TestCase( "nul\0here",  "nul�here" )]
	[TestCase( "",           "" )]
	public void Sanitize_ReplacesNul( string input, string expected ) => Assert.That( TelemetryJson.Sanitize( input ), Is.EqualTo( expected ) );

	[TestCase( null,     null )]
	[TestCase( "",       null )]
	[TestCase( "a\0",    "a�" )]
	public void SanitizeOrNull_EmptyIsNull( string? input, string? expected ) => Assert.That( TelemetryJson.SanitizeOrNull( input ), Is.EqualTo( expected ) );

	[Test]
	public void FromUnixNano_ZeroIsNull_OtherwiseTicksPrecision()
	{
		Assert.Multiple( () =>
						 {
							 Assert.That( TelemetryJson.FromUnixNano( 0 ),                              Is.Null );
							 Assert.That( TelemetryJson.FromUnixNano( Otlp.UnixNano( Otlp.Time ) ),     Is.EqualTo( Otlp.Time ) );
							 Assert.That( TelemetryJson.FromUnixNano( 1_000_000_199 ),                  Is.EqualTo( DateTimeOffset.UnixEpoch.AddTicks( 10_000_001 ) ), "sub-tick nanoseconds are truncated" );
							 Assert.That( TelemetryJson.FromUnixNano( ulong.MaxValue ),                 Is.Not.Null, "the largest OTLP timestamp is still representable" );
						 } );
	}

	[Test]
	public void ToHex_EmptyIsNull()
	{
		Assert.Multiple( () =>
						 {
							 Assert.That( TelemetryJson.ToHex( ByteString.Empty ),                  Is.Null );
							 Assert.That( TelemetryJson.ToHex( ByteString.CopyFrom( 0xAB, 0x01 ) ), Is.EqualTo( "ab01" ) );
							 Assert.That( TelemetryJson.ToHex( (byte[]?)null ),                    Is.Null );
							 Assert.That( TelemetryJson.ToHex( [ ] ),                              Is.Null );
							 Assert.That( TelemetryJson.ToHex( [ 0xCD ] ),                         Is.EqualTo( "cd" ) );
						 } );
	}

	[Test]
	public void FindString_ReturnsFirstStringValue()
	{
		RepeatedField<KeyValue> attributes = Attributes( Otlp.Kv( "service.name", Otlp.Int( 1 ) ), Otlp.Kv( "service.name", "svc\0" ), Otlp.Kv( "service.name", "second" ), Otlp.Kv( "empty", "" ), Otlp.Kv( "missing-value", (AnyValue?)null ) );

		Assert.Multiple( () =>
						 {
							 Assert.That( TelemetryJson.FindString( attributes, "service.name" ),  Is.EqualTo( "svc�" ) );
							 Assert.That( TelemetryJson.FindString( attributes, "empty" ),         Is.Null );
							 Assert.That( TelemetryJson.FindString( attributes, "missing-value" ), Is.Null );
							 Assert.That( TelemetryJson.FindString( attributes, "absent" ),        Is.Null );
							 Assert.That( TelemetryJson.FindString( null, "service.name" ),        Is.Null );
						 } );
	}

	[Test]
	public void ToDisplayText_RendersEachAnyValueKind()
	{
		Assert.Multiple( () =>
						 {
							 Assert.That( TelemetryJson.ToDisplayText( (AnyValue?)null ),       Is.Null );
							 Assert.That( TelemetryJson.ToDisplayText( new AnyValue() ),        Is.Null );
							 Assert.That( TelemetryJson.ToDisplayText( Otlp.Str( "a\0" ) ),     Is.EqualTo( "a�" ) );
							 Assert.That( TelemetryJson.ToDisplayText( Otlp.Bool( true ) ),     Is.EqualTo( "true" ) );
							 Assert.That( TelemetryJson.ToDisplayText( Otlp.Bool( false ) ),    Is.EqualTo( "false" ) );
							 Assert.That( TelemetryJson.ToDisplayText( Otlp.Int( -42 ) ),       Is.EqualTo( "-42" ) );
							 Assert.That( TelemetryJson.ToDisplayText( Otlp.Double( 0.1 ) ),    Is.EqualTo( "0.1" ) );
							 Assert.That( TelemetryJson.ToDisplayText( Otlp.Bytes( [ 1, 2 ] ) ), Is.EqualTo( "AQI=" ) );
							 Assert.That( TelemetryJson.ToDisplayText( Otlp.Array( Otlp.Str( "x" ), Otlp.Int( 1 ) ) ), Is.EqualTo( """["x",1]""" ) );
							 Assert.That( TelemetryJson.ToDisplayText( Otlp.KvList( Otlp.Kv( "k", "v" ) ) ),         Is.EqualTo( """{"k":"v"}""" ) );
						 } );
	}

	#endregion


	#region OTLP -> jsonb

	[Test]
	public void AttributesToJson_EmptyOrNull_IsEmptyObject()
	{
		Assert.Multiple( () =>
						 {
							 Assert.That( TelemetryJson.AttributesToJson( null ),         Is.EqualTo( TelemetryJson.EMPTY_OBJECT ) );
							 Assert.That( TelemetryJson.AttributesToJson( Attributes() ), Is.EqualTo( TelemetryJson.EMPTY_OBJECT ) );
						 } );
	}

	[Test]
	public void AttributesToJson_KeepsOtlpTypes()
	{
		string json = TelemetryJson.AttributesToJson( Attributes( Otlp.Kv( "s", "<é>" ),
																  Otlp.Kv( "b", Otlp.Bool( false ) ),
																  Otlp.Kv( "i", Otlp.Int( 9_007_199_254_740_993 ) ),
																  Otlp.Kv( "d", Otlp.Double( 1.5 ) ),
																  Otlp.Kv( "nan", Otlp.Double( double.NaN ) ),
																  Otlp.Kv( "inf", Otlp.Double( double.PositiveInfinity ) ),
																  Otlp.Kv( "-inf", Otlp.Double( double.NegativeInfinity ) ),
																  Otlp.Kv( "bytes", Otlp.Bytes( [ 0xFF ] ) ),
																  Otlp.Kv( "arr", Otlp.Array( Otlp.Str( "a" ), Otlp.Array( Otlp.Int( 1 ) ) ) ),
																  Otlp.Kv( "kv", Otlp.KvList( Otlp.Kv( "nested", Otlp.Bool( true ) ) ) ),
																  Otlp.Kv( "none", new AnyValue() ),
																  Otlp.Kv( "null", (AnyValue?)null ),
																  Otlp.Kv( "key\0nul", "v" ) ) );

		Assert.That( json, Is.EqualTo( """{"s":"<é>","b":false,"i":9007199254740993,"d":1.5,"nan":"NaN","inf":"Infinity","-inf":"-Infinity","bytes":"/w==","arr":["a",[1]],"kv":{"nested":true},"none":null,"null":null,"key�nul":"v"}""" ) );
		Assert.DoesNotThrow( () => JsonDocument.Parse( json ).Dispose(), "the document is valid JSON for jsonb" );
	}

	[Test]
	public void Build_LargePayload_ReleasesOversizedScratchBuffer()
	{
		string big = new('x', 300 * 1024);

		string first  = TelemetryJson.Build( big, static ( writer, value ) => writer.WriteStringValue( value ) );
		string second = TelemetryJson.Build( "small", static ( writer, value ) => writer.WriteStringValue( value ) );

		Assert.Multiple( () =>
						 {
							 Assert.That( first,  Has.Length.EqualTo( big.Length + 2 ) );
							 Assert.That( second, Is.EqualTo( "\"small\"" ), "the writer is reset onto the new buffer" );
						 } );
	}

	[Test]
	public void WriteTimestampAndHex_WriteNullForMissingValues()
	{
		string json = TelemetryJson.Build( 0,
										   static ( writer, _ ) =>
										   {
											   writer.WriteStartObject();
											   TelemetryJson.WriteTimestamp( writer, "zero", 0 );
											   TelemetryJson.WriteTimestamp( writer, "time", Otlp.UnixNano( Otlp.Time ) );
											   TelemetryJson.WriteHex( writer, "empty", ByteString.Empty );
											   TelemetryJson.WriteHex( writer, "id", ByteString.CopyFrom( Otlp.SpanId ) );
											   TelemetryJson.WriteHex( writer, "huge", ByteString.CopyFrom( new byte[200] ) );
											   writer.WriteEndObject();
										   } );

		using JsonDocument document = JsonDocument.Parse( json );
		JsonElement        root     = document.RootElement;

		Assert.Multiple( () =>
						 {
							 Assert.That( root.GetProperty( "zero" ).ValueKind,                Is.EqualTo( JsonValueKind.Null ) );
							 Assert.That( root.GetProperty( "time" ).GetDateTimeOffset(),      Is.EqualTo( Otlp.Time ) );
							 Assert.That( root.GetProperty( "empty" ).ValueKind,               Is.EqualTo( JsonValueKind.Null ) );
							 Assert.That( root.GetProperty( "id" ).GetString(),                Is.EqualTo( "a1a2a3a4a5a6a7a8" ) );
							 Assert.That( root.GetProperty( "huge" ).GetString(),              Has.Length.EqualTo( 400 ), "ids longer than the stack buffer are still written" );
						 } );
	}

	#endregion


	#region jsonb -> read model

	[TestCase( null )]
	[TestCase( "" )]
	[TestCase( "{}" )]
	[TestCase( "[1,2]" )]
	[TestCase( "{ }" )]
	public void ReadDictionary_EmptyOrNonObject_IsEmpty( string? json ) => Assert.That( TelemetryJson.ReadDictionary( json ), Is.Empty );

	[Test]
	public void ReadDictionary_PrimitivesAreRawText()
	{
		ReadOnlyDictionary<string, string?> values = TelemetryJson.ReadDictionary( """{"s":"text","i":5,"b":true,"n":null,"a":[1,"x"],"o":{"k":1}}""" );

		Assert.That( values, Is.EquivalentTo( new Dictionary<string, string?> { ["s"] = "text", ["i"] = "5", ["b"] = "true", ["n"] = null, ["a"] = """[1,"x"]""", ["o"] = """{"k":1}""" } ) );
	}

	[Test]
	public void ReadQuantiles_ParsesNumericKeysAndSpecialValues()
	{
		ReadOnlyDictionary<double, double>? quantiles = TelemetryJson.ReadQuantiles( """{"0.5":5,"0.99":"Infinity","1":"garbage","x":1,"0.9":true}""" );

		Assert.Multiple( () =>
						 {
							 Assert.That( quantiles![0.5],                        Is.EqualTo( 5 ) );
							 Assert.That( quantiles[0.99],                        Is.EqualTo( double.PositiveInfinity ) );
							 Assert.That( double.IsNaN( quantiles[1] ),           Is.True );
							 Assert.That( double.IsNaN( quantiles[0.9] ),         Is.True );
							 Assert.That( quantiles.ContainsKey( double.NaN ),    Is.False );
							 Assert.That( quantiles,                              Has.Count.EqualTo( 4 ) );
							 Assert.That( TelemetryJson.ReadQuantiles( null ),    Is.Null );
							 Assert.That( TelemetryJson.ReadQuantiles( "[]" ),    Is.Empty );
						 } );
	}

	[Test]
	public void ReadSpanEvents_ParsesEveryField()
	{
		SpanEvent[]? events = TelemetryJson.ReadSpanEvents( """[{"timeUtc":"2026-09-01T12:34:56+00:00","name":"exception","attributes":{"k":"v"},"droppedAttributesCount":3},{"name":5,"timeUtc":"not a date","droppedAttributesCount":-1},{}]""" );

		Assert.Multiple( () =>
						 {
							 Assert.That( events!,                           Has.Length.EqualTo( 3 ) );
							 Assert.That( events[0].TimeUtc,                 Is.EqualTo( Otlp.Time ) );
							 Assert.That( events[0].Name,                    Is.EqualTo( "exception" ) );
							 Assert.That( events[0].Attributes["k"],         Is.EqualTo( "v" ) );
							 Assert.That( events[0].DroppedAttributesCount,  Is.EqualTo( 3 ) );
							 Assert.That( events[1].TimeUtc,                 Is.Null );
							 Assert.That( events[1].Name,                    Is.Empty );
							 Assert.That( events[1].DroppedAttributesCount,  Is.Zero );
							 Assert.That( events[2].Attributes,              Is.Empty );
							 Assert.That( TelemetryJson.ReadSpanEvents( null ), Is.Null );
							 Assert.That( TelemetryJson.ReadSpanEvents( "{}" ), Is.Empty );
						 } );
	}

	[Test]
	public void ReadSpanLinks_ParsesEveryField()
	{
		SpanLink[]? links = TelemetryJson.ReadSpanLinks( """[{"traceId":"t","spanId":"s","traceState":"a=b","attributes":{"k":"v"},"flags":769,"droppedAttributesCount":2},{}]""" );

		Assert.Multiple( () =>
						 {
							 Assert.That( links!,                        Has.Length.EqualTo( 2 ) );
							 Assert.That( links[0].TraceId,              Is.EqualTo( "t" ) );
							 Assert.That( links[0].SpanId,               Is.EqualTo( "s" ) );
							 Assert.That( links[0].TraceState,           Is.EqualTo( "a=b" ) );
							 Assert.That( links[0].Attributes["k"],      Is.EqualTo( "v" ) );
							 Assert.That( links[0].Flags,                Is.EqualTo( 769 ) );
							 Assert.That( links[0].DroppedAttributesCount, Is.EqualTo( 2 ) );
							 Assert.That( links[1].TraceId,              Is.Null );
							 Assert.That( links[1].TraceState,           Is.Empty );
							 Assert.That( links[1].Attributes,           Is.Empty );
							 Assert.That( TelemetryJson.ReadSpanLinks( null ), Is.Null );
							 Assert.That( TelemetryJson.ReadSpanLinks( "1" ),  Is.Empty );
						 } );
	}

	#endregion


	#region EF converters and comparers

	[Test]
	public void StringDictionaryConverter_RoundTrips()
	{
		ReadOnlyDictionary<string, string?> map  = Map( ( "a", "1" ), ( "b", null ) );
		string                              json = (string)TelemetryJson.StringDictionaryConverter.ConvertToProvider( map )!;

		Assert.Multiple( () =>
						 {
							 Assert.That( json,                                                                                        Is.EqualTo( """{"a":"1","b":null}""" ) );
							 Assert.That( TelemetryJson.StringDictionaryConverter.ConvertFromProvider( json ),                         Is.EquivalentTo( map ) );
							 Assert.That( TelemetryJson.StringDictionaryConverter.ConvertToProvider( TelemetryJson.Empty ),            Is.EqualTo( "{}" ) );
						 } );
	}

	[Test]
	public void DoubleDictionaryConverter_RoundTripsAndHandlesNull()
	{
		ReadOnlyDictionary<double, double> quantiles = new(new Dictionary<double, double> { [0.5] = 1, [0.99] = double.NaN });
		string                             json      = (string)TelemetryJson.DoubleDictionaryConverter.ConvertToProvider( quantiles )!;

		Assert.Multiple( () =>
						 {
							 Assert.That( json,                                                                  Is.EqualTo( """{"0.5":1,"0.99":"NaN"}""" ) );
							 Assert.That( ( (ReadOnlyDictionary<double, double>)TelemetryJson.DoubleDictionaryConverter.ConvertFromProvider( json )! )[0.5], Is.EqualTo( 1 ) );
							 Assert.That( TelemetryJson.DoubleDictionaryConverter.ConvertToProvider( null ),     Is.Null );
							 Assert.That( TelemetryJson.WriteQuantiles( TelemetryJson.EmptyQuantiles ),          Is.EqualTo( "{}" ) );
						 } );
	}

	[Test]
	public void SpanEventAndLinkConverters_RoundTrip()
	{
		SpanEvent[] events = [ new(Otlp.Time, "e", Map( ( "k", "v" ) ), 1), new(null, "no-time", TelemetryJson.Empty) ];
		SpanLink[]  links  = [ new("t", "s", "a=b", Map( ( "k", null ) ), 1, 2) ];

		string eventsJson = (string)TelemetryJson.SpanEventArrayConverter.ConvertToProvider( events )!;
		string linksJson  = (string)TelemetryJson.SpanLinkArrayConverter.ConvertToProvider( links )!;

		SpanEvent[] eventsBack = (SpanEvent[])TelemetryJson.SpanEventArrayConverter.ConvertFromProvider( eventsJson )!;
		SpanLink[]  linksBack  = (SpanLink[])TelemetryJson.SpanLinkArrayConverter.ConvertFromProvider( linksJson )!;

		Assert.Multiple( () =>
						 {
							 Assert.That( eventsBack[0].TimeUtc,  Is.EqualTo( Otlp.Time ) );
							 Assert.That( eventsBack[0].Attributes["k"], Is.EqualTo( "v" ) );
							 Assert.That( eventsBack[1].TimeUtc,  Is.Null );
							 Assert.That( linksBack[0].TraceState, Is.EqualTo( "a=b" ) );
							 Assert.That( linksBack[0].Flags,     Is.EqualTo( 1 ) );
							 Assert.That( linksBack[0].DroppedAttributesCount, Is.EqualTo( 2 ) );
							 Assert.That( TelemetryJson.SpanEventArrayConverter.ConvertToProvider( null ), Is.Null );
							 Assert.That( TelemetryJson.SpanLinkArrayConverter.ConvertToProvider( null ),  Is.Null );
						 } );
	}

	[Test]
	public void StringDictionaryComparer_ComparesByContent()
	{
		var comparer = TelemetryJson.StringDictionaryComparer;
		ReadOnlyDictionary<string, string?> a = Map( ( "x", "1" ), ( "y", null ) );
		ReadOnlyDictionary<string, string?> b = Map( ( "y", null ), ( "x", "1" ) );

		Assert.Multiple( () =>
						 {
							 Assert.That( comparer.Equals( a, a ),                                 Is.True );
							 Assert.That( comparer.Equals( a, b ),                                 Is.True );
							 Assert.That( comparer.GetHashCode( a ),                               Is.EqualTo( comparer.GetHashCode( b ) ), "order independent" );
							 Assert.That( comparer.Equals( a, Map( ( "x", "1" ) ) ),               Is.False );
							 Assert.That( comparer.Equals( a, Map( ( "x", "2" ), ( "y", null ) ) ), Is.False );
							 Assert.That( comparer.Equals( a, Map( ( "z", "1" ), ( "y", null ) ) ), Is.False );
							 Assert.That( comparer.Equals( a, null ),                              Is.False );
							 Assert.That( comparer.Equals( null, null ),                           Is.True );
							 Assert.That( comparer.GetHashCode( null! ),                           Is.Zero );
							 Assert.That( comparer.Snapshot( a ),                                  Is.SameAs( a ) );
						 } );
	}

	[Test]
	public void DoubleDictionaryComparer_ComparesByContent()
	{
		var comparer = TelemetryJson.DoubleDictionaryComparer;
		ReadOnlyDictionary<double, double> a = new(new Dictionary<double, double> { [0.5] = 1 });
		ReadOnlyDictionary<double, double> b = new(new Dictionary<double, double> { [0.5] = 1 });

		Assert.Multiple( () =>
						 {
							 Assert.That( comparer.Equals( a, b ),                                                                        Is.True );
							 Assert.That( comparer.GetHashCode( a ),                                                                      Is.EqualTo( comparer.GetHashCode( b ) ) );
							 Assert.That( comparer.Equals( a, new ReadOnlyDictionary<double, double>( new Dictionary<double, double> { [0.5] = 2 } ) ), Is.False );
							 Assert.That( comparer.Equals( a, new ReadOnlyDictionary<double, double>( new Dictionary<double, double> { [0.9] = 1 } ) ), Is.False );
							 Assert.That( comparer.Equals( a, TelemetryJson.EmptyQuantiles ),                                           Is.False );
							 Assert.That( comparer.Equals( a, null ),                                                                    Is.False );
							 Assert.That( comparer.Equals( null, null ),                                                                 Is.True );
							 Assert.That( comparer.GetHashCode( null! ),                                                                 Is.Zero );
						 } );
	}

	[Test]
	public void SpanArrayComparers_UseReferenceEquality()
	{
		SpanEvent[] events = [ ];
		SpanLink[]  links  = [ new("t", "s", "", TelemetryJson.Empty) ];

		Assert.Multiple( () =>
						 {
							 Assert.That( TelemetryJson.SpanEventArrayComparer.Equals( events, events ),   Is.True );
							 Assert.That( TelemetryJson.SpanEventArrayComparer.Equals( events, new SpanEvent[0] ), Is.False );
							 Assert.That( TelemetryJson.SpanEventArrayComparer.GetHashCode( events ),      Is.Zero );
							 Assert.That( TelemetryJson.SpanEventArrayComparer.GetHashCode( null! ),       Is.Zero );
							 Assert.That( TelemetryJson.SpanLinkArrayComparer.Equals( links, links ),      Is.True );
							 Assert.That( TelemetryJson.SpanLinkArrayComparer.GetHashCode( links ),        Is.EqualTo( 1 ) );
							 Assert.That( TelemetryJson.SpanLinkArrayComparer.GetHashCode( null! ),        Is.Zero );
							 Assert.That( TelemetryJson.SpanEventArrayComparer.Snapshot( events ),         Is.SameAs( events ) );
							 Assert.That( TelemetryJson.SpanLinkArrayComparer.Snapshot( links ),           Is.SameAs( links ) );
							 Assert.That( TelemetryJson.DoubleDictionaryComparer.Snapshot( TelemetryJson.EmptyQuantiles ), Is.SameAs( TelemetryJson.EmptyQuantiles ) );
						 } );
	}

	#endregion
}
