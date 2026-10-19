using System.Globalization;
using Jakar.Json;

namespace Jakar.OpenTelemetry.Contracts;

/// <summary> Jakar.Json reads and writes <see cref="IReadOnlyDictionary{TKey,TValue}"/> but not the concrete <see cref="ReadOnlyDictionary{TKey,TValue}"/> the DTOs carry. </summary>
public readonly struct AttributesJsonConverter : IJsonConverter<ReadOnlyDictionary<string, string?>>
{
	public static void Write<TWriter>( ref TWriter writer, scoped in ReadOnlyDictionary<string, string?> value )
		where TWriter : IJsonWriter, allows ref struct
	{
		writer.WriteStartObject();

		foreach ( ( string key, string? item ) in value )
		{
			writer.WritePropertyName( key );

			if ( item is null ) { writer.WriteNull(); }
			else { writer.WriteString( item ); }
		}

		writer.WriteEndObject();
	}

	public static bool TryRead<TReader>( ref TReader reader, [MaybeNullWhen( false )] out ReadOnlyDictionary<string, string?> value )
		where TReader : IJsonReader, allows ref struct
	{
		value = null;
		if ( !reader.TryReadStartObject() ) { return false; }

		Dictionary<string, string?> items = new(StringComparer.Ordinal);

		while ( true )
		{
			if ( !reader.TryReadProperty( out JsonSpan name, out bool end ) ) { return false; }

			if ( end ) { break; }

			string key = name.ToString();

			if ( reader.TryReadNull() ) { items[key] = null; }
			else if ( reader.TryReadString( out string? item ) ) { items[key] = item; }
			else { return false; }
		}

		value = items.Count == 0
					? ReadOnlyDictionary<string, string?>.Empty
					: new ReadOnlyDictionary<string, string?>( items );

		return true;
	}
}



/// <summary> Summary quantiles keyed by quantile (<c>{"0.5" : 12.3}</c>); NaN and infinities are written as strings, as the rest of the DTOs do. </summary>
public readonly struct QuantilesJsonConverter : IJsonConverter<ReadOnlyDictionary<double, double>>
{
	public static void Write<TWriter>( ref TWriter writer, scoped in ReadOnlyDictionary<double, double> value )
		where TWriter : IJsonWriter, allows ref struct
	{
		Span<char> key = stackalloc char[32];
		writer.WriteStartObject();

		foreach ( ( double quantile, double item ) in value )
		{
			quantile.TryFormat( key, out int written, "R", CultureInfo.InvariantCulture );
			writer.WritePropertyName( key[..written] );

			if ( double.IsFinite( item ) ) { writer.WriteFloat( item ); }
			else { writer.WriteFormatted( item, "R" ); }
		}

		writer.WriteEndObject();
	}

	public static bool TryRead<TReader>( ref TReader reader, [MaybeNullWhen( false )] out ReadOnlyDictionary<double, double> value )
		where TReader : IJsonReader, allows ref struct
	{
		value = null;
		if ( !reader.TryReadStartObject() ) { return false; }

		Dictionary<double, double> items = [ ];
		Span<char>                 key   = stackalloc char[64];

		while ( true )
		{
			if ( !reader.TryReadProperty( out JsonSpan name, out bool end ) ) { return false; }

			if ( end ) { break; }

			if ( name.Length > key.Length ||
				 !double.TryParse( key[..name.CopyTo( key )], NumberStyles.Float, CultureInfo.InvariantCulture, out double quantile ) ) { return reader.Fail( JsonErrorKind.InvalidValue ); }

			if ( !reader.TryReadFloat( out double item, JsonFloatRead.AllowNonFinite ) ) { return false; }

			items[quantile] = item;
		}

		value = new ReadOnlyDictionary<double, double>( items );
		return true;
	}
}
