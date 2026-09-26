using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using OpenTelemetry.Proto.Common.V1;

namespace Jakar.OpenTelemetry.Api.Services;

/// <summary>
///     JSON helpers for the PostgreSQL <c>jsonb</c> columns.
///     <para> Attributes are stored as a JSON object whose values keep their OTLP type (string, bool, int, double, array, kvlist; bytes as base64), so they stay queryable with the jsonb operators. </para>
///     <para> When read back for the dashboard, primitive values are exposed as their raw text (no JSON quoting) and arrays/kvlists as compact JSON. </para>
/// </summary>
public static class TelemetryJson
{
	public const string EMPTY_OBJECT = "{}";

	private const int MAX_RETAINED_SCRATCH_BYTES = 256 * 1024;

	public static readonly ReadOnlyDictionary<string, string?> Empty          = new(new Dictionary<string, string?>( 0, StringComparer.Ordinal ));
	public static readonly ReadOnlyDictionary<double, double>  EmptyQuantiles = new(new Dictionary<double, double>( 0 ));

	private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, SkipValidation = true };

	[ThreadStatic] private static Scratch? t_scratch;


	public static readonly ValueConverter<ReadOnlyDictionary<string, string?>, string> StringDictionaryConverter = new(static value => WriteDictionary( value ), static value => ReadDictionary( value ));

	public static readonly ValueComparer<ReadOnlyDictionary<string, string?>> StringDictionaryComparer = new(static ( left, right ) => CompareDictionaries( left, right ), static value => HashDictionary( value ), static value => value);

	public static readonly ValueConverter<ReadOnlyDictionary<double, double>?, string?> DoubleDictionaryConverter = new(static value => value == null
																																			? null
																																			: WriteQuantiles( value ),
																														static value => ReadQuantiles( value ));

	public static readonly ValueComparer<ReadOnlyDictionary<double, double>?> DoubleDictionaryComparer = new(static ( left, right ) => CompareDictionaries( left, right ), static value => HashDictionary( value ), static value => value);

	public static readonly ValueConverter<SpanEvent[]?, string?> SpanEventArrayConverter = new(static value => value == null
																												   ? null
																												   : WriteSpanEvents( value ),
																							   static value => ReadSpanEvents( value ));

	public static readonly ValueComparer<SpanEvent[]?> SpanEventArrayComparer = new(static ( left, right ) => ReferenceEquals( left, right ),
																					static value => value == null
																										? 0
																										: value.Length,
																					static value => value);

	public static readonly ValueConverter<SpanLink[]?, string?> SpanLinkArrayConverter = new(static value => value == null
																												 ? null
																												 : WriteSpanLinks( value ),
																							 static value => ReadSpanLinks( value ));

	public static readonly ValueComparer<SpanLink[]?> SpanLinkArrayComparer = new(static ( left, right ) => ReferenceEquals( left, right ),
																				  static value => value == null
																									  ? 0
																									  : value.Length,
																				  static value => value);


	#region Scalars

	/// <summary> PostgreSQL rejects U+0000 in both <c>text</c> and <c>jsonb</c>; a single NUL would otherwise fail the whole COPY batch. </summary>
	public static string Sanitize( string value ) => value.Contains( '\0' )
														 ? value.Replace( '\0', '�' )
														 : value;

	public static string? SanitizeOrNull( string? value ) => string.IsNullOrEmpty( value )
																 ? null
																 : Sanitize( value );

	public static DateTimeOffset? FromUnixNano( ulong value ) => value == 0
																	 ? null
																	 : DateTimeOffset.UnixEpoch.AddTicks( (long)( value / 100UL ) );

	public static string? ToHex( ByteString bytes ) => bytes.IsEmpty
														   ? null
														   : Convert.ToHexStringLower( bytes.Span );

	public static string? ToHex( byte[]? bytes ) => bytes is null || bytes.Length == 0
														? null
														: Convert.ToHexStringLower( bytes );

	/// <summary> Returns the value of the first attribute named <paramref name="key"/> when it is a string, without materializing the attribute list. </summary>
	public static string? FindString( RepeatedField<KeyValue>? attributes, string key )
	{
		if ( attributes is null ) { return null; }

		for ( int i = 0; i < attributes.Count; i++ )
		{
			KeyValue attribute = attributes[i];
			if ( string.Equals( attribute.Key, key, StringComparison.Ordinal ) &&
				 attribute.Value?.ValueCase == AnyValue.ValueOneofCase.StringValue ) { return SanitizeOrNull( attribute.Value.StringValue ); }
		}

		return null;
	}

	/// <summary> Human readable text for a log body: strings are stored verbatim, structured values as compact JSON. </summary>
	public static string? ToDisplayText( AnyValue? value )
	{
		if ( value is null ) { return null; }

		switch ( value.ValueCase )
		{
			case AnyValue.ValueOneofCase.None:        return null;
			case AnyValue.ValueOneofCase.StringValue: return Sanitize( value.StringValue );

			case AnyValue.ValueOneofCase.BoolValue:
				return value.BoolValue
						   ? "true"
						   : "false";

			case AnyValue.ValueOneofCase.IntValue:    return value.IntValue.ToString( CultureInfo.InvariantCulture );
			case AnyValue.ValueOneofCase.DoubleValue: return value.DoubleValue.ToString( "R", CultureInfo.InvariantCulture );
			case AnyValue.ValueOneofCase.BytesValue:  return Convert.ToBase64String( value.BytesValue.Span );
		}

		Scratch        scratch = RentScratch();
		Utf8JsonWriter writer  = scratch.Begin();
		WriteAnyValue( writer, value );
		return scratch.End();
	}

	#endregion


	#region OTLP -> jsonb

	public static string AttributesToJson( RepeatedField<KeyValue>? attributes )
	{
		if ( attributes is null ||
			 attributes.Count == 0 ) { return EMPTY_OBJECT; }

		Scratch        scratch = RentScratch();
		Utf8JsonWriter writer  = scratch.Begin();
		WriteAttributes( writer, attributes );
		return scratch.End();
	}

	/// <summary> Builds the jsonb payload with a caller supplied writer callback, reusing the thread's scratch buffer. The callback must be synchronous. </summary>
	public static string Build<TState>( TState state, Action<Utf8JsonWriter, TState> write )
	{
		Scratch        scratch = RentScratch();
		Utf8JsonWriter writer  = scratch.Begin();
		write( writer, state );
		return scratch.End();
	}

	public static void WriteAttributes( Utf8JsonWriter writer, RepeatedField<KeyValue> attributes )
	{
		writer.WriteStartObject();

		for ( int i = 0; i < attributes.Count; i++ )
		{
			KeyValue attribute = attributes[i];
			writer.WritePropertyName( Sanitize( attribute.Key ) );
			WriteAnyValue( writer, attribute.Value );
		}

		writer.WriteEndObject();
	}

	public static void WriteAnyValue( Utf8JsonWriter writer, AnyValue? value )
	{
		if ( value is null )
		{
			writer.WriteNullValue();
			return;
		}

		switch ( value.ValueCase )
		{
			case AnyValue.ValueOneofCase.StringValue:
				writer.WriteStringValue( Sanitize( value.StringValue ) );
				return;

			case AnyValue.ValueOneofCase.BoolValue:
				writer.WriteBooleanValue( value.BoolValue );
				return;

			case AnyValue.ValueOneofCase.IntValue:
				writer.WriteNumberValue( value.IntValue );
				return;

			case AnyValue.ValueOneofCase.DoubleValue:
				WriteDouble( writer, value.DoubleValue );
				return;

			case AnyValue.ValueOneofCase.BytesValue:
				writer.WriteBase64StringValue( value.BytesValue.Span );
				return;

			case AnyValue.ValueOneofCase.ArrayValue:
				writer.WriteStartArray();
				RepeatedField<AnyValue> items = value.ArrayValue.Values;
				for ( int i = 0; i < items.Count; i++ ) { WriteAnyValue( writer, items[i] ); }

				writer.WriteEndArray();
				return;

			case AnyValue.ValueOneofCase.KvlistValue:
				WriteAttributes( writer, value.KvlistValue.Values );
				return;

			default: // None, or string_value_strindex which is only meaningful inside the profiles dictionary.
				writer.WriteNullValue();
				return;
		}
	}

	/// <summary> JSON has no NaN/±Infinity literals; use the proto3 JSON string spelling so jsonb accepts the document. </summary>
	public static void WriteDouble( Utf8JsonWriter writer, double value )
	{
		if ( double.IsFinite( value ) ) { writer.WriteNumberValue( value ); }
		else if ( double.IsNaN( value ) ) { writer.WriteStringValue( "NaN" ); }
		else
		{
			writer.WriteStringValue( value > 0
										 ? "Infinity"
										 : "-Infinity" );
		}
	}

	public static void WriteTimestamp( Utf8JsonWriter writer, string propertyName, ulong unixNano )
	{
		if ( FromUnixNano( unixNano ) is { } timestamp ) { writer.WriteString( propertyName, timestamp ); }
		else { writer.WriteNull( propertyName ); }
	}

	public static void WriteHex( Utf8JsonWriter writer, string propertyName, ByteString bytes )
	{
		if ( bytes.IsEmpty )
		{
			writer.WriteNull( propertyName );
			return;
		}

		Span<char> hex = stackalloc char[bytes.Length * 2 <= 256
											 ? bytes.Length * 2
											 : 0];
		if ( hex.Length > 0 &&
			 Convert.TryToHexStringLower( bytes.Span, hex, out int written ) ) { writer.WriteString( propertyName, hex[..written] ); }
		else { writer.WriteString( propertyName,                                                                   Convert.ToHexStringLower( bytes.Span ) ); }
	}

	#endregion


	#region jsonb -> read model

	public static ReadOnlyDictionary<string, string?> ReadDictionary( string? json )
	{
		if ( string.IsNullOrEmpty( json ) ||
			 json == EMPTY_OBJECT ) { return Empty; }

		using JsonDocument document = JsonDocument.Parse( json );
		return ReadDictionary( document.RootElement );
	}

	public static ReadOnlyDictionary<string, string?> ReadDictionary( JsonElement element )
	{
		if ( element.ValueKind != JsonValueKind.Object ) { return Empty; }

		Dictionary<string, string?> values = new(StringComparer.Ordinal);
		foreach ( JsonProperty property in element.EnumerateObject() ) { values[property.Name] = ToDisplayText( property.Value ); }

		return values.Count == 0
				   ? Empty
				   : new ReadOnlyDictionary<string, string?>( values );
	}

	private static string? ToDisplayText( JsonElement value ) => value.ValueKind switch
																	 {
																		 JsonValueKind.Null or JsonValueKind.Undefined => null,
																		 JsonValueKind.String                          => value.GetString(),
																		 _                                             => value.GetRawText()
																	 };

	public static ReadOnlyDictionary<double, double>? ReadQuantiles( string? json )
	{
		if ( string.IsNullOrEmpty( json ) ) { return null; }

		using JsonDocument document = JsonDocument.Parse( json );
		if ( document.RootElement.ValueKind != JsonValueKind.Object ) { return EmptyQuantiles; }

		Dictionary<double, double> values = new();

		foreach ( JsonProperty property in document.RootElement.EnumerateObject() )
		{
			if ( double.TryParse( property.Name, NumberStyles.Float, CultureInfo.InvariantCulture, out double quantile ) ) { values[quantile] = ReadDouble( property.Value ); }
		}

		return new ReadOnlyDictionary<double, double>( values );
	}

	public static SpanEvent[]? ReadSpanEvents( string? json )
	{
		if ( string.IsNullOrEmpty( json ) ) { return null; }

		using JsonDocument document = JsonDocument.Parse( json );
		JsonElement        root     = document.RootElement;
		if ( root.ValueKind != JsonValueKind.Array ) { return [ ]; }

		SpanEvent[] events = new SpanEvent[root.GetArrayLength()];
		int         index  = 0;

		foreach ( JsonElement item in root.EnumerateArray() )
		{
			events[index++] = new SpanEvent( ReadTimestamp( item, "timeUtc" ),
											 ReadString( item, "name" ) ?? string.Empty,
											 item.TryGetProperty( "attributes", out JsonElement attributes )
												 ? ReadDictionary( attributes )
												 : Empty,
											 ReadUInt32( item, "droppedAttributesCount" ) );
		}

		return events;
	}

	public static SpanLink[]? ReadSpanLinks( string? json )
	{
		if ( string.IsNullOrEmpty( json ) ) { return null; }

		using JsonDocument document = JsonDocument.Parse( json );
		JsonElement        root     = document.RootElement;
		if ( root.ValueKind != JsonValueKind.Array ) { return [ ]; }

		SpanLink[] links = new SpanLink[root.GetArrayLength()];
		int        index = 0;

		foreach ( JsonElement item in root.EnumerateArray() )
		{
			links[index++] = new SpanLink( ReadString( item, "traceId" ),
										   ReadString( item, "spanId" ),
										   ReadString( item, "traceState" ) ?? string.Empty,
										   item.TryGetProperty( "attributes", out JsonElement attributes )
											   ? ReadDictionary( attributes )
											   : Empty,
										   ReadUInt32( item, "flags" ),
										   ReadUInt32( item, "droppedAttributesCount" ) );
		}

		return links;
	}

	private static string? ReadString( JsonElement element, string name ) => element.TryGetProperty( name, out JsonElement value ) && value.ValueKind == JsonValueKind.String
																				 ? value.GetString()
																				 : null;

	private static uint ReadUInt32( JsonElement element, string name ) => element.TryGetProperty( name, out JsonElement value ) && value.ValueKind == JsonValueKind.Number && value.TryGetUInt32( out uint result )
																			  ? result
																			  : 0U;

	private static DateTimeOffset? ReadTimestamp( JsonElement element, string name ) => element.TryGetProperty( name, out JsonElement value ) && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset( out DateTimeOffset result )
																							? result
																							: null;

	private static double ReadDouble( JsonElement value ) => value.ValueKind switch
																 {
																	 JsonValueKind.Number => value.GetDouble(),
																	 JsonValueKind.String => double.TryParse( value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed )
																								 ? parsed
																								 : double.NaN,
																	 _ => double.NaN
																 };

	#endregion


	#region read model -> jsonb (only used if EF ever writes these entities; ingest uses binary COPY)

	private static string WriteDictionary( ReadOnlyDictionary<string, string?> value ) => value.Count == 0
																							  ? EMPTY_OBJECT
																							  : Build( value,
																									   static ( writer, map ) =>
																									   {
																										   writer.WriteStartObject();
																										   foreach ( KeyValuePair<string, string?> pair in map ) { writer.WriteString( pair.Key, pair.Value ); }

																										   writer.WriteEndObject();
																									   } );

	public static string WriteQuantiles( ReadOnlyDictionary<double, double> value ) => Build( value,
																							  static ( writer, map ) =>
																							  {
																								  writer.WriteStartObject();

																								  foreach ( KeyValuePair<double, double> pair in map )
																								  {
																									  writer.WritePropertyName( pair.Key.ToString( "R", CultureInfo.InvariantCulture ) );
																									  WriteDouble( writer, pair.Value );
																								  }

																								  writer.WriteEndObject();
																							  } );

	private static string WriteSpanEvents( SpanEvent[] value ) => Build( value,
																		 static ( writer, items ) =>
																		 {
																			 writer.WriteStartArray();

																			 foreach ( SpanEvent item in items )
																			 {
																				 writer.WriteStartObject();
																				 if ( item.TimeUtc is { } time ) { writer.WriteString( "timeUtc", time ); }

																				 writer.WriteString( "name", item.Name );
																				 writer.WritePropertyName( "attributes" );
																				 writer.WriteStartObject();
																				 foreach ( KeyValuePair<string, string?> pair in item.Attributes ) { writer.WriteString( pair.Key, pair.Value ); }

																				 writer.WriteEndObject();
																				 writer.WriteNumber( "droppedAttributesCount", item.DroppedAttributesCount );
																				 writer.WriteEndObject();
																			 }

																			 writer.WriteEndArray();
																		 } );

	private static string WriteSpanLinks( SpanLink[] value ) => Build( value,
																	   static ( writer, items ) =>
																	   {
																		   writer.WriteStartArray();

																		   foreach ( SpanLink item in items )
																		   {
																			   writer.WriteStartObject();
																			   writer.WriteString( "traceId",    item.TraceId );
																			   writer.WriteString( "spanId",     item.SpanId );
																			   writer.WriteString( "traceState", item.TraceState );
																			   writer.WritePropertyName( "attributes" );
																			   writer.WriteStartObject();
																			   foreach ( KeyValuePair<string, string?> pair in item.Attributes ) { writer.WriteString( pair.Key, pair.Value ); }

																			   writer.WriteEndObject();
																			   writer.WriteNumber( "flags",                  item.Flags );
																			   writer.WriteNumber( "droppedAttributesCount", item.DroppedAttributesCount );
																			   writer.WriteEndObject();
																		   }

																		   writer.WriteEndArray();
																	   } );

	#endregion


	#region Comparers

	private static int HashDictionary( ReadOnlyDictionary<string, string?>? value )
	{
		if ( value is null ) { return 0; }

		// Order independent, allocation free.
		int hash = value.Count;
		foreach ( KeyValuePair<string, string?> pair in value )
		{
			hash ^= HashCode.Combine( StringComparer.Ordinal.GetHashCode( pair.Key ),
									  pair.Value is null
										  ? 0
										  : StringComparer.Ordinal.GetHashCode( pair.Value ) );
		}

		return hash;
	}

	private static int HashDictionary( ReadOnlyDictionary<double, double>? value )
	{
		if ( value is null ) { return 0; }

		int hash = value.Count;
		foreach ( KeyValuePair<double, double> pair in value ) { hash ^= HashCode.Combine( pair.Key, pair.Value ); }

		return hash;
	}

	private static bool CompareDictionaries( ReadOnlyDictionary<string, string?>? left, ReadOnlyDictionary<string, string?>? right )
	{
		if ( ReferenceEquals( left, right ) ) { return true; }

		if ( left is null  ||
			 right is null ||
			 left.Count != right.Count ) { return false; }

		foreach ( KeyValuePair<string, string?> pair in left )
		{
			if ( !right.TryGetValue( pair.Key, out string? value ) ||
				 !string.Equals( pair.Value, value, StringComparison.Ordinal ) ) { return false; }
		}

		return true;
	}

	private static bool CompareDictionaries( ReadOnlyDictionary<double, double>? left, ReadOnlyDictionary<double, double>? right )
	{
		if ( ReferenceEquals( left, right ) ) { return true; }

		if ( left is null  ||
			 right is null ||
			 left.Count != right.Count ) { return false; }

		foreach ( KeyValuePair<double, double> pair in left )
		{
			if ( !right.TryGetValue( pair.Key, out double value ) ||
				 !pair.Value.Equals( value ) ) { return false; }
		}

		return true;
	}

	#endregion


	#region Scratch buffer

	/// <summary> One reusable UTF-8 buffer + writer per thread. Only used by synchronous Begin/End sections, so it is never shared across an await. </summary>
	private static Scratch RentScratch() => t_scratch ??= new Scratch();

	private sealed class Scratch
	{
		private          ArrayBufferWriter<byte> _buffer = new(4096);
		private readonly Utf8JsonWriter          _writer;

		public Scratch() => _writer = new Utf8JsonWriter( _buffer, WriterOptions );

		public Utf8JsonWriter Begin()
		{
			_buffer.ResetWrittenCount();
			_writer.Reset( _buffer );
			return _writer;
		}

		public string End()
		{
			_writer.Flush();
			string result = Encoding.UTF8.GetString( _buffer.WrittenSpan );

			// Don't pin an unusually large buffer to the thread forever.
			if ( _buffer.Capacity > MAX_RETAINED_SCRATCH_BYTES )
			{
				_buffer = new ArrayBufferWriter<byte>( 4096 );
				_writer.Reset( _buffer );
			}

			return result;
		}
	}

	#endregion
}
