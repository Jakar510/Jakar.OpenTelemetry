using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Jakar.Extensions;
using Jakar.OpenTelemetry.Api.Client.Crashes;
using Jakar.OpenTelemetry.Api.Client.Images;
using Jakar.OpenTelemetry.Api.Client.Logging;
using Jakar.OpenTelemetry.Contracts;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZLinq;

namespace Jakar.OpenTelemetry.Api.Client;

/// <summary> A captured screenshot. </summary>
public sealed record CapturedImage( byte[] Data,
									string ContentType,
									string FileName );

/// <summary> Platform screenshot capture (MAUI registers one; in Blazor the component captures via JS). </summary>
public interface IScreenshotProvider
{
	ValueTask<CapturedImage?> CaptureAsync( CancellationToken token = default );
}

/// <summary> Application-facing API of the Jakar.OpenTelemetry client. </summary>
public interface ITelemetryClient
{
	JakarCrashReporter Crashes { get; }

	/// <summary>
	///     Durably queues an image and returns its tag. Put the tag on the related log (see <c>JakarLoggerExtensions.BeginImageScope</c>);
	///     the log is exported right away and the image uploads when the server can accept it.
	/// </summary>
	ValueTask<ImageTag> AttachImageAsync( ReadOnlyMemory<byte> data, string fileName, string contentType = ImageContentTypes.PNG, CancellationToken token = default );

	/// <summary> Captures and queues a screenshot; <see langword="null"/> when the platform cannot capture one. </summary>
	ValueTask<ImageTag?> CaptureScreenshotAsync( string? fileName = null, CancellationToken token = default );

	/// <summary> Sends buffered logs, traces and metrics now and runs an image upload pass. Returns <see langword="false"/> if the telemetry could not be flushed in time. </summary>
	Task<bool> FlushAsync( CancellationToken token = default );
}

/// <summary>
/// This is meant to be a singleton--matching the lifespan of the app/runtime, as it's very expensive to instantiate. 
/// </summary>
public sealed class TelemetryClient : TelemetrySource, ITelemetryClient
{
	internal static readonly ActivitySource                                     s_InternalActivitySource = new(JakarTelemetryServiceCollectionExtensions.INSTRUMENTATION_NAME);
	private readonly         IScreenshotProvider?                               _screenshots;
	private readonly         ILogger<TelemetryClient>                           _logger;
	internal readonly        ILoggerFactory                                     LoggerFactory;
	private readonly         ITelemetryImageQueue                               _queue;
	private readonly         TelemetryImageUploader                             _uploader;
	private readonly         ITelemetryFlusher                                  _flusher;
	internal readonly        ConcurrentDictionary<string, TelemetryLogger>      _loggers = new();
	private readonly         ConcurrentDictionary<string, Lazy<TelemetryMeter>> _meters  = new(StringComparer.Ordinal);
	internal                 JakarTelemetryOptions                              Options { get; }
	public                   JakarCrashReporter                                 Crashes { get; }


	/// <summary> Wraps the inherited <see cref="TelemetrySource.Meter"/>: the application's meter with the instrument cache, always exported. </summary>
	public TelemetryMeter AppMeter { get; }

	/// <summary>
	///     Returns the meter with this exact name, creating it on first use. The name must be <see cref="JakarTelemetryOptions.AppName"/>,
	///     start with <c>{AppName}.</c>, or be listed in <see cref="JakarTelemetryOptions.Meters"/>; anything else would never be exported, so it throws <see cref="ArgumentException"/>.
	/// </summary>
	public TelemetryMeter this[ string name ] => _meters.GetOrAdd( name, static ( x, client ) => new Lazy<TelemetryMeter>( () => client.CreateMeter( x ) ), this ).Value; // Lazy: concurrent first access creates one Meter
	internal TelemetryMeter this[ Type category ] => this[Options.GetName( category )];

	/// <summary>
	/// This is meant to be a singleton--matching the lifespan of the app/runtime, as it's very expensive to instantiate. 
	/// </summary>
	/// <param name="queue"></param>
	/// <param name="uploader"></param>
	/// <param name="flusher"></param>
	/// <param name="crashes"></param>
	/// <param name="screenshotProviders"></param>
	/// <param name="options"></param>
	/// <param name="loggerFactory"></param>
	public TelemetryClient( ITelemetryImageQueue queue, TelemetryImageUploader uploader, ITelemetryFlusher flusher, JakarCrashReporter crashes, IEnumerable<IScreenshotProvider> screenshotProviders, IOptions<JakarTelemetryOptions> options, ILoggerFactory loggerFactory ) : base( options.Value.GetAppInfo() )
	{
		_queue        = queue;
		_uploader     = uploader;
		_flusher      = flusher;
		Crashes       = crashes;
		Options       = options.Value;
		_screenshots  = screenshotProviders.LastOrDefault();
		_logger       = loggerFactory.CreateLogger<TelemetryClient>();
		LoggerFactory = loggerFactory;
		AppMeter      = new TelemetryMeter( Meter, false );
	}

	public async ValueTask<ImageTag?> CaptureScreenshotAsync( string? fileName = null, CancellationToken token = default )
	{
		if ( _screenshots is null ||
			 !Options.Images.Enabled ) { return null; }

		CapturedImage? image = await _screenshots.CaptureAsync( token );
		if ( image is null ||
			 image.Data.Length == 0 ) { return null; }

		if ( image.Data.Length > Options.Images.MaxImageBytes )
		{
			_logger.LogWarning( "Screenshot of {Length} bytes exceeds the {MaxImageBytes} byte limit, not attached", image.Data.Length, Options.Images.MaxImageBytes );
			return null;
		}

		return await AttachImageAsync( image.Data, fileName ?? image.FileName, image.ContentType, token );
	}
	public async ValueTask<ImageTag> AttachImageAsync( ReadOnlyMemory<byte> data, string fileName, string contentType = ImageContentTypes.PNG, CancellationToken token = default )
	{
		ImageTag tag = await _queue.EnqueueAsync( data, fileName, contentType, token );
		_uploader.Signal();
		return tag;
	}
	public async Task<bool> FlushAsync( CancellationToken token = default )
	{
		bool flushed = await _flusher.FlushAsync( Options.ExportTimeout, token );

		try { await _uploader.UploadNowAsync( token ); }
		catch ( Exception e ) when ( e is not OperationCanceledException ) { _logger.LogDebug( e, "On-demand image upload pass failed" ); }

		return flushed;
	}


	/// <summary> Platform device details (MAUI registers a provider); the client is sealed, so this replaces subclassing <see cref="TelemetrySource"/>. </summary>
	public override DeviceInformation? TryGetDeviceInformation() => null; // TODO: use HardwareInfo for all but Android/iOS.

	/// <summary> Disposes the client-owned meters, then the inherited <see cref="TelemetrySource.Source"/> and <see cref="TelemetrySource.Meter"/>. Called by the container only. </summary>
	public override void Dispose()
	{
		foreach ( Lazy<TelemetryMeter> meter in _meters.Values )
		{
			if ( meter.IsValueCreated ) { meter.Value.Dispose(); }
		}

		_meters.Clear();
		base.Dispose();
	}

	private TelemetryMeter CreateMeter( string name )
	{
		if ( string.Equals( name, Options.AppName, StringComparison.Ordinal ) ) { return AppMeter; }

		if ( name.StartsWith( $"{Options.AppName}.", StringComparison.Ordinal ) ) { return new TelemetryMeter( new MeterOptions( name ) { Version = Info.Version.ToString() } ); }

		foreach ( TelemetryMeterOptions options in Options.Meters )
		{
			if ( string.Equals( options.Name, name, StringComparison.Ordinal ) ) { return new TelemetryMeter( options ); }
		}

		throw new ArgumentException( $"Meter '{name}' is never exported: use '{Options.AppName}', '{Options.AppName}.*' or a name listed in {JakarTelemetryOptions.SECTION_NAME}:{nameof(JakarTelemetryOptions.Meters)}", nameof(name) );
	}


	public  TelemetryLogger<TCategory> CreateLogger<TCategory>( TCategory _ ) where TCategory : notnull { return new TelemetryLogger<TCategory>( this, LoggerFactory.CreateLogger<TCategory>(), this[Options.GetName<TCategory>()] ); }
	public  TelemetryLogger<TCategory> CreateLogger<TCategory>() where TCategory : notnull              => (TelemetryLogger<TCategory>)_loggers.GetOrAdd( Options.GetName<TCategory>(), _ => DoCreateLogger<TCategory>() );
	public  TelemetryLogger            CreateLogger( string   name )                                    => _loggers.GetOrAdd( name, DoCreateLogger );
	private TelemetryLogger            DoCreateLogger( string name )                                    => new(this, LoggerFactory.CreateLogger( name ), this[Options.GetName( name )]);
	private TelemetryLogger<TCategory> DoCreateLogger<TCategory>()                                      => new(this, LoggerFactory.CreateLogger<TCategory>(), this[Options.GetName<TCategory>()]);


	[System.Diagnostics.Contracts.Pure] [MustDisposeResource]
	public static TelemetrySpan CreateSpan( [CallerMemberName] string? name = null ) => TelemetrySpan.Create( name ?? throw new ArgumentNullException( nameof(name) ) );
}

/// <summary>
/// This is meant to be a logger for a service, page, background task, api call, UI control, etc. 
/// </summary>
/// <param name="client"></param>
/// <param name="inner"></param> 
/// <param name="meter"></param>
public class TelemetryLogger( TelemetryClient client,
							  ILogger         inner,
							  TelemetryMeter  meter ) : ILogger, IDisposable
{
	public TelemetryMeter Meter { get; } = meter;
	public void Dispose()
	{
		Meter.Dispose();
		GC.SuppressFinalize( this );
	}


	// ILogger: forwards to the inner logger (the host factory's logger for this category).
	public void         Log<TState>( LogLevel      logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter ) => inner.Log( logLevel, eventId, state, exception, formatter );
	public bool         IsEnabled( LogLevel        logLevel )                     => inner.IsEnabled( logLevel );
	public IDisposable? BeginScope<TState>( TState state ) where TState : notnull => inner.BeginScope( state );

	[System.Diagnostics.Contracts.Pure] [MustDisposeResource]
	public static TelemetrySpan CreateSpan( [CallerMemberName] string? name = null ) => TelemetryClient.CreateSpan( name );

	// Images (moved from JakarLoggerExtensions, no longer take a client argument)
	public Task<ImageTag?> LogErrorWithScreenshotAsync( Exception? exception, string? message, params object?[] args ) => JakarLoggerExtensions.LogErrorWithScreenshotAsync( this, client, exception, message, args );
}

public sealed class TelemetryLogger<TCategory> : TelemetryLogger, ILogger<TCategory>
{
	public TelemetryLogger( TelemetryClient   client ) : this( client, client.LoggerFactory.CreateLogger<TCategory>(), client[typeof(TCategory)] ) { }
	internal TelemetryLogger( TelemetryClient client, ILogger<TCategory> inner, TelemetryMeter meter ) : base( client, inner, meter ) { }
}

public sealed class TelemetryMeter : IDisposable
{
	private readonly ConcurrentDictionary<string, Instrument>    _instruments = new(StringComparer.Ordinal);
	private readonly Meter                                       _meter;
	private readonly bool                                        _ownsMeter;
	public           string                                      Name               => _meter.Name;
	public           string?                                     Version            => _meter.Version;
	public           IEnumerable<KeyValuePair<string, object?>>? Tags               => _meter.Tags;
	public           object?                                     Scope              => _meter.Scope;
	public           string?                                     TelemetrySchemaUrl => _meter.TelemetrySchemaUrl;

	public TelemetryMeter( string       name ) : this( new Meter( name ), true ) { }
	public TelemetryMeter( MeterOptions options ) : this( new Meter( options ), true ) { }
	internal TelemetryMeter( Meter meter, bool ownsMeter )
	{
		_meter     = meter;
		_ownsMeter = ownsMeter;
	}
	public static implicit operator TelemetryMeter( Meter meter ) => new(meter, false);

	/// <summary> The instrument created with this name, or <see langword="null"/>. Read-only: instruments only enter the cache through <c>Create*</c>. </summary>
	public Instrument? this[ string name ] => _instruments.GetValueOrDefault( name );

	public bool TryGetInstrument<TInstrument>( string name, [NotNullWhen( true )] out TInstrument? instrument ) where TInstrument : Instrument
	{
		instrument = _instruments.GetValueOrDefault( name ) as TInstrument;
		return instrument is not null;
	}

	/// <inheritdoc cref="Meter.CreateCounter{T}(string, string?, string?)"/>
	public Counter<T> CreateCounter<T>( string name, string? unit = null, string? description = null ) where T : struct => Track( _meter.CreateCounter<T>( name, unit, description ) );

	// … every Meter.Create* overload (Counter, UpDownCounter, Gauge, Histogram, ObservableCounter, ObservableUpDownCounter, ObservableGauge),
	//   each documented with <inheritdoc cref="Meter.…"/> and routed through Track.

	/// <summary> Clears the cache and, when <c>_ownsMeter</c>, disposes the meter (its instruments stop publishing). </summary>
	public void Dispose()
	{
		_instruments.Clear();
		if ( _ownsMeter ) { _meter.Dispose(); }
	}

	private TInstrument Track<TInstrument>( TInstrument instrument ) where TInstrument : Instrument
	{
		Instrument cached = _instruments.GetOrAdd( instrument.Name, instrument );
		return cached as TInstrument ?? throw new InvalidOperationException( $"Instrument '{instrument.Name}' already exists on meter '{Name}' as {cached.GetType().Name}" );
	}
}
