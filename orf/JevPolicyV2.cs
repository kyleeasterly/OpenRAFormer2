using System.Text.Json;
using System.Text.Json.Nodes;
using static Orf.JevPolicy;

namespace Orf;

public sealed class JevGroup
{
	public string Role { get; set; } = "front";
	public string Phase { get; set; } = "assemble";
	public List<long> Members { get; set; } = [];
	public long CreatedAt { get; set; }
}

public sealed class JevTactics
{
	public int NextGroup { get; set; }
	public Dictionary<string, JevGroup> Groups { get; set; } = [];
	public int[]? OperationCell { get; set; }
	public string Operation { get; set; } = "Discover enemy production and economy";
	public long OperationAt { get; set; } = -1000;
	public string Campaign { get; set; } = "prepare";
	public long CampaignAt { get; set; } = -1000;
}

/// <summary>
/// V2 adds spatial economy and local combat decisions around the unchanged V1
/// policy/validator. Every issued directive is still selected by native Jev.
/// </summary>
public sealed class JevPolicyV2(JevSpec config, JevPolicy core, JevTactics? tactics = null)
{
	public JevTactics Tactics { get; } = tactics ?? new();
	public JsonObject RequestState { get; private set; } = [];
	readonly Dictionary<string, (string Name, (int X, int Y) Cell)> operations = [];
	readonly Dictionary<string, Dictionary<string, JevAction>> targets = [];
	int requestCharacterBudget = 48000;
	public int RequestCharacterBudget => requestCharacterBudget;
	readonly List<string> campaignGroups = [];
	static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

	public JevFrame Prepare(JsonObject state, JsonArray results, JsonArray pending)
	{
		var frame = core.Prepare(state, results, pending);
		foreach (var id in frame.Questions.Select(q => q.Key).Where(core.Memory.Squads.ContainsKey).ToList())
		{
			frame.Questions.Remove(id);
			frame.Actions.Remove(id);
		}
		ReconcileGroups(state);
		core.Memory.Squads = Tactics.Groups.ToDictionary(kv => kv.Key, kv => kv.Value.Members.ToList());
		foreach (var key in core.Memory.Assignments.Keys.Where(k => k.StartsWith("group") && !Tactics.Groups.ContainsKey(k)).ToList())
			core.Memory.Assignments.Remove(key);
		RequestState = (JsonObject)core.RequestState.DeepClone();
		RequestState["memory"] = JsonSerializer.SerializeToNode(core.Memory, Options);
		RequestState["tactics"] = JsonSerializer.SerializeToNode(Tactics, Options);
		RequestState["economy"] = Economy(state);
		RequestState["rules"] = Text(RequestState, "rules").Replace("repair, sell and cancel", "cancel") +
			" Group membership follows physical proximity and combat role. New units do not automatically reinforce a deployed wave. " +
			"Dollar values adjusted for health describe nearby forces; they are not a simulation or win probability. " +
			"Operation choices become context next observation. A group's target question is conditional on its maneuver selecting engage; " +
			"answer both against this observation without assuming the other's answer. " +
			"Item and target capabilities are in game.ruleCatalog, keyed by name. Refinery patch IDs refer to economy.visibleResourcePatches.";
		ImproveEconomyQuestions(frame);
		RefineryQuestions(frame);
		BuildOperation(frame);
		BuildCombat(frame);
		BuildCampaign(frame);
		BuildMaintenance(frame);
		// Site descriptions are already attached to the placement question. Keep
		// the rest of the economy observation without duplicating 128 alternatives.
		(RequestState["game"] as JsonObject)?.Remove("spatialEconomy");
		(RequestState["memory"] as JsonObject)?.Remove("squads");
		foreach (var question in frame.Questions.Select(q => q.Value).OfType<JsonObject>())
			if (question["criteria"] is JsonObject criteria)
				foreach (var option in criteria.Select(c => c.Value).OfType<JsonObject>())
					option.Remove("properties");
		FitRequest(frame);
		return frame;
	}

	// Native tokenization is provider-owned. Keep ample empirical headroom and
	// adapt on its explicit size error using the next fresh observation.
	public bool ReduceRequestBudget()
	{
		if (requestCharacterBudget <= 24000) return false;
		requestCharacterBudget = Math.Max(24000, requestCharacterBudget * 3 / 4);
		return true;
	}

	void FitRequest(JevFrame frame)
	{
		while (RequestState.ToJsonString().Length + frame.Questions.Select(q => q.Value!.ToJsonString().Length).DefaultIfEmpty().Max() > requestCharacterBudget - 256)
		{
			var largest = frame.Questions.Where(q => q.Key.StartsWith("placement") || q.Key.EndsWith("_target"))
				.Select(q => (q.Key, Criteria: q.Value?["criteria"] as JsonObject))
				.Where(q => q.Criteria?.Count > 9).OrderByDescending(q => q.Criteria!.ToJsonString().Length).FirstOrDefault();
			if (largest.Criteria == null)
				throw new InvalidOperationException("V2 observation exceeds its request budget after candidate reduction");
			var keys = largest.Criteria.Select(c => c.Key).Skip(1).ToList();
			var count = Math.Max(8, keys.Count / 2);
			var retain = Enumerable.Range(0, count).Select(i => keys[i * keys.Count / count]).ToHashSet();
			foreach (var key in keys.Where(k => !retain.Contains(k)))
			{
				largest.Criteria.Remove(key);
				if (frame.Actions.TryGetValue(largest.Key, out var actions)) actions.Remove(key);
			}
		}
	}

	static JsonObject Economy(JsonObject state)
	{
		var buildings = Objects(state, "buildings").ToList();
		var units = Objects(state, "units").ToList();
		return new JsonObject
		{
			["refineries"] = buildings.Count(b => IsRefinery(state, Name(b))),
			["harvesters"] = units.Count(u => Bool(Catalog(state, Name(u)), "isHarvester")),
			["armedUnits"] = units.Count(u => Weapons(state, u).Any()),
			["captureUnits"] = units.Count(u => Bool(Catalog(state, Name(u)), "canCapture")),
			["powerSurplus"] = Number(state["you"], "powerProvided") - Number(state["you"], "powerDrained"),
			["idleQueues"] = Objects(state, "production").Count(q => q["current"] == null),
			["incomePerMinute"] = Number(state["you"], "incomePerMinute"),
			["spendPerMinute"] = Number(state["you"], "spendPerMinute"),
			["visibleResourcePatches"] = state["spatialEconomy"]?["patches"]?.DeepClone(),
			["refineryRules"] = state["spatialEconomy"]?["refineries"]?.DeepClone()
		};
	}

	void ImproveEconomyQuestions(JevFrame frame)
	{
		if (frame.Questions["goal"] is JsonObject goal)
			goal["instructions"] = "Choose the next capital investment that most improves the ability to win. " +
				"Use economy and game: income must sustain fighting, and refineries may grant a free harvester as refineryRules specifies. " +
				"Additional harvesting is useful only when income is actually limiting production and accessible Tiberium supports another harvester. " +
				"If cash is accumulating or income already covers spending, invest in fighting strength and pressure instead of more refineries. " +
				"Positive powerSurplus means current power demand is satisfied; extra power is useful only for planned demand or redundancy. " +
				"Technology and static defenses compete with income and an army. Do not repeatedly buy infrastructure with no current benefit. " +
				"Choose one additional building, unlock an unavailable unit, or none. Account for investments already in progress.";
		foreach (var (id, question) in frame.Questions)
		{
			if (!id.StartsWith("production") || id.EndsWith("_urgency") || question is not JsonObject q) continue;
			q["instructions"] = Text(q, "instructions") +
				" Use economy and localCombat to identify the actual bottleneck. For military production, build a force that can fight together " +
				"and counter observed opposition. Engineers cannot fight; recruit one only for a credible capture task, not as general combat infantry. " +
				"For infrastructure, income and required power enable sustained production; excess power or unused tech does not. " +
				"Do not add refineries when existing income covers spending or cash is accumulating. Expand only onto useful accessible Tiberium. " +
				"Avoid replacing lost scouts repeatedly when a fighting force is needed.";
		}
	}

	void RefineryQuestions(JevFrame frame)
	{
		foreach (var (id, actions) in frame.Actions.ToArray())
		{
			if (!id.StartsWith("placement")) continue;
			var item = actions.Values.FirstOrDefault()?.Order["item"]?.GetValue<string>();
			if (item == null || !IsRefinery(frame.State, item)) continue;
			var legal = LegalCells(Objects(frame.State, "pendingPlacement").First(p => Text(p, "item") == item)).ToHashSet();
			var sites = Objects(frame.State["spatialEconomy"], "placementSites")
				.Where(s => Text(s, "item") == item && legal.Contains(Cell(s["cell"]))).Take(config.MaxPlacementOptions).ToList();
			if (sites.Count == 0) continue;
			var choices = new JsonObject { ["wait"] = "Wait only if every available site is unacceptable; the completed investment is not harvesting yet." };
			actions.Clear();
			foreach (var site in sites)
			{
				var cell = Cell(site["cell"]);
				var key = $"c{cell.X}_{cell.Y}";
				var description = (JsonObject)site.DeepClone();
				description.Remove("item");
				foreach (var patch in Objects(description, "patches"))
				{
					patch.Remove("visibleCells");
					patch.Remove("density");
				}
				description["distanceToExistingRefinery"] = Closest(cell, Objects(frame.State, "buildings").Where(b => IsRefinery(frame.State, Name(b))));
				description["distanceToVisibleEnemy"] = Closest(cell, Objects(frame.State, "visibleEnemies"));
				choices[key] = description;
				actions[key] = new(new JsonObject { ["type"] = "place_building", ["item"] = item, ["cell"] = CellNode(cell) }, "placement:" + item);
			}
			frame.Questions[id] = Choice("Where should this completed refinery go to maximize useful harvesting income? " +
				"Compare the actual docking cell's walking routes to the visible Tiberium patches, looking up their density and size by ID in economy.visibleResourcePatches, " +
				"and dock access. Short repeated round trips to a substantial patch matter more than distance from the base center. " +
				"A tiny fragment can be closer yet yield less than a rich patch. Prefer useful coverage beyond existing refineries while considering " +
				"visible enemy threats and congestion. An empty patches list means no known walking route from that dock. " +
				"All offered building footprints are currently legal; route distances are estimates, not travel times.", choices);
		}
	}

	void ReconcileGroups(JsonObject state)
	{
		var units = Objects(state, "units").Where(u => !Bool(Catalog(state, Name(u)), "isHarvester")
			&& Text(Catalog(state, Name(u)), "deploysInto").Length == 0).ToDictionary(u => Number(u, "id"));
		foreach (var (id, group) in Tactics.Groups.ToArray())
		{
			group.Members.RemoveAll(i => !units.ContainsKey(i));
			if (group.Members.Count == 0) Tactics.Groups.Remove(id);
		}
		var assigned = Tactics.Groups.Values.SelectMany(g => g.Members).ToHashSet();
		foreach (var (id, unit) in units.OrderBy(kv => kv.Key))
		{
			if (assigned.Contains(id)) continue;
			var role = Role(state, unit);
			var nearby = Tactics.Groups.Values.Where(g => g.Role == role && g.Phase is "assemble" or "defend"
				&& g.Members.Count < config.SquadSize && g.Members.Any(i => Distance(Cell(units[i]["cell"]), Cell(unit["cell"])) <= 8))
				.OrderBy(g => Distance(Anchor(g, units), Cell(unit["cell"]))).FirstOrDefault();
			if (nearby == null && Tactics.Groups.Count < config.MaxSquads)
			{
				nearby = new JevGroup { Role = role, CreatedAt = Number(state, "second") };
				Tactics.Groups["group" + Tactics.NextGroup++] = nearby;
			}
			nearby ??= Tactics.Groups.Values.OrderBy(g => g.Role == role ? 0 : 1)
				.ThenBy(g => Distance(Anchor(g, units), Cell(unit["cell"]))).First();
			nearby.Members.Add(id);
		}
	}

	void BuildOperation(JevFrame frame)
	{
		operations.Clear();
		if (Number(frame.State, "second") - Tactics.OperationAt < config.ObjectiveSeconds) return;
		var choices = new JsonObject { ["retain"] = "Retain the current operation." };
		var state = frame.State;
		var locations = Objects(state, "visibleEnemies").Where(e => Bool(e, "isBuilding"))
			.Concat(Objects(state, "lastKnownEnemyBuildings"))
			.Select(e => (Name: "Destroy enemy " + Name(e), Cell: Cell(e["cell"])))
			.Concat(Objects(state, "enemySpawns").Select(e => (Name: "Locate and attack enemy base; explored=" + Bool(e, "explored"), Cell: Cell(e["cell"]))))
			.DistinctBy(e => e.Cell).Take(40);
		foreach (var location in locations)
		{
			var key = "objective" + operations.Count;
			operations[key] = location;
			choices[key] = new JsonObject { ["objective"] = location.Name, ["cell"] = CellNode(location.Cell) };
		}
		frame.Questions["operation"] = Choice("Choose the army's common operational objective. " +
			"Destroy enemy production and harvesting to remove their ability to resist, then finish remaining structures. " +
			"Avoid leaving a nearly defeated base to chase a distant scout. Known sightings may be stale. " +
			"Local groups can still defend, retreat, or assemble before committing to this objective.", choices);
	}

	void BuildCombat(JevFrame frame)
	{
		targets.Clear();
		var state = frame.State;
		var units = Objects(state, "units").ToDictionary(u => Number(u, "id"));
		var combat = new JsonObject();
		var home = Cell(state["map"]?["yourSpawnCell"]);
		var objective = Tactics.OperationCell is { Length: 2 } c ? (c[0], c[1]) : Objects(state, "enemySpawns").Select(e => Cell(e["cell"])).FirstOrDefault(home);
		foreach (var (id, group) in Tactics.Groups)
		{
			var members = group.Members.Select(i => units[i]).ToList();
			var anchor = Anchor(group, units);
			var spread = members.Max(u => Distance(anchor, Cell(u["cell"])));
			var enemies = Objects(state, "visibleEnemies").Where(e => Distance(anchor, Cell(e["cell"])) <= 18 + members.Max(u => Range(state, u))).ToList();
			var friendly = Objects(state, "units").Where(u => Distance(anchor, Cell(u["cell"])) < 12 && Weapons(state, u).Any()).ToList();
			combat[id] = new JsonObject
			{
				["role"] = group.Role, ["phase"] = group.Phase, ["members"] = Ids(group.Members),
				["anchor"] = CellNode(anchor), ["spreadCells"] = spread,
				["nearAnchor"] = members.Count(u => Distance(anchor, Cell(u["cell"])) <= 6),
				["idleMembers"] = members.Count(u => Bool(u, "idle")),
				["healthPercent"] = members.Average(u => Number(u, "hpPercent")),
				["ownCombatValue"] = members.Where(u => Weapons(state, u).Any()).Sum(u => Value(state, u)),
				["nearbyFriendlyCombatValue"] = friendly.Sum(u => Value(state, u)),
				["nearbyEnemyCombatValue"] = enemies.Where(e => Weapons(state, e).Any()).Sum(e => Value(state, e)),
				["visibleEnemyIds"] = Ids(enemies.Select(e => Number(e, "id")))
			};
			var choices = new JsonObject { ["continue"] = "Keep the current command if it is still useful. Idle troops are not advancing an operation." };
			var actions = new Dictionary<string, JevAction>();
			void Add(string key, string type, (int X, int Y) cell, string description)
			{
				choices[key] = new JsonObject { ["directive"] = description, ["cell"] = CellNode(cell) };
				actions[key] = new(new JsonObject { ["type"] = type, ["actorIds"] = Ids(group.Members), ["cell"] = CellNode(cell) }, id);
			}
			Add("assemble", "move", anchor, "Gather this group at its local anchor and wait for reinforcements; ordinary automatic targeting remains active.");
			Add("advance", "attack_move", objective, "Commit this group together toward the common operation, fighting opposition en route.");
			Add("withdraw", "move", home, "Withdraw to the base to preserve this group.");
			var threat = Objects(state, "visibleEnemies").Where(e => Objects(state, "buildings").Any(b => Distance(Cell(b["cell"]), Cell(e["cell"])) < 12))
				.OrderBy(e => Distance(anchor, Cell(e["cell"]))).FirstOrDefault();
			if (threat != null) Add("defend", "attack_move", Cell(threat["cell"]), "Intercept a visible threat close to our base.");
			var other = Tactics.Groups.Where(kv => kv.Key != id && kv.Value.Role == "front" && kv.Value.Members.Count > 1)
				.OrderBy(kv => Distance(anchor, Anchor(kv.Value, units))).FirstOrDefault();
			if (other.Value != null) Add("reinforce", "attack_move", Anchor(other.Value, units), "Join friendly " + other.Key + " as a group instead of crossing the map alone.");
			var targetChoices = new JsonObject { ["none"] = "No useful local target; retain movement or wait." };
			var targetActions = new Dictionary<string, JevAction>();
			foreach (var enemy in enemies.Where(e => group.Role == "capture" ? Bool(Catalog(state, Name(e)), "capturable") : CanHit(state, members, e))
				.OrderBy(e => Distance(anchor, Cell(e["cell"]))).Take(24))
			{
				var target = Number(enemy, "id");
				var key = "target" + target;
				targetChoices[key] = new JsonObject
				{
					["target"] = enemy.DeepClone(), ["distanceCells"] = Distance(anchor, Cell(enemy["cell"])),
					["properties"] = Catalog(state, Name(enemy))?.DeepClone()
				};
				targetActions[key] = new(new JsonObject { ["type"] = group.Role == "capture" ? "capture" : "attack",
					["actorIds"] = Ids(group.Role == "capture" ? group.Members.Take(1) : group.Members), ["targetActorId"] = target }, id);
			}
			if (targetActions.Count > 0)
			{
				choices["engage"] = "Engage the local target selected by this group's separate target question.";
				frame.Questions[id + "_target"] = Choice($"If {id} engages now, which visible local target should its {group.Role} units attack or capture? " +
					"Use localCombat, actual weapon ranges, target roles and health. Finish vulnerable threats that these weapons counter. " +
					"Artillery should exploit its range against defenses and production while friendly units cover it; avoid chasing mobile bait. " +
					"Capture specialists should take a valuable reachable structure rather than fight infantry. This answer is unused unless maneuver is engage.", targetChoices);
				targets[id] = targetActions;
			}
			frame.Actions[id] = actions;
			frame.Questions[id] = Choice($"Choose the maneuver for {id}, a {group.Role} group, using localCombat.{id} and tactics. " +
				"Decide explicitly whether to assemble, reinforce, engage locally, advance together, or withdraw. " +
				"A spread-out group does not deliver its listed combat value at once. New recruits can join another group rather than trickle into enemy fire. " +
				"An isolated light unit can scout, but do not repeatedly sacrifice lone reinforcements. " +
				"A cohesive force with support should exploit an advantage and finish the enemy, without waiting for an arbitrary army size. " +
				"After a campaign launch or scout order, continue the committed advance unless local danger or a real cohesion problem requires a change; " +
				"do not cancel the offensive simply to wait for future reinforcements. Artillery needs cover and standoff; capture units are unarmed.", choices);
		}
		RequestState["localCombat"] = combat;
	}

	void BuildCampaign(JevFrame frame)
	{
		campaignGroups.Clear();
		if (Number(frame.State, "second") - Tactics.CampaignAt < config.ObjectiveSeconds) return;
		campaignGroups.AddRange(Tactics.Groups.Where(g => g.Value.Role is "front" or "artillery" or "air"
			&& g.Value.Phase is not ("advance" or "engage" or "scout")).Select(g => g.Key));
		if (campaignGroups.Count == 0) return;
		var members = campaignGroups.SelectMany(g => Tactics.Groups[g].Members).ToList();
		var home = Cell(frame.State["map"]?["yourSpawnCell"]);
		var objective = Tactics.OperationCell is { Length: 2 } c ? (c[0], c[1]) : Objects(frame.State, "enemySpawns").Select(e => Cell(e["cell"])).FirstOrDefault(home);
		frame.Questions["campaign"] = Choice("Should the preparing combat groups now launch a coordinated offensive? " +
			"Use all friendly forces, current economy, localCombat and observed opposition. Gathering indefinitely cannot win. " +
			"An unknown enemy force is a reason to obtain contact, not to wait forever. " +
			"An early scout can find the enemy, but repeatedly launching lone reinforcements into resistance wastes units. " +
			"Commit a useful combined force when its strength justifies pressure; hold when an actual threat or missing capability warrants delay. " +
			"This is an executable army-wide decision. Launch takes precedence over these groups' local movement answers for this observation.",
			new JsonObject { ["hold"] = "Keep the groups' local decisions; delay the coordinated offensive.",
				["launch"] = new JsonObject { ["directive"] = "Send all listed preparing groups toward the common objective together, engaging opposition en route.",
					["groups"] = new JsonArray([.. campaignGroups.Select(g => (JsonNode)JsonValue.Create(g)!)]), ["cell"] = CellNode(objective) } });
		frame.Actions["campaign"] = new() { ["launch"] = new(new JsonObject { ["type"] = "attack_move", ["actorIds"] = Ids(members), ["cell"] = CellNode(objective) }, "campaign") };
		var units = Objects(frame.State, "units").ToDictionary(u => Number(u, "id"));
		var scouts = campaignGroups.Where(g => Tactics.Groups[g].Role == "front"
			&& (Tactics.Groups.Count < config.MaxSquads || Tactics.Groups[g].Members.Count == 1))
			.SelectMany(g => Tactics.Groups[g].Members).OrderBy(id => Number(Catalog(frame.State, Name(units[id])), "cost")).Take(3);
		foreach (var id in scouts)
		{
			var key = "scout" + id;
			frame.Questions["campaign"]!["criteria"]![key] = new JsonObject
			{
				["directive"] = "Send this one unit to discover the enemy's base and strength while the rest prepare. Avoid repeated sacrificial scouting once the enemy is located.",
				["unit"] = units[id].DeepClone(), ["cell"] = CellNode(objective)
			};
			frame.Actions["campaign"][key] = new(new JsonObject { ["type"] = "attack_move", ["actorIds"] = Ids([id]), ["cell"] = CellNode(objective) }, "campaign");
		}
	}

	void BuildMaintenance(JevFrame frame)
	{
		var state = frame.State;
		var choices = new JsonObject { ["none"] = "No repair or sale now." };
		var actions = new Dictionary<string, JevAction>();
		var lowPower = Number(state["you"], "powerProvided") < Number(state["you"], "powerDrained");
		foreach (var b in Objects(state, "buildings"))
		{
			var id = Number(b, "id");
			if (Number(b, "hpPercent") < 80 && !Bool(b, "repairing"))
			{
				choices["repair" + id] = $"Enable paid repairs for {Name(b)} #{id}, health {Number(b, "hpPercent")}%.";
				actions["repair" + id] = new(new JsonObject { ["type"] = "repair", ["actorId"] = id, ["expectedRepairing"] = false }, "maintenance");
			}
			if (lowPower && Number(Catalog(state, Name(b)), "power") < 0)
			{
				choices["sell" + id] = $"Sell {Name(b)} #{id} to remove its power drain; lose the building's services and prerequisites.";
				actions["sell" + id] = new(new JsonObject { ["type"] = "sell", ["actorId"] = id }, "maintenance");
			}
		}
		if (actions.Count == 0) return;
		frame.Questions["maintenance"] = Choice("Should a damaged building be repaired or an expendable power consumer sold? " +
			"Repairs continuously cost money. Low power slows production and disables defenses; damaged power plants produce less. " +
			"Preserve harvesting and useful military production, and avoid repeated repair toggles.", choices);
		frame.Actions["maintenance"] = actions;
	}

	public JsonObject Apply(JevFrame frame, JsonObject response, JsonObject latest, PlayerOrderChannel channel)
	{
		JevClient.ValidateAnswers(frame.Questions, response);
		if (Number(latest, "tick") - frame.Tick > config.MaxResponseAgeTicks || Bool(latest["you"], "defeated"))
			return core.Apply(frame, response, latest, channel);
		var answers = response["answers"]!;
		foreach (var (group, options) in targets)
			if (Text(answers[group], "choice") == "engage" && options.TryGetValue(Text(answers[group + "_target"], "choice"), out var target))
				frame.Actions[group]["engage"] = target;
		var trace = core.Apply(frame, response, latest, channel);
		foreach (var d in Objects(trace, "decisions"))
		{
			if (Text(d, "outcome") == "submitted" && Tactics.Groups.TryGetValue(Text(d, "question"), out var group))
				group.Phase = Text(d, "choice");
			if (Text(d, "question") == "campaign" && Text(d, "outcome") == "submitted")
			{
				var choice = Text(d, "choice");
				Tactics.Campaign = choice == "launch" ? "launch" : "scout";
				if (choice == "launch")
					foreach (var id in campaignGroups) Tactics.Groups[id].Phase = "advance";
				else
				{
					var scout = d["order"]!["actorIds"]![0]!.GetValue<long>();
					var source = Tactics.Groups.Values.Single(g => g.Members.Contains(scout));
					if (source.Members.Count == 1) source.Phase = "scout";
					else
					{
						source.Members.Remove(scout);
						Tactics.Groups["group" + Tactics.NextGroup++] = new JevGroup { Role = source.Role, Phase = "scout", Members = [scout], CreatedAt = Number(latest, "second") };
					}
				}
			}
		}
		if (answers["campaign"] != null) Tactics.CampaignAt = Number(latest, "second");
		if (answers["operation"] != null)
		{
			Tactics.OperationAt = Number(latest, "second");
			if (operations.TryGetValue(Text(answers["operation"], "choice"), out var op))
			{
				Tactics.Operation = op.Name;
				Tactics.OperationCell = [op.Cell.X, op.Cell.Y];
			}
		}
		trace["policyVersion"] = 2;
		return trace;
	}

	static bool IsRefinery(JsonObject state, string name) => state["spatialEconomy"]?["refineries"]?[name] != null || name == "Tiberium Refinery";
	static IEnumerable<JsonObject> Weapons(JsonObject state, JsonObject unit) => Objects(Catalog(state, Name(unit)), "weapons");
	static double Range(JsonObject state, JsonObject unit) => Weapons(state, unit).Select(w => Real(w["rangeCells"])).DefaultIfEmpty(0).Max();
	static double Value(JsonObject state, JsonObject unit) => Number(Catalog(state, Name(unit)), "cost") * Number(unit, "hpPercent") / 100.0;
	static string Role(JsonObject state, JsonObject unit) => Bool(Catalog(state, Name(unit)), "canCapture") ? "capture"
		: Bool(Catalog(state, Name(unit)), "isAircraft") ? "air" : Range(state, unit) >= 7 ? "artillery" : Weapons(state, unit).Any() ? "front" : "support";
	static bool CanHit(JsonObject state, List<JsonObject> units, JsonObject enemy) => units.Any(u => Weapons(state, u)
		.Any(w => Text(w, "targets").Split(',').Contains(Bool(Catalog(state, Name(enemy)), "isAircraft") ? "Air" : "Ground")));
	static JsonArray Ids(IEnumerable<long> ids) => new([.. ids.Select(i => (JsonNode)JsonValue.Create(i)!)]);
	static double Distance((int X, int Y) a, (int X, int Y) b) => Math.Round(Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2)), 1);
	static double? Closest((int X, int Y) cell, IEnumerable<JsonObject> entries) => entries.Select(e => (double?)Distance(cell, Cell(e["cell"]))).Min();
	static (int X, int Y) Anchor(JevGroup group, Dictionary<long, JsonObject> units) => group.Members.Select(i => Cell(units[i]["cell"]))
		.MinBy(c => group.Members.Sum(i => Distance(c, Cell(units[i]["cell"]))));
}
