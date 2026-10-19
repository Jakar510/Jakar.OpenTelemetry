using System.Text.Json;

namespace Jakar.OpenTelemetry.Api.Client.Blazor.WebAssembly.Tests;

[TestFixture]
public sealed class OtlpJsonLogSerializerTests
{
	private static JsonElement Serialize( JakarTelemetryOptions options, params BrowserLogRecord[] records ) => JsonDocument.Parse( OtlpJsonLogSerializer.Serialize( [ .. records ], options ) ).RootElement.GetProperty( "resourceLogs" )[0];

	private static Dictionary<string, JsonElement> Attributes( JsonElement owner ) => owner.GetProperty( "attributes" ).EnumerateArray().ToDictionary( static x => x.GetProperty( "key" ).GetString()!, static x => x.GetProperty( "value" ) );

	private static BrowserLogRecord Record( string category = "cat", LogLevel level = LogLevel.Information, params KeyValuePair<string, object?>[] attributes ) => new(1_700_000_000_123_456_789, level, category, "body", null, null, null, [ .. attributes ]);


	[Test] public void Resource_ContainsServiceSdkAndCustomAttributes()
	{
		JakarTelemetryOptions options = ClientTestOptions.Create( "/tmp",
																  static o =>
																  {
																	  o.ServiceNamespace                       = "ns";
																	  o.ServiceInstanceId                      = "instance";
																	  o.DeploymentEnvironment                  = "prod";
																	  o.ResourceAttributes["browser.language"] = "en-US";
																	  o.ResourceAttributes["screen.width"]     = 1280;
																  } );

		Dictionary<string, JsonElement> resource = Attributes( Serialize( options, Record() ).GetProperty( "resource" ) );

		Assert.Multiple( () =>
						 {
							 Assert.That( resource["service.name"].GetProperty( "stringValue" ).GetString(),                Is.EqualTo( "client-tests" ) );
							 Assert.That( resource["service.version"].GetProperty( "stringValue" ).GetString(),             Is.EqualTo( "1.2.3" ) );
							 Assert.That( resource["service.namespace"].GetProperty( "stringValue" ).GetString(),           Is.EqualTo( "ns" ) );
							 Assert.That( resource["service.instance.id"].GetProperty( "stringValue" ).GetString(),         Is.EqualTo( "instance" ) );
							 Assert.That( resource["deployment.environment.name"].GetProperty( "stringValue" ).GetString(), Is.EqualTo( "prod" ) );
							 Assert.That( resource["telemetry.sdk.language"].GetProperty( "stringValue" ).GetString(),      Is.EqualTo( "dotnet" ) );
							 Assert.That( resource["telemetry.sdk.name"].GetProperty( "stringValue" ).GetString(),          Is.EqualTo( JakarTelemetryServiceCollectionExtensions.INSTRUMENTATION_NAME ) );
							 Assert.That( resource["browser.language"].GetProperty( "stringValue" ).GetString(),            Is.EqualTo( "en-US" ) );
							 Assert.That( resource["screen.width"].GetProperty( "intValue" ).GetString(),                   Is.EqualTo( "1280" ) );
						 } );
	}

	[Test] public void Resource_OptionalServiceFields_AreOmittedWhenUnset()
	{
		JakarTelemetryOptions options = ClientTestOptions.Create( "/tmp", static o => o.ServiceVersion = null );

		Dictionary<string, JsonElement> resource = Attributes( Serialize( options, Record() ).GetProperty( "resource" ) );

		Assert.That( resource.Keys,
					 Is.EquivalentTo( new[]
										  {
											  "service.name",
											  "telemetry.sdk.language",
											  "telemetry.sdk.name"
										  } ) );
	}

	[Test] public void Records_AreGroupedIntoScopesByCategory()
	{
		JsonElement scopes = Serialize( ClientTestOptions.Create( "/tmp" ), Record( "a" ), Record( "b" ), Record( "a" ) ).GetProperty( "scopeLogs" );

		Assert.Multiple( () =>
						 {
							 Assert.That( scopes.GetArrayLength(),                                            Is.EqualTo( 2 ) );
							 Assert.That( scopes[0].GetProperty( "scope" ).GetProperty( "name" ).GetString(), Is.EqualTo( "a" ) );
							 Assert.That( scopes[0].GetProperty( "logRecords" ).GetArrayLength(),             Is.EqualTo( 2 ) );
						 } );
	}

	[Test] public void Record_WritesTimesBodyIdsAndEventName()
	{
		BrowserLogRecord record = new(1_700_000_000_123_456_789, LogLevel.Warning, "cat", "the body", "browser.error", "0102030405060708090a0b0c0d0e0f10", "a1a2a3a4a5a6a7a8", [ ]);

		JsonElement log = Serialize( ClientTestOptions.Create( "/tmp" ), record ).GetProperty( "scopeLogs" )[0].GetProperty( "logRecords" )[0];

		Assert.Multiple( () =>
						 {
							 Assert.That( log.GetProperty( "timeUnixNano" ).GetString(),                      Is.EqualTo( "1700000000123456789" ) );
							 Assert.That( log.GetProperty( "observedTimeUnixNano" ).GetString(),              Is.EqualTo( "1700000000123456789" ) );
							 Assert.That( log.GetProperty( "severityNumber" ).GetInt32(),                     Is.EqualTo( 13 ) );
							 Assert.That( log.GetProperty( "severityText" ).GetString(),                      Is.EqualTo( "Warning" ) );
							 Assert.That( log.GetProperty( "body" ).GetProperty( "stringValue" ).GetString(), Is.EqualTo( "the body" ) );
							 Assert.That( log.GetProperty( "eventName" ).GetString(),                         Is.EqualTo( "browser.error" ) );
							 Assert.That( log.GetProperty( "traceId" ).GetString(),                           Is.EqualTo( "0102030405060708090a0b0c0d0e0f10" ) );
							 Assert.That( log.GetProperty( "spanId" ).GetString(),                            Is.EqualTo( "a1a2a3a4a5a6a7a8" ) );
						 } );
	}

	[Test] public void Record_WithoutOptionalFields_OmitsThem()
	{
		BrowserLogRecord record = new(1, LogLevel.Information, "cat", null, null, null, null, [ ]);

		JsonElement log = Serialize( ClientTestOptions.Create( "/tmp" ), record ).GetProperty( "scopeLogs" )[0].GetProperty( "logRecords" )[0];

		Assert.That( log.EnumerateObject().Select( static x => x.Name ),
					 Is.EquivalentTo( new[]
										  {
											  "timeUnixNano",
											  "observedTimeUnixNano",
											  "severityNumber",
											  "severityText",
											  "attributes"
										  } ) );
	}

	[TestCase( LogLevel.Trace, 1 )] [TestCase( LogLevel.Debug, 5 )] [TestCase( LogLevel.Information, 9 )] [TestCase( LogLevel.Warning, 13 )] [TestCase( LogLevel.Error, 17 )] [TestCase( LogLevel.Critical, 21 )] [TestCase( LogLevel.None, 0 )]
	public void SeverityNumber_MatchesOpenTelemetrySdk( LogLevel level, int expected ) => Assert.That( Serialize( ClientTestOptions.Create( "/tmp" ), Record( level: level ) ).GetProperty( "scopeLogs" )[0].GetProperty( "logRecords" )[0].GetProperty( "severityNumber" ).GetInt32(), Is.EqualTo( expected ) );

	[Test] public void Attributes_KeepTheirOtlpType()
	{
		DateTimeOffset when = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

		BrowserLogRecord record = Record( attributes:
											  [
												  new("string", "text"),
												  new("bool", true),
												  new("sbyte", (sbyte)-1),
												  new("byte", (byte)2),
												  new("short", (short)3),
												  new("ushort", (ushort)4),
												  new("int", 5),
												  new("uint", 6U),
												  new("long", 9_007_199_254_740_993L),
												  new("float", 1.5F),
												  new("double", 2.25),
												  new("decimal", 3.5M),
												  new("nan", double.NaN),
												  new("inf", double.PositiveInfinity),
												  new("-inf", float.NegativeInfinity),
												  new("array",
													  new object?[]
														  {
															  "a",
															  1,
															  null
														  }),
												  new("datetime", when.UtcDateTime),
												  new("datetimeoffset", when),
												  new("guid", Guid.Empty),
												  new("dictionary", new Dictionary<string, int> { ["k"] = 1 }),
												  new("null", null)
											  ] );

		Dictionary<string, JsonElement> attributes = Attributes( Serialize( ClientTestOptions.Create( "/tmp" ), record ).GetProperty( "scopeLogs" )[0].GetProperty( "logRecords" )[0] );

		Assert.Multiple( () =>
						 {
							 Assert.That( attributes["string"].GetProperty( "stringValue" ).GetString(),                        Is.EqualTo( "text" ) );
							 Assert.That( attributes["bool"].GetProperty( "boolValue" ).GetBoolean(),                           Is.True );
							 Assert.That( attributes["sbyte"].GetProperty( "intValue" ).GetString(),                            Is.EqualTo( "-1" ) );
							 Assert.That( attributes["byte"].GetProperty( "intValue" ).GetString(),                             Is.EqualTo( "2" ) );
							 Assert.That( attributes["short"].GetProperty( "intValue" ).GetString(),                            Is.EqualTo( "3" ) );
							 Assert.That( attributes["ushort"].GetProperty( "intValue" ).GetString(),                           Is.EqualTo( "4" ) );
							 Assert.That( attributes["int"].GetProperty( "intValue" ).GetString(),                              Is.EqualTo( "5" ) );
							 Assert.That( attributes["uint"].GetProperty( "intValue" ).GetString(),                             Is.EqualTo( "6" ) );
							 Assert.That( attributes["long"].GetProperty( "intValue" ).GetString(),                             Is.EqualTo( "9007199254740993" ), "int64 is a string in OTLP/JSON" );
							 Assert.That( attributes["float"].GetProperty( "doubleValue" ).GetDouble(),                         Is.EqualTo( 1.5 ) );
							 Assert.That( attributes["double"].GetProperty( "doubleValue" ).GetDouble(),                        Is.EqualTo( 2.25 ) );
							 Assert.That( attributes["decimal"].GetProperty( "doubleValue" ).GetDouble(),                       Is.EqualTo( 3.5 ) );
							 Assert.That( attributes["nan"].GetProperty( "doubleValue" ).GetString(),                           Is.EqualTo( "NaN" ) );
							 Assert.That( attributes["inf"].GetProperty( "doubleValue" ).GetString(),                           Is.EqualTo( "Infinity" ) );
							 Assert.That( attributes["-inf"].GetProperty( "doubleValue" ).GetString(),                          Is.EqualTo( "-Infinity" ) );
							 Assert.That( attributes["array"].GetProperty( "arrayValue" ).GetProperty( "values" ).GetRawText(), Is.EqualTo( """[{"stringValue":"a"},{"intValue":"1"},{}]""" ) );
							 Assert.That( attributes["datetime"].GetProperty( "stringValue" ).GetString(),                      Is.EqualTo( "2026-01-02T03:04:05.0000000Z" ) );
							 Assert.That( attributes["datetimeoffset"].GetProperty( "stringValue" ).GetString(),                Is.EqualTo( "2026-01-02T03:04:05.0000000+00:00" ) );
							 Assert.That( attributes["guid"].GetProperty( "stringValue" ).GetString(),                          Is.EqualTo( Guid.Empty.ToString() ) );
							 Assert.That( attributes["dictionary"].GetProperty( "stringValue" ).GetString(),                    Does.Contain( "Dictionary" ) );
							 Assert.That( attributes,                                                                           Does.Not.ContainKey( "null" ), "null attributes are skipped" );
						 } );
	}

	[Test] public void Output_IsCompactAndUnescaped() => Assert.That( OtlpJsonLogSerializer.Serialize( [ new BrowserLogRecord( 1, LogLevel.Information, "c", "<b>é & 'q'</b>", null, null, null, [ ] ) ], ClientTestOptions.Create( "/tmp" ) ), Does.Contain( "<b>é & 'q'</b>" ) );
}
