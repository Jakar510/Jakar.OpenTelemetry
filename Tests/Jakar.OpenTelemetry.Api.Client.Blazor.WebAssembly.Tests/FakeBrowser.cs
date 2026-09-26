using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.JSInterop;

namespace Jakar.OpenTelemetry.Api.Client.Blazor.WebAssembly.Tests;

/// <summary>
///     In-memory stand-in for <c>jakar-otel.js</c>: localStorage, the IndexedDB image store, screenshot capture and install/uninstall.
///     Every call is recorded; <see cref="Fail"/> makes the next calls of one function throw <see cref="JSException"/>.
/// </summary>
public sealed class FakeJsModule : IJSObjectReference
{
	private readonly ConcurrentDictionary<string, int> _failures = new();


	public ConcurrentDictionary<string, string>                          Storage     { get; } = new();
	public ConcurrentDictionary<string, (string Meta, byte[] Data)>      Images      { get; } = new();
	public ConcurrentQueue<(string Identifier, object?[] Arguments)>     Calls       { get; } = new();
	public byte[]?                                                       Screenshot  { get; set; }
	public bool                                                          StorageFull { get; set; }
	public bool                                                          Disposed    { get; private set; }


	public IEnumerable<object?[]> CallsTo( string identifier ) => Calls.Where( x => x.Identifier == identifier ).Select( static x => x.Arguments );

	public void Fail( string identifier, int times = int.MaxValue ) => _failures[identifier] = times;


	public ValueTask<TValue> InvokeAsync<TValue>( string identifier, object?[]? args ) => InvokeAsync<TValue>( identifier, CancellationToken.None, args );

	public ValueTask<TValue> InvokeAsync<TValue>( string identifier, CancellationToken cancellationToken, object?[]? args )
	{
		args ??= [ ];
		Calls.Enqueue( ( identifier, args ) );

		if ( _failures.TryGetValue( identifier, out int remaining ) && remaining > 0 )
		{
			_failures[identifier] = remaining - 1;
			return ValueTask.FromException<TValue>( new JSException( $"{identifier} failed" ) );
		}

		object? result = identifier switch
						 {
							 "storageGet"        => Storage.TryGetValue( (string)args[0]!, out string? value ) ? value : null,
							 "storageSet"        => SetStorage( (string)args[0]!, (string?)args[1] ),
							 "imagePut"          => Images[(string)args[0]!] = ( (string)args[1]!, (byte[])args[2]! ),
							 "imageUpdate"       => Images.TryGetValue( (string)args[0]!, out var existing ) ? Images[(string)args[0]!] = ( (string)args[1]!, existing.Data ) : null,
							 "imageMetas"        => Images.Values.Select( static x => x.Meta ).ToArray(),
							 "imageGet"          => Images.TryGetValue( (string)args[0]!, out var image ) ? image.Data : null,
							 "imageDelete"       => Images.TryRemove( (string)args[0]!, out _ ),
							 "captureScreenshot" => Screenshot,
							 _                   => null
						 };

		return ValueTask.FromResult( result is TValue typed
										 ? typed
										 : default! );
	}

	public ValueTask DisposeAsync()
	{
		Disposed = true;
		return ValueTask.CompletedTask;
	}

	private bool SetStorage( string key, string? value )
	{
		if ( StorageFull ) { return false; }

		if ( value is null ) { Storage.TryRemove( key, out _ ); }
		else { Storage[key] = value; }

		return true;
	}
}

/// <summary> <see cref="IJSRuntime"/> whose <c>import</c> returns one shared <see cref="FakeJsModule"/>. </summary>
public sealed class FakeJsRuntime : IJSRuntime
{
	public FakeJsModule Module  { get; } = new();
	public int          Imports { get; private set; }

	public Exception? ImportFailure { get; set; }


	public ValueTask<TValue> InvokeAsync<[DynamicallyAccessedMembers( DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties )] TValue>( string identifier, object?[]? args ) => InvokeAsync<TValue>( identifier, CancellationToken.None, args );

	public ValueTask<TValue> InvokeAsync<[DynamicallyAccessedMembers( DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties )] TValue>( string identifier, CancellationToken cancellationToken, object?[]? args )
	{
		if ( identifier != "import" ) { throw new InvalidOperationException( $"Unexpected global JS call '{identifier}'" ); }

		if ( !Equals( args?[0], BrowserTelemetryModule.MODULE_PATH ) ) { throw new InvalidOperationException( $"Unexpected module '{args?[0]}'" ); }

		if ( ImportFailure is { } failure ) { return ValueTask.FromException<TValue>( failure ); }

		Imports++;
		return ValueTask.FromResult( (TValue)(object)Module );
	}
}
