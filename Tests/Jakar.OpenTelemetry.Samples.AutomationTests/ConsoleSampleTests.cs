namespace Jakar.OpenTelemetry.Samples.AutomationTests;

/// <summary> <c>Jakar.OpenTelemetry.Samples.Console</c>: the generic client in a plain .NET host, in each of its modes. </summary>
[TestFixture]
public sealed class ConsoleSampleTests : LiveApiFixture
{
	private const string SAMPLE = "Jakar.OpenTelemetry.Samples.Console";

	private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds( 90 );


	private static (string Name, object Value) Service( string name ) => ( "service", name );


	[Test] public async Task Run_ExportsLogsTraceAndErrorWithScreenshot_ThenExitsCleanly()
	{
		string service = $"console-run-{Guid.NewGuid():N}";

		await using SampleProcess sample   = SampleProcess.StartSample( SAMPLE, ClientEnvironment( service ) );
		int                       exitCode = await sample.WaitForExitAsync( RunTimeout );

		Assert.Multiple( () =>
						 {
							 Assert.That( exitCode,      Is.Zero, sample.Output );
							 Assert.That( sample.Output, Does.Contain( "Telemetry flushed" ) );
						 } );

		await Telemetry.WaitForAsync( "Logs",   """ "ServiceName" = @service AND "Body" = 'Console sample started in run mode'""",                                                            parameters: Service( service ) );
		await Telemetry.WaitForAsync( "Spans",  """ "ServiceName" = @service AND "Name" = 'sample.operation' AND "AttributesJson"->>'sample.mode' = 'run'""",                                 parameters: Service( service ) );
		await Telemetry.WaitForAsync( "Logs",   """ "ServiceName" = @service AND "Body" LIKE 'Sample error with screenshot %' AND "SeverityNumber" = 17 AND "AttributesJson" ? 'log.tags'""", parameters: Service( service ) );
		await Telemetry.WaitForAsync( "Images", """ "FileName" = 'console-error.png' AND "ContentType" = 'image/png'""" );

		Assert.That( await Telemetry.CountAsync( "Logs", """ "ServiceName" = @service AND "Body" = 'Traced operation finished' AND "TraceId" IS NOT NULL""", Service( service ) ), Is.EqualTo( 1 ), "logs inside the operation carry its trace id" );
	}

	[Test] public async Task Crash_IsReportedImmediately_AndNotAsAbnormalTerminationNextTime()
	{
		string service = $"console-crash-{Guid.NewGuid():N}";

		await using ( SampleProcess crashing = SampleProcess.StartSample( SAMPLE, ClientEnvironment( service ), "crash" ) ) { Assert.That( await crashing.WaitForExitAsync( RunTimeout ), Is.Not.Zero, "an unhandled exception terminates the process" ); }

		await Telemetry.WaitForAsync( "Logs", """ "ServiceName" = @service AND "SeverityNumber" = 21 AND "AttributesJson"->>'crash.source' = 'appdomain.unhandled_exception' AND "AttributesJson"->>'exception.type' = 'System.InvalidProgramException'""", parameters: Service( service ) );

		await using ( SampleProcess next = SampleProcess.StartSample( SAMPLE, ClientEnvironment( service ) ) ) { Assert.That( await next.WaitForExitAsync( RunTimeout ), Is.Zero, next.Output ); }

		await Telemetry.WaitForAsync( "Logs", """ "ServiceName" = @service AND "Body" = 'Console sample started in run mode'""", parameters: Service( service ) );
		Assert.That( await Telemetry.CountAsync( "Logs", """ "ServiceName" = @service AND "Body" LIKE '%without a clean shutdown%'""", Service( service ) ), Is.Zero, "the crash already explained the termination" );
	}

	[Test] public async Task CrashWhileOffline_IsReplayedOnTheNextRun()
	{
		string service = $"console-offline-{Guid.NewGuid():N}";
		Uri    offline = new($"http://127.0.0.1:{Ports.Free()}/");

		Dictionary<string, string?> environment = ClientEnvironment( service, offline );
		environment["JakarTelemetry__ImageEndpoint"] = offline.ToString();
		environment["JakarTelemetry__FlushTimeout"]  = "00:00:01";

		await using ( SampleProcess crashing = SampleProcess.StartSample( SAMPLE, environment, "crash" ) ) { await crashing.WaitForExitAsync( RunTimeout ); }

		Assert.That( Directory.GetFiles( Path.Combine( Storage.Path, "crashes" ), "*.json" ), Has.Length.EqualTo( 1 ), "the crash report was persisted before the process died" );

		await using ( SampleProcess next = SampleProcess.StartSample( SAMPLE, ClientEnvironment( service ) ) ) { Assert.That( await next.WaitForExitAsync( RunTimeout ), Is.Zero, next.Output ); }

		await Telemetry.WaitForAsync( "Logs",   """ "ServiceName" = @service AND "Body" LIKE 'Crash in previous session%' AND "AttributesJson"->>'exception.type' = 'System.InvalidProgramException' AND "AttributesJson"->>'crash.previous_session' = 'true'""", parameters: Service( service ) );
		await Telemetry.WaitForAsync( "Images", """ "FileName" = 'console-error.png'""",                                                                                                                                                                          minimum: 2 );

		Assert.That( Directory.GetFiles( Path.Combine( Storage.Path, "crashes" ), "*.json" ), Is.Empty, "replayed reports are deleted" );
	}

	[Test] public async Task KilledProcess_IsReportedAsAbnormalTerminationOnTheNextRun()
	{
		string service = $"console-hang-{Guid.NewGuid():N}";

		await using ( SampleProcess hanging = SampleProcess.StartSample( SAMPLE, ClientEnvironment( service ), "hang" ) )
		{
			await Telemetry.WaitForAsync( "Logs", """ "ServiceName" = @service AND "Body" = 'Console sample started in hang mode'""", parameters: Service( service ) );
			hanging.Kill();
		}

		await using ( SampleProcess next = SampleProcess.StartSample( SAMPLE, ClientEnvironment( service ) ) ) { Assert.That( await next.WaitForExitAsync( RunTimeout ), Is.Zero, next.Output ); }

		await Telemetry.WaitForAsync( "Logs", """ "ServiceName" = @service AND "SeverityNumber" = 17 AND "Body" LIKE 'Previous session % ended without a clean shutdown or crash report%'""", parameters: Service( service ) );
	}
}
