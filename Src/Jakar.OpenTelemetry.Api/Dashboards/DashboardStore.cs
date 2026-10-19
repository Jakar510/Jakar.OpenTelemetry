using System.Security.Claims;
using Npgsql;
using NpgsqlTypes;

namespace Jakar.OpenTelemetry.Api.Dashboards;

/// <summary> A row of the <c>"Dashboards"</c> table. <see cref="Version"/> guards against overwriting someone else's save. </summary>
public sealed record DashboardRecord( Guid                ID,
									  string              Name,
									  string?             Description,
									  string              Owner,
									  DashboardVisibility Visibility,
									  string[]            Roles,
									  DashboardDefinition Definition,
									  bool                IsStarter,
									  DateTimeOffset      CreatedAtUtc,
									  DateTimeOffset      UpdatedAtUtc,
									  int                 Version );



public enum DashboardSaveResult
{
	Saved,
	NotFound,
	Forbidden,

	/// <summary> Someone saved it since it was loaded; reload before saving again. </summary>
	Conflict
}



/// <summary> Reads and writes dashboards, enforcing <see cref="DashboardPermissions"/> on every call. </summary>
public sealed class DashboardStore( NpgsqlDataSource dataSource, DashboardPermissions permissions, TimeProvider? time = null )
{
	private const string COLUMNS = """
								   "ID", "Name", "Description", "Owner", "Visibility", "Roles", "Definition", "IsStarter", "CreatedAtUtc", "UpdatedAtUtc", "Version"
								   """;

	private readonly TimeProvider _time = time ?? TimeProvider.System;


	/// <summary> The dashboards this user can open, starters first, then by name. </summary>
	public async Task<IReadOnlyList<DashboardRecord>> ListAsync( ClaimsPrincipal user, CancellationToken token )
	{
		await using NpgsqlCommand command = dataSource.CreateCommand( $"""SELECT {COLUMNS} FROM "Dashboards" ORDER BY "IsStarter" DESC, lower("Name"), "ID" """ );
		List<DashboardRecord>     records = await ReadAllAsync( command, token );
		return records.Where( record => DashboardPermissions.CanView( record, user ) ).ToArray();
	}

	public async Task<DashboardRecord?> GetAsync( Guid id, ClaimsPrincipal user, CancellationToken token )
	{
		DashboardRecord? record = await GetAsync( id, token );
		return record is not null && DashboardPermissions.CanView( record, user )
				   ? record
				   : null;
	}

	/// <summary> A new dashboard owned by <paramref name="user"/>. Only admins may share it; a viewer's dashboard is always private. </summary>
	public async Task<DashboardRecord?> CreateAsync( string name, string? description, DashboardDefinition definition, ClaimsPrincipal user, CancellationToken token, DashboardVisibility visibility = DashboardVisibility.Private, string[]? roles = null )
	{
		if ( !permissions.CanCreate( user ) ) { return null; }

		DateTimeOffset now = _time.GetUtcNow();
		DashboardRecord record = new(Guid.CreateVersion7(),
									 CleanName( name ),
									 CleanDescription( description ),
									 DashboardPermissions.UserName( user ),
									 DashboardPermissions.CanShare( user )
										 ? visibility
										 : DashboardVisibility.Private,
									 DashboardPermissions.CanShare( user )
										 ? CleanRoles( roles )
										 : [ ],
									 definition.Normalize(),
									 false,
									 now,
									 now,
									 1);

		await InsertAsync( record, token );
		return record;
	}

	/// <summary> Saves name, description and definition (and, for admins, visibility and roles) when <paramref name="record"/>'s version is still current. </summary>
	public async Task<(DashboardSaveResult Result, DashboardRecord? Saved)> SaveAsync( DashboardRecord record, ClaimsPrincipal user, CancellationToken token )
	{
		DashboardRecord? current = await GetAsync( record.ID, token );
		if ( current is null || !DashboardPermissions.CanView( current, user ) ) { return ( DashboardSaveResult.NotFound, null ); }

		if ( !permissions.CanEdit( current, user ) ) { return ( DashboardSaveResult.Forbidden, null ); }

		bool share = DashboardPermissions.CanShare( user );
		DashboardRecord saved = current with
								{
									Name = CleanName( record.Name ),
									Description = CleanDescription( record.Description ),
									Definition = record.Definition.Normalize(),
									Visibility = share
													 ? record.Visibility
													 : current.Visibility,
									Roles = share
												? CleanRoles( record.Roles )
												: current.Roles,
									UpdatedAtUtc = _time.GetUtcNow(),
									Version = record.Version + 1
								};

		await using NpgsqlCommand command = dataSource.CreateCommand( """
																	  UPDATE "Dashboards"
																	  SET "Name" = @name, "Description" = @description, "Visibility" = @visibility, "Roles" = @roles, "Definition" = @definition,
																	      "UpdatedAtUtc" = @updated, "Version" = @version
																	  WHERE "ID" = @id AND "Version" = @expected
																	  """ );

		AddParameters( command, saved );
		command.Parameters.AddWithValue( "expected", record.Version );

		return await command.ExecuteNonQueryAsync( token ) == 1
				   ? ( DashboardSaveResult.Saved, saved )
				   : ( DashboardSaveResult.Conflict, null );
	}

	public async Task<DashboardSaveResult> DeleteAsync( Guid id, ClaimsPrincipal user, CancellationToken token )
	{
		DashboardRecord? current = await GetAsync( id, token );
		if ( current is null || !DashboardPermissions.CanView( current, user ) ) { return DashboardSaveResult.NotFound; }

		if ( !permissions.CanEdit( current, user ) ) { return DashboardSaveResult.Forbidden; }

		await using NpgsqlCommand command = dataSource.CreateCommand( """DELETE FROM "Dashboards" WHERE "ID" = @id""" );
		command.Parameters.AddWithValue( "id", id );
		await command.ExecuteNonQueryAsync( token );
		return DashboardSaveResult.Saved;
	}

	/// <summary> Adds the starter dashboards that aren't there yet (by their fixed ids), so an admin's edits or deletion of one are never undone. </summary>
	public async Task<int> EnsureStartersAsync( IEnumerable<DashboardRecord> starters, CancellationToken token )
	{
		int added = 0;

		foreach ( DashboardRecord starter in starters )
		{
			await using NpgsqlCommand command = dataSource.CreateCommand( $"""
																		   INSERT INTO "Dashboards" ({COLUMNS})
																		   VALUES (@id, @name, @description, @owner, @visibility, @roles, @definition, true, @created, @updated, @version)
																		   ON CONFLICT ("ID") DO NOTHING
																		   """ );

			AddParameters( command, starter );
			command.Parameters.AddWithValue( "owner",   starter.Owner );
			command.Parameters.AddWithValue( "created", starter.CreatedAtUtc );
			added += await command.ExecuteNonQueryAsync( token );
		}

		return added;
	}


	private async Task<DashboardRecord?> GetAsync( Guid id, CancellationToken token )
	{
		await using NpgsqlCommand command = dataSource.CreateCommand( $"""SELECT {COLUMNS} FROM "Dashboards" WHERE "ID" = @id""" );
		command.Parameters.AddWithValue( "id", id );
		return ( await ReadAllAsync( command, token ) ).SingleOrDefault();
	}

	private async Task InsertAsync( DashboardRecord record, CancellationToken token )
	{
		await using NpgsqlCommand command = dataSource.CreateCommand( $"""
																	   INSERT INTO "Dashboards" ({COLUMNS})
																	   VALUES (@id, @name, @description, @owner, @visibility, @roles, @definition, @starter, @created, @updated, @version)
																	   """ );

		AddParameters( command, record );
		command.Parameters.AddWithValue( "owner",   record.Owner );
		command.Parameters.AddWithValue( "starter", record.IsStarter );
		command.Parameters.AddWithValue( "created", record.CreatedAtUtc );
		await command.ExecuteNonQueryAsync( token );
	}

	private static void AddParameters( NpgsqlCommand command, DashboardRecord record )
	{
		command.Parameters.AddWithValue( "id",          record.ID );
		command.Parameters.AddWithValue( "name",        record.Name );
		command.Parameters.AddWithValue( "description", (object?)record.Description ?? DBNull.Value );
		command.Parameters.AddWithValue( "visibility",  record.Visibility.ToString() );
		command.Parameters.AddWithValue( "roles",       record.Roles );
		command.Parameters.Add( new NpgsqlParameter( "definition", NpgsqlDbType.Jsonb ) { Value = record.Definition.ToJson() } );
		command.Parameters.AddWithValue( "updated", record.UpdatedAtUtc );
		command.Parameters.AddWithValue( "version", record.Version );
	}

	private static async Task<List<DashboardRecord>> ReadAllAsync( NpgsqlCommand command, CancellationToken token )
	{
		List<DashboardRecord>        records = [ ];
		await using NpgsqlDataReader reader  = await command.ExecuteReaderAsync( token );

		while ( await reader.ReadAsync( token ) )
		{
			records.Add( new DashboardRecord( reader.GetGuid( 0 ),
											  reader.GetString( 1 ),
											  reader.IsDBNull( 2 )
												  ? null
												  : reader.GetString( 2 ),
											  reader.GetString( 3 ),
											  Enum.TryParse( reader.GetString( 4 ), out DashboardVisibility visibility )
												  ? visibility
												  : DashboardVisibility.Private,
											  reader.GetFieldValue<string[]>( 5 ),
											  DashboardDefinition.Load( reader.GetString( 6 ) ),
											  reader.GetBoolean( 7 ),
											  reader.GetFieldValue<DateTimeOffset>( 8 ),
											  reader.GetFieldValue<DateTimeOffset>( 9 ),
											  reader.GetInt32( 10 ) ) );
		}

		return records;
	}

	internal static string CleanName( string? name ) => string.IsNullOrWhiteSpace( name )
															? "Untitled dashboard"
															: name.Trim()[..Math.Min( name.Trim().Length, 120 )];

	internal static string? CleanDescription( string? description ) => string.IsNullOrWhiteSpace( description )
																		   ? null
																		   : description.Trim()[..Math.Min( description.Trim().Length, 500 )];

	internal static string[] CleanRoles( string[]? roles ) => roles?.Where( static x => !string.IsNullOrWhiteSpace( x ) ).Select( static x => x.Trim() ).Distinct( StringComparer.OrdinalIgnoreCase ).ToArray() ?? [ ];
}
