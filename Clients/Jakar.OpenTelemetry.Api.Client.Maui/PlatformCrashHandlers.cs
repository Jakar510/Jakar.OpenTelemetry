using Jakar.OpenTelemetry.Api.Client.Crashes;
using Microsoft.Extensions.Logging;
#if IOS || MACCATALYST
using System.Runtime.InteropServices;
using Foundation;
using ObjCRuntime;
#endif

namespace Jakar.OpenTelemetry.Api.Client.Maui;

/// <summary>
///     Native crash hooks per platform. Each hook persists a crash report synchronously through <see cref="JakarCrashReporter"/> (replayed next launch if the
///     export cannot get out in time) and then lets the platform's previous handler run, so the app still crashes normally and other crash reporters keep working.
///     <para>
///         Signal-level crashes (SIGSEGV/SIGABRT in native code) cannot run managed code safely; they surface on the next launch as an abnormal termination
///         (see <see cref="JakarTelemetryOptions.DetectAbnormalTermination"/>).
///     </para>
/// </summary>
internal static partial class PlatformCrashHandlers
{
	private static JakarCrashReporter? s_reporter;

	public static void Install( JakarCrashReporter reporter, ILogger logger )
	{
		if ( Interlocked.Exchange( ref s_reporter, reporter ) is not null ) { return; }

		InstallPlatform( reporter, logger );
	}

	#if ANDROID
    private static void InstallPlatform( JakarCrashReporter reporter, ILogger logger )
    {
        // Managed exceptions crossing into Java (e.g. thrown from a UI callback).
        Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += ( _, args ) =>
                                                                       {
                                                                           reporter.ReportException( args.Exception, "android.unhandled_exception_raiser", isTerminating: !args.Handled );
                                                                       };

        // Java/Kotlin exceptions on any thread (including those thrown by native Android libraries).
        Java.Lang.Thread.IUncaughtExceptionHandler? previous = Java.Lang.Thread.DefaultUncaughtExceptionHandler;
        Java.Lang.Thread.DefaultUncaughtExceptionHandler = new JavaUncaughtExceptionHandler( reporter, previous );
        logger.LogDebug( "Installed Android crash handlers" );
    }

    private sealed class JavaUncaughtExceptionHandler( JakarCrashReporter reporter, Java.Lang.Thread.IUncaughtExceptionHandler? previous ) : Java.Lang.Object, Java.Lang.Thread.IUncaughtExceptionHandler
    {
        public void UncaughtException( Java.Lang.Thread thread, Java.Lang.Throwable throwable )
        {
            try
            {
                reporter.ReportNative( "android.java_uncaught_exception",
                                       throwable.Class?.Name ?? "java.lang.Throwable",
                                       throwable.Message,
                                       Android.Util.Log.GetStackTraceString( throwable ),
                                       isTerminating: true,
                                       new Dictionary<string, string> { ["thread.name"] = thread.Name ?? string.Empty } );
            }
            catch ( Exception ) { } // never interfere with the crash itself
            finally { previous?.UncaughtException( thread, throwable ); }
        }
    }

	#elif IOS || MACCATALYST
	private static unsafe delegate* unmanaged<IntPtr, void> s_previousHandler;

	[LibraryImport( Constants.FoundationLibrary )] private static unsafe partial delegate* unmanaged<IntPtr, void> NSGetUncaughtExceptionHandler();

	[LibraryImport( Constants.FoundationLibrary )] private static unsafe partial void NSSetUncaughtExceptionHandler( delegate* unmanaged<IntPtr, void> handler );

	private static unsafe void InstallPlatform( JakarCrashReporter reporter, ILogger logger )
	{
		// A managed exception about to unwind through native frames is always fatal on Apple platforms.
		Runtime.MarshalManagedException += ( _, args ) => reporter.ReportException( args.Exception, "apple.marshal_managed_exception", isTerminating: true );

		// An Objective-C exception about to be converted into a managed ObjCException: usually recoverable, so recorded as an error.
		Runtime.MarshalObjectiveCException += ( _, args ) => ReportNSException( args.Exception, "apple.marshal_objective_c_exception", isTerminating: false );

		// Uncaught Objective-C exceptions (UIKit/AppKit assertions, NSInvalidArgumentException, ...). Chain to any previously installed handler.
		s_previousHandler = NSGetUncaughtExceptionHandler();
		NSSetUncaughtExceptionHandler( &OnUncaughtObjectiveCException );
		logger.LogDebug( "Installed Apple crash handlers" );
	}

	[UnmanagedCallersOnly] private static unsafe void OnUncaughtObjectiveCException( IntPtr exceptionHandle )
	{
		try
		{
			if ( Runtime.GetNSObject<NSException>( exceptionHandle ) is { } exception ) { ReportNSException( exception, "apple.uncaught_objective_c_exception", isTerminating: true ); }
		}
		catch ( Exception ) { } // never interfere with the crash itself
		finally
		{
			if ( s_previousHandler != null ) { s_previousHandler( exceptionHandle ); }
		}
	}

	private static void ReportNSException( NSException exception, string source, bool isTerminating ) => s_reporter?.ReportNative( source, exception.Name?.ToString() ?? "NSException", exception.Reason, string.Join( '\n', exception.CallStackSymbols ), isTerminating );

	#elif WINDOWS
    private static void InstallPlatform( JakarCrashReporter reporter, ILogger logger )
    {
        // WinUI's Application.UnhandledException is attached in OnLaunched (see AttachWinUI); AppDomain handling comes from the core runtime.
        logger.LogDebug( "Windows crash handlers will attach when the WinUI application launches" );
    }

    /// <summary> Unhandled exceptions on the WinUI dispatcher. The WinRT message often carries details the managed exception lacks. </summary>
    public static void AttachWinUI( Microsoft.UI.Xaml.Application application ) =>
        application.UnhandledException += ( _, args ) =>
                                          {
                                              if ( s_reporter is not { } reporter ) { return; }

                                              if ( args.Exception is { } exception )
                                              {
                                                  reporter.ReportException( exception, "winui.unhandled_exception", isTerminating: !args.Handled, new Dictionary<string, string> { ["winrt.message"] = args.Message ?? string.Empty } );
                                              }
                                              else { reporter.ReportNative( "winui.unhandled_exception", "WinRTException", args.Message, null, isTerminating: !args.Handled ); }
                                          };

	#else
    private static void InstallPlatform( JakarCrashReporter reporter, ILogger logger ) { }
	#endif
}
