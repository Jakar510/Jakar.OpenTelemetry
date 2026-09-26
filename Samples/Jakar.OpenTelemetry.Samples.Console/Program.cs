using System.Diagnostics;
using Jakar.OpenTelemetry.Api.Client;
using Jakar.OpenTelemetry.Api.Client.Logging;
using Jakar.OpenTelemetry.Contracts;
using Jakar.OpenTelemetry.Source;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Sample for Jakar.OpenTelemetry.Api.Client (any .NET host). Configuration comes from the "JakarTelemetry" section of appsettings.json.
//
//   dotnet run                 logs, a traced operation and an error with a screenshot, then flushes on demand and exits cleanly
//   dotnet run -- crash        dies from an unhandled exception on a background thread; the crash report is persisted and replayed on the next run
//   dotnet run -- hang         keeps running so it can be killed; the next run reports the abnormal termination

const string ACTIVITY_SOURCE_NAME = "Jakar.OpenTelemetry.Samples.Console";
ActivitySource activitySource = new(ACTIVITY_SOURCE_NAME);
string         mode           = args.FirstOrDefault() ?? "run";

HostApplicationBuilder builder = Host.CreateApplicationBuilder( args );
builder.AddJakarOpenTelemetry( options => options.ActivitySources.Add( ACTIVITY_SOURCE_NAME ) );

using IHost host = builder.Build();
await host.StartAsync();

ILogger         logger    = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger( "Jakar.OpenTelemetry.Samples.Console" );
IJakarTelemetry telemetry = host.Services.GetRequiredService<IJakarTelemetry>();

logger.LogInformation( "Console sample started in {Mode} mode", mode );

using ( Activity? activity = activitySource.StartActivity( "sample.operation" ) )
{
	activity?.SetTag( "sample.mode", mode );
	await Task.Delay( 150 );
	logger.LogInformation( "Traced operation finished" );
}

// The image id is generated here, so the log goes out right away and the image uploads whenever the server can accept it.
ImageTag screenshot = await telemetry.AttachImageAsync( SamplePng.Create( 800, 450, Random.Shared.Next( 1, 9 ) ), "console-error.png" );

try { throw new InvalidOperationException( "Sample handled error" ); }
catch ( InvalidOperationException e ) { logger.LogErrorWithImages( e, [ screenshot ], "Sample error with screenshot {ScreenshotId}", screenshot.ID ); }

switch ( mode )
{
	case "crash":
		Thread thread = new(static () => throw new InvalidProgramException( "Sample unhandled exception on a background thread" ));
		thread.Start();
		thread.Join();
		break;

	case "hang":
		Console.WriteLine( "Running until killed; the next run reports the abnormal termination" );
		await Task.Delay( Timeout.Infinite );
		break;
}

bool flushed = await telemetry.FlushAsync();
Console.WriteLine( flushed ? "Telemetry flushed" : "Flush timed out; telemetry stays queued on disk and is retried on the next run" );

await host.StopAsync();
