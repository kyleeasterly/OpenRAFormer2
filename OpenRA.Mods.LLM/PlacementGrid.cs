#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenRA.Mods.Common.Traits;

namespace OpenRA.Mods.LLM
{
	/// <summary>
	/// ASCII map of the area around a player's base, specialized for placing one
	/// specific pending building: '+' marks cells where THAT building can legally go
	/// (terrain, footprint, and base-adjacency all checked), so agents pick a '+'
	/// and reason about position, not legality.
	/// </summary>
	public static class PlacementGrid
	{
		public const string Legend =
			"+ you can place here, B your building, E enemy building, T tiberium, U unit in the way, . cannot place (blocked terrain or too far from your base)";

		public static (CPos Origin, string[] Rows, List<CPos> ValidCells) Build(
			World world, Player player, ActorInfo ai, BuildingInfo bi, int radius)
		{
			var center = BaseCenter(world, player);
			var rows = new List<string>();
			var valid = new List<CPos>();
			var origin = new CPos(center.X - radius, center.Y - radius);

			var resources = world.WorldActor.TraitOrDefault<IResourceLayer>();

			for (var y = center.Y - radius; y <= center.Y + radius; y++)
			{
				var row = new StringBuilder();
				for (var x = center.X - radius; x <= center.X + radius; x++)
				{
					var cell = new CPos(x, y);
					row.Append(Classify(world, player, ai, bi, resources, cell, valid));
				}

				rows.Add(row.ToString());
			}

			return (origin, rows.ToArray(), valid);
		}

		static char Classify(World world, Player player, ActorInfo ai, BuildingInfo bi,
			IResourceLayer resources, CPos cell, List<CPos> valid)
		{
			// Placement legality checks dynamic blockers across the footprint. Only
			// expose visible footprints so the grid cannot reveal units through fog.
			if (!world.Map.Contains(cell) || bi.Tiles(cell).Any(t => !player.Shroud.IsVisible(t)))
				return '.';

			var actors = world.ActorMap.GetActorsAt(cell)
				.Where(a => !a.IsDead && a.IsInWorld && a.CanBeViewedByPlayer(player)).ToList();
			var building = actors.FirstOrDefault(a => a.Info.HasTraitInfo<BuildingInfo>());
			if (building != null)
				return building.Owner == player ? 'B'
					: building.Owner.NonCombatant ? '.'
					: 'E';

			if (resources != null && resources.GetResource(cell).Type != null)
				return 'T';

			if (world.CanPlaceBuilding(cell, ai, bi, null) && bi.IsCloseEnoughToBase(world, player, ai, cell))
			{
				valid.Add(cell);
				return '+';
			}

			return actors.Any(a => a.Info.HasTraitInfo<MobileInfo>()) ? 'U' : '.';
		}

		public static CPos BaseCenter(World world, Player player)
		{
			var buildings = world.Actors
				.Where(a => a.Owner == player && !a.IsDead && a.Info.HasTraitInfo<BuildingInfo>())
				.ToList();

			return buildings.Count > 0
				? world.Map.CellContaining(new WPos(
					(int)(buildings.Sum(b => (long)b.CenterPosition.X) / buildings.Count),
					(int)(buildings.Sum(b => (long)b.CenterPosition.Y) / buildings.Count), 0))
				: player.HomeLocation;
		}
	}
}
