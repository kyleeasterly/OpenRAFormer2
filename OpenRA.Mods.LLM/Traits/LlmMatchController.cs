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
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;
using OpenRA.Widgets;

namespace OpenRA.Mods.LLM.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Exports per-player fog-scoped game state and observer stats for the orf orchestrator.")]
	public sealed class LlmMatchControllerInfo : TraitInfo
	{
		[Desc("Fallback export interval in ticks when match.json does not specify one.")]
		public readonly int StateIntervalTicks = 25;

		public override object Create(ActorInitializer init) { return new LlmMatchController(this); }
	}

	public sealed class LlmMatchController : ITick, ITickRender, IWorldLoaded, IGameOver
	{
		// Observer actor feed for the browser renderer: a compact frame written
		// every few ticks (~8 Hz), consumed by the dashboard's /api/actors stream.
		const int ActorExportInterval = 3;

		// Enough for the agent to plan a tech path without drowning the prompt.
		const int LockedItemsPerQueue = 12;

		readonly LlmMatchControllerInfo info;

		// Per-player sighting log for frozen (remembered-under-fog) actors, which carry
		// no timestamp of their own. Keyed by actor id, valued in world ticks.
		readonly Dictionary<Player, Dictionary<uint, int>> lastSeenTicks = [];

		Dictionary<Player, LlmPlayerConfig> configs;
		IResourceLayer resourceLayer;
		int interval;
		bool resultWritten;
		int exitAtTick = -1;

		readonly Dictionary<string, int> actorTypeIds = [];
		readonly List<string> actorTypeNames = [];
		Dictionary<Player, int> playerIds;

		public LlmMatchController(LlmMatchControllerInfo info)
		{
			this.info = info;
		}

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			if (!LlmRun.Active)
				return;

			LlmDamageLog.Clear();
			configs = LlmRun.PlayerConfigs(w);
			resourceLayer = w.WorldActor.TraitOrDefault<IResourceLayer>();
			interval = LlmRun.Match.StateIntervalTicks > 0 ? LlmRun.Match.StateIntervalTicks : info.StateIntervalTicks;

			// God view for the capture: no shroud, viewport pinned to the map center.
			// Zoom is enforced continuously in TickRender because the engine resets it
			// when the window or graphics settings settle after load.
			w.RenderPlayer = null;
			var map = w.Map;
			var center = map.CenterOfCell(new MPos(map.MapSize.Width / 2, map.MapSize.Height / 2).ToCPos(map));
			wr.Viewport.ViewportCenterProvider = () => new float2(center.X, center.Y);
		}

		void ITickRender.TickRender(WorldRenderer wr, Actor self)
		{
			if (!LlmRun.Active)
				return;

			// The browser view is the product: hide the in-game UI chrome so the capture
			// is pure map, and keep the observer view shroud-free and fit to the map.
			foreach (var child in Ui.Root.Children)
				if (child.IsVisible())
					child.IsVisible = () => false;

			if (self.World.RenderPlayer != null)
				self.World.RenderPlayer = null;

			var map = self.World.Map;
			var tileSize = map.Rules.TerrainInfo.TileSize;
			var mapPxWidth = map.MapSize.Width * tileSize.Width;
			var mapPxHeight = map.MapSize.Height * tileSize.Height;
			var resolution = Game.Renderer.NativeResolution;
			var desired = Math.Min((float)resolution.Width / mapPxWidth, (float)resolution.Height / mapPxHeight);
			desired = Math.Min(desired, wr.Viewport.MaxZoom);

			if (Math.Abs(wr.Viewport.Zoom - desired) > 0.01f)
			{
				wr.Viewport.UnlockMinimumZoom(desired / Math.Max(wr.Viewport.MinZoom, 0.001f));
				wr.Viewport.AdjustZoom(-100f);
			}
		}

		void ITick.Tick(Actor self)
		{
			if (!LlmRun.Active || configs == null || configs.Count == 0)
				return;

			var world = self.World;
			if (world.WorldTick > 0 && world.WorldTick % ActorExportInterval == 0)
			{
				try
				{
					ExportActors(world);
				}
				catch (Exception e)
				{
					Log.Write("debug", $"LlmMatchController: actor export failed: {e}");
				}
			}

			if (world.WorldTick == 0 || world.WorldTick % interval != 0)
				return;

			try
			{
				foreach (var (player, cfg) in configs)
					if (!cfg.IsHuman)
						LlmRun.WriteJsonAtomic(Path.Combine(LlmRun.StateDir, cfg.Slug + ".json"), PlayerState(world, player, cfg));

				LlmRun.WriteJsonAtomic(Path.Combine(LlmRun.StateDir, "game.json"), GameState(world));

				if (!resultWritten)
				{
					var undefeated = configs.Keys.Count(p => p.WinState != WinState.Lost);
					if (world.IsGameOver || (configs.Count > 1 && undefeated <= 1))
						WriteResult(world);
				}
			}
			catch (Exception e)
			{
				Log.Write("debug", $"LlmMatchController: export failed: {e}");
			}

			// After game over, linger briefly (end screen for the stream/capture),
			// then exit cleanly so the replay recorder finalizes its metadata and
			// the orchestrator can archive the file without hitting a lock.
			if (resultWritten)
			{
				if (exitAtTick < 0)
					exitAtTick = world.WorldTick + 10000 / world.Timestep; // ~10s
				else if (world.WorldTick >= exitAtTick)
					Game.Exit();
			}
		}

		/// <summary>Compact observer frame for the browser renderer: id, type, owner, px, py, pz, facing, turret, hp%.</summary>
		void ExportActors(World world)
		{
			if (playerIds == null)
			{
				playerIds = [];
				foreach (var p in configs.Keys)
					playerIds[p] = playerIds.Count;
			}

			var rows = new List<int[]>();
			foreach (var actor in world.Actors)
			{
				// OccupiesSpace == null covers position-less actors (e.g. the per-player
				// PlayerActor, which is owned by a tracked player but has no location).
				if (actor.IsDead || !actor.IsInWorld || actor.Owner == null || actor.OccupiesSpace == null)
					continue;

				if (!playerIds.TryGetValue(actor.Owner, out var ownerId))
					continue;

				if (!actorTypeIds.TryGetValue(actor.Info.Name, out var typeId))
				{
					typeId = actorTypeNames.Count;
					actorTypeIds[actor.Info.Name] = typeId;
					actorTypeNames.Add(actor.Info.Name);
				}

				var pos = actor.CenterPosition;
				var facing = actor.TraitOrDefault<IFacing>()?.Facing.Angle ?? -1;
				var turret = actor.TraitsImplementing<Turreted>().FirstOrDefault()?.WorldOrientation.Yaw.Angle ?? -1;
				var health = actor.TraitOrDefault<IHealth>();
				var hp = health != null && health.MaxHP > 0 ? 100 * health.HP / health.MaxHP : 100;

				// Positions in terrain pixels (24 px per 1024 world units per cell).
				rows.Add([(int)actor.ActorID, typeId, ownerId,
					pos.X * 24 / 1024, pos.Y * 24 / 1024, pos.Z * 24 / 1024, facing, turret, hp]);
			}

			LlmRun.WriteJsonAtomic(Path.Combine(LlmRun.StateDir, "actors.json"), new
			{
				t = world.WorldTick,
				types = actorTypeNames,
				players = configs.Values.Select(c => c.Slug).ToList(),
				a = rows,
			});
		}

		void IGameOver.GameOver(World world)
		{
			if (LlmRun.Active && configs != null)
				WriteResult(world);
		}

		void WriteResult(World world)
		{
			if (resultWritten)
				return;

			resultWritten = true;
			LlmRun.WriteJsonAtomic(Path.Combine(LlmRun.RunDir, "result.json"), new
			{
				finishedAtTick = world.WorldTick,
				second = world.WorldTick * world.Timestep / 1000,
				players = configs.Select(kv => new { slug = kv.Value.Slug, winState = kv.Key.WinState.ToString() }).ToList()
			});
		}

		readonly Dictionary<Player, Queue<(int Tick, long Earned, long Spent)>> econHistory = [];

		(int IncomePerMinute, int SpendPerMinute) EconRates(World world, Player player, PlayerResources resources)
		{
			if (resources == null)
				return (0, 0);

			if (!econHistory.TryGetValue(player, out var hist))
				econHistory[player] = hist = new Queue<(int, long, long)>();

			hist.Enqueue((world.WorldTick, resources.Earned, resources.Spent));
			while (hist.Count > 1 && world.WorldTick - hist.Peek().Tick > 750)
				hist.Dequeue();

			var (oldTick, oldEarned, oldSpent) = hist.Peek();
			var seconds = (world.WorldTick - oldTick) * world.Timestep / 1000f;
			if (seconds < 1f)
				return (0, 0);

			return (
				(int)((resources.Earned - oldEarned) / seconds * 60),
				(int)((resources.Spent - oldSpent) / seconds * 60));
		}

		Dictionary<uint, int> LastSeenTicks(Player player)
		{
			if (!lastSeenTicks.TryGetValue(player, out var seen))
				lastSeenTicks[player] = seen = [];

			return seen;
		}

		object PlayerState(World world, Player player, LlmPlayerConfig cfg)
		{
			var map = world.Map;
			var resources = player.PlayerActor.TraitOrDefault<PlayerResources>();
			var power = player.PlayerActor.TraitOrDefault<PowerManager>();
			var (incomePerMinute, spendPerMinute) = EconRates(world, player, resources);

			var buildings = new List<object>();
			var units = new List<object>();
			var visibleEnemies = new List<object>();

			foreach (var a in world.Actors)
			{
				if (a.IsDead || !a.IsInWorld)
					continue;

				var isBuilding = a.Info.HasTraitInfo<BuildingInfo>();
				var isUnit = !isBuilding && (a.Info.HasTraitInfo<MobileInfo>() || a.Info.HasTraitInfo<AircraftInfo>());
				if (!isBuilding && !isUnit)
					continue;

				if (a.Owner == player)
				{
					var cell = map.CellContaining(a.CenterPosition);
					if (isBuilding)
					{
						var rally = a.TraitOrDefault<RallyPoint>()?.Path.FirstOrDefault();
						buildings.Add(new
						{
							id = a.ActorID,
							name = LlmNames.Display(world, a.Info),
							cell = CellArray(cell),
							hpPercent = HpPercent(a),
							rally = rally.HasValue ? CellArray(rally.Value) : null
						});
					}
					else
					{
						var unit = new Dictionary<string, object>
						{
							["id"] = a.ActorID,
							["name"] = LlmNames.Display(world, a.Info),
							["cell"] = CellArray(cell),
							["hpPercent"] = HpPercent(a),
							["idle"] = a.IsIdle
						};

						var (activity, destination) = ActivityInfo(a, map);
						if (activity != null)
							unit["activity"] = activity;
						if (destination != null)
							unit["destination"] = CellArray(destination.Value);

						units.Add(unit);
					}
				}
				else if (configs.TryGetValue(a.Owner, out var ownerCfg) && ownerCfg != cfg
					&& player.RelationshipWith(a.Owner) == PlayerRelationship.Enemy
					&& a.CanBeViewedByPlayer(player))
				{
					visibleEnemies.Add(new
					{
						id = a.ActorID,
						name = LlmNames.Display(world, a.Info),
						owner = ownerCfg.Slug,
						cell = CellArray(map.CellContaining(a.CenterPosition)),
						hpPercent = HpPercent(a),
						isBuilding
					});
				}
			}

			var queues = world.ActorsWithTrait<ProductionQueue>()
				.Where(x => x.Actor.Owner == player && x.Trait.Enabled)
				.Select(x => x.Trait)
				.ToList();

			// Everything the player's queues could ever offer, used to keep prerequisite
			// advice faction-correct, plus the prerequisites currently in hand.
			var producible = queues.SelectMany(q => q.AllItems()).Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
			var owned = PrerequisiteInfo.Owned(player);

			var production = new List<object>();
			var busyQueues = 0;
			foreach (var queue in queues)
			{
				var current = queue.CurrentItem();
				if (current != null)
					busyQueues++;

				// The greyed-out half of the palette, with the reason named. Without
				// this an agent can only guess why an item refuses to be built.
				var buildableNames = queue.BuildableItems().Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
				var locked = new List<object>();
				foreach (var b in queue.AllItems()
					.Where(b => !buildableNames.Contains(b.Name))
					.OrderBy(queue.GetProductionCost)
					.Take(LockedItemsPerQueue))
				{
					var (missing, blocking, _) = PrerequisiteInfo.Describe(world, b, owned, producible);
					locked.Add(new
					{
						name = LlmNames.Display(world, b),
						cost = queue.GetProductionCost(b),
						requires = missing,
						blockedBy = blocking
					});
				}

				production.Add(new
				{
					queue = queue.Info.Type,
					busy = current != null,
					current = current == null ? null : new
					{
						name = LlmNames.Display(world, world.Map.Rules.Actors[current.Item]),
						progressPercent = current.TotalTime > 0
							? (current.TotalTime - current.RemainingTime) * 100 / current.TotalTime
							: 0,
						paused = current.Paused,
						ready = current.Done
					},

					// Waiting items only — AllQueued() includes the in-progress item,
					// which made one building look like a duplicate and triggered
					// cancel-flapping in duplicate-trimming agents.
					queued = queue.AllQueued().Where(i => i != current)
						.Select(i => LlmNames.Display(world, world.Map.Rules.Actors[i.Item])).ToList(),
					buildable = queue.BuildableItems().Select(b => new
					{
						name = LlmNames.Display(world, b),
						cost = queue.GetProductionCost(b)
					}).ToList(),
					locked
				});
			}

			// Finished buildings awaiting a place_building decision: give the agent a
			// legality-annotated view of its base area so it picks position, not rules.
			var pendingPlacement = new List<object>();
			foreach (var queue in queues)
			{
				var done = queue.AllQueued().FirstOrDefault(i => i.Done);
				if (done == null || !world.Map.Rules.Actors.TryGetValue(done.Item, out var ai))
					continue;

				var bi = ai.TraitInfoOrDefault<BuildingInfo>();
				if (bi == null)
					continue;

				var (origin, rows, valid) = PlacementGrid.Build(world, player, ai, bi, 12);
				pendingPlacement.Add(new
				{
					item = LlmNames.Display(world, ai),
					gridOrigin = CellArray(origin),
					grid = rows,
					legend = PlacementGrid.Legend,
					validCellsSample = valid.Take(40).Select(CellArray).ToList()
				});
			}

			var frozen = new List<object>();
			var frozenLayer = player.PlayerActor.TraitOrDefault<FrozenActorLayer>();
			if (frozenLayer != null)
			{
				var seen = LastSeenTicks(player);
				foreach (var fa in frozenLayer.FrozenActorsInRegion(map.AllCells, false))
				{
					// Allies used to land in this list and agents duly shelled their own
					// team's bases: mirror the relationship check visibleEnemies uses.
					if (!fa.IsValid || fa.Owner == null || fa.Owner == player
						|| player.RelationshipWith(fa.Owner) != PlayerRelationship.Enemy)
						continue;

					if (!configs.TryGetValue(fa.Owner, out var ownerCfg))
						continue;

					// FrozenActor carries no "when did I last see this". Visible is only
					// set while the footprint sits under fog, so !Visible means the real
					// actor is in view right now — that is our sighting signal.
					if (!fa.Visible)
						seen[fa.ID] = world.WorldTick;

					frozen.Add(new
					{
						name = LlmNames.Display(world, fa.Info),
						owner = ownerCfg.Slug,
						cell = CellArray(map.CellContaining(fa.CenterPosition)),
						lastSeenSecond = seen.TryGetValue(fa.ID, out var seenTick)
							? seenTick * world.Timestep / 1000
							: (int?)null
					});
				}
			}

			var state = new
			{
				tick = world.WorldTick,
				second = world.WorldTick * world.Timestep / 1000,
				you = new
				{
					slug = cfg.Slug,
					faction = player.Faction.InternalName,
					cash = resources == null ? 0 : resources.Cash + resources.Resources,
					incomePerMinute,
					spendPerMinute,
					powerProvided = power?.PowerProvided ?? 0,
					powerDrained = power?.PowerDrained ?? 0,
					defeated = player.WinState == WinState.Lost
				},
				buildCapacity = new
				{
					queues = production.Count,
					busy = busyQueues,
					idle = production.Count - busyQueues
				},
				underAttack = LlmDamageLog.Recent(player, world.WorldTick - 250)
					.GroupBy(e => (e.VictimId, e.AttackerId))
					.Take(12)
					.Select(g =>
					{
						var e = g.Last();
						configs.TryGetValue(e.AttackerOwner, out var attackerCfg);
						return (object)new
						{
							yourUnitId = e.VictimId,
							yourUnit = NameOf(world, e.VictimType),
							attackerId = e.AttackerId,
							attacker = NameOf(world, e.AttackerType),
							attackerOwner = e.AttackerId == 0 ? null : attackerCfg?.Slug,
							attackerCell = e.AttackerCell == CPos.Zero ? null : CellArray(e.AttackerCell)
						};
					})
					.ToList(),
				map = new
				{
					width = map.MapSize.Width,
					height = map.MapSize.Height,
					yourSpawnCell = CellArray(player.HomeLocation)
				},

				// Fair map knowledge: the lobby shows every start position, so players
				// always know roughly where the enemies begin. Order matches nothing —
				// the agent is not told which opponent holds which spawn.
				enemySpawns = configs.Keys
					.Where(other => other != player && player.RelationshipWith(other) == PlayerRelationship.Enemy)
					.Select(other => new
					{
						cell = CellArray(other.HomeLocation),
						explored = player.Shroud.IsExplored(other.HomeLocation)
					})
					.OrderBy(s => s.cell[0]).ThenBy(s => s.cell[1])
					.ToList(),
				production,
				ruleCatalog = cfg.AutoManage ? null : RuleCatalog.Build(world, player, queues),
				pendingPlacement,
				buildings,
				units,
				visibleEnemies,
				lastKnownEnemyBuildings = frozen,
				exploredResources = ExploredResources(world, player)
			};
			if (cfg.JevPolicyVersion < 2)
				return state;

			var extended = JsonSerializer.SerializeToNode(state);
			extended["spatialEconomy"] = JsonSerializer.SerializeToNode(JevSpatialState.Build(world, player, queues));
			foreach (var building in extended["buildings"].AsArray())
			{
				var actor = world.GetActorById(building["id"].GetValue<uint>());
				building["repairing"] = actor?.TraitOrDefault<RepairableBuilding>()?.Repairers.Contains(player) ?? false;
			}

			return extended;
		}

		List<object> ExploredResources(World world, Player player)
		{
			var blocks = new Dictionary<(int X, int Y), (long SumX, long SumY, int Count)>();
			if (resourceLayer == null)
				return [];

			foreach (var cell in world.Map.AllCells)
			{
				if (resourceLayer.GetResource(cell).Density == 0 || !player.Shroud.IsExplored(cell))
					continue;

				var key = (cell.X / 8, cell.Y / 8);
				var (sumX, sumY, count) = blocks.TryGetValue(key, out var b) ? b : (0, 0, 0);
				blocks[key] = (sumX + cell.X, sumY + cell.Y, count + 1);
			}

			return blocks.Values
				.OrderByDescending(b => b.Count)
				.Take(20)
				.Select(b => (object)new { cell = new[] { (int)(b.SumX / b.Count), (int)(b.SumY / b.Count) }, cells = b.Count })
				.ToList();
		}

		object GameState(World world)
		{
			var players = new List<object>();
			foreach (var (player, cfg) in configs)
			{
				var resources = player.PlayerActor.TraitOrDefault<PlayerResources>();
				var power = player.PlayerActor.TraitOrDefault<PowerManager>();
				var stats = player.PlayerActor.TraitOrDefault<PlayerStatistics>();

				var unitCount = 0;
				var buildingCount = 0;
				foreach (var a in world.Actors)
				{
					if (a.IsDead || !a.IsInWorld || a.Owner != player)
						continue;

					if (a.Info.HasTraitInfo<BuildingInfo>())
						buildingCount++;
					else if (a.Info.HasTraitInfo<MobileInfo>() || a.Info.HasTraitInfo<AircraftInfo>())
						unitCount++;
				}

				players.Add(new
				{
					slug = cfg.Slug,
					name = player.PlayerName,
					faction = player.Faction.InternalName,
					colorHex = $"{player.Color.R:X2}{player.Color.G:X2}{player.Color.B:X2}",
					spawnCell = CellArray(player.HomeLocation),
					cash = resources == null ? 0 : resources.Cash + resources.Resources,
					earned = resources?.Earned ?? 0,
					powerProvided = power?.PowerProvided ?? 0,
					powerDrained = power?.PowerDrained ?? 0,
					unitCount,
					buildingCount,
					armyValue = stats?.ArmyValue ?? 0,
					assetsValue = stats?.AssetsValue ?? 0,
					unitsKilled = stats?.UnitsKilled ?? 0,
					unitsLost = stats?.UnitsDead ?? 0,
					buildingsKilled = stats?.BuildingsKilled ?? 0,
					buildingsLost = stats?.BuildingsDead ?? 0,
					winState = player.WinState.ToString()
				});
			}

			return new
			{
				tick = world.WorldTick,
				second = world.WorldTick * world.Timestep / 1000,
				paused = world.Paused,
				gameOver = world.IsGameOver,
				map = new { title = world.Map.Title, width = world.Map.MapSize.Width, height = world.Map.MapSize.Height },
				players
			};
		}

		static string NameOf(World world, string internalType)
		{
			return world.Map.Rules.Actors.TryGetValue(internalType, out var ai)
				? LlmNames.Display(world, ai)
				: internalType;
		}

		static int[] CellArray(CPos cell)
		{
			return [cell.X, cell.Y];
		}

		// What a unit is currently doing and where its current orders end, from the
		// same activity target-line walk the selection waypoint renderer uses.
		static (string Activity, CPos? Destination) ActivityInfo(Actor a, Map map)
		{
			var current = a.CurrentActivity;
			if (current == null)
				return (null, null);

			var name = current.GetType().Name;
			if (name.EndsWith("Activity", StringComparison.Ordinal))
				name = name[..^"Activity".Length];

			WPos? destination = null;
			for (var act = current; act != null; act = act.NextActivity)
			{
				if (act.IsCanceling)
					continue;

				foreach (var n in act.TargetLineNodes(a))
					if (n.Target.Type != TargetType.Invalid)
						destination = n.Target.CenterPosition;
			}

			return (name, destination.HasValue ? map.CellContaining(destination.Value) : null);
		}

		static int HpPercent(Actor a)
		{
			var health = a.TraitOrDefault<IHealth>();
			return health == null || health.MaxHP == 0 ? 100 : (int)((long)health.HP * 100 / health.MaxHP);
		}
	}
}
