using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Jakar.OpenTelemetry.Api.Client.Blazor.Server.Tests;

/// <summary> <see cref="Circuit"/> only has an internal constructor; builds one whose <see cref="Circuit.Id"/> is <paramref name="id"/>. </summary>
internal static class FakeCircuit
{
	private const BindingFlags ALL = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

	public static Circuit Create( string id )
	{
		Circuit   circuit   = (Circuit)RuntimeHelpers.GetUninitializedObject( typeof(Circuit) );
		FieldInfo hostField = typeof(Circuit).GetFields( ALL ).Single( static x => x.FieldType.Name == "CircuitHost" );
		object    host      = RuntimeHelpers.GetUninitializedObject( hostField.FieldType );

		PropertyInfo idProperty = hostField.FieldType.GetProperty( "CircuitId", ALL )!;
		object       circuitId  = Activator.CreateInstance( idProperty.PropertyType, ALL, null, [ "secret", id ], null )!;
		FieldInfo    backing    = hostField.FieldType.GetFields( ALL ).Single( x => x.FieldType == idProperty.PropertyType );

		backing.SetValue( host, circuitId );
		hostField.SetValue( circuit, host );
		return circuit;
	}
}
