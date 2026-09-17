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

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Pathfinder;
using OpenRA.Mods.Common.Traits;

namespace OpenRA.Mods.LLM
{
	/// <summary>Player-visible resource patches and prospective refinery docking routes.</summary>
	public static class JevSpatialState
	{
		static readonly CVec[] Neighbors = [new(1, 0), new(-1, 0), new(0, 1), new(0, -1)];
		sealed record Patch(string Id, List<CPos> Cells, int Density);
		sealed record Access(string Patch, int? TravelCells, int VisibleCells, int Density);
		sealed record Site(CPos Cell, CPos Dock, int OpenNeighbors, Access[] Patches);

		public static object Build(World world, Player player, List<ProductionQueue> queues)
		{
			var resources = world.WorldActor.TraitOrDefault<IResourceLayer>();
			var remaining = world.Map.AllCells.Where(c => player.Shroud.IsVisible(c)
				&& resources != null && resources.GetResource(c).Density > 0).ToHashSet();
			var patches = new List<Patch>();
			while (remaining.Count > 0)
			{
				var start = remaining.OrderBy(c => c.Y).ThenBy(c => c.X).First();
				var cells = new List<CPos>();
				var frontier = new Queue<CPos>();
				frontier.Enqueue(start);
				remaining.Remove(start);
				while (frontier.TryDequeue(out var cell))
				{
					cells.Add(cell);
					foreach (var offset in Neighbors)
						if (remaining.Remove(cell + offset))
							frontier.Enqueue(cell + offset);
				}

				patches.Add(new($"patch{start.X}_{start.Y}", cells, cells.Sum(c => resources.GetResource(c).Density)));
			}

			patches = patches.OrderByDescending(p => p.Density).Take(16).ToList();
			var owned = world.Actors.Where(a => a.Owner == player && !a.IsDead && a.IsInWorld).ToList();
			var refineryTypes = queues.SelectMany(q => q.AllItems()).Concat(owned.Select(a => a.Info))
				.Where(ai => ai.HasTraitInfo<RefineryInfo>()).DistinctBy(ai => ai.Name).ToList();
			var metadata = new Dictionary<string, object>();
			var placements = new List<object>();
			foreach (var ai in refineryTypes)
			{
				var bi = ai.TraitInfo<BuildingInfo>();
				var dock = ai.TraitInfo<DockHostInfo>();
				var free = ai.TraitInfoOrDefault<FreeActorInfo>();
				var harvester = free != null ? world.Map.Rules.Actors[free.Actor.ToLowerInvariant()] : null;
				var mobile = harvester?.TraitInfoOrDefault<MobileInfo>();
				var locomotor = world.WorldActor.TraitsImplementing<Locomotor>().FirstOrDefault(l => l.Info.Name == mobile?.Locomotor);
				CPos DockAt(CPos cell) => world.Map.CellContaining(world.Map.CenterOfCell(cell) + bi.CenterOffset(world) + dock.DockOffset);
				metadata[LlmNames.Display(world, ai)] = new
				{
					freeUnit = harvester != null ? LlmNames.Display(world, harvester) : null,
					dimensions = new[] { bi.Dimensions.X, bi.Dimensions.Y },
					dockOffsetFromTopLeft = Cell(DockAt(CPos.Zero))
				};

				if (!queues.Any(q => q.AllQueued().Any(i => i.Done && i.Item == ai.Name)) || locomotor == null)
					continue;

				// Terrain is known only where explored. Dynamic obstacles contribute
				// only when visible; moving traffic is deliberately not modeled here.
				var walkable = world.Map.AllCells.Where(c => player.Shroud.IsExplored(c)
					&& locomotor.MovementCostForCell(c) != PathGraph.MovementCostForUnreachableCell).ToHashSet();
				foreach (var actor in world.Actors.Where(a => !a.IsDead && a.IsInWorld && a.CanBeViewedByPlayer(player)))
				{
					var building = actor.TraitOrDefault<Building>();
					if (building != null)
						walkable.ExceptWith(SolidTiles(building.Info, building.TopLeft));
				}

				var (_, _, legal) = PlacementGrid.Build(world, player, ai, bi, 12);
				var sites = new List<Site>();
				foreach (var cell in legal)
				{
					var dockCell = DockAt(cell);
					var blocked = SolidTiles(bi, cell).ToHashSet();
					var distances = Distances(dockCell, walkable, blocked);
					var access = patches.Select(p => new Access(p.Id,
						p.Cells.Min(c => distances.TryGetValue(c, out var steps) ? steps : null), p.Cells.Count, p.Density)).ToArray();
					sites.Add(new(cell, dockCell, Neighbors.Count(n => walkable.Contains(dockCell + n) && !blocked.Contains(dockCell + n)), access));
				}

				// Preserve each patch's best docking alternatives before filling the
				// remaining candidate budget with coverage across the legal grid.
				var selected = patches.SelectMany(p => sites.Where(s => s.Patches.Any(a => a.Patch == p.Id && a.TravelCells.HasValue))
					.OrderBy(s => s.Patches.First(a => a.Patch == p.Id).TravelCells).ThenByDescending(s => s.OpenNeighbors).Take(6))
					.DistinctBy(s => s.Cell).Take(96).ToList();
				var samples = Math.Min(128, sites.Count);
				for (var i = 0; i < samples && selected.Count < 128; i++)
				{
					var site = sites[i * sites.Count / samples];
					if (!selected.Any(s => s.Cell == site.Cell))
						selected.Add(site);
				}

				placements.AddRange(selected.Select(s => (object)new
				{
					item = LlmNames.Display(world, ai),
					cell = Cell(s.Cell),
					dockCell = Cell(s.Dock),
					openDockNeighbors = s.OpenNeighbors,
					patches = s.Patches.Where(p => p.TravelCells.HasValue).OrderBy(p => p.TravelCells).Take(4)
						.Select(p => new { id = p.Patch, travelCells = p.TravelCells, visibleCells = p.VisibleCells, density = p.Density }).ToArray()
				}));
			}

			return new
			{
				routeEstimate = "Four-neighbor walking distance over explored terrain and visible buildings, including the proposed footprint. " +
					"Ignores moving traffic and terrain speed; not an exact travel time. Patch size/density count currently visible resources only.",
				refineries = metadata,
				patches = patches.Select(p => new
				{
					id = p.Id,
					cell = new[] { (int)p.Cells.Average(c => c.X), (int)p.Cells.Average(c => c.Y) },
					visibleCells = p.Cells.Count,
					density = p.Density
				}).ToArray(),
				placementSites = placements
			};
		}

		// '+' occupies a building cell but permits transit, including the refinery
		// docking apron. Only solid 'x'/'X' cells obstruct these walking routes.
		public static IEnumerable<CPos> SolidTiles(BuildingInfo building, CPos cell) =>
			building.OccupiedTiles(cell).Except(building.TransitOnlyTiles(cell));

		public static Dictionary<CPos, int> Distances(CPos start, HashSet<CPos> walkable, HashSet<CPos> blocked)
		{
			var distance = new Dictionary<CPos, int>();
			if (!walkable.Contains(start) || blocked.Contains(start))
				return distance;
			var queue = new Queue<CPos>();
			queue.Enqueue(start);
			distance[start] = 0;
			while (queue.TryDequeue(out var cell))
				foreach (var offset in Neighbors)
				{
					var next = cell + offset;
					if (!walkable.Contains(next) || blocked.Contains(next) || !distance.TryAdd(next, distance[cell] + 1))
						continue;
					queue.Enqueue(next);
				}

			return distance;
		}

		static int[] Cell(CPos cell) => [cell.X, cell.Y];
	}
}
