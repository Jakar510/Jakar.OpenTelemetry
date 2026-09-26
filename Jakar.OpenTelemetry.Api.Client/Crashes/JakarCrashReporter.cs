using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jakar.OpenTelemetry.Api.Client.Crashes;

/// <summary>
///     Single entry point for crashes from every source (AppDomain, Android Java/managed bridges, Objective-C, WinUI, browser).
///     <para>
///         A terminating crash is first written synchronously to the <see cref="ICrashReportStore"/>, then logged and flushed with a bounded timeout.
///         If the process dies before the export gets out, the report is replayed as a log on the next launch.
///     </para>
/// </summary>
public sealed class JakarCrashReporter
{
	public const string CATEGORY = "Jakar.OpenTelemetry.Crash";

	private static readonly EventId CrashEvent               = new(9001, "app.crash");
	private static readonly EventId PreviousCrashEvent       = new(9002, "app.crash.previous_session");
	private static readonly EventId AbnormalTerminationEvent = new(9003, "app.abnormal_termination");

	private readonly ICrashReportStore     _store;
	private readonly ITelemetryFlusher     _flusher;
	private readonly JakarTelemetryOptions _options;
	private readonly ILogger               _logger;
	private          int                   _terminating;


	public JakarCrashReporter( ICrashReportStore store, ITelemetryFlusher flusher, IOptions<JakarTelemetryOptions> options, ILoggerFactory loggerFactory )
	{
		_store   = store;
		_flusher = flusher;
		_options = options.Value;
		_logger  = loggerFactory.CreateLogger( CATEGORY );
	}


	public string SessionID { get; } = Guid.CreateVersion7().ToString( "N" );


	/// <summary> Reports a managed exception (unhandled, or caught at a platform boundary). </summary>
	public void ReportException( Exception exception, string source, bool isTerminating, IReadOnlyDictionary<string, string>? details = null ) => Report( new CrashReport( Guid.CreateVersion7(),
																																										   DateTimeOffset.UtcNow,
																																										   SessionID,
																																										   source,
																																										   isTerminating,
																																										   exception.GetType().FullName ?? exception.GetType().Name,
																																										   exception.Message,
																																										   exception.ToString(),
																																										   details is null
																																											   ? null
																																											   : new Dictionary<string, string>( details ) ),
																																						  exception );

	/// <summary> Reports a crash that has no managed exception object (Java throwable, NSException, WinRT error, JavaScript error). </summary>
	public void ReportNative( string source, string exceptionType, string? message, string? stackTrace, bool isTerminating, IReadOnlyDictionary<string, string>? details = null ) => Report( new CrashReport( Guid.CreateVersion7(),
																																																			  DateTimeOffset.UtcNow,
																																																			  SessionID,
																																																			  source,
																																																			  isTerminating,
																																																			  exceptionType,
																																																			  message,
																																																			  stackTrace,
																																																			  details is null
																																																				  ? null
																																																				  : new Dictionary<string, string>( details ) ),
																																															 null );


	private void Report( CrashReport report, Exception? exception )
	{
		// Several hooks can observe the same fatal crash (e.g. Android raiser + AppDomain); persist and flush it once.
		if ( report.IsTerminating &&
			 Interlocked.Exchange( ref _terminating, 1 ) == 1 ) { return; }

		if ( report.IsTerminating )
		{
			try
			{
				_store.Save( report );

				// The termination is now explained by a durable crash report; the next launch must not also flag it as an unexplained abnormal termination.
				_store.EndSession();
			}
			catch ( Exception e ) { _logger.LogWarning( e, "Could not persist crash report {CrashId}", report.ID ); }
		}

		using ( _logger.BeginScope( Attributes( report, previousSession: false ) ) )
		{
			_logger.Log( report.IsTerminating
							 ? LogLevel.Critical
							 : LogLevel.Error,
						 CrashEvent,
						 exception,
						 "{CrashSource}: {ExceptionType}: {ExceptionMessage}",
						 report.Source,
						 report.ExceptionType,
						 report.Message );
		}

		if ( !report.IsTerminating ) { return; }

		bool flushed = false;

		try { flushed = _flusher.Flush( _options.FlushTimeout ); }
		catch ( Exception e ) { _logger.LogDebug( e, "Flush during crash failed" ); }

		// With failed-export persistence the log is now durable either way; without it keep the report so it is replayed next launch.
		if ( flushed && _options.PersistFailedExports ) { _store.Delete( report.ID ); }
	}

	/// <summary> Called at startup: re-emits crashes persisted by earlier sessions and detects a previous session that ended abnormally. </summary>
	internal void ReplayPreviousSessions()
	{
		SessionMarker?             previous = _store.ReadUncleanSession();
		IReadOnlyList<CrashReport> reports  = _store.LoadAll();

		foreach ( CrashReport report in reports )
		{
			using ( _logger.BeginScope( Attributes( report, previousSession: true ) ) ) { _logger.Log( LogLevel.Critical, PreviousCrashEvent, "Crash in previous session at {CrashTime:O}, {CrashSource}: {ExceptionType}: {ExceptionMessage}", report.TimestampUtc, report.Source, report.ExceptionType, report.Message ); }

			_store.Delete( report.ID );
		}

		if ( _options.DetectAbnormalTermination &&
			 previous is not null               &&
			 reports.All( x => x.SessionID != previous.SessionID ) ) { _logger.Log( LogLevel.Error, AbnormalTerminationEvent, "Previous session {PreviousSessionId} (version {PreviousVersion}, started {PreviousStarted:O}) ended without a clean shutdown or crash report, possible native crash, out-of-memory kill or force quit", previous.SessionID, previous.ServiceVersion, previous.StartedUtc ); }

		_store.BeginSession( new SessionMarker( SessionID,
												DateTimeOffset.UtcNow,
												OperatingSystem.IsBrowser()
													? 0
													: Environment.ProcessId,
												_options.ServiceVersion ) );
	}

	/// <summary> OpenTelemetry semantic-convention attributes, attached through a logging scope (exported when scopes are enabled). </summary>
	private static List<KeyValuePair<string, object?>> Attributes( CrashReport report, bool previousSession )
	{
		List<KeyValuePair<string, object?>> attributes =
			[
				new("exception.type", report.ExceptionType),
				new("exception.message", report.Message),
				new("exception.stacktrace", report.StackTrace),
				new("crash.id", report.ID.ToString( "D" )),
				new("crash.source", report.Source),
				new("crash.terminating", report.IsTerminating),
				new("crash.session_id", report.SessionID),
				new("crash.previous_session", previousSession),
				new("crash.time", report.TimestampUtc.ToString( "O" ))
			];

		if ( report.Details is not null )
		{
			foreach ( ( string key, string value ) in report.Details ) { attributes.Add( new KeyValuePair<string, object?>( $"crash.{key}", value ) ); }
		}

		return attributes;
	}
}
