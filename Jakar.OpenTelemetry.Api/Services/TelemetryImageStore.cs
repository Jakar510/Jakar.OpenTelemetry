using Npgsql;
using NpgsqlTypes;

namespace Jakar.OpenTelemetry.Api.Services;

public enum ImageSaveResult
{
	Created,
	AlreadyExists,
	Conflict
}

public readonly record struct StoredImageInfo( Guid           ID,
											   string         FileName,
											   string         ContentType,
											   long           Length,
											   DateTimeOffset ReceivedAtUtc );

/// <summary> PostgreSQL storage for images referenced by log records (<c>"Images"</c> table, see <see cref="Data.TelemetrySchema"/>). </summary>
public sealed class TelemetryImageStore( NpgsqlDataSource dataSource )
{
	/// <summary> Only raster formats a browser renders without script execution; SVG is deliberately excluded. </summary>
	public static readonly string[] AllowedContentTypes = [ "image/png", "image/jpeg", "image/webp", "image/gif", "image/bmp" ];

	private const string INSERT_SQL = """
									  INSERT INTO "Images" ("ID", "ReceivedAtUtc", "FileName", "ContentType", "Length", "Sha256", "Data")
									  VALUES (@id, @receivedAt, @fileName, @contentType, @length, @sha256, @data)
									  ON CONFLICT ("ID") DO NOTHING
									  """;


	/// <summary> Idempotent: re-uploading the same id with the same content succeeds, a different body for an existing id is a conflict. </summary>
	public async Task<ImageSaveResult> SaveAsync( Guid id, string fileName, string contentType, ReadOnlyMemory<byte> data, byte[] sha256, CancellationToken token )
	{
		await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync( token );

		await using ( NpgsqlCommand insert = new(INSERT_SQL, connection) )
		{
			insert.Parameters.Add( new NpgsqlParameter<Guid>( "id", NpgsqlDbType.Uuid ) { TypedValue                          = id } );
			insert.Parameters.Add( new NpgsqlParameter<DateTimeOffset>( "receivedAt", NpgsqlDbType.TimestampTz ) { TypedValue = DateTimeOffset.UtcNow } );
			insert.Parameters.Add( new NpgsqlParameter<string>( "fileName",    NpgsqlDbType.Text ) { TypedValue               = fileName } );
			insert.Parameters.Add( new NpgsqlParameter<string>( "contentType", NpgsqlDbType.Text ) { TypedValue               = contentType } );
			insert.Parameters.Add( new NpgsqlParameter<long>( "length", NpgsqlDbType.Bigint ) { TypedValue                    = data.Length } );
			insert.Parameters.Add( new NpgsqlParameter<byte[]>( "sha256", NpgsqlDbType.Bytea ) { TypedValue                   = sha256 } );
			insert.Parameters.Add( new NpgsqlParameter<ReadOnlyMemory<byte>>( "data", NpgsqlDbType.Bytea ) { TypedValue       = data } );

			if ( await insert.ExecuteNonQueryAsync( token ) == 1 ) { return ImageSaveResult.Created; }
		}

		await using NpgsqlCommand existing = new("""SELECT "Sha256" FROM "Images" WHERE "ID" = @id""", connection);
		existing.Parameters.Add( new NpgsqlParameter<Guid>( "id", NpgsqlDbType.Uuid ) { TypedValue = id } );

		return await existing.ExecuteScalarAsync( token ) is byte[] storedHash && storedHash.AsSpan().SequenceEqual( sha256 )
				   ? ImageSaveResult.AlreadyExists
				   : ImageSaveResult.Conflict;
	}

	public async Task<bool> ExistsAsync( Guid id, CancellationToken token )
	{
		await using NpgsqlCommand command = dataSource.CreateCommand( """SELECT EXISTS (SELECT 1 FROM "Images" WHERE "ID" = @id)""" );
		command.Parameters.Add( new NpgsqlParameter<Guid>( "id", NpgsqlDbType.Uuid ) { TypedValue = id } );
		return await command.ExecuteScalarAsync( token ) is true;
	}

	public async Task<(string ContentType, byte[] Data)?> GetAsync( Guid id, CancellationToken token )
	{
		await using NpgsqlCommand command = dataSource.CreateCommand( """SELECT "ContentType", "Data" FROM "Images" WHERE "ID" = @id""" );
		command.Parameters.Add( new NpgsqlParameter<Guid>( "id", NpgsqlDbType.Uuid ) { TypedValue = id } );

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync( System.Data.CommandBehavior.SingleRow | System.Data.CommandBehavior.SequentialAccess, token );
		if ( !await reader.ReadAsync( token ) ) { return null; }

		string contentType = reader.GetString( 0 );
		byte[] data        = await reader.GetFieldValueAsync<byte[]>( 1, token );
		return ( contentType, data );
	}

	/// <summary> Metadata (without the image bytes) for the given ids that have been uploaded. </summary>
	public async Task<Dictionary<Guid, StoredImageInfo>> GetInfoAsync( IReadOnlyCollection<Guid> ids, CancellationToken token )
	{
		Dictionary<Guid, StoredImageInfo> result = new(ids.Count);
		if ( ids.Count == 0 ) { return result; }

		await using NpgsqlCommand command = dataSource.CreateCommand( """SELECT "ID", "FileName", "ContentType", "Length", "ReceivedAtUtc" FROM "Images" WHERE "ID" = ANY (@ids)""" );
		command.Parameters.Add( new NpgsqlParameter<Guid[]>( "ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid ) { TypedValue = ids.ToArray() } );

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync( token );

		while ( await reader.ReadAsync( token ) )
		{
			StoredImageInfo info = new(reader.GetGuid( 0 ), reader.GetString( 1 ), reader.GetString( 2 ), reader.GetInt64( 3 ), reader.GetFieldValue<DateTimeOffset>( 4 ));
			result[info.ID] = info;
		}

		return result;
	}

	private static ReadOnlySpan<byte> PngSignature  => [ 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A ];
	private static ReadOnlySpan<byte> JpegSignature => [ 0xFF, 0xD8, 0xFF ];

	/// <summary> Verifies the leading bytes match the declared type so a mislabeled upload can never be served with an image content type. </summary>
	public static bool MatchesSignature( string contentType, ReadOnlySpan<byte> data ) => contentType switch
																							  {
																								  "image/png"  => data.StartsWith( PngSignature ),
																								  "image/jpeg" => data.StartsWith( JpegSignature ),
																								  "image/gif"  => data.StartsWith( "GIF87a"u8 ) || data.StartsWith( "GIF89a"u8 ),
																								  "image/webp" => data.Length >= 12 && data.StartsWith( "RIFF"u8 ) && data[8..12].SequenceEqual( "WEBP"u8 ),
																								  "image/bmp"  => data.StartsWith( "BM"u8 ),
																								  _            => false
																							  };
}
