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
using OpenRA.Traits;

namespace OpenRA.Mods.LLM.Traits
{
	[Desc("Reports damage events to the LLM match controller so agents know what is shooting them.")]
	public sealed class LlmDamageReporterInfo : TraitInfo
	{
		public override object Create(ActorInitializer init) { return new LlmDamageReporter(); }
	}

	public sealed class LlmDamageReporter : INotifyDamage
	{
		void INotifyDamage.Damaged(Actor self, AttackInfo e)
		{
			// Ignore self-inflicted damage (tiberium fields, crush damage) — agents only
			// need to know about enemy fire, and terrain damage reports self as attacker.
			if (!LlmRun.Active || e.Damage.Value <= 0 || e.Attacker == null
				|| e.Attacker == self || e.Attacker.Disposed || e.Attacker.Owner == self.Owner)
				return;

			LlmDamageLog.Report(self, e.Attacker);
		}
	}

	/// <summary>Short-window buffer of who-shot-whom, drained by LlmMatchController exports.</summary>
	public static class LlmDamageLog
	{
		public readonly struct Entry(
			int tick, Player victimOwner, uint victimId, string victimType,
			uint attackerId, string attackerType, Player attackerOwner, CPos attackerCell)
		{
			public readonly int Tick = tick;
			public readonly Player VictimOwner = victimOwner;
			public readonly uint VictimId = victimId;
			public readonly string VictimType = victimType;
			public readonly uint AttackerId = attackerId;
			public readonly string AttackerType = attackerType;
			public readonly Player AttackerOwner = attackerOwner;
			public readonly CPos AttackerCell = attackerCell;
		}

		const int MaxEntries = 1024;
		static readonly System.Threading.Lock Lock = new();
		static readonly List<Entry> Entries = [];

		public static void Report(Actor victim, Actor attacker)
		{
			// Telemetry must never take down the simulation: guard and swallow.
			try
			{
				ReportInner(victim, attacker);
			}
			catch
			{
			}
		}

		static void ReportInner(Actor victim, Actor attacker)
		{
			var world = victim.World;
			var visible = attacker.CanBeViewedByPlayer(victim.Owner);
			lock (Lock)
			{
				if (Entries.Count >= MaxEntries)
					Entries.RemoveRange(0, Entries.Count - MaxEntries + 1);

				Entries.Add(new Entry(
					world.WorldTick,
					victim.Owner,
					victim.ActorID,
					victim.Info.Name,
					visible ? attacker.ActorID : 0,
					visible ? attacker.Info.Name : "unknown",
					visible ? attacker.Owner : victim.Owner,
					visible && attacker.IsInWorld && !attacker.IsDead ? world.Map.CellContaining(attacker.CenterPosition) : CPos.Zero));
			}
		}

		public static List<Entry> Recent(Player victimOwner, int sinceTick)
		{
			lock (Lock)
				return [.. Entries.Where(e => e.VictimOwner == victimOwner && e.Tick >= sinceTick)];
		}

		public static void Clear()
		{
			lock (Lock)
				Entries.Clear();
		}
	}
}
