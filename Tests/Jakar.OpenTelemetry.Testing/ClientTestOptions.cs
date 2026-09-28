using Jakar.OpenTelemetry.Api.Client;
using Microsoft.Extensions.Options;

namespace Jakar.OpenTelemetry.Testing;

public static class ClientTestOptions
{
	public const string API_KEY = "test-api-key";

	public static readonly Uri Endpoint = new("https://telemetry.test/");

	/// <summary> Valid options with durable state under <paramref name="storageDirectory"/> and short intervals suited to tests. </summary>
	public static JakarTelemetryOptions Create( string storageDirectory, Action<JakarTelemetryOptions>? configure = null )
	{
		JakarTelemetryOptions options = new()
											{
												Endpoint         = Endpoint,
												ApiKey           = API_KEY,
												ServiceName      = "client-tests",
												ServiceVersion   = "1.2.3",
												StorageDirectory = storageDirectory,
												ExportInterval   = TimeSpan.FromMilliseconds( 200 ),
												ExportTimeout    = TimeSpan.FromSeconds( 5 ),
												FlushTimeout     = TimeSpan.FromSeconds( 2 )
											};

		options.Images.PollInterval      = TimeSpan.FromMilliseconds( 100 );
		options.Images.InitialRetryDelay = TimeSpan.FromSeconds( 1 );
		options.Images.MaxRetryDelay     = TimeSpan.FromMinutes( 5 );
		configure?.Invoke( options );
		return options;
	}

	public static IOptions<JakarTelemetryOptions> Wrap( JakarTelemetryOptions options ) => Options.Create( options );
}
