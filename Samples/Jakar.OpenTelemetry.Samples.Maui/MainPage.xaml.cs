using System.Diagnostics;
using Jakar.OpenTelemetry.Api.Client;
using Jakar.OpenTelemetry.Api.Client.Logging;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.Extensions.Logging;

namespace Jakar.OpenTelemetry.Samples.Maui;

public partial class MainPage : ContentPage
{
	public const string ACTIVITY_SOURCE_NAME = "Jakar.OpenTelemetry.Samples.Maui";

	private static readonly ActivitySource _s_activitySource = new(ACTIVITY_SOURCE_NAME);

	private readonly ITelemetryClient   _telemetry;
	private readonly ILogger<MainPage> _logger;

	public MainPage()
	{
		InitializeComponent();

		// Shell creates pages through a DataTemplate, so resolve services from the app container.
		IServiceProvider services = IPlatformApplication.Current!.Services;
		_telemetry = services.GetRequiredService<ITelemetryClient>();
		_logger    = services.GetRequiredService<ILogger<MainPage>>();

		NativeCrashButton.IsVisible = DeviceInfo.Platform == DevicePlatform.Android || DeviceInfo.Platform == DevicePlatform.iOS || DeviceInfo.Platform == DevicePlatform.MacCatalyst;
	}

	private async void OnLogErrorWithScreenshot( object? sender, EventArgs e )
	{
		try { throw new InvalidOperationException( "Sample handled error in the MAUI app" ); }
		catch ( InvalidOperationException exception )
		{
			ImageTag? screenshot = await _logger.LogErrorWithScreenshotAsync( _telemetry, exception, "Sample error from {Page}", nameof(MainPage) );
			StatusLabel.Text = screenshot is { } tag
								   ? $"Logged error with {tag}"
								   : "Logged error (screenshots not supported on this platform)";
		}
	}

	/// <summary>
	///     A simulated checkout: one root span with nested and parallel child spans of varying durations. Child spans add context to the root span (tags) and
	///     remove it again; a root span's tags are exported when it ends, so each change is also recorded as a <c>context.added</c> / <c>context.removed</c> event
	///     on the root, naming the span that made it. About one checkout in five has its payment declined (an error span).
	/// </summary>
	private async void OnTracedOperation( object? sender, EventArgs e )
	{
		using Activity? root = _s_activitySource.StartActivity( "sample.checkout" );
		root?.SetTag( "sample.platform", DeviceInfo.Platform.ToString() );
		root?.SetTag( "order.id",        Guid.CreateVersion7().ToString( "N" )[..12] );
		root?.SetTag( "order.pending_discount", true ); // removed again once pricing has applied it

		// 1. Load the cart: adds the item count and cart value.
		using ( Activity? span = _s_activitySource.StartActivity( "cart.load" ) )
		{
			await Work( 40, 90 );
			int    items = Random.Shared.Next( 1, 6 );
			double value = Math.Round( items * Random.Shared.Next( 5, 60 ) + 0.99, 2 );
			span?.SetTag( "cart.items", items );
			AddContext( root, span, "cart.items", items );
			AddContext( root, span, "cart.value", value );
			_logger.LogInformation( "Cart loaded with {Items} items worth {Value}", items, value );
		}

		// 2. Reserve stock: three checks in parallel (overlapping bars), then a hold token on the root.
		using ( Activity? span = _s_activitySource.StartActivity( "inventory.reserve" ) )
		{
			await Task.WhenAll( CheckStockAsync( "sku-1001" ), CheckStockAsync( "sku-2002" ), CheckStockAsync( "sku-3003" ) );
			AddContext( root, span, "inventory.hold_token", Guid.NewGuid().ToString( "N" )[..8] );
		}

		// 3. Price the order; the nested discount step replaces the root's pending flag with the discount.
		using ( Activity? span = _s_activitySource.StartActivity( "pricing.calculate" ) )
		{
			await Work( 60, 140 );

			using ( Activity? discount = _s_activitySource.StartActivity( "pricing.discount" ) )
			{
				await Work( 10, 40 );
				int percent = Random.Shared.Next( 0, 4 ) * 5;
				discount?.SetTag( "discount.percent", percent );
				AddContext( root, discount, "order.discount_percent", percent );
				RemoveContext( root, discount, "order.pending_discount" );
			}

			span?.SetTag( "pricing.currency", "USD" );
		}

		// 4. Authorize the payment (the slowest step); sometimes declined.
		bool declined = Random.Shared.Next( 5 ) == 0;

		using ( Activity? span = _s_activitySource.StartActivity( "payment.authorize", ActivityKind.Client ) )
		{
			span?.SetTag( "payment.method", "card" );
			await Work( 150, 400 );

			if ( declined )
			{
				InvalidOperationException error = new("Sample payment declined");
				span?.AddException( error );
				span?.SetStatus( ActivityStatusCode.Error, error.Message );
				AddContext( root, span, "payment.status", "declined" );
				_logger.LogWarning( "Payment declined for the sample checkout" );
			}
			else { AddContext( root, span, "payment.status", "authorized" ); }
		}

		// 5. Confirm (or release) the order: the hold token is no longer needed either way.
		using ( Activity? span = _s_activitySource.StartActivity( declined ? "order.release" : "order.confirm", ActivityKind.Producer ) )
		{
			await Work( 5, 25 );
			RemoveContext( root, span, "inventory.hold_token" );
		}

		if ( declined ) { root?.SetStatus( ActivityStatusCode.Error, "Payment declined" ); }

		_logger.LogInformation( "Sample checkout finished: {Outcome}", declined ? "declined" : "confirmed" );
		StatusLabel.Text = $"Traced checkout {root?.TraceId} ({( declined ? "payment declined" : "confirmed" )})";

		static Task Work( int minMilliseconds, int maxMilliseconds ) => Task.Delay( Random.Shared.Next( minMilliseconds, maxMilliseconds ) );
	}

	/// <summary> One stock check; three run at once under <c>inventory.reserve</c>. </summary>
	private static async Task CheckStockAsync( string sku )
	{
		using Activity? span = _s_activitySource.StartActivity( "inventory.check", ActivityKind.Client );
		span?.SetTag( "inventory.sku", sku );
		await Task.Delay( Random.Shared.Next( 20, 160 ) );
		span?.SetTag( "inventory.available", Random.Shared.Next( 1, 50 ) );
	}

	/// <summary> A child span adds context to the root span: a tag on the root, and an event saying which span added it. </summary>
	private static void AddContext( Activity? root, Activity? by, string key, object value )
	{
		root?.SetTag( key, value );
		root?.AddEvent( new ActivityEvent( "context.added", tags: new ActivityTagsCollection { ["context.key"] = key, ["context.value"] = value, ["context.by"] = by?.DisplayName } ) );
	}

	/// <summary> A child span removes context from the root span (the tag is gone from the exported root) and records that it did. </summary>
	private static void RemoveContext( Activity? root, Activity? by, string key )
	{
		root?.SetTag( key, null );
		root?.AddEvent( new ActivityEvent( "context.removed", tags: new ActivityTagsCollection { ["context.key"] = key, ["context.by"] = by?.DisplayName } ) );
	}

	private async void OnFlush( object? sender, EventArgs e )
	{
		bool flushed = await _telemetry.FlushAsync();
		StatusLabel.Text = flushed
							   ? "Telemetry flushed"
							   : "Flush timed out, telemetry stays queued";
	}

	private void OnUnobservedTaskException( object? sender, EventArgs e )
	{
		_ = Task.Run( static () => throw new InvalidOperationException( "Sample unobserved task exception" ) );
		Task.Delay( 200 ).ContinueWith( static _ => GC.Collect() ); // unobserved exceptions surface when the faulted task is finalized
		StatusLabel.Text = "Unobserved task exception raised (logged once the task is collected)";
	}

	private void OnCrashUiThread( object? sender, EventArgs e ) => throw new InvalidOperationException( "Sample crash on the UI thread" );

	private void OnCrashBackgroundThread( object? sender, EventArgs e ) => new Thread( static () => throw new InvalidOperationException( "Sample crash on a background thread" ) ).Start();

	private void OnCrashNative( object? sender, EventArgs e )
	{
		#if ANDROID
		// A Java exception escaping a Java thread reaches Thread.DefaultUncaughtExceptionHandler.
		new Java.Lang.Thread( static () => throw new Java.Lang.IllegalStateException( "Sample Java crash" ) ).Start();
		#elif IOS || MACCATALYST
		// A genuine Objective-C throw from native code reaches the uncaught NSException handler.
		ObjectiveCThrow( new Foundation.NSException( "JakarSampleException", "Sample Objective-C crash", null ).Handle );
		#endif
	}

	#if IOS || MACCATALYST
	[System.Runtime.InteropServices.DllImport( "/usr/lib/libobjc.dylib", EntryPoint = "objc_exception_throw" )]
	private static extern void ObjectiveCThrow( IntPtr exception );
	#endif
}
