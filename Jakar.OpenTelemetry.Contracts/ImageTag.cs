using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Jakar.OpenTelemetry.Contracts;

/// <summary>
///     Reference from a log record to an image uploaded separately, encoded as <c>image:{file-name}:{image-id}</c> in the <see cref="LogTags.ATTRIBUTE_KEY"/> log attribute.
///     <para> The id is generated on the client so the log can be exported immediately while the image is uploaded later, whenever the server can accept it. </para>
/// </summary>
public readonly partial record struct ImageTag( Guid   ID,
												string FileName )
{
	public const string PREFIX = "image:";

	public const int MAX_FILE_NAME_LENGTH = 255;

	public static ImageTag Create( string? fileName ) => new(Guid.CreateVersion7(), SanitizeFileName( fileName ));

	public override string ToString() => $"{PREFIX}{FileName}:{ID:D}";

	/// <summary> Parses a single <c>image:{file-name}:{id}</c> token. </summary>
	public static bool TryParse( [NotNullWhen( true )] string? value, out ImageTag tag )
	{
		tag = default;
		if ( string.IsNullOrWhiteSpace( value ) ) { return false; }

		Match match = TagPattern().Match( value );
		if ( !match.Success    ||
			 match.Index  != 0 ||
			 match.Length != value.Trim().Length ) { return false; }

		if ( !Guid.TryParse( match.Groups["id"].ValueSpan, out Guid id ) ) { return false; }

		tag = new ImageTag( id, match.Groups["name"].Value );
		return true;
	}

	/// <summary>
	///     Extracts every image tag from a <c>log.tags</c> attribute value, whatever its container:
	///     a single string, a delimited string, or a JSON array. File names may themselves contain <c>:</c>.
	/// </summary>
	public static List<ImageTag> ParseAll( string? value )
	{
		List<ImageTag> tags = [ ];
		if ( string.IsNullOrWhiteSpace( value ) ) { return tags; }

		foreach ( Match match in TagPattern().Matches( value ) )
		{
			if ( !Guid.TryParse( match.Groups["id"].ValueSpan, out Guid id ) ) { continue; }

			ImageTag tag = new(id, match.Groups["name"].Value);
			if ( !tags.Contains( tag ) ) { tags.Add( tag ); }
		}

		return tags;
	}

	/// <summary> Keeps only the leaf name; strips path separators, quotes, control characters and delimiters used by the tag containers. </summary>
	public static string SanitizeFileName( string? fileName )
	{
		if ( string.IsNullOrWhiteSpace( fileName ) ) { return "image"; }

		ReadOnlySpan<char> name      = fileName.AsSpan().Trim();
		int                separator = name.LastIndexOfAny( '/', '\\' );
		if ( separator >= 0 ) { name = name[( separator + 1 )..]; }

		Span<char> buffer = stackalloc char[Math.Min( name.Length, MAX_FILE_NAME_LENGTH )];
		int        length = 0;

		foreach ( char c in name )
		{
			if ( length == buffer.Length ) { break; }

			buffer[length++] = char.IsControl( c ) || c is '"' or ',' or ';' or '[' or ']'
								   ? '_'
								   : c;
		}

		return length == 0
				   ? "image"
				   : new string( buffer[..length] );
	}

	// name: anything that is not a container delimiter (non-greedy, so it may contain ':'); id: a GUID with or without dashes.
	[GeneratedRegex( """image:(?<name>[^"',;\[\]\r\n]+?):(?<id>[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12})(?![0-9a-fA-F])""", RegexOptions.CultureInvariant )]
	private static partial Regex TagPattern();
}

/// <summary> Well-known log attribute keys. </summary>
public static class LogTags
{
	/// <summary> Log attribute holding one or more <see cref="ImageTag"/> values (string or string[]). </summary>
	public const string ATTRIBUTE_KEY = "log.tags";

	/// <summary> Header carrying the original file name on image uploads (URL-encoded). </summary>
	public const string FILE_NAME_HEADER = "X-File-Name";

	/// <summary> Optional header with the base64 SHA-256 of the image body; the server rejects mismatches with 400. </summary>
	public const string SHA256_HEADER = "X-Content-SHA256";

	/// <summary> Image upload route (<c>PUT</c> / <c>HEAD</c>), relative to the OTLP/HTTP base address. </summary>
	public const string IMAGE_ROUTE = "/v1/images";

	public static string ImagePath( Guid id ) => $"{IMAGE_ROUTE}/{id:D}";
}
