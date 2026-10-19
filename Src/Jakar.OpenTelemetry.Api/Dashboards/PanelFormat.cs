using System.Globalization;

namespace Jakar.OpenTelemetry.Api.Dashboards;

/// <summary> How panels write values: OpenTelemetry units scaled to something readable, and threshold colors. </summary>
public static class PanelFormat
{
	/// <summary> Named threshold colors; anything else must be a <c>#rgb</c>/<c>#rrggbb</c> hex color (so a stored color can never inject CSS). </summary>
	private static readonly Dictionary<string, string> _s_colors = new(StringComparer.OrdinalIgnoreCase)
																	{
																		["green"]  = "#49dcb1",
																		["red"]    = "#ff5c7a",
																		["orange"] = "#ff9f43",
																		["yellow"] = "#facc15",
																		["blue"]   = "#62d6ff",
																		["purple"] = "#a78bfa"
																	};

	public static IReadOnlyCollection<string> ColorNames => _s_colors.Keys;


	public static string Value( double? value, string? unit, int? decimals = null )
	{
		if ( value is not { } number || !double.IsFinite( number ) ) { return "-"; }

		string  rate    = string.Empty;
		string? measure = unit;

		if ( measure?.EndsWith( "/s", StringComparison.Ordinal ) == true )
		{
			rate    = "/s";
			measure = measure[..^2];
		}

		return measure switch
			   {
				   "s"                     => Duration( number * 1000, decimals ) + rate,
				   "ms"                    => Duration( number,        decimals ) + rate,
				   "By"                    => Bytes( number, decimals ) + rate,
				   "%"                     => Number( number, decimals ?? 1 ) + "%",
				   // "{request}", "{assembly}" ... are annotations on a plain count, not units.
				   null or "" or "1"                                     => Compact( number, decimals ) + rate,
				   _ when measure.StartsWith( '{' ) && measure.EndsWith( '}' ) => Compact( number, decimals ) + rate,
				   _                       => $"{Compact( number, decimals )} {measure.Trim( '{', '}' )}{rate}"
			   };
	}

	/// <summary> The color of the highest threshold the value reaches (thresholds in any order); null when there are none or it reaches none. </summary>
	public static string? Color( double? value, IReadOnlyList<PanelThreshold> thresholds )
	{
		if ( value is not { } number ) { return null; }

		PanelThreshold? reached = thresholds.Where( x => number >= x.Value ).MaxBy( static x => x.Value );
		return reached is null
				   ? null
				   : SafeColor( reached.Color );
	}

	public static string? SafeColor( string? color )
	{
		if ( string.IsNullOrWhiteSpace( color ) ) { return null; }

		string trimmed = color.Trim();
		if ( _s_colors.TryGetValue( trimmed, out string? named ) ) { return named; }

		return trimmed.Length is 4 or 7 && trimmed[0] == '#' && trimmed[1..].All( char.IsAsciiHexDigit )
				   ? trimmed
				   : null;
	}

	/// <summary> 1.2K, 3.4M ... for counts; small values keep their decimals. </summary>
	public static string Compact( double value, int? decimals = null )
	{
		double magnitude = Math.Abs( value );

		return magnitude switch
			   {
				   >= 1e12 => Number( value / 1e12, decimals ?? 1 ) + "T",
				   >= 1e9  => Number( value / 1e9,  decimals ?? 1 ) + "G",
				   >= 1e6  => Number( value / 1e6,  decimals ?? 1 ) + "M",
				   >= 1e4  => Number( value / 1e3,  decimals ?? 1 ) + "K",
				   >= 100  => Number( value,        decimals ?? 0 ),
				   _       => Number( value,        decimals ?? 2 )
			   };
	}


	private static string Duration( double milliseconds, int? decimals ) => Math.Abs( milliseconds ) switch
																			 {
																				 >= 3_600_000 => Number( milliseconds / 3_600_000, decimals ?? 1 ) + " h",
																				 >= 60_000    => Number( milliseconds / 60_000,    decimals ?? 1 ) + " min",
																				 >= 1_000     => Number( milliseconds / 1_000,     decimals ?? 2 ) + " s",
																				 >= 1         => Number( milliseconds,             decimals ?? 1 ) + " ms",
																				 0            => "0 ms",
																				 _            => Number( milliseconds * 1_000,     decimals ?? 0 ) + " µs"
																			 };

	private static string Bytes( double bytes, int? decimals )
	{
		string[] units = [ "B", "KiB", "MiB", "GiB", "TiB" ];
		int      index = 0;

		while ( Math.Abs( bytes ) >= 1024 && index < units.Length - 1 )
		{
			bytes /= 1024;
			index++;
		}

		return Number( bytes, decimals ?? ( index == 0 ? 0 : 1 ) ) + " " + units[index];
	}

	private static string Number( double value, int decimals ) => value.ToString( "#,0." + new string( '#', Math.Clamp( decimals, 0, 6 ) ), CultureInfo.InvariantCulture ).TrimEnd( '.' );
}
