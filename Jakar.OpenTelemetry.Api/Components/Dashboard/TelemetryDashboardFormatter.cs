using System.Globalization;
using System.Text.Json;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.AspNetCore.Components.Web;

namespace Jakar.OpenTelemetry.Api.Components.Dashboard;

internal static class TelemetryDashboardFormatter
{
	private static readonly JsonWriterOptions IndentedOptions = new() { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	public static string FormatCount( long                  value ) => value.ToString( "N0", CultureInfo.InvariantCulture );
	public static string FormatDate( DateTimeOffset         value ) => value.ToUniversalTime().ToString( "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture )         + "Z";
	public static string FormatDatePrecise( DateTimeOffset? value ) => value?.ToUniversalTime().ToString( "yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture ) + "Z";
	public static string FormatMap<T>( T map ) where T : IReadOnlyDictionary<string, string?> => map.Count == 0
																									 ? "none"
																									 : string.Join( ", ", map.Select( pair => $"{pair.Key}={CleanJson( pair.Value )}" ) );

	public static string FormatDuration( double milliseconds ) => milliseconds switch
																	  {
																		  >= 60_000 => $"{milliseconds / 60_000:0.##} min",
																		  >= 1_000  => $"{milliseconds / 1_000:0.###} s",
																		  >= 1      => $"{milliseconds:0.###} ms",
																		  _         => $"{milliseconds * 1_000:0.###} µs"
																	  };

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
			using ( Utf8JsonWriter writer = new(stream, IndentedOptions) ) { document.WriteTo( writer ); }

			return System.Text.Encoding.UTF8.GetString( stream.GetBuffer(), 0, (int)stream.Length );
		}
		catch ( JsonException ) { return json; }
	}

	public static IReadOnlyList<ImageTag> GetImageTags( TelemetryLogRecordDto log ) => log.Attributes.TryGetValue( LogTags.ATTRIBUTE_KEY, out string? tags )
																						   ? ImageTag.ParseAll( tags )
																						   : [ ];

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
