using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Google.Protobuf;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.Extensions.DependencyInjection;
using global::OpenTelemetry.Proto.Logs.V1;

namespace Jakar.OpenTelemetry.Samples.AutomationTests;

/// <summary> The dashboard served by Jakar.OpenTelemetry.Api (interactive server Blazor), driven in a real browser. </summary>
[TestFixture]
public sealed partial class DashboardTests : LiveApiFixture
{
	private IBrowserContext _context = null!;
	private IPage           _page    = null!;
	private HttpClient      _ingest  = null!;


	[OneTimeSetUp]
	public void CreateIngestClient() => _ingest = new HttpClient { BaseAddress = Api.HttpAddress };

	[OneTimeTearDown] public void DisposeIngestClient() => _ingest.Dispose();

	[SetUp]
	public async Task OpenBrowserAsync()
	{
		await Telemetry.ExecuteAsync( """TRUNCATE "Logs", "Spans", "Metrics", "Images" """ );
		await Api.Services.GetRequiredService<ZiggyCreatures.Caching.Fusion.IFusionCache>().RemoveByTagAsync( "telemetry:snapshot" );

		_context = await Browsers.NewContextAsync();
		_page    = await _context.NewPageAsync();
	}

	[TearDown] public async Task CloseBrowserAsync() => await _context.DisposeAsync();


	private string Url( string path ) => new Uri( Api.HttpAddress, path ).ToString();

	private async Task PostAsync( string path, IMessage message )
	{
		ByteArrayContent content = new(message.ToByteArray());
		content.Headers.ContentType = new MediaTypeHeaderValue( "application/x-protobuf" );
		using HttpRequestMessage  request  = new(HttpMethod.Post, path) { Content = content, Headers = { { "x-api-key", TestApi.API_KEY } } };
		using HttpResponseMessage response = await _ingest.SendAsync( request );
		response.EnsureSuccessStatusCode();
	}

	private async Task UploadAsync( ImageTag tag, byte[] png )
	{
		using HttpRequestMessage request = new(HttpMethod.Put, LogTags.ImagePath( tag.ID )) { Content = new ByteArrayContent( png ) };
		request.Content.Headers.ContentType = new MediaTypeHeaderValue( "image/png" );
		request.Headers.Add( "x-api-key", TestApi.API_KEY );
		request.Headers.Add( LogTags.SHA256_HEADER, Convert.ToBase64String( SHA256.HashData( png ) ) );
		request.Headers.Add( LogTags.FILE_NAME_HEADER, tag.FileName );
		using HttpResponseMessage response = await _ingest.SendAsync( request );
		response.EnsureSuccessStatusCode();
	}

	private async Task SignInAsync( string username = TestApi.ADMIN_USER, string password = TestApi.ADMIN_PASSWORD, string path = "/" )
	{
		await _page.GotoAsync( Url( path ) );
		await WaitForCircuitAsync();
		await _page.GetByLabel( "Username" ).FillAsync( username );
		await _page.GetByLabel( "Password" ).FillAsync( password );
		await _page.GetByRole( AriaRole.Button, new PageGetByRoleOptions { Name = "Sign in" } ).ClickAsync();
		await _page.WaitForLoadStateAsync( LoadState.DOMContentLoaded );
		await WaitForCircuitAsync();
	}

	/// <summary>
	///     Waits until the interactive circuit has rendered (FocusOnNavigate marks the heading). Every page is interactive, and the first interactive render
	///     replaces the prerendered DOM, so input typed or clicks made before that are lost.
	/// </summary>
	private async Task WaitForCircuitAsync() => await _page.WaitForSelectorAsync( "h1[tabindex='-1']", new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached } );

	private Task WaitForInteractivityAsync() => WaitForCircuitAsync();

	private ILocator LogRows => _page.Locator( "table.selectable tbody tr" );


	[Test]
	public async Task Login_RejectsWrongPassword_ThenSignsIn()
	{
		await SignInAsync( password: "wrong" );

		await Assertions.Expect( _page.Locator( ".login-error" ) ).ToHaveTextAsync( "Invalid username or password." );

		await WaitForCircuitAsync();
		await _page.GetByLabel( "Username" ).FillAsync( TestApi.ADMIN_USER );
		await _page.GetByLabel( "Password" ).FillAsync( TestApi.ADMIN_PASSWORD );
		await _page.GetByRole( AriaRole.Button, new PageGetByRoleOptions { Name = "Sign in" } ).ClickAsync();

		await Assertions.Expect( _page ).ToHaveURLAsync( Url( "/" ) );
		await Assertions.Expect( _page.Locator( ".shell-user" ) ).ToHaveTextAsync( TestApi.ADMIN_USER );
	}

	[Test]
	public async Task Dashboard_ShowsTelemetry_FiltersAndPivotsToTheTrace()
	{
		await PostAsync( "/v1/logs",   Otlp.Logs( "checkout", Otlp.Log( "payment declined", SeverityNumber.Error, "Error" ), Otlp.Log( "cart loaded" ) ) );
		await PostAsync( "/v1/traces", Otlp.Traces( Otlp.Span( "POST /checkout" ) ) );
		await PostAsync( "/v1/metrics", Otlp.Metrics( Otlp.Gauge( "checkout.queue" ) ) );

		await SignInAsync();
		await WaitForInteractivityAsync();

		await Assertions.Expect( LogRows ).ToHaveCountAsync( 2 );
		await Assertions.Expect( _page.Locator( ".stat-grid" ) ).ToContainTextAsync( "Total Logs" );

		await _page.GetByPlaceholder( "Search message" ).FillAsync( "declined" );
		await Assertions.Expect( LogRows ).ToHaveCountAsync( 1 );

		await LogRows.First.ClickAsync();
		await Assertions.Expect( _page.Locator( ".details-card" ) ).ToContainTextAsync( "payment declined" );

		await _page.GetByRole( AriaRole.Button, new PageGetByRoleOptions { Name = "Whole trace" } ).ClickAsync();
		await Assertions.Expect( LogRows ).ToHaveCountAsync( 1 );
		await Assertions.Expect( LogRows.First ).ToContainTextAsync( "POST /checkout" );

		await _page.GetByRole( AriaRole.Button, new PageGetByRoleOptions { Name = "Metrics" } ).ClickAsync();
		await _page.GetByPlaceholder( "Search message" ).FillAsync( "" );
		await Assertions.Expect( LogRows.First ).ToContainTextAsync( "checkout.queue" );
	}

	[Test]
	public async Task Dashboard_RefreshesLive_AndCanBePaused()
	{
		await SignInAsync();
		await WaitForInteractivityAsync();
		await Assertions.Expect( _page.Locator( ".status-pill" ) ).ToHaveTextAsync( "Live" );

		await PostAsync( "/v1/logs", Otlp.Logs( "live", Otlp.Log( "arrived while watching" ) ) );
		await Assertions.Expect( _page.GetByText( "arrived while watching" ) ).ToBeVisibleAsync();
		await Assertions.Expect( _page.Locator( ".status-detail" ) ).ToContainTextAsync( "Ingested" );

		await _page.GetByRole( AriaRole.Button, new PageGetByRoleOptions { Name = "Pause live updates" } ).ClickAsync();
		await Assertions.Expect( _page.Locator( ".status-pill" ) ).ToHaveTextAsync( "Paused" );

		await PostAsync( "/v1/logs", Otlp.Logs( "live", Otlp.Log( "arrived while paused" ) ) );
		await Assertions.Expect( _page.Locator( ".status-detail" ) ).ToContainTextAsync( "Ingested 1 logs" );
		await Assertions.Expect( _page.GetByText( "arrived while paused" ) ).ToHaveCountAsync( 0 );

		await _page.GetByRole( AriaRole.Button, new PageGetByRoleOptions { Name = "Resume live updates" } ).ClickAsync();
		await Assertions.Expect( _page.GetByText( "arrived while paused" ) ).ToBeVisibleAsync();
	}

	[Test]
	public async Task Screenshots_ShowUploadedAndPendingImages_WithLightboxAndFilters()
	{
		ImageTag uploaded = ImageTag.Create( "uploaded.png" );
		ImageTag pending  = ImageTag.Create( "pending.png" );
		await UploadAsync( uploaded, TestImages.Png );
		await PostAsync( "/v1/logs", Otlp.Logs( "shop", Otlp.Log( "checkout crashed", SeverityNumber.Error, "Error", Otlp.Kv( LogTags.ATTRIBUTE_KEY, Otlp.Array( Otlp.Str( uploaded.ToString() ), Otlp.Str( pending.ToString() ) ) ), Otlp.Kv( "exception.message", "card declined" ) ) ) );
		await PostAsync( "/v1/logs", Otlp.Logs( "admin", Otlp.Log( "info with image", SeverityNumber.Info, "Information", Otlp.Kv( LogTags.ATTRIBUTE_KEY, ImageTag.Create( "info.png" ).ToString() ) ) ) );

		await SignInAsync( path: "/screenshots" );
		await Assertions.Expect( _page.Locator( ".screenshot-card" ) ).ToHaveCountAsync( 1 );
		await Assertions.Expect( _page.Locator( ".screenshot-card" ) ).ToContainTextAsync( "card declined" );
		await Assertions.Expect( _page.Locator( ".screenshot-card" ) ).ToContainTextAsync( "1 / 2 uploaded" );
		await Assertions.Expect( _page.Locator( ".screenshot-tile.pending" ) ).ToContainTextAsync( "Pending upload" );

		// The thumbnail is actually served (decoded by the browser).
		ILocator thumbnail = _page.Locator( ".screenshot-tile img" );
		await Assertions.Expect( thumbnail ).ToHaveAttributeAsync( "src", $"/telemetry/images/{uploaded.ID:D}" );
		await _page.WaitForFunctionAsync( "() => { const img = document.querySelector('.screenshot-tile img'); return img && img.complete && img.naturalWidth > 0; }" );

		await _page.Locator( "button.screenshot-tile" ).ClickAsync();
		await Assertions.Expect( _page.GetByRole( AriaRole.Dialog ) ).ToBeVisibleAsync();
		await Assertions.Expect( _page.GetByRole( AriaRole.Dialog ) ).ToContainTextAsync( "image/png" );
		await _page.Keyboard.PressAsync( "Escape" );
		await Assertions.Expect( _page.GetByRole( AriaRole.Dialog ) ).ToHaveCountAsync( 0 );

		await _page.GetByLabel( "Severity" ).SelectOptionAsync( "all" );
		await Assertions.Expect( _page ).ToHaveURLAsync( new Regex( "all=true", RegexOptions.IgnoreCase ) );
		await Assertions.Expect( _page.Locator( ".screenshot-card" ) ).ToHaveCountAsync( 2 );

		await _page.GetByLabel( "Service" ).SelectOptionAsync( "admin" );
		await Assertions.Expect( _page ).ToHaveURLAsync( new Regex( "service=admin" ) );
		await Assertions.Expect( _page.Locator( ".screenshot-card" ) ).ToHaveCountAsync( 1 );
		await Assertions.Expect( _page.Locator( ".screenshot-card" ) ).ToContainTextAsync( "info with image" );
	}

	[Test]
	public async Task Screenshots_DeepLinkFromTheDashboardShowsOneLog()
	{
		ImageTag tag = ImageTag.Create( "linked.png" );
		await UploadAsync( tag, TestImages.Png );
		await PostAsync( "/v1/logs", Otlp.Logs( "linked", Otlp.Log( "deep linked error", SeverityNumber.Error, "Error", Otlp.Kv( LogTags.ATTRIBUTE_KEY, tag.ToString() ) ), Otlp.Log( "other error", SeverityNumber.Error, "Error", Otlp.Kv( LogTags.ATTRIBUTE_KEY, ImageTag.Create( "x.png" ).ToString() ) ) ) );

		await SignInAsync();
		await WaitForInteractivityAsync();
		await _page.GetByPlaceholder( "Search message" ).FillAsync( "deep linked" );
		await LogRows.First.ClickAsync();
		await _page.GetByRole( AriaRole.Link, new PageGetByRoleOptions { Name = "Open in screenshots" } ).ClickAsync();

		await Assertions.Expect( _page ).ToHaveURLAsync( new Regex( @"/screenshots\?log=" ) );
		await Assertions.Expect( _page.Locator( ".screenshot-card" ) ).ToHaveCountAsync( 1 );
		await Assertions.Expect( _page.Locator( ".notice" ) ).ToContainTextAsync( "Showing a single log" );

		await _page.GetByRole( AriaRole.Link, new PageGetByRoleOptions { Name = "Show all screenshots" } ).ClickAsync();
		await Assertions.Expect( _page.Locator( ".screenshot-card" ) ).ToHaveCountAsync( 2 );
	}

	[Test]
	public async Task SignOut_ReturnsToLogin()
	{
		await SignInAsync();
		await _page.GetByRole( AriaRole.Button, new PageGetByRoleOptions { Name = "Sign out" } ).ClickAsync();

		await Assertions.Expect( _page ).ToHaveURLAsync( new Regex( "/login" ) );

		await _page.GotoAsync( Url( "/screenshots" ) );
		await Assertions.Expect( _page ).ToHaveURLAsync( new Regex( @"/login\?ReturnUrl=%2Fscreenshots" ) );
	}

	[Test]
	public async Task SwaggerUi_LoadsForSignedInUsers()
	{
		await SignInAsync();

		await _page.GotoAsync( Url( "/swagger/index.html" ) );

		await Assertions.Expect( _page.Locator( ".info .title" ) ).ToContainTextAsync( "Jakar.OpenTelemetry.Api" );
		await Assertions.Expect( _page.Locator( ".opblock-tag", new PageLocatorOptions { HasText = "OTLP gRPC" } ) ).ToBeVisibleAsync();
		await Assertions.Expect( _page.GetByText( "/OpenTelemetry.Proto.Collector.Logs.V1.LogsService/Export" ) ).ToBeVisibleAsync();
	}

	[Test]
	public async Task PhoneWidth_HasNoHorizontalScroll()
	{
		await _context.DisposeAsync();
		_context = await Browsers.NewContextAsync( new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 375, Height = 812 }, IsMobile = true, HasTouch = true } );
		_page    = await _context.NewPageAsync();
		await PostAsync( "/v1/logs", Otlp.Logs( "mobile", Otlp.Log( new string( 'w', 400 ) ) ) );

		await SignInAsync();
		await WaitForInteractivityAsync();

		foreach ( string path in new[] { "/", "/screenshots", "/login" } )
		{
			await _page.GotoAsync( Url( path ) );
			await WaitForCircuitAsync();
			bool overflows = await _page.EvaluateAsync<bool>( "() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1" );
			Assert.That( overflows, Is.False, $"{path} scrolls horizontally at phone width" );
		}
	}
}
