using System.Net;
using System.Net.Http.Headers;
using Jakar.OpenTelemetry.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jakar.OpenTelemetry.Api.Client.Images;

/// <summary> Optional platform check (connectivity, metered network, battery saver) consulted before each upload pass. </summary>
public interface ITelemetryUploadGate
{
	bool CanUpload { get; }
}

/// <summary>
///     Uploads queued images to <c>PUT /v1/images/{id}</c> "when the server can accept them":
///     <list type="bullet">
///         <item> <c>429</c>/<c>503</c> (server at capacity) pauses all uploads until <c>Retry-After</c>; </item>
///         <item> network errors, timeouts and <c>5xx</c> retry that image with exponential backoff and jitter; </item>
///         <item> <c>401</c>/<c>403</c> keep the image and retry at the maximum delay (a configuration problem must not lose data); </item>
///         <item> <c>400</c>/<c>409</c>/<c>413</c>/<c>415</c> can never succeed and drop the image. </item>
///     </list>
///     Uploads are idempotent on the server, so a retry after an ambiguous failure never duplicates an image.
/// </summary>
public sealed class TelemetryImageUploader : IAsyncDisposable
{
	public const string HTTP_CLIENT_NAME = "Jakar.OpenTelemetry.Images";

	private readonly ITelemetryImageQueue            _queue;
	private readonly IHttpClientFactory              _httpClientFactory;
	private readonly ITelemetryUploadGate[]          _gates;
	private readonly JakarTelemetryOptions           _options;
	private readonly ILogger<TelemetryImageUploader> _logger;
	private readonly TimeProvider                    _time;
	private readonly SemaphoreSlim                   _wake = new(0, 1);
	private readonly SemaphoreSlim                   _pass = new(1, 1);
	private          CancellationTokenSource?        _stopping;
	private          Task?                           _loop;
	private          DateTimeOffset                  _serverBusyUntil;


	public TelemetryImageUploader( ITelemetryImageQueue queue, IHttpClientFactory httpClientFactory, IEnumerable<ITelemetryUploadGate> gates, IOptions<JakarTelemetryOptions> options, ILogger<TelemetryImageUploader> logger, TimeProvider? time = null )
	{
		_queue             = queue;
		_httpClientFactory = httpClientFactory;
		_gates             = gates.ToArray();
		_options           = options.Value;
		_logger            = logger;
		_time              = time ?? TimeProvider.System;
	}


	public void Start()
	{
		if ( !_options.Images.Enabled ||
			 _loop is not null ) { return; }

		_stopping = new CancellationTokenSource();
		_loop     = Task.Run( () => RunAsync( _stopping.Token ) );
	}

	public async Task StopAsync()
	{
		if ( _stopping is null ||
			 _loop is null ) { return; }

		await _stopping.CancelAsync();

		try { await _loop; }
		catch ( OperationCanceledException ) { }

		_loop = null;
	}

	public async ValueTask DisposeAsync()
	{
		await StopAsync();
		_stopping?.Dispose();
		_wake.Dispose();
		_pass.Dispose();
	}

	/// <summary> Wakes the loop (new image queued, network came back, app resumed). </summary>
	public void Signal()
	{
		try
		{
			if ( _wake.CurrentCount == 0 ) { _wake.Release(); }
		}
		catch ( SemaphoreFullException ) { }
		catch ( ObjectDisposedException ) { }
	}

	/// <summary> Runs one upload pass now, honoring server back-pressure. Returns the number of images still pending. </summary>
	public async Task<int> UploadNowAsync( CancellationToken token = default )
	{
		await UploadDueAsync( token );
		return await _queue.CountAsync( token );
	}


	private async Task RunAsync( CancellationToken token )
	{
		while ( !token.IsCancellationRequested )
		{
			try { await UploadDueAsync( token ); }
			catch ( OperationCanceledException ) when ( token.IsCancellationRequested ) { return; }
			catch ( Exception e ) { _logger.LogWarning( e, "Image upload pass failed, retrying next interval" ); }

			try { await _wake.WaitAsync( _options.Images.PollInterval, token ); }
			catch ( OperationCanceledException ) { return; }
		}
	}

	private async Task UploadDueAsync( CancellationToken token )
	{
		if ( !_options.Images.Enabled ||
			 !_gates.All( static gate => gate.CanUpload ) ) { return; }

		await _pass.WaitAsync( token );

		try
		{
			while ( !token.IsCancellationRequested )
			{
				DateTimeOffset now = _time.GetUtcNow();
				if ( now < _serverBusyUntil ) { return; }

				IReadOnlyList<PendingImage> due = await _queue.GetDueAsync( now, _options.Images.MaxConcurrentUploads * 4, token );
				if ( due.Count == 0 ) { return; }

				using CancellationTokenSource busy = CancellationTokenSource.CreateLinkedTokenSource( token );

				try
				{
					await Parallel.ForEachAsync( due,
												 new ParallelOptions { MaxDegreeOfParallelism = _options.Images.MaxConcurrentUploads, CancellationToken = busy.Token },
												 async ( image, ct ) =>
												 {
													 if ( await UploadAsync( image, ct ) == UploadOutcome.ServerBusy ) { await busy.CancelAsync(); }
												 } );
				}
				catch ( OperationCanceledException ) when ( !token.IsCancellationRequested ) { return; } // server asked us to back off
			}
		}
		finally { _pass.Release(); }
	}

	private async Task<UploadOutcome> UploadAsync( PendingImage image, CancellationToken token )
	{
		byte[]? data = await _queue.ReadAsync( image, token );

		if ( data is null )
		{
			await _queue.CompleteAsync( image.ID, token );
			return UploadOutcome.Dropped;
		}

		using HttpRequestMessage request = new(HttpMethod.Put, new Uri( _options.ImageBaseAddress, LogTags.ImagePath( image.ID ) ));
		request.Content                     = new ByteArrayContent( data );
		request.Content.Headers.ContentType = new MediaTypeHeaderValue( image.ContentType );
		request.Headers.TryAddWithoutValidation( _options.ApiKeyHeaderName, _options.ApiKey );
		request.Headers.TryAddWithoutValidation( LogTags.FILE_NAME_HEADER,  Uri.EscapeDataString( image.FileName ) );
		request.Headers.TryAddWithoutValidation( LogTags.SHA256_HEADER,     image.Sha256 );

		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource( token );
		timeout.CancelAfter( _options.Images.RequestTimeout );

		HttpResponseMessage response;

		try { response = await _httpClientFactory.CreateClient( HTTP_CLIENT_NAME ).SendAsync( request, timeout.Token ); }
		catch ( Exception e ) when ( e is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested )
		{
			await RetryLaterAsync( image, e.Message, token );
			return UploadOutcome.Retry;
		}

		using ( response )
		{
			switch ( response.StatusCode )
			{
				case HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent:
					await _queue.CompleteAsync( image.ID, token );
					_logger.LogDebug( "Uploaded image {ImageId} ({FileName})", image.ID, image.FileName );
					return UploadOutcome.Uploaded;

				case HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable:
					TimeSpan       delay = RetryAfter( response ) ?? _options.Images.InitialRetryDelay;
					DateTimeOffset until = _time.GetUtcNow() + delay;
					_serverBusyUntil = until;
					await _queue.RescheduleAsync( image, until, $"server busy ({(int)response.StatusCode})", token );
					_logger.LogInformation( "Image server at capacity, pausing uploads for {Delay}", delay );
					return UploadOutcome.ServerBusy;

				case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
					await _queue.RescheduleAsync( image, _time.GetUtcNow() + _options.Images.MaxRetryDelay, $"rejected credentials ({(int)response.StatusCode})", token );
					_logger.LogError( "Image upload rejected with {StatusCode}, check {Header}; keeping image {ImageId} for later", (int)response.StatusCode, _options.ApiKeyHeaderName, image.ID );
					return UploadOutcome.Retry;

				case HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnsupportedMediaType:
					await _queue.CompleteAsync( image.ID, token );
					_logger.LogError( "Image {ImageId} ({FileName}) permanently rejected with {StatusCode}: {Reason}, dropping it", image.ID, image.FileName, (int)response.StatusCode, await ReadReasonAsync( response, token ) );
					return UploadOutcome.Dropped;

				default:
					await RetryLaterAsync( image, $"HTTP {(int)response.StatusCode}", token );
					return UploadOutcome.Retry;
			}
		}
	}

	private async Task RetryLaterAsync( PendingImage image, string error, CancellationToken token )
	{
		// Exponential backoff with +/-20% jitter so many devices coming back online do not retry in lockstep.
		double exponent = Math.Min( image.Attempts,                             20 );
		double seconds  = Math.Min( _options.Images.MaxRetryDelay.TotalSeconds, _options.Images.InitialRetryDelay.TotalSeconds * Math.Pow( 2, exponent ) );
		seconds *= 0.8 + Random.Shared.NextDouble() * 0.4;

		await _queue.RescheduleAsync( image, _time.GetUtcNow() + TimeSpan.FromSeconds( seconds ), error, token );
		_logger.LogDebug( "Image {ImageId} upload failed ({Error}), retrying in {Delay:0}s", image.ID, error, seconds );
	}

	private static TimeSpan? RetryAfter( HttpResponseMessage response )
	{
		RetryConditionHeaderValue? retryAfter = response.Headers.RetryAfter;
		if ( retryAfter?.Delta is { } delta ) { return delta; }

		return retryAfter?.Date is { } date && date > DateTimeOffset.UtcNow
				   ? date - DateTimeOffset.UtcNow
				   : null;
	}

	private static async Task<string> ReadReasonAsync( HttpResponseMessage response, CancellationToken token )
	{
		try
		{
			string body = await response.Content.ReadAsStringAsync( token );
			return body.Length > 512
					   ? body[..512]
					   : body;
		}
		catch ( Exception ) { return response.ReasonPhrase ?? string.Empty; }
	}


	private enum UploadOutcome
	{
		Uploaded,
		Retry,
		Dropped,
		ServerBusy
	}
}
