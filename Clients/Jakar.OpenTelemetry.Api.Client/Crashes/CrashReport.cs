using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jakar.OpenTelemetry.Api.Client.Crashes;

/// <summary> A crash captured at the moment it happened and persisted synchronously, so it can be reported on the next launch even if the export never got out. </summary>
public sealed record CrashReport( Guid                        ID,
								  DateTimeOffset              TimestampUtc,
								  string                      SessionID,
								  string                      Source,
								  bool                        IsTerminating,
								  string                      ExceptionType,
								  string?                     Message,
								  string?                     StackTrace,
								  Dictionary<string, string>? Details );

/// <summary> Session bookkeeping used to detect a previous session that ended without a clean shutdown and without a crash report. </summary>
public sealed record SessionMarker( string         SessionID,
									DateTimeOffset StartedUtc,
									int            ProcessID,
									string?        ServiceVersion );

public interface ICrashReportStore
{
	/// <summary> Must be synchronous and durable: it runs on a crashing thread that may be killed right after. </summary>
	void Save( CrashReport report );

	IReadOnlyList<CrashReport> LoadAll();

	void Delete( Guid id );

	/// <summary> Reads the marker of the previous session, if it did not end cleanly. </summary>
	SessionMarker? ReadUncleanSession();

	void BeginSession( SessionMarker marker );

	/// <summary> Marks the current session as cleanly stopped (or backgrounded on mobile, where the OS may kill the app without notice). </summary>
	void EndSession();

	/// <summary> Re-arms the current session after <see cref="EndSession"/> when a backgrounded app returns to the foreground. </summary>
	void ResumeSession();
}

/// <summary> Stores crash reports and the session marker as small JSON files under <c>{StorageDirectory}/crashes</c>, written through to disk. </summary>
public sealed class FileSystemCrashReportStore : ICrashReportStore
{
	private readonly string                              _directory;
	private readonly string                              _sessionPath;
	private readonly ILogger<FileSystemCrashReportStore> _logger;
	private readonly Lock                                _lock = new();
	private          SessionMarker?                      _current;


	public FileSystemCrashReportStore( IOptions<JakarTelemetryOptions> options, ILogger<FileSystemCrashReportStore> logger )
	{
		_logger      = logger;
		_directory   = Path.Combine( options.Value.ResolveStorageDirectory(), "crashes" );
		_sessionPath = Path.Combine( options.Value.ResolveStorageDirectory(), "session.json" );
	}


	public void Save( CrashReport report )
	{
		lock ( _lock )
		{
			Directory.CreateDirectory( _directory );
			WriteThrough( Path.Combine( _directory, report.ID.ToString( "N" ) + ".json" ), JsonSerializer.SerializeToUtf8Bytes( report, ClientJsonContext.Default.CrashReport ) );
		}
	}

	public IReadOnlyList<CrashReport> LoadAll()
	{
		lock ( _lock )
		{
			if ( !Directory.Exists( _directory ) ) { return [ ]; }

			List<CrashReport> reports = [ ];

			foreach ( string path in Directory.EnumerateFiles( _directory, "*.json" ) )
			{
				try
				{
					if ( JsonSerializer.Deserialize( File.ReadAllBytes( path ), ClientJsonContext.Default.CrashReport ) is { } report ) { reports.Add( report ); }
				}
				catch ( Exception e ) when ( e is JsonException or IOException )
				{
					_logger.LogWarning( e, "Discarding unreadable crash report {Path}", path );
					TryDelete( path );
				}
			}

			return reports.OrderBy( static x => x.TimestampUtc ).ToArray();
		}
	}

	public void Delete( Guid id )
	{
		lock ( _lock ) { TryDelete( Path.Combine( _directory, id.ToString( "N" ) + ".json" ) ); }
	}

	public SessionMarker? ReadUncleanSession()
	{
		lock ( _lock )
		{
			try
			{
				return File.Exists( _sessionPath )
						   ? JsonSerializer.Deserialize( File.ReadAllBytes( _sessionPath ), ClientJsonContext.Default.SessionMarker )
						   : null;
			}
			catch ( Exception e ) when ( e is JsonException or IOException )
			{
				_logger.LogDebug( e, "Unreadable session marker" );
				return null;
			}
		}
	}

	public void BeginSession( SessionMarker marker )
	{
		lock ( _lock )
		{
			_current = marker;
			Directory.CreateDirectory( Path.GetDirectoryName( _sessionPath )! );
			WriteThrough( _sessionPath, JsonSerializer.SerializeToUtf8Bytes( marker, ClientJsonContext.Default.SessionMarker ) );
		}
	}

	public void EndSession()
	{
		lock ( _lock ) { TryDelete( _sessionPath ); }
	}

	public void ResumeSession()
	{
		if ( _current is { } marker ) { BeginSession( marker ); }
	}

	private static void WriteThrough( string path, byte[] data )
	{
		string temp = path + ".tmp";

		using ( FileStream stream = new(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough) )
		{
			stream.Write( data );
			stream.Flush( flushToDisk: true );
		}

		File.Move( temp, path, overwrite: true );
	}

	private void TryDelete( string path )
	{
		try { File.Delete( path ); }
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException ) { _logger.LogDebug( e, "Could not delete {Path}", path ); }
	}
}

/// <summary> For hosts without durable local storage (the browser): crashes are only logged, never persisted. </summary>
public sealed class NullCrashReportStore : ICrashReportStore
{
	public void                       Save( CrashReport report )           { }
	public IReadOnlyList<CrashReport> LoadAll()                            => [ ];
	public void                       Delete( Guid id )                    { }
	public SessionMarker?             ReadUncleanSession()                 => null;
	public void                       BeginSession( SessionMarker marker ) { }
	public void                       EndSession()                         { }
	public void                       ResumeSession()                      { }
}
