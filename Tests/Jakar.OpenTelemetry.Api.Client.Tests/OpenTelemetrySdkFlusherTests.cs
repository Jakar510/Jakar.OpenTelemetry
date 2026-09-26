using System.Diagnostics;
using global::OpenTelemetry;
using global::OpenTelemetry.Logs;
using global::OpenTelemetry.Metrics;
using global::OpenTelemetry.Trace;
using Microsoft.Extensions.DependencyInjection;

namespace Jakar.OpenTelemetry.Api.Client.Tests;

[TestFixture]
public sealed class OpenTelemetrySdkFlusherTests
{
	[Test]
	public async Task Flush_NoProviders_Succeeds()
	{
		await using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
		OpenTelemetrySdkFlusher     flusher  = new(services);

		await Assert.MultipleAsync( async () =>
						 {
							 Assert.That( flusher.Flush( TimeSpan.FromSeconds( 1 ) ),                  Is.True );
							 Assert.That( await flusher.FlushAsync( TimeSpan.FromSeconds( 1 ) ),        Is.True );
							 Assert.That( flusher.Flush( TimeSpan.Zero ),                               Is.True, "a zero timeout is clamped to 1 ms" );
							 Assert.That( flusher.Flush( TimeSpan.FromDays( 365 ) ),                    Is.True, "a huge timeout is clamped to int.MaxValue ms" );
						 } );
	}

	[Test]
	public async Task Flush_AllProvidersFlush_ReturnsTrue()
	{
		ServiceCollection collection = new();
		collection.AddLogging();
		collection.AddOpenTelemetry().WithTracing( static _ => { } ).WithMetrics( static _ => { } ).WithLogging();
		await using ServiceProvider services = collection.BuildServiceProvider();

		Assert.That( new OpenTelemetrySdkFlusher( services ).Flush( TimeSpan.FromSeconds( 5 ) ), Is.True );
	}

	[Test]
	public async Task Flush_ProcessorFailsToFlush_ReturnsFalse()
	{
		ServiceCollection collection = new();
		collection.AddLogging();
		collection.AddOpenTelemetry().WithTracing( static tracing => tracing.AddProcessor( new FailingProcessor() ) ).WithMetrics( static _ => { } ).WithLogging();
		await using ServiceProvider services = collection.BuildServiceProvider();
		_ = services.GetRequiredService<TracerProvider>();
		_ = services.GetRequiredService<MeterProvider>();
		_ = services.GetRequiredService<LoggerProvider>();

		Assert.That( await new OpenTelemetrySdkFlusher( services ).FlushAsync( TimeSpan.FromSeconds( 5 ) ), Is.False );
	}


	private sealed class FailingProcessor : BaseProcessor<Activity>
	{
		protected override bool OnForceFlush( int timeoutMilliseconds ) => false;
	}
}
