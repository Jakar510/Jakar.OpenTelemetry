using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.AspNetCore.Components.Web;

namespace Jakar.OpenTelemetry.Api.Components.Dashboard;

internal static class TelemetryDashboardFormatter
{
	// Keyed by reference and released with the snapshot that holds the records.
	private static readonly ConditionalWeakTable<object, string>                                   _s_texts     = new();
	private static readonly ConditionalWeakTable<TelemetryLogRecordDto, IReadOnlyList<ImageTag>> _s_imageTags = new();

	private static readonly JsonWriterOptions _s_indentedOptions = new() { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	public static string FormatCount( long                  value ) => value.ToString( "N0", CultureInfo.InvariantCulture );
	public static string FormatDate( DateTimeOffset         value ) => value.ToUniversalTime().ToString( "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture )         + "Z";
	public static string FormatDatePrecise( DateTimeOffset? value ) => value?.ToUniversalTime().ToString( "yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture ) + "Z";
	public static string FormatMap<T>( T map ) where T : IReadOnlyDictionary<string, string?> => map.Count == 0
																									 ? "none"
																									 : string.Join( ", ", map.Select( pair => $"{pair.Key}={CleanJson( pair.Value )}" ) );

	/// <summary> A duration in its natural unit, with up to <paramref name="decimals"/> decimals (fewer for ruler labels, which sit side by side). </summary>
	public static string FormatDuration( double milliseconds, int decimals = 3 )
	{
		string format = "0." + new string( '#', Math.Clamp( decimals, 0, 6 ) );

		return milliseconds switch
			   {
				   >= 60_000 => ( milliseconds / 60_000 ).ToString( "0.##", CultureInfo.InvariantCulture ) + " min",
				   >= 1_000  => ( milliseconds / 1_000 ).ToString( format, CultureInfo.InvariantCulture ) + " s",
				   >= 1      => milliseconds.ToString( format, CultureInfo.InvariantCulture ) + " ms",
				   _         => ( milliseconds * 1_000 ).ToString( format, CultureInfo.InvariantCulture ) + " µs"
			   };
	}

	/// <summary> Integer points keep their exact int64 value; doubles are shown with up to 6 decimals. </summary>
	public static string FormatMetricValue( TelemetryMetricRecordDto metric ) => metric.IntValue is { } exact
																					 ? exact.ToString( "N0", CultureInfo.InvariantCulture )
																					 : metric.NumericValue.ToString( "0.######", CultureInfo.InvariantCulture );

	public static string FormatMetricMap( TelemetryMetricRecordDto metric )
	{
		Dictionary<string, string?> merged = new(StringComparer.Ordinal);

		foreach ( ( string key, string? value ) in metric.Attributes ) { merged[key] = value; }

		foreach ( ( string key, string? value ) in metric.MetadataAttributes ) { merged[$"meta.{key}"] = value; }

		return FormatMap( merged );
	}

	public static string Trim( string? value, int maxLength )
	{
		if ( string.IsNullOrWhiteSpace( value ) ) { return "-"; }

		string cleaned = CleanJson( value );
		return cleaned.Length <= maxLength
				   ? cleaned
				   : cleaned[..maxLength] + "...";
	}

	/// <summary> Legacy rows stored JSON-quoted strings; new rows are already plain. </summary>
	public static string CleanJson( string? value )
	{
		if ( string.IsNullOrWhiteSpace( value ) ) { return string.Empty; }

		return value.Trim().Trim( '"' ).Replace( "\\\"", "\"", StringComparison.Ordinal ).Replace( "\\n", " ", StringComparison.Ordinal );
	}

	/// <summary> Pretty-prints a JSON document (distribution, exemplars, quantiles); returns the input unchanged when it is not JSON. </summary>
	public static string PrettyJson( string? json )
	{
		if ( string.IsNullOrWhiteSpace( json ) ) { return string.Empty; }

		try
		{
			using JsonDocument document = JsonDocument.Parse( json );
			using MemoryStream stream   = new();
			using ( Utf8JsonWriter writer = new(stream, _s_indentedOptions) ) { document.WriteTo( writer ); }

			return System.Text.Encoding.UTF8.GetString( stream.GetBuffer(), 0, (int)stream.Length );
		}
		catch ( JsonException ) { return json; }
	}

	/// <summary> Parsed once per record (records are immutable and replaced with every snapshot), not on every render. </summary>
	public static IReadOnlyList<ImageTag> GetImageTags( TelemetryLogRecordDto log ) => _s_imageTags.GetValue( log,
																											 static record => record.Attributes.TryGetValue( LogTags.ATTRIBUTE_KEY, out string? tags )
																																  ? ImageTag.ParseAll( tags )
																																  : [ ] );

	/// <summary> <see cref="FormatMap{T}"/> of an attribute map, built once per map instance (the explorer shows it in every row on every render). </summary>
	public static string AttributesText( IReadOnlyDictionary<string, string?> map ) => _s_texts.GetValue( map, static value => FormatMap( (IReadOnlyDictionary<string, string?>)value ) );

	/// <summary> <see cref="FormatMetricMap"/>, built once per record. </summary>
	public static string MetricAttributesText( TelemetryMetricRecordDto metric ) => _s_texts.GetValue( metric, static value => FormatMetricMap( (TelemetryMetricRecordDto)value ) );

	public static string RowClass( bool selected ) => selected
														  ? "selected"
														  : string.Empty;

	/// <summary> OTLP severity numbers: 1-4 trace, 5-8 debug, 9-12 info, 13-16 warn, 17-20 error, 21-24 fatal. </summary>
	public static string SeverityClass( int severityNumber ) => severityNumber switch
																	{
																		>= 21 => "sev-fatal",
																		>= 17 => "sev-error",
																		>= 13 => "sev-warn",
																		>= 9  => "sev-info",
																		>= 1  => "sev-debug",
																		_     => string.Empty
																	};

	public static string StatusClass( string? statusCode ) => statusCode switch
																  {
																	  "Error" => "sev-error",
																	  "Ok"    => "sev-ok",
																	  _       => string.Empty
																  };

	/// <summary> W3C trace flags live in the low byte; bits 8/9 carry the OTLP "has is_remote"/"is_remote" span context flags. </summary>
	public static string FormatSpanFlags( uint flags )
	{
		if ( flags == 0 ) { return "0"; }

		List<string> parts = [ $"0x{flags:x}" ];
		if ( ( flags & 0x01 ) != 0 ) { parts.Add( "sampled" ); }

		if ( ( flags & 0x100 ) != 0 )
		{
			parts.Add( ( flags & 0x200 ) != 0
						   ? "remote parent"
						   : "local parent" );
		}

		return string.Join( ", ", parts );
	}

	public static bool IsActivationKey( KeyboardEventArgs args ) => args.Key is "Enter" or " ";
}
