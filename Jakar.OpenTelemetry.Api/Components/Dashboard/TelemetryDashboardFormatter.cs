using System.Globalization;
using Jakar.OpenTelemetry.Contracts;

namespace Jakar.OpenTelemetry.Api.Components.Dashboard;

internal static class TelemetryDashboardFormatter
{
    public static string FormatCount( int value ) => value.ToString( "N0", CultureInfo.InvariantCulture );
    public static string FormatDate( DateTimeOffset value ) => value.ToUniversalTime().ToString( "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture ) + "Z";
    public static string FormatMap<T>( T map ) where T : IReadOnlyDictionary<string, string?> => map.Count == 0 ? "none" : string.Join( ", ", map.Select( pair => $"{pair.Key}={CleanJson( pair.Value )}" ) );

    public static string FormatMetricMap( TelemetryMetricRecordDto metric )
    {
        Dictionary<string, string?> merged = new( StringComparer.OrdinalIgnoreCase );

        foreach ( ( string key, string? value ) in metric.Attributes ) { merged[key] = value; }
        foreach ( ( string key, string? value ) in metric.MetadataAttributes ) { merged[$"meta.{key}"] = value; }

        return FormatMap( merged );
    }

    public static string Trim( string? value, int maxLength )
    {
        if ( string.IsNullOrWhiteSpace( value ) ) { return "-"; }

        string cleaned = CleanJson( value );
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength] + "...";
    }

    public static string CleanJson( string? value )
    {
        if ( string.IsNullOrWhiteSpace( value ) ) { return string.Empty; }

        return value.Trim().Trim( '"' ).Replace( "\\\"", "\"", StringComparison.Ordinal ).Replace( "\\n", " ", StringComparison.Ordinal );
    }
}
