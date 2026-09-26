using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jakar.OpenTelemetry.Testing;

/// <summary>
///     Jakar.OpenTelemetry.Api hosted in-process against a test database, with known credentials. Uses the in-memory test server unless
///     <see cref="WebApplicationFactory{TEntryPoint}.UseKestrel()"/> is called (real sockets: OTLP exporters, browsers and devices).
/// </summary>
public sealed class TestApi( string connectionString ) : WebApplicationFactory<Program>
{
	public const string API_KEY         = "integration-api-key";
	public const string ADMIN_USER      = "admin";
	public const string ADMIN_PASSWORD  = "admin-password";
	public const string VIEWER_USER     = "viewer";
	public const string VIEWER_PASSWORD = "viewer-password";
	public const string GUEST_USER      = "guest";
	public const string GUEST_PASSWORD  = "guest-password";


	public string ConnectionString { get; } = connectionString;

	/// <summary> Extra configuration, applied last. </summary>
	public Dictionary<string, string?> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);

	/// <summary> Hosting environment; <see cref="StartRealServer"/> switches to Development so static web assets (blazor.web.js, scoped CSS) are served from the build output. </summary>
	public string EnvironmentName { get; set; } = "Testing";

	/// <summary> Remote address reported for in-memory requests (the test server has none, which the dashboard IP allow list would reject). </summary>
	public IPAddress? RemoteIpAddress { get; set; } = IPAddress.Loopback;

	/// <summary> Base address of the HTTP/1.1 + HTTP/2 endpoint once <see cref="StartRealServer"/> has run (OTLP/HTTP, images, dashboard). </summary>
	public Uri HttpAddress { get; private set; } = null!;

	/// <summary> Base address of the HTTP/2-only (cleartext gRPC) endpoint once <see cref="StartRealServer"/> has run. </summary>
	public Uri GrpcAddress { get; private set; } = null!;


	/// <summary>
	///     Starts the API on real sockets: one HTTP/1.1 + HTTP/2 endpoint and one HTTP/2-only endpoint (gRPC without TLS needs prior-knowledge HTTP/2).
	///     <paramref name="host"/> is the interface to bind; use <c>0.0.0.0</c> when devices or emulators must reach it. <paramref name="httpPort"/> pins the
	///     HTTP port for clients that cannot be reconfigured (the MAUI sample).
	/// </summary>
	public TestApi StartRealServer( string host = "127.0.0.1", int? httpPort = null )
	{
		int http = httpPort ?? FreePort();
		int grpc = FreePort();

		// The appsettings "Urls" would bind the developer ports; the endpoints below replace them.
		Settings["Urls:0"]                          = string.Empty;
		Settings["Urls:1"]                          = string.Empty;
		Settings["Kestrel:Endpoints:Http:Url"]      = $"http://{host}:{http}";
		Settings["Kestrel:Endpoints:Grpc:Url"]      = $"http://{host}:{grpc}";
		Settings["Kestrel:Endpoints:Grpc:Protocols"] = "Http2";

		EnvironmentName = "Development";
		UseKestrel();
		StartServer();

		string reachable = host is "0.0.0.0" or "::" ? "127.0.0.1" : host;
		HttpAddress = new Uri( $"http://{reachable}:{http}/" );
		GrpcAddress = new Uri( $"http://{reachable}:{grpc}/" );
		return this;
	}

	private static int FreePort()
	{
		using System.Net.Sockets.TcpListener listener = new(IPAddress.Loopback, 0);
		listener.Start();
		return ( (IPEndPoint)listener.LocalEndpoint ).Port;
	}


	protected override void ConfigureWebHost( IWebHostBuilder builder )
	{
		builder.UseEnvironment( EnvironmentName );

		builder.ConfigureAppConfiguration( configuration =>
										   {
											   Dictionary<string, string?> settings = new(StringComparer.OrdinalIgnoreCase)
																					  {
																						  ["ConnectionStrings:Telemetry"]            = ConnectionString,
																						  ["OtlpIngest:ApiKey"]                      = API_KEY,
																						  ["OtlpIngest:RetryAfterSeconds"]           = "2",
																						  ["DashboardAuth:Users:0:Username"]         = ADMIN_USER,
																						  ["DashboardAuth:Users:0:Password"]         = ADMIN_PASSWORD,
																						  ["DashboardAuth:Users:0:Roles:0"]          = "Admin",
																						  ["DashboardAuth:Users:0:Roles:1"]          = "Viewer",
																						  ["DashboardAuth:Users:1:Username"]         = VIEWER_USER,
																						  ["DashboardAuth:Users:1:Password"]         = VIEWER_PASSWORD,
																						  ["DashboardAuth:Users:2:Username"]         = GUEST_USER,
																						  ["DashboardAuth:Users:2:Password"]         = GUEST_PASSWORD,
																						  ["DashboardAuth:Users:2:Roles:0"]          = "Guest",
																						  ["DashboardIpWhitelist:AllowedIPs:0"]      = "127.0.0.1",
																						  ["DashboardIpWhitelist:AllowedIPs:1"]      = "::1",
																						  ["TelemetryRetention:RetentionDays"]       = "0",
																						  ["Logging:LogLevel:Default"]               = "Warning",
																						  ["Logging:LogLevel:Microsoft.AspNetCore"]  = "Warning"
																					  };

											   foreach ( ( string key, string? value ) in Settings ) { settings[key] = value; }

											   configuration.AddInMemoryCollection( settings );
										   } );

		builder.ConfigureServices( services => services.AddTransient<IStartupFilter>( _ => new RemoteIpStartupFilter( this ) ) );
	}


	/// <summary> Runs first in the pipeline and assigns <see cref="RemoteIpAddress"/> when the server did not set one. </summary>
	private sealed class RemoteIpStartupFilter( TestApi api ) : IStartupFilter
	{
		public Action<IApplicationBuilder> Configure( Action<IApplicationBuilder> next ) => app =>
																							{
																								app.Use( ( context, nextMiddleware ) =>
																										 {
																											 context.Connection.RemoteIpAddress ??= api.RemoteIpAddress;
																											 return nextMiddleware( context );
																										 } );
																								next( app );
																							};
	}
}
