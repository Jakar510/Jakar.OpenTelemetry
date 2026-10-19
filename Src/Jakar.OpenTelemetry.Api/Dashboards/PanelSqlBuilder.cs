using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Jakar.OpenTelemetry.Api.Dashboards;

/// <summary>
///     Builds the WHERE clause and key expressions of a panel query. Column names come from a fixed list and attribute keys and values are always parameters,
///     so nothing typed into a panel ever becomes SQL text.
/// </summary>
public sealed class PanelSqlBuilder( TelemetrySignal signal )
{
	private const string RESOURCE_PREFIX = "resource.";

	private readonly List<NpgsqlParameter> _parameters = [ ];


	public IReadOnlyList<NpgsqlParameter> Parameters => _parameters;
	public TelemetrySignal                Signal     => signal;

	public string Table => signal switch
						   {
							   TelemetrySignal.Logs  => "\"Logs\"",
							   TelemetrySignal.Spans => "\"Spans\"",
							   _                     => "\"Metrics\""
						   };


	public string Parameter( object value, NpgsqlDbType? type = null )
	{
		string          name      = $"p{_parameters.Count}";
		NpgsqlParameter parameter = new(name, value);
		if ( type is { } dbType ) { parameter.NpgsqlDbType = dbType; }

		_parameters.Add( parameter );
		return "@" + name;
	}

	/// <summary> The SQL text for a filter or group-by key (<see cref="PanelFilter"/> describes the keys). </summary>
	public string Key( string key )
	{
		string trimmed = key.Trim();

		return trimmed.ToLowerInvariant() switch
			   {
				   "service"                                                       => "\"ServiceName\"",
				   "scope"                                                         => "\"ScopeName\"",
				   "severity" when signal == TelemetrySignal.Logs                  => "\"SeverityText\"",
				   "name" when signal is TelemetrySignal.Spans or TelemetrySignal.Metrics => "\"Name\"",
				   "kind" when signal == TelemetrySignal.Spans                     => "\"Kind\"",
				   "status" when signal == TelemetrySignal.Spans                   => "\"StatusCode\"",
				   _ when trimmed.StartsWith( RESOURCE_PREFIX, StringComparison.OrdinalIgnoreCase ) && trimmed.Length > RESOURCE_PREFIX.Length
					   => $"(\"ResourceAttributesJson\" ->> {Parameter( trimmed[RESOURCE_PREFIX.Length..] )})",
				   _ => $"(\"AttributesJson\" ->> {Parameter( trimmed )})"
			   };
	}

	/// <summary> The time range, the metric name (metrics only) and every filter, joined with AND. </summary>
	public string Where( DateTimeOffset from, DateTimeOffset to, string? metric, IEnumerable<PanelFilter> filters )
	{
		List<string> conditions = [ $"\"SortTimeUtc\" >= {Parameter( from.ToUniversalTime(), NpgsqlDbType.TimestampTz )}", $"\"SortTimeUtc\" <= {Parameter( to.ToUniversalTime(), NpgsqlDbType.TimestampTz )}" ];

		if ( signal == TelemetrySignal.Metrics ) { conditions.Add( $"\"Name\" = {Parameter( metric ?? string.Empty )}" ); }

		conditions.AddRange( filters.Where( static x => !string.IsNullOrWhiteSpace( x.Key ) ).Select( Filter ) );
		return string.Join( " AND ", conditions );
	}

	public string Filter( PanelFilter filter )
	{
		// Severity compares by level, so "severity >= Warning" also matches Error and Fatal.
		if ( signal == TelemetrySignal.Logs && filter.Operator is FilterOperator.GreaterOrEqual or FilterOperator.LessThan && string.Equals( filter.Key.Trim(), "severity", StringComparison.OrdinalIgnoreCase ) )
		{
			return SeverityNumber( filter.Value ) is { } level
					   ? $"\"SeverityNumber\" {( filter.Operator == FilterOperator.GreaterOrEqual ? ">=" : "<" )} {Parameter( level )}"
					   : "false";
		}

		string key = Key( filter.Key );

		return filter.Operator switch
			   {
				   FilterOperator.NotEquals => $"{key} IS DISTINCT FROM {Parameter( filter.Value )}",
				   FilterOperator.Contains  => $"strpos(lower({key}), lower({Parameter( filter.Value )})) > 0",
				   FilterOperator.GreaterOrEqual or FilterOperator.LessThan => double.TryParse( filter.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number )
																				  ? $"{Numeric( key )} {( filter.Operator == FilterOperator.GreaterOrEqual ? ">=" : "<" )} {Parameter( number )}"
																				  : "false",
				   _ => $"{key} = {Parameter( filter.Value )}"
			   };
	}

	/// <summary> OTLP severity numbers for the usual level names (or the number itself). </summary>
	public static int? SeverityNumber( string? value ) => value?.Trim().ToLowerInvariant() switch
														  {
															  "trace"                       => 1,
															  "debug"                       => 5,
															  "info" or "information"       => 9,
															  "warn" or "warning"           => 13,
															  "error"                       => 17,
															  "fatal" or "critical"         => 21,
															  { } text when int.TryParse( text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number ) => number,
															  _                             => null
														  };


	/// <summary> Text attributes compared as numbers; values that aren't numbers never match. </summary>
	private static string Numeric( string key ) => $"(CASE WHEN {key} ~ '^-?[0-9]+(\\.[0-9]+)?([eE][-+]?[0-9]+)?$' THEN ({key})::double precision END)";
}
