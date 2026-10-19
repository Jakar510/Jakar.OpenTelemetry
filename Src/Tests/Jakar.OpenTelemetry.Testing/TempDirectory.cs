namespace Jakar.OpenTelemetry.Testing;

/// <summary> A unique, empty directory under the system temp folder, deleted (recursively, best effort) on dispose. </summary>
public sealed class TempDirectory : IDisposable
{
	public TempDirectory()
	{
		Path = System.IO.Path.Combine( System.IO.Path.GetTempPath(), "jakar-otel-tests", Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( Path );
	}


	public string Path { get; }


	public string Combine( params string[] parts ) => System.IO.Path.Combine( [ Path, .. parts ] );

	public void Dispose()
	{
		for ( int attempt = 0; attempt < 5; attempt++ )
		{
			try
			{
				if ( Directory.Exists( Path ) ) { Directory.Delete( Path, recursive: true ); }

				return;
			}
			catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException ) { Thread.Sleep( 50 ); }
		}
	}
}
