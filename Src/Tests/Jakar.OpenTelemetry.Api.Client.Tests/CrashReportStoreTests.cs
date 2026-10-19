using Jakar.OpenTelemetry.Api.Client.Crashes;

namespace Jakar.OpenTelemetry.Api.Client.Tests;

[TestFixture]
public sealed class FileSystemCrashReportStoreTests
{
	private TempDirectory                          _temp   = null!;
	private FakeLogger<FileSystemCrashReportStore> _logger = null!;
	private FileSystemCrashReportStore             _store  = null!;


	private string CrashDirectory => _temp.Combine( "crashes" );
	private string SessionPath    => _temp.Combine( "session.json" );


	[SetUp] public void SetUp()
	{
		_temp   = new TempDirectory();
		_logger = new FakeLogger<FileSystemCrashReportStore>();
		_store  = new FileSystemCrashReportStore( ClientTestOptions.Wrap( ClientTestOptions.Create( _temp.Path ) ), _logger );
	}

	[TearDown] public void TearDown() => _temp.Dispose();


	private static CrashReport Report( DateTimeOffset time, string session = "s1" ) => new(Guid.CreateVersion7(), time, session, "test", true, "System.Exception", "boom", "stack", new Dictionary<string, string> { ["k"] = "v" });


	[Test] public void LoadAll_NothingSaved_ReturnsEmpty() => Assert.That( _store.LoadAll(), Is.Empty );

	[Test] public void Save_ThenLoadAll_ReturnsReportsOldestFirst()
	{
		CrashReport newer = Report( DateTimeOffset.UtcNow );
		CrashReport older = Report( DateTimeOffset.UtcNow.AddMinutes( -5 ) );

		_store.Save( newer );
		_store.Save( older );

		IReadOnlyList<CrashReport> loaded = _store.LoadAll();

		Assert.Multiple( () =>
						 {
							 Assert.That( loaded.Select( static x => x.ID ),
										  Is.EqualTo( new[]
														  {
															  older.ID,
															  newer.ID
														  } ) );
							 Assert.That( loaded[0].Details,                             Is.EquivalentTo( older.Details! ) );
							 Assert.That( Directory.GetFiles( CrashDirectory, "*.tmp" ), Is.Empty, "writes go through a temp file that is renamed" );
						 } );
	}

	[Test] public void Save_SameIdTwice_Overwrites()
	{
		CrashReport report = Report( DateTimeOffset.UtcNow );
		_store.Save( report );
		_store.Save( report with { Message = "second" } );

		Assert.That( _store.LoadAll().Single().Message, Is.EqualTo( "second" ) );
	}

	[Test] public void Delete_RemovesReport_AndIgnoresUnknownIds()
	{
		CrashReport report = Report( DateTimeOffset.UtcNow );
		_store.Save( report );

		_store.Delete( report.ID );
		_store.Delete( Guid.NewGuid() );

		Assert.That( _store.LoadAll(), Is.Empty );
	}

	[Test] public void LoadAll_UnreadableReport_IsDiscardedWithWarning()
	{
		CrashReport report = Report( DateTimeOffset.UtcNow );
		_store.Save( report );
		string corrupt = Path.Combine( CrashDirectory, "corrupt.json" );
		File.WriteAllText( corrupt, "{ not json" );

		IReadOnlyList<CrashReport> loaded = _store.LoadAll();

		Assert.Multiple( () =>
						 {
							 Assert.That( loaded.Select( static x => x.ID ),
										  Is.EqualTo( new[]
														  {
															  report.ID
														  } ) );
							 Assert.That( File.Exists( corrupt ),                                                                                                                       Is.False );
							 Assert.That( _logger.Collector.GetSnapshot().Any( static x => x.Level == LogLevel.Warning && x.Message.Contains( "Discarding unreadable crash report" ) ), Is.True );
						 } );
	}

	[Test] public void LoadAll_NullJson_IsSkipped()
	{
		Directory.CreateDirectory( CrashDirectory );
		File.WriteAllText( Path.Combine( CrashDirectory, "null.json" ), "null" );

		Assert.That( _store.LoadAll(), Is.Empty );
	}

	[Test] public void ReadUncleanSession_NoMarker_ReturnsNull() => Assert.That( _store.ReadUncleanSession(), Is.Null );

	[Test] public void BeginSession_ThenRead_ReturnsMarker()
	{
		SessionMarker marker = new("session-1", DateTimeOffset.UtcNow, 42, "1.0");

		_store.BeginSession( marker );

		Assert.That( _store.ReadUncleanSession(), Is.EqualTo( marker ) );
	}

	[Test] public void BeginSession_CreatesMissingDirectory()
	{
		_temp.Dispose();

		_store.BeginSession( new SessionMarker( "s", DateTimeOffset.UtcNow, 1, null ) );

		Assert.That( File.Exists( SessionPath ), Is.True );
	}

	[Test] public void EndSession_RemovesMarker()
	{
		_store.BeginSession( new SessionMarker( "s", DateTimeOffset.UtcNow, 1, null ) );

		_store.EndSession();

		Assert.That( _store.ReadUncleanSession(), Is.Null );
	}

	[Test] public void ResumeSession_AfterEnd_RestoresCurrentMarker()
	{
		SessionMarker marker = new("s", DateTimeOffset.UtcNow, 1, null);
		_store.BeginSession( marker );
		_store.EndSession();

		_store.ResumeSession();

		Assert.That( _store.ReadUncleanSession(), Is.EqualTo( marker ) );
	}

	[Test] public void ResumeSession_WithoutBegin_DoesNothing()
	{
		_store.ResumeSession();

		Assert.That( File.Exists( SessionPath ), Is.False );
	}

	[Test] public void ReadUncleanSession_CorruptMarker_ReturnsNull()
	{
		File.WriteAllText( SessionPath, "][" );

		Assert.That( _store.ReadUncleanSession(), Is.Null );
	}

	[Test] public void Delete_FileInUse_IsLoggedNotThrown()
	{
		if ( !OperatingSystem.IsWindows() ) { Assert.Ignore( "Only Windows refuses to delete a file that is open without FileShare.Delete" ); }

		CrashReport report = Report( DateTimeOffset.UtcNow );
		_store.Save( report );

		using ( File.Open( Path.Combine( CrashDirectory, report.ID.ToString( "N" ) + ".json" ), FileMode.Open, FileAccess.Read, FileShare.None ) ) { Assert.DoesNotThrow( () => _store.Delete( report.ID ) ); }

		Assert.That( _logger.Collector.GetSnapshot().Any( static x => x.Level == LogLevel.Debug && x.Message.StartsWith( "Could not delete" ) ), Is.True );
	}
}

[TestFixture]
public sealed class NullCrashReportStoreTests
{
	[Test] public void EveryOperation_IsANoOp()
	{
		NullCrashReportStore store = new();

		store.Save( new CrashReport( Guid.NewGuid(), DateTimeOffset.UtcNow, "s", "src", true, "T", null, null, null ) );
		store.BeginSession( new SessionMarker( "s", DateTimeOffset.UtcNow, 1, null ) );
		store.Delete( Guid.NewGuid() );
		store.EndSession();
		store.ResumeSession();

		Assert.Multiple( () =>
						 {
							 Assert.That( store.LoadAll(),            Is.Empty );
							 Assert.That( store.ReadUncleanSession(), Is.Null );
						 } );
	}
}
