using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Orf;

public sealed class Spec
{
	public string Name { get; set; } = "";
	public string Map { get; set; } = "";
	public WebSpec Web { get; set; } = new();
	public string Display { get; set; } = ":97";
	public List<int> Resolution { get; set; } = [2540, 2540];
	public StreamSpec Stream { get; set; } = new();
	public int TurnIntervalSeconds { get; set; } = 12;
	public int MaxTurnsPerPlayer { get; set; }
	public int StateIntervalTicks { get; set; } = 25;
	public string StateFormat { get; set; } = "json";
	public List<PlayerSpec> Players { get; set; } = [];
	public Dictionary<string, ProviderSpec> Providers { get; set; } = [];

	/// <summary>Port for the LAN-joinable game server when any player is human
	/// (provider: human). Human matches default to 1301.</summary>
	public int ListenPort { get; set; }
	public string? ServerName { get; set; }
	public string? Password { get; set; }

	public bool HasHumans => Players.Any(p => p.IsHuman);

	public int DisplayNumber => int.TryParse(Display.TrimStart(':'), out var n) ? n : 97;
	public int Width => Resolution.Count > 0 ? Resolution[0] : 2540;
	public int Height => Resolution.Count > 1 ? Resolution[1] : 2540;

	public ProviderSpec ProviderFor(PlayerSpec player)
	{
		if (player.IsHuman)
			throw new InvalidOperationException($"Player '{player.Slug}' is human and has no provider");
		if (!Providers.TryGetValue(player.Provider, out var provider))
			throw new InvalidOperationException($"Player '{player.Slug}' references unknown provider '{player.Provider}'");
		return provider;
	}

	public static Spec Load(string path)
	{
		var deserializer = new DeserializerBuilder()
			.WithNamingConvention(CamelCaseNamingConvention.Instance)
			.IgnoreUnmatchedProperties()
			.Build();

		var spec = deserializer.Deserialize<Spec>(File.ReadAllText(path))
			?? throw new InvalidOperationException($"Spec file '{path}' is empty");

		if (string.IsNullOrWhiteSpace(spec.Name))
			throw new InvalidOperationException("Spec is missing 'name'");
		if (spec.StateFormat is not ("json" or "markdown"))
			throw new InvalidOperationException($"Unknown stateFormat '{spec.StateFormat}' (expected 'json' or 'markdown')");
		if (spec.Players.Count == 0)
			throw new InvalidOperationException("Spec has no players");
		if (spec.StateIntervalTicks is < 1 or > 250)
			throw new InvalidOperationException("stateIntervalTicks must be between 1 and 250");

		var dupes = spec.Players.CountBy(p => p.Slug).Where(kv => kv.Value > 1).Select(kv => kv.Key).ToList();
		if (dupes.Count > 0)
			throw new InvalidOperationException($"Duplicate player slugs: {string.Join(", ", dupes)}");

		if (spec.HasHumans && spec.ListenPort <= 0)
			spec.ListenPort = 1301;

		foreach (var p in spec.Players)
		{
			if (p.Controller is not ("llm" or "jev"))
				throw new InvalidOperationException($"Unknown controller '{p.Controller}' for '{p.Slug}'");
			if (p.Controller == "jev")
			{
				if (p.IsHuman || p.Advisor != null || p.Swarm != null || p.FallbackModels.Count > 0)
					throw new InvalidOperationException("Jev controllers cannot use human slots, advisors, swarms, or LLM fallback models");
				if (spec.ProviderFor(p).IsTest)
					throw new InvalidOperationException("Jev requires a TypeSafe provider URL");
				p.Jev.Validate();
			}
			if (p.IsHuman)
			{
				if (p.Advisor != null)
					throw new InvalidOperationException($"Player '{p.Slug}' is human and cannot have an advisor");
				continue;
			}

			spec.ProviderFor(p); // validates provider references

			if (p.IsSwarm)
			{
				if (p.Swarm!.Bootstrap is { } boot && string.IsNullOrWhiteSpace(boot.Name))
					boot.Name = "boot";

				var roleDupes = p.Swarm!.AllRoles().CountBy(r => r.Name).Where(kv => kv.Value > 1).Select(kv => kv.Key).ToList();
				if (roleDupes.Count > 0)
					throw new InvalidOperationException($"Player '{p.Slug}' has duplicate swarm roles: {string.Join(", ", roleDupes)}");

				foreach (var role in p.Swarm!.AllRoles())
				{
					if (string.IsNullOrWhiteSpace(role.Name) || !role.Name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
						throw new InvalidOperationException($"Player '{p.Slug}' swarm role name '{role.Name}' must be a non-empty alphanumeric identifier");

					var roleProvider = role.Provider ?? p.Provider;
					if (!spec.Providers.ContainsKey(roleProvider))
						throw new InvalidOperationException($"Player '{p.Slug}' role '{role.Name}' references unknown provider '{roleProvider}'");
				}

				foreach (var d in p.Swarm!.Dynamic)
					if (d.Trigger != "under_attack" && !d.Trigger.StartsWith("at_seconds:", StringComparison.Ordinal))
						throw new InvalidOperationException($"Player '{p.Slug}' dynamic role '{d.Name}' has unknown trigger '{d.Trigger}'");
			}

			if (p.Advisor != null)
			{
				if (string.IsNullOrWhiteSpace(p.Advisor.Model))
					throw new InvalidOperationException($"Player '{p.Slug}' advisor is missing 'model'");
				var advisorProvider = p.Advisor.Provider ?? p.Provider;
				if (!spec.Providers.ContainsKey(advisorProvider))
					throw new InvalidOperationException($"Player '{p.Slug}' advisor references unknown provider '{advisorProvider}'");
			}
		}

		return spec;
	}
}

public sealed class WebSpec
{
	public int Port { get; set; } = 5199;
}

public sealed class StreamSpec
{
	public int Fps { get; set; } = 6;
	public int Scale { get; set; } = 1280;
	public int Quality { get; set; } = 5;
}

public sealed class PlayerSpec
{
	public string Slug { get; set; } = "";
	public string Display { get; set; } = "";
	public string Provider { get; set; } = "test";
	public string Controller { get; set; } = "llm";
	public JevSpec Jev { get; set; } = new();

	/// <summary>provider: human — the slot is left open in the lobby for a real
	/// player to join over the LAN. No agent loop, no orders, no state export.</summary>
	public bool IsHuman => Provider == "human";
	public string Model { get; set; } = "scripted";

	/// <summary>
	/// Models to fall back to, in order, when the primary stops answering. The
	/// provider lane is the single biggest source of lost matches: one night's
	/// champion took nine turns at 40s because its endpoint quietly degraded, and
	/// a whole match was scored with 108 HTTP-200 empty bodies counted as turns.
	/// After FallbackAfterFailures consecutive bad turns the lane switches model
	/// and keeps playing. Same provider — only the model string changes.
	/// </summary>
	public List<string> FallbackModels { get; set; } = [];

	public int FallbackAfterFailures { get; set; } = 3;

	public string Faction { get; set; } = "Random";
	public int Spawn { get; set; }
	public int Team { get; set; }
	public double Temperature { get; set; } = 0.6;
	public string? PromptFile { get; set; }
	public bool RecentActions { get; set; } = true;

	/// <summary>Passed through as reasoning_effort when set (e.g. "low"/"high"/"max" for stealth/ox-alpha).</summary>
	public string? ReasoningEffort { get; set; }

	/// <summary>Completion budget per turn. Reasoning models spend this on thinking too — give them room.</summary>
	public int MaxTokens { get; set; } = 2000;

	/// <summary>Per-request timeout. High reasoning efforts can legitimately exceed the old 120s default.</summary>
	public int TimeoutSeconds { get; set; } = 120;

	/// <summary>Optional add-on module: a second model that reviews this player's recent turns
	/// asynchronously and feeds advice into its prompts. Never blocks the driver's turn loop.</summary>
	public AdvisorSpec? Advisor { get; set; }

	/// <summary>Swarm mode: multiple concurrent specialist loops playing this one player
	/// slot — optionally opening with a solo bootstrap commander that hands off once the
	/// base is established, and growing dynamically as the game heats up. The player-level
	/// Advisor (if set) becomes the shared strategist: its advice is injected into every
	/// thread's prompts.</summary>
	public SwarmSpec? Swarm { get; set; }

	public bool IsSwarm => Swarm != null && (Swarm.Roles.Count > 0 || Swarm.Bootstrap != null);
}

public sealed class JevSpec
{
	public int PolicyVersion { get; set; } = 1;
	public int IntervalMilliseconds { get; set; } = 1000;
	public int RequestTimeoutMilliseconds { get; set; } = 5000;
	public int MaxResponseAgeTicks { get; set; } = 75;
	public int ObjectiveSeconds { get; set; } = 20;
	public int CommandHoldSeconds { get; set; } = 5;
	public int SquadSize { get; set; } = 12;
	public int MaxSquads { get; set; } = 8;
	public int MaxPlacementOptions { get; set; } = 128;
	public double InputUsdPerMillionTokens { get; set; } = 0.042;

	public void Validate()
	{
		if (PolicyVersion is < 1 or > 2 || IntervalMilliseconds < 100 || RequestTimeoutMilliseconds is < 100 or > 30000
			|| MaxResponseAgeTicks is < 1 or > 250 || ObjectiveSeconds is < 1 or > 120
			|| CommandHoldSeconds is < 1 or > 60 || SquadSize is < 1 or > 32
			|| MaxSquads is < 1 or > 16 || MaxPlacementOptions is < 2 or > 254
			|| !double.IsFinite(InputUsdPerMillionTokens) || InputUsdPerMillionTokens < 0)
			throw new InvalidOperationException("Invalid Jev cadence, deadline, grouping, candidate limit, or pricing configuration");
	}
}

/// <summary>Swarm composition: bootstrap commander, core roles, and dynamic spawn rules.</summary>
public sealed class SwarmSpec
{
	/// <summary>Solo commander that opens the game alone (full order authority) and hands
	/// off to the core roles once the handoff condition is met.</summary>
	public BootstrapSpec? Bootstrap { get; set; }

	/// <summary>Core specialists spawned at handoff (or at game start when no bootstrap).</summary>
	public List<RoleSpec> Roles { get; set; } = [];

	/// <summary>Threads spawned mid-game when their trigger fires (the swarm scales with the fight).</summary>
	public List<DynamicRoleSpec> Dynamic { get; set; } = [];

	public IEnumerable<RoleSpec> AllRoles()
	{
		if (Bootstrap != null)
			yield return Bootstrap;
		foreach (var r in Roles)
			yield return r;
		foreach (var d in Dynamic)
			yield return d;
	}
}

/// <summary>One specialist thread of a swarm player. Unset model fields inherit the player's.</summary>
public class RoleSpec
{
	public string Name { get; set; } = "";
	public string? PromptFile { get; set; }
	public string? Provider { get; set; }
	public string? Model { get; set; }
	public string? ReasoningEffort { get; set; }
	public double? Temperature { get; set; }
	public int? MaxTokens { get; set; }
	public int? TimeoutSeconds { get; set; }

	/// <summary>Allowed order types (e.g. move, attack_move, start_production). Orders of
	/// other types are dropped by the harness with a note in the tool result. Empty = all.</summary>
	public List<string> Orders { get; set; } = [];

	/// <summary>Allowed production queues by base name (Building, Defense, Support, Infantry,
	/// Vehicle, Aircraft). start_production/cancel_production orders for items belonging to
	/// other queues are dropped — this is what stops the unit chief from building a Barracks.
	/// Empty = all queues.</summary>
	public List<string> Queues { get; set; } = [];

	/// <summary>Per-role cadence; 0 = as fast as the provider allows (per fresh state).</summary>
	public int TurnIntervalSeconds { get; set; }

	/// <summary>Message-history exchanges kept in the prompt. 0 = default (2 for role
	/// lanes — each exchange embeds a full state snapshot, and fat prompts were the
	/// 25-40s turn killer of swarm v3).</summary>
	public int HistoryTurns { get; set; }

	/// <summary>Mass-before-attack gate: attack_move orders targeting cells far from our
	/// spawn are dropped unless they move at least this many units. Stops the lone-scout
	/// death trickle (v3: 34 units lost in ones and twos). 0 = off.</summary>
	public int MinAttackGroup { get; set; }
}

/// <summary>The opening commander: plays solo with full authority, then hands off.</summary>
public sealed class BootstrapSpec : RoleSpec
{
	/// <summary>Scripted opening (no LLM): Power -> Refinery -> Barracks at machine
	/// speed, placed the second they're ready. The LLM swarm takes over at handoff
	/// with the base already standing — zero thinking-latency in the opening.</summary>
	public bool Deterministic { get; set; }

	/// <summary>Hand off once this building exists (e.g. "Weapons Factory").</summary>
	public string? HandoffBuilding { get; set; } = "Weapons Factory";

	/// <summary>Hard handoff deadline in game seconds, in case the building never lands.</summary>
	public int HandoffAtSeconds { get; set; } = 420;
}

/// <summary>A thread the coordinator spawns mid-game when its trigger fires.</summary>
public sealed class DynamicRoleSpec : RoleSpec
{
	/// <summary>Spawn trigger. Supported: "under_attack" (sustained incoming damage after
	/// handoff), "at_seconds:N" (game clock).</summary>
	public string Trigger { get; set; } = "";

	/// <summary>Maximum instances of this thread (instances get -2, -3 name suffixes).</summary>
	public int Cap { get; set; } = 1;
}

/// <summary>Config for the over-the-shoulder advisor add-on.</summary>
public sealed class AdvisorSpec
{
	/// <summary>Lane label in the dashboard and modules/<name>.json filename.</summary>
	public string Name { get; set; } = "advisor";

	/// <summary>Provider key; defaults to the driver player's provider.</summary>
	public string? Provider { get; set; }

	public string Model { get; set; } = "";
	public string? ReasoningEffort { get; set; }
	public double Temperature { get; set; } = 1.0;
	public int MaxTokens { get; set; } = 16000;
	public int TimeoutSeconds { get; set; } = 300;
	public string? PromptFile { get; set; }

	/// <summary>How many recent driver turns each review covers.</summary>
	public int WindowTurns { get; set; } = 10;

	/// <summary>Minimum new driver turns before the next review fires (advisor is otherwise idle).</summary>
	public int MinNewTurns { get; set; } = 3;
}

public sealed class ProviderSpec
{
	public string BaseUrl { get; set; } = "";
	public string ApiKeyEnv { get; set; } = "";

	public bool IsTest => string.IsNullOrEmpty(BaseUrl);
}
