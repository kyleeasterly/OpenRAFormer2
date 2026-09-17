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

namespace OpenRA.Mods.LLM
{
	public sealed class LlmMatchConfig
	{
		public string RunId { get; set; }
		public string Map { get; set; }
		public int StateIntervalTicks { get; set; } = 25;
		public Dictionary<string, string> Options { get; set; } = [];
		public List<LlmPlayerConfig> Players { get; set; } = [];

		/// <summary>When > 0 (and any player is human), host a LAN-joinable multiplayer
		/// server on this port instead of a loopback-only local server.</summary>
		public int ListenPort { get; set; }
		public string ServerName { get; set; }
		public string Password { get; set; }

		public bool HasHumans => Players.Exists(p => p.IsHuman);
	}

	public sealed class LlmPlayerConfig
	{
		public string Slug { get; set; }
		public string Display { get; set; }
		public string Bot { get; set; } = "llm";
		public string Faction { get; set; } = "Random";
		public int Spawn { get; set; }
		public int Team { get; set; }
		public bool AutoManage { get; set; } = true;

		/// <summary>Human slot: left open in the lobby for a real player to join over
		/// the network. No LlmBot, no state export, no order channel.</summary>
		public bool IsHuman => Bot == "human";
	}

	public static class LlmRun
	{
		static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
		static readonly JsonSerializerOptions WriteOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

		static bool initialized;
		static string runDir;
		static LlmMatchConfig match;

		public static string RunDir
		{
			get
			{
				Initialize();
				return runDir;
			}
		}

		public static LlmMatchConfig Match
		{
			get
			{
				Initialize();
				return match;
			}
		}

		public static bool Active => RunDir != null && Match != null;

		static void Initialize()
		{
			if (initialized)
				return;

			initialized = true;
			var dir = Environment.GetEnvironmentVariable("ORF_RUN_DIR");
			if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
				return;

			var matchPath = Path.Combine(dir, "match.json");
			if (!File.Exists(matchPath))
				return;

			try
			{
				match = JsonSerializer.Deserialize<LlmMatchConfig>(File.ReadAllText(matchPath), ReadOptions);
				runDir = dir;
			}
			catch (Exception e)
			{
				Log.Write("debug", $"LlmRun: failed to parse {matchPath}: {e.Message}");
			}
		}

		/// <summary>Maps world players to their configured slugs: players[i] of match.json
		/// corresponds to the i-th playable slot sorted by player reference name.</summary>
		public static Dictionary<Player, LlmPlayerConfig> PlayerConfigs(World world)
		{
			var result = new Dictionary<Player, LlmPlayerConfig>();
			if (!Active)
				return result;

			var playable = world.Players
				.Where(p => p.Playable && !p.NonCombatant)
				.OrderBy(p => p.PlayerReference.Name, StringComparer.OrdinalIgnoreCase)
				.ToList();

			for (var i = 0; i < playable.Count && i < Match.Players.Count; i++)
				result[playable[i]] = Match.Players[i];

			return result;
		}

		public static string StateDir => Path.Combine(RunDir, "state");
		public static string InboxDir(string slug) => Path.Combine(RunDir, "orders", slug, "inbox");
		public static string ResultsDir(string slug) => Path.Combine(RunDir, "orders", slug, "results");

		public static void WriteJsonAtomic(string path, object value)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			var tmp = path + ".tmp";
			File.WriteAllText(tmp, JsonSerializer.Serialize(value, WriteOptions));
			File.Move(tmp, path, true);
		}
	}
}
