using System.Collections.ObjectModel;
using System.Globalization;
using Google.Protobuf;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Jakar.OpenTelemetry.Api.Services;

public static class TelemetryJson
{
    public static readonly  ReadOnlyDictionary<string, string?> Empty          = new(new Dictionary<string, string?>());
    public static readonly  ReadOnlyDictionary<double, double>  EmptyQuantiles = new(new Dictionary<double, double>());
    private static readonly JsonSerializerSettings              JsonSettings   = new() { ContractResolver = new CamelCasePropertyNamesContractResolver(), NullValueHandling = NullValueHandling.Ignore };


    public static readonly ValueConverter<ReadOnlyDictionary<string, string?>, string> StringDictionaryConverter = new(static value => JsonConvert.SerializeObject( value, JsonSettings ),
                                                                                                                       static value =>
                                                                                                                           new ReadOnlyDictionary<string, string?>( JsonConvert.DeserializeObject<Dictionary<string, string?>>( value, JsonSettings ) ??
                                                                                                                                                                    new Dictionary<string, string?>() ));

    public static readonly ValueComparer<ReadOnlyDictionary<string, string?>> StringDictionaryComparer = new(static ( left, right ) => CompareDictionaries( left, right ),
                                                                                                             static value => HashDictionary( value ),
                                                                                                             static value => CloneDictionary( value ));

    public static readonly ValueConverter<ReadOnlyDictionary<double, double>?, string?> DoubleDictionaryConverter = new(static value => value == null
                                                                                                                                            ? null
                                                                                                                                            : JsonConvert.SerializeObject( value, JsonSettings ),
                                                                                                                        static value => string.IsNullOrWhiteSpace( value )
                                                                                                                                            ? null
                                                                                                                                            : new ReadOnlyDictionary<double, double>( JsonConvert.DeserializeObject<Dictionary<double, double>>( value,
                                                                                                                                                                                                                                                 JsonSettings ) ??
                                                                                                                                                                                      new Dictionary<double, double>() ));

    public static readonly ValueComparer<ReadOnlyDictionary<double, double>?> DoubleDictionaryComparer = new(static ( left, right ) => CompareDictionaries( left, right ),
                                                                                                             static value => HashDictionary( value ),
                                                                                                             static value => CloneDictionary( value ));

    public static readonly ValueConverter<SpanEvent[]?, string?> SpanEventArrayConverter = CreateArrayConverter<SpanEvent>();
    public static readonly ValueComparer<SpanEvent[]?>           SpanEventArrayComparer  = CreateArrayComparer<SpanEvent>();

    public static readonly ValueConverter<SpanLink[]?, string?> SpanLinkArrayConverter = CreateArrayConverter<SpanLink>();
    public static readonly ValueComparer<SpanLink[]?>           SpanLinkArrayComparer  = CreateArrayComparer<SpanLink>();


    private static ValueConverter<T[]?, string?> CreateArrayConverter<T>() => new(static value => value == null
                                                                                                      ? null
                                                                                                      : JsonConvert.SerializeObject( value, JsonSettings ),
                                                                                  static value => string.IsNullOrWhiteSpace( value )
                                                                                                      ? null
                                                                                                      : JsonConvert.DeserializeObject<T[]>( value, JsonSettings ));

    private static ValueComparer<T[]?> CreateArrayComparer<T>() => new(static ( left, right ) => CompareArrays( left, right ), static value => HashArray( value ), static value => CloneArray( value ));

    private static bool CompareArrays<T>( T[]? left, T[]? right )
    {
        if ( ReferenceEquals( left, right ) ) { return true; }

        if ( left        == null ||
             right       == null ||
             left.Length != right.Length ) { return false; }

        return left.SequenceEqual( right );
    }

    private static int HashArray<T>( T[]? value ) => value == null
                                                         ? 0
                                                         : JsonConvert.SerializeObject( value, JsonSettings ).GetHashCode( StringComparison.Ordinal );

    private static T[]? CloneArray<T>( T[]? value ) => value?.ToArray();


    private static ReadOnlyDictionary<string, string?> CloneDictionary( ReadOnlyDictionary<string, string?>? value ) =>
        new(value?.ToDictionary( static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal ) ?? new Dictionary<string, string?>());

    private static ReadOnlyDictionary<double, double> CloneDictionary( ReadOnlyDictionary<double, double>? value ) => new(value?.ToDictionary( static pair => pair.Key, static pair => pair.Value ) ?? new Dictionary<double, double>());

    private static int HashDictionary( ReadOnlyDictionary<string, string?>? value )
    {
        if ( value is null ) { return 0; }

        HashCode hash = new();
        foreach ( KeyValuePair<string, string?> pair in value.OrderBy( static x => x.Key, StringComparer.Ordinal ) )
        {
            hash.Add( pair.Key,   StringComparer.Ordinal );
            hash.Add( pair.Value, StringComparer.Ordinal );
        }

        return hash.ToHashCode();
    }

    private static int HashDictionary( ReadOnlyDictionary<double, double>? value )
    {
        if ( value is null ) { return 0; }

        HashCode hash = new();
        foreach ( KeyValuePair<double, double> pair in value.OrderBy( static x => x.Key ) )
        {
            hash.Add( pair.Key );
            hash.Add( pair.Value );
        }

        return hash.ToHashCode();
    }

    private static bool CompareDictionaries( ReadOnlyDictionary<string, string?>? left, ReadOnlyDictionary<string, string?>? right )
    {
        if ( ReferenceEquals( left, right ) ) { return true; }

        if ( left is null ||
             right is null ) { return false; }

        if ( left.Count != right.Count ) { return false; }

        foreach ( KeyValuePair<string, string?> pair in left )
        {
            if ( !right.TryGetValue( pair.Key, out string? value ) ) { return false; }

            if ( !string.Equals( pair.Value, value, StringComparison.Ordinal ) ) { return false; }
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
                 pair.Value != value ) { return false; }
        }

        return true;
    }


    public static ReadOnlyDictionary<string, string?> ToDictionary( IEnumerable<KeyValue> attributes )
    {
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);
        foreach ( KeyValue attribute in attributes ) { values[attribute.Key] = ToText( attribute.Value ); }

        return new ReadOnlyDictionary<string, string?>( values );
    }

    public static string SerializeAttributes( IEnumerable<KeyValue> attributes ) => JsonConvert.SerializeObject( ToDictionary( attributes ), JsonSettings );

    public static string SerializeObject( object? value ) => JsonConvert.SerializeObject( value, JsonSettings );

    public static ReadOnlyDictionary<string, string?> DeserializeAttributes( string? json )
    {
        if ( string.IsNullOrWhiteSpace( json ) ) { return Empty; }

        return new ReadOnlyDictionary<string, string?>( JsonConvert.DeserializeObject<Dictionary<string, string?>>( json, JsonSettings ) ?? new Dictionary<string, string?>() );
    }

    public static string? TryGetServiceName( ReadOnlyDictionary<string, string?> resourceAttributes )
    {
        return resourceAttributes.TryGetValue( "service.name", out string? serviceName )
                   ? serviceName
                   : null;
    }

    public static DateTimeOffset? FromUnixNano( ulong value )
    {
        if ( value == 0 ) { return null; }

        long ticks = checked ( (long)( value / 100UL ) );
        return DateTimeOffset.UnixEpoch.AddTicks( ticks );
    }

    public static string? ToHex( ByteString bytes ) => bytes.IsEmpty
                                                           ? null
                                                           : Convert.ToHexString( bytes.ToByteArray() ).ToLowerInvariant();
    public static string? ToText( AnyValue? value ) => ToNode( value )?.ToString( Formatting.None );

    public static JToken? ToNode( AnyValue? value )
    {
        if ( value is null ) { return null; }

        return value.ValueCase switch
                   {
                       AnyValue.ValueOneofCase.StringValue => new JValue( value.StringValue ),
                       AnyValue.ValueOneofCase.BoolValue   => new JValue( value.BoolValue ),
                       AnyValue.ValueOneofCase.IntValue    => new JValue( value.IntValue ),
                       AnyValue.ValueOneofCase.DoubleValue => new JValue( value.DoubleValue ),
                       AnyValue.ValueOneofCase.BytesValue  => new JValue( Convert.ToBase64String( value.BytesValue.ToByteArray() ) ),
                       AnyValue.ValueOneofCase.ArrayValue  => new JArray( value.ArrayValue.Values.Select( ToNode ) ),
                       AnyValue.ValueOneofCase.KvlistValue => new JObject( value.KvlistValue.Values.Select( kv => new JProperty( kv.Key, ToNode( kv.Value ) ) ) ),
                       _                                   => null
                   };
    }

    public static SpanEvent[] SerializeSpanEvents( IEnumerable<Span.Types.Event> events )
    {
        SpanEvent[] payload = events.Select( static evt => new SpanEvent( FromUnixNano( evt.TimeUnixNano ), evt.Name, ToDictionary( evt.Attributes ) ) ).ToArray();
        return payload;
    }

    public static SpanLink[] SerializeSpanLinks( IEnumerable<Span.Types.Link> links )
    {
        SpanLink[] payload = links.Select( static link => new SpanLink( ToHex( link.TraceId ), ToHex( link.SpanId ), link.TraceState, ToDictionary( link.Attributes ) ) ).ToArray();
        return payload;
    }

    public static string? FormatNumber( double? value ) => value?.ToString( "0.###", CultureInfo.InvariantCulture );
}
