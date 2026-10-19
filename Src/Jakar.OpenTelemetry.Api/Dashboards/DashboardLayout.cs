namespace Jakar.OpenTelemetry.Api.Dashboards;

/// <summary> Edits to a dashboard's panels. Panels flow left to right in a 12-column grid, so moving one changes its place in the list. </summary>
public static class DashboardLayout
{
	public static DashboardDefinition Add( DashboardDefinition definition, DashboardPanel panel ) => ( definition with { Panels = [ .. definition.Panels, panel ] } ).Normalize();

	/// <summary> Replaces the panel with the same id (an edited panel); unknown ids are added. </summary>
	public static DashboardDefinition Replace( DashboardDefinition definition, DashboardPanel panel )
	{
		int index = definition.Panels.FindIndex( x => x.ID == panel.ID );
		if ( index < 0 ) { return Add( definition, panel ); }

		List<DashboardPanel> panels = [ .. definition.Panels ];
		panels[index] = panel;
		return ( definition with { Panels = panels } ).Normalize();
	}

	public static DashboardDefinition Remove( DashboardDefinition definition, string id ) => definition with { Panels = definition.Panels.Where( x => x.ID != id ).ToList() };

	/// <summary> A copy right after the original, with a new id. </summary>
	public static DashboardDefinition Duplicate( DashboardDefinition definition, string id )
	{
		int index = definition.Panels.FindIndex( x => x.ID == id );
		if ( index < 0 ) { return definition; }

		List<DashboardPanel> panels = [ .. definition.Panels ];
		panels.Insert( index + 1, panels[index] with { ID = string.Empty, Title = $"{panels[index].Title} (copy)" } );
		return ( definition with { Panels = panels } ).Normalize();
	}

	/// <summary> Moves a panel <paramref name="offset"/> places earlier (negative) or later in the flow, stopping at either end. </summary>
	public static DashboardDefinition Move( DashboardDefinition definition, string id, int offset )
	{
		int index = definition.Panels.FindIndex( x => x.ID == id );
		if ( index < 0 ) { return definition; }

		List<DashboardPanel> panels = [ .. definition.Panels ];
		DashboardPanel       panel  = panels[index];
		panels.RemoveAt( index );
		panels.Insert( Math.Clamp( index + offset, 0, panels.Count ), panel );
		return definition with { Panels = panels };
	}

	public static DashboardDefinition Resize( DashboardDefinition definition, string id, int widthChange, int heightChange ) =>
		definition with
		{
			Panels = definition.Panels.Select( x => x.ID == id
														? x with
														  {
															  Width = Math.Clamp( x.Width   + widthChange,  1, DashboardPanel.MAX_WIDTH ),
															  Height = Math.Clamp( x.Height + heightChange, 1, DashboardPanel.MAX_HEIGHT )
														  }
														: x )
							   .ToList()
		};
}
