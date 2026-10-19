using System.Reflection;

namespace Jakar.OpenTelemetry.Api.Client.Maui.Tests;

/// <summary>
///     MAUI Essentials (<c>AppInfo.Current</c>, <c>Connectivity.Current</c>, ...) throw on the platform-neutral target. Each facade keeps its implementation in a
///     private static field; this swaps in a test double and restores the original on dispose.
/// </summary>
internal sealed class Essentials : IDisposable
{
	private readonly List<(FieldInfo Field, object? Original)> _replaced = [ ];


	public Essentials Use<TImplementation>( Type facade, TImplementation implementation ) where TImplementation : class
	{
		FieldInfo field = facade.GetFields( BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public ).Single( static x => x.FieldType == typeof(TImplementation) );
		_replaced.Add( ( field, field.GetValue( null ) ) );
		field.SetValue( null, implementation );
		return this;
	}

	public void Dispose()
	{
		foreach ( ( FieldInfo field, object? original ) in Enumerable.Reverse( _replaced ) ) { field.SetValue( null, original ); }
	}
}
