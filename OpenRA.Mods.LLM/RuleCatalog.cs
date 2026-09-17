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
using OpenRA.Mods.Common.Traits;

namespace OpenRA.Mods.LLM
{
	/// <summary>Rules, not strategy: metadata for owned/visible actors and the player's production palette.</summary>
	public static class RuleCatalog
	{
		public static object Build(World world, Player player, List<ProductionQueue> queues)
		{
			var palette = queues.SelectMany(q => q.AllItems()).ToList();
			var producible = palette.Select(a => a.Name).ToHashSet();
			var buildable = queues.SelectMany(q => q.BuildableItems()).Select(a => a.Name).ToHashSet();
			var owned = PrerequisiteInfo.Owned(player);
			var types = palette.Concat(world.Actors
				.Where(a => !a.IsDead && a.IsInWorld && (a.Owner == player || a.CanBeViewedByPlayer(player)))
				.Select(a => a.Info)).DistinctBy(a => a.Name);
			var catalog = new Dictionary<string, object>();
			foreach (var ai in types)
			{
				var bi = ai.TraitInfoOrDefault<BuildableInfo>();
				if (bi == null && !ai.HasTraitInfo<MobileInfo>())
					continue;

				var (missing, blocking, _) = PrerequisiteInfo.Describe(world, ai, owned, producible);
				var description = bi?.Description;
				if (!string.IsNullOrEmpty(description))
					description = FluentProvider.GetMessage(description);

				var transform = ai.TraitInfoOrDefault<TransformsInfo>()?.IntoActor;
				catalog.TryAdd(LlmNames.Display(world, ai), new
				{
					cost = ai.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0,
					isBuilding = ai.HasTraitInfo<BuildingInfo>(),
					isHarvester = ai.HasTraitInfo<HarvesterInfo>(),
					isAircraft = ai.HasTraitInfo<AircraftInfo>(),
					isTransport = ai.HasTraitInfo<CargoInfo>(),
					canCapture = ai.HasTraitInfo<CapturesInfo>(),
					capturable = ai.HasTraitInfo<CapturableInfo>(),
					deploysInto = transform != null && world.Map.Rules.Actors.TryGetValue(transform, out var into)
						? LlmNames.Display(world, into) : null,
					power = ai.TraitInfos<PowerInfo>().Sum(p => p.Amount),
					maxHp = ai.TraitInfoOrDefault<HealthInfo>()?.HP ?? 0,
					description,
					buildable = buildable.Contains(ai.Name),
					requires = missing,
					blockedBy = blocking,
					weapons = ai.TraitInfos<ArmamentInfo>().Where(a => a.WeaponInfo != null).Select(a => new
					{
						rangeCells = a.WeaponInfo.Range.Length / 1024.0,
						reloadTicks = a.WeaponInfo.ReloadDelay,
						targets = a.WeaponInfo.ValidTargets.ToString()
					}).ToArray()
				});
			}

			return catalog;
		}
	}
}
