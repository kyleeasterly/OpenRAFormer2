using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orf;

public sealed class JevMemory
{
	public string Focus { get; set; } = "develop";
	public long FocusAt { get; set; } = -1000;
	public string? Goal { get; set; }
	public int GoalCount { get; set; }
	public long GoalAt { get; set; } = -1000;
	public Dictionary<string, List<long>> Squads { get; set; } = [];
	public Dictionary<string, JevAssignment> Assignments { get; set; } = [];
	public List<string> Events { get; set; } = [];

	public void Note(string message)
	{
		Events.Add(message);
		if (Events.Count > 24)
			Events.RemoveAt(0);
	}
}

public sealed record JevAssignment(string Action, long Second);
public sealed record JevAction(JsonObject Order, string Lane, long Cost = 0);

public sealed class JevFrame(JsonObject state)
{
	public JsonObject State { get; } = state;
	public JsonObject Questions { get; } = [];
	public Dictionary<string, Dictionary<string, JevAction>> Actions { get; } = [];
	public Dictionary<string, string> Goals { get; } = [];
	public string? ReserveItem { get; set; }
	public long ReserveCost { get; set; }
	public long Tick => JevPolicy.Number(State, "tick");
}

/// <summary>
/// Builds bounded decisions from player-visible state. No opening script, build order,
/// automatic attack timing, or LLM: Jev chooses the objectives and executable actions.
/// Grouping, budget accounting, command persistence, and legal candidate generation are code.
/// </summary>
public sealed class JevPolicy(JevSpec config, JevMemory? memory = null)
{
	public JevMemory Memory { get; } = memory ?? new();
	static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

	public JevFrame Prepare(JsonObject state, JsonArray results, JsonArray pending)
	{
		Reconcile(state);
		var frame = new JevFrame(state);
		var second = Number(state, "second");
		if (second - Memory.FocusAt >= config.ObjectiveSeconds)
			frame.Questions["focus"] = Choice(
				"Which single strategic priority is most urgent for the next short interval, given `game` and `memory`? " +
				"Victory requires destroying enemy bases. Economy and technology support that objective.", new JsonObject
				{
					["develop"] = "Grow income and production capacity for sustained military spending.",
					["technology"] = "Unlock a useful military capability through its actual prerequisites.",
					["pressure"] = "Produce forces and apply pressure to known enemy positions.",
					["defend"] = "Preserve the economy and base against a present attack.",
					["expand"] = "Establish income near another explored resource field.",
					["recover"] = "Restore lost construction, production, power, or harvesting capability.",
					["scout"] = "Discover enemy positions and threats so subsequent attacks are informed."
				});

		if (second - Memory.GoalAt >= config.ObjectiveSeconds * (Memory.Goal == null ? 1 : 3))
		{
			var options = new JsonObject { ["none"] = "No capital commitment; keep resources available for immediate orders." };
			foreach (var item in Items(state).Where(i => IsBuilding(state, Name(i)) || !Bool(i, "available")).Take(120))
			{
				var key = "g" + frame.Goals.Count;
				frame.Goals[key] = Name(item);
				options[key] = item.DeepClone();
			}

			if (options.Count > 1)
				frame.Questions["goal"] = Choice(
					"Choose one next capital objective for `game`, consistent with `memory.focus`: " +
					"one additional building, or unlocking an unavailable unit. Options contain current prerequisites. " +
					"Account for what is already owned or queued, income, power, and the role of each item. " +
					"This commitment persists until achieved or reconsidered; it does not itself issue a purchase.", options);
		}

		BuildProduction(frame);
		BuildPlacements(frame);
		BuildSquads(frame);
		BuildWorkers(frame);
		var stepNames = GoalStepNames(state);
		var queued = Objects(state, "production").SelectMany(q => Objects(q, "buildable"))
			.Where(i => stepNames.Contains(Name(i)))
			.Where(i => !Objects(state, "production").Any(q => Text(q["current"], "name") == Name(i)
				|| (q["queued"] as JsonArray ?? []).Any(n => n?.GetValue<string>() == Name(i))))
			.Where(i => !Objects(state, "pendingPlacement").Any(p => Text(p, "item") == Name(i)))
			.OrderBy(i => Number(i, "cost")).FirstOrDefault();
		if (queued != null)
		{
			frame.ReserveItem = Name(queued);
			frame.ReserveCost = Number(queued, "cost");
			frame.Questions["budget"] = Choice($"Should current funds be reserved toward {frame.ReserveItem} (${frame.ReserveCost}), " +
				$"a step toward `memory.goal` ({Memory.Goal})? Consider present threats and the opportunity cost of delaying this goal.", new JsonObject
				{
					["reserve"] = "Protect funds up to this purchase's price from other queues; this purchase can still use them.",
					["available"] = "Allow all selected purchases to compete for current cash."
				});
		}

		// All questions see this one state. Decisions use the previous committed
		// objective; a newly selected objective is visible on the next observation.
		var observation = (JsonObject)state.DeepClone();
		foreach (var placement in Objects(observation, "pendingPlacement"))
		{
			placement.Remove("grid");
			placement.Remove("legend");
			placement.Remove("validCellsSample");
		}

		RequestState = new JsonObject
		{
			["game"] = observation,
			["memory"] = JsonSerializer.SerializeToNode(Memory, JsonOptions),
			["engineResults"] = results.DeepClone(),
			["pendingOrders"] = pending.DeepClone(),
			["rules"] = "This is OpenRA Tiberian Dawn, not the original game's tech tree. " +
				"Use the supplied ruleCatalog, buildable lists, and prerequisites. A queue progresses over time. " +
				"An order accepted by the bridge is not proof it completed; observe the next game state. " +
				"Enemy observations are partial. Unknown positions and old sightings are not evidence of safety. " +
				"Each question is independent. Do not assume another answer in this request. " +
				"Choices are immediate actions unless explicitly described as objectives. Continue preserves an existing command. " +
				"Transport loading, superweapon activation, repair, sell and cancel are unavailable in this controller."
		};
		return frame;
	}

	public JsonObject RequestState { get; private set; } = [];

	void BuildProduction(JevFrame frame)
	{
		var state = frame.State;
		var queueIndex = 0;
		foreach (var queue in Objects(state, "production"))
		{
			var lane = Text(queue, "queue");
			// One waiting unit batch per queue; buildings must be placed before the
			// next structure. This is a bounded action horizon, not a build doctrine.
			var buildingQueue = lane.StartsWith("Building") || lane.StartsWith("Defense") || lane.StartsWith("Support");
			if (buildingQueue && queue["current"] is JsonObject || (queue["queued"] as JsonArray)?.Count > 0)
				continue;

			var id = "production" + queueIndex++;
			var choices = new JsonObject { ["wait"] = "Save resources; do not add to this queue on this observation." };
			var actions = new Dictionary<string, JevAction>();
			foreach (var item in Objects(queue, "buildable"))
			{
				var name = Name(item);
				var cost = Number(item, "cost");
				foreach (var count in buildingQueue ? new[] { 1 } : new[] { 1, 3 })
				{
					if (cost * count > Number(state["you"], "cash"))
						continue;
					var key = "a" + actions.Count;
					var order = new JsonObject { ["type"] = "start_production", ["item"] = name, ["count"] = count };
					actions[key] = new(order, lane, cost * count);
					choices[key] = new JsonObject
					{
						["item"] = name, ["count"] = count, ["cost"] = cost * count,
						["owned"] = CountOwned(state, name),
						["towardGoal"] = GoalStepNames(state).Contains(name),
						["properties"] = Catalog(state, name)?.DeepClone()
					};
				}
			}

			if (actions.Count == 0)
				continue;
			AddActions(frame, id, $"Select the next purchase for the {lane} queue in `game`. " +
				"Compare actual capabilities, owned counts, income, power, visible threats, and `memory.goal`. " +
				"More copies are useful only if their benefit justifies the cost. Harvesters and defenses have ongoing opportunity costs. " +
				"Keep a functioning economy and pursue victory, including scouting and attacking. Choose wait when saving is preferable. " +
				"Your objective is to win the match by destroying the enemy base. Idle production and unused cash also have an opportunity cost. " +
				"Choose purchases that make progress toward victory even when no enemies are currently visible. " +
				"Saving should serve a concrete near-term need.", choices, actions);
			frame.Questions[id + "_urgency"] = Score(
				$"How urgent is spending on the {lane} queue in `game` relative to other queues right now? " +
				"Judge the queue's unmet need, not an answer from another question. Use the same urgency scale for all queues.");
		}
	}

	void BuildPlacements(JevFrame frame)
	{
		var index = 0;
		foreach (var pending in Objects(frame.State, "pendingPlacement"))
		{
			var name = Text(pending, "item");
			var choices = new JsonObject { ["wait"] = "Leave this completed building unplaced for now." };
			var actions = new Dictionary<string, JevAction>();
			var cells = LegalCells(pending).ToList();
			// Uniform coverage of the entire grid, avoiding top-left sampling bias.
			var count = Math.Min(config.MaxPlacementOptions, cells.Count);
			for (var i = 0; i < count; i++)
			{
				var cell = cells[i * cells.Count / count];
				var key = $"c{cell.X}_{cell.Y}";
				var order = new JsonObject { ["type"] = "place_building", ["item"] = name, ["cell"] = CellNode(cell) };
				actions[key] = new(order, "placement:" + name);
				choices[key] = new JsonObject
				{
					["cell"] = CellNode(cell),
					["resourceDistance"] = NearestDistance(cell, Objects(frame.State, "exploredResources")),
					["visibleEnemyDistance"] = NearestDistance(cell, Objects(frame.State, "visibleEnemies")),
					["homeDistance"] = Distance(cell, Cell(frame.State["map"]?["yourSpawnCell"]))
				};
			}

			if (actions.Count > 0)
				AddActions(frame, "placement" + index++, $"Choose the site for the completed {name} in `game.pendingPlacement`. " +
					"All offered sites are legal in this observation. Consider the building's role: harvesting travel distance, " +
					"defensive coverage, vulnerable infrastructure, and space around factories. Placement activates a completed investment.", choices, actions);
		}
	}

	void BuildSquads(JevFrame frame)
	{
		var state = frame.State;
		var all = Objects(state, "units").ToDictionary(u => Number(u, "id"));
		foreach (var (squad, ids) in Memory.Squads)
		{
			if (ids.Count == 0)
				continue;
			var members = ids.Select(id => all[id]).ToList();
			var center = Centroid(members);
			var choices = new JsonObject { ["continue"] = "Keep the existing command because it still makes progress toward the current objective; issue no order." };
			var actions = new Dictionary<string, JevAction>();
			void Add(string key, string type, (int X, int Y) cell, string description, long? target = null, IEnumerable<long>? selected = null)
			{
				var order = new JsonObject { ["type"] = type, ["actorIds"] = Ids(selected ?? ids) };
				if (target.HasValue)
					order["targetActorId"] = target.Value;
				else
					order["cell"] = CellNode(cell);
				actions[key] = new(order, squad);
				choices[key] = new JsonObject { ["directive"] = description, ["cell"] = CellNode(cell), ["distance"] = Distance(center, cell) };
			}

			foreach (var enemy in Objects(state, "visibleEnemies").OrderBy(e => Distance(center, Cell(e["cell"]))).Take(24))
			{
				var target = Number(enemy, "id");
				Add("attack" + target, "attack", Cell(enemy["cell"]), $"Focus attack on visible {Name(enemy)} #{target}", target);
				var capturer = members.Where(u => Bool(Catalog(state, Name(u)), "canCapture"))
					.OrderBy(u => Distance(Cell(u["cell"]), Cell(enemy["cell"]))).FirstOrDefault();
				if (capturer != null && Bool(Catalog(state, Name(enemy)), "capturable"))
					Add("capture" + target, "capture", Cell(enemy["cell"]), $"Send one capture-capable member to capture visible {Name(enemy)} #{target}",
						target, [Number(capturer, "id")]);
			}
			var dests = Objects(state, "lastKnownEnemyBuildings").Select(e => (Cell(e["cell"]), "Search and attack last-seen " + Name(e)))
				.Concat(Objects(state, "enemySpawns").Select(e => (Cell(e["cell"]), "Scout/attack enemy start; explored=" + Bool(e, "explored"))))
				.DistinctBy(d => d.Item1).Take(24).ToList();
			foreach (var (cell, description) in dests)
			{
				Add($"advance{cell.X}_{cell.Y}", "attack_move", cell, description + " with this squad");
				var scout = members.OrderBy(u => Catalog(state, Name(u))?["cost"]?.GetValue<long>() ?? 0).First();
				Add($"scout{cell.X}_{cell.Y}", "attack_move", cell, description + " with one member; other members keep their orders", selected: [Number(scout, "id")]);
			}

			var home = Cell(state["map"]?["yourSpawnCell"]);
			Add("regroup", "move", home, "Withdraw and regroup at home");
			Add("defend", "attack_move", home, "Return to defend home, engaging enemies along the way");
			foreach (var building in Objects(state, "buildings").Take(12))
				Add("guard" + Number(building, "id"), "guard", Cell(building["cell"]), "Guard own " + Name(building), Number(building, "id"));

			AddActions(frame, squad, $"Choose one complete tactical directive for `memory.squads.{squad}` using only `game` and its current assignment. " +
				"Consider the members' health, capabilities, distance, visible opposition and the committed focus. " +
				"A scout action sends only one member. Unknown territory is uncertain. " +
				"Continue preserves a useful current command; regroup is movement, not an attack. " +
				"Your objective is to win by destroying the enemy base. Follow memory.focus with a useful assignment; " +
				"continuing an old guard task does not scout or apply pressure. When no enemies are visible, discovering their position enables progress. " +
				"Balance this against actual threats and the value of these units.", choices, actions);
		}
	}

	void BuildWorkers(JevFrame frame)
	{
		foreach (var unit in Objects(frame.State, "units"))
		{
			var id = Number(unit, "id");
			var properties = Catalog(frame.State, Name(unit));
			var harvester = Bool(properties, "isHarvester") || Name(unit) == "Harvester";
			var deploy = Text(properties, "deploysInto");
			if (!harvester && deploy.Length == 0 && Name(unit) != "Mobile Construction Vehicle")
				continue;
			var choices = new JsonObject { ["continue"] = "Keep the current task." };
			var actions = new Dictionary<string, JevAction>();
			if (harvester)
			{
				actions["harvest"] = new(new JsonObject { ["type"] = "harvest", ["actorIds"] = Ids([id]) }, "worker" + id);
				choices["harvest"] = "Resume automatic harvesting.";
			}
			else
			{
				actions["deploy"] = new(new JsonObject { ["type"] = "deploy", ["actorIds"] = Ids([id]) }, "worker" + id);
				choices["deploy"] = "Deploy here into " + (deploy.Length > 0 ? deploy : "Construction Yard");
			}

			foreach (var field in Objects(frame.State, "exploredResources").Take(24))
			{
				var cell = Cell(field["cell"]);
				var key = $"field{cell.X}_{cell.Y}";
				// MCVs stop near resources rather than directly on unbuildable tiberium.
				if (!harvester)
					cell = (Math.Max(0, cell.X - 4), cell.Y);
				actions[key] = new(new JsonObject { ["type"] = harvester ? "harvest" : "move", ["actorIds"] = Ids([id]), ["cell"] = CellNode(cell) }, "worker" + id);
				choices[key] = new JsonObject { ["task"] = harvester ? "Harvest this field" : "Move near this field for a possible expansion", ["cell"] = CellNode(cell), ["resourceCells"] = field["cells"]?.DeepClone() };
			}

			AddActions(frame, "worker" + id, $"Choose the next task for your {Name(unit)} #{id} in `game.units`. " +
				"Consider its existing activity, economy, explored resources, and visible threats. " +
				"A Construction Yard is required to build a base. Repeatedly changing a working harvester's destination disrupts income.", choices, actions);
		}
	}

	public JsonObject Apply(JevFrame frame, JsonObject response, JsonObject latest, PlayerOrderChannel channel)
	{
		JevClient.ValidateAnswers(frame.Questions, response);
		var answers = (JsonObject)response["answers"]!;
		var second = Number(latest, "second");
		var trace = new JsonArray();
		var orders = new JsonArray();
		if (Number(latest, "tick") - frame.Tick > config.MaxResponseAgeTicks || Bool(latest["you"], "defeated"))
			return new JsonObject { ["orders"] = orders, ["discarded"] = "observation expired or player defeated", ["decisions"] = trace };

		if (answers["focus"]?["choice"]?.GetValue<string>() is { } focus)
		{
			Memory.Focus = focus;
			Memory.FocusAt = second;
			Memory.Note($"t={second}: focus {focus}");
		}
		if (answers["goal"]?["choice"]?.GetValue<string>() is { } goal)
		{
			Memory.Goal = frame.Goals.GetValueOrDefault(goal);
			Memory.GoalCount = Memory.Goal == null ? 0 : CountOwned(latest, Memory.Goal) + 1;
			Memory.GoalAt = second;
			Memory.Note($"t={second}: objective {Memory.Goal ?? "none"}");
		}

		var budget = Math.Max(0, Number(latest["you"], "cash") - channel.ReservedCash);
		var reserve = Text(answers["budget"], "choice") == "reserve" ? Math.Min(budget, frame.ReserveCost) : 0;
		var usedActors = new HashSet<long>();
		var usedLanes = new HashSet<string>();
		var candidates = frame.Actions.Select(kv =>
		{
			var choice = Text(answers[kv.Key], "choice");
			var urgency = answers[kv.Key + "_urgency"]?["score"] is { } score ? Real(score) : 5;
			return (Id: kv.Key, Choice: choice, Action: kv.Value.GetValueOrDefault(choice), Urgency: urgency);
		}).OrderByDescending(c => c.Urgency).ThenBy(c => c.Id, StringComparer.Ordinal);

		foreach (var c in candidates)
		{
			var decision = new JsonObject { ["question"] = c.Id, ["choice"] = c.Choice, ["confidence"] = answers[c.Id]?["confidence"]?.DeepClone() };
			trace.Add(decision);
			if (c.Action == null)
			{
				decision["outcome"] = "continue / save";
				continue;
			}

			var order = (JsonObject)c.Action.Order.DeepClone();
			var reason = channel.Validate(order, latest);
			var actualCost = PlayerOrderChannel.OrderCost(latest, order);
			var actorIds = (order["actorIds"] as JsonArray)?.Select(i => i!.GetValue<long>()).ToList() ?? [];
			if (reason == null && (actorIds.Any(usedActors.Contains) || usedLanes.Contains(c.Action.Lane)))
				reason = "another selected order owns this actor or queue";
			var isReservedPurchase = Text(order, "type") == "start_production" && Text(order, "item") == frame.ReserveItem;
			if (reason == null && actualCost > budget - (isReservedPurchase ? 0 : reserve))
				reason = "budget reserved by other selected or pending orders";
			var signature = order.ToJsonString();
			if (reason == null && c.Action.Cost == 0 && Memory.Assignments.TryGetValue(c.Action.Lane, out var assignment))
			{
				var idle = actorIds.All(id => Objects(latest, "units").Any(u => Number(u, "id") == id && Bool(u, "idle")));
				if (second - assignment.Second < config.CommandHoldSeconds
					|| (assignment.Action == signature && actorIds.Count > 0 && !idle))
					reason = "current command retained";
			}

			if (reason != null)
			{
				decision["outcome"] = reason;
				continue;
			}

			budget -= actualCost;
			if (isReservedPurchase)
				reserve = 0;
			usedLanes.Add(c.Action.Lane);
			usedActors.UnionWith(actorIds);
			orders.Add(order);
			if (c.Action.Cost == 0)
				Memory.Assignments[c.Action.Lane] = new(signature, second);
			decision["outcome"] = "submitted";
			decision["order"] = order.DeepClone();
		}

		return new JsonObject { ["orders"] = orders, ["decisions"] = trace, ["remainingBudget"] = budget };
	}

	void Reconcile(JsonObject state)
	{
		var live = Objects(state, "units").Select(u => Number(u, "id")).ToHashSet();
		foreach (var squad in Memory.Squads.Values)
			squad.RemoveAll(id => !live.Contains(id));
		var assigned = Memory.Squads.Values.SelectMany(s => s).ToHashSet();
		foreach (var unit in Objects(state, "units").OrderBy(u => Number(u, "id")))
		{
			var id = Number(unit, "id");
			var data = Catalog(state, Name(unit));
			if (assigned.Contains(id) || Bool(data, "isHarvester") || Text(data, "deploysInto").Length > 0
				|| Name(unit) is "Harvester" or "Mobile Construction Vehicle")
				continue;
			var squad = Memory.Squads.FirstOrDefault(kv => kv.Value.Count < config.SquadSize).Value;
			if (squad == null)
			{
				if (Memory.Squads.Count < config.MaxSquads)
					Memory.Squads["squad" + Memory.Squads.Count] = squad = [];
				else
					squad = Memory.Squads.MinBy(kv => kv.Value.Count).Value;
			}
			squad.Add(id);
		}

		if (Memory.Goal is { } goal && (IsBuilding(state, goal)
			? CountOwned(state, goal) >= Memory.GoalCount
			: Objects(state, "production").Any(q => Objects(q, "buildable").Any(i => Name(i) == goal))))
		{
			Memory.Note($"t={Number(state, "second")}: achieved {goal}");
			Memory.Goal = null;
			Memory.GoalAt = Number(state, "second") - config.ObjectiveSeconds;
		}
	}

	HashSet<string> GoalStepNames(JsonObject state)
	{
		var names = new HashSet<string>();
		void Visit(string name)
		{
			if (!names.Add(name))
				return;
			foreach (var requirement in Catalog(state, name)?["requires"] as JsonArray ?? [])
				foreach (var alternative in (requirement?.GetValue<string>() ?? "").Split(" or "))
					Visit(alternative);
		}
		if (Memory.Goal != null)
			Visit(Memory.Goal);
		return names;
	}

	static void AddActions(JevFrame frame, string id, string instructions, JsonObject choices, Dictionary<string, JevAction> actions)
	{
		frame.Questions[id] = Choice(instructions, choices);
		frame.Actions[id] = actions;
	}

	public static JsonObject Choice(string instructions, JsonObject criteria) => new() { ["type"] = "choice", ["instructions"] = instructions, ["criteria"] = criteria };
	static JsonObject Score(string instructions) => new()
	{
		["type"] = "score", ["instructions"] = instructions,
		["criteria"] = new JsonArray("No unmet need; saving is preferable", "Optional improvement", "Useful next investment", "Urgent bottleneck", "Immediate survival need")
	};
	public static IEnumerable<JsonObject> Objects(JsonNode? parent, string key) => (parent?[key] as JsonArray ?? []).OfType<JsonObject>();
	public static long Number(JsonNode? parent, string key) => parent?[key] is JsonValue value
		? value.TryGetValue<long>(out var n) ? n : value.TryGetValue<int>(out var i) ? i : throw new InvalidDataException($"Expected integer: {key}") : 0;
	public static double Real(JsonNode? node) => node is JsonValue value
		? value.TryGetValue<double>(out var n) ? n : value.TryGetValue<int>(out var i) ? i : value.TryGetValue<long>(out var l) ? l : double.NaN : double.NaN;
	public static string Text(JsonNode? parent, string key) => parent?[key]?.GetValue<string>() ?? "";
	public static bool Bool(JsonNode? parent, string key) => parent?[key]?.GetValue<bool>() == true;
	public static string Name(JsonObject item) => Text(item, "name");
	public static JsonObject? Catalog(JsonObject state, string name) => state["ruleCatalog"]?[name] as JsonObject;
	static int CountOwned(JsonObject state, string name) => Objects(state, "buildings").Concat(Objects(state, "units")).Count(b => Name(b) == name);
	static bool IsBuilding(JsonObject state, string name) => Bool(Catalog(state, name), "isBuilding") || Objects(state, "production")
		.Any(q => (Text(q, "queue").StartsWith("Building") || Text(q, "queue").StartsWith("Support") || Text(q, "queue").StartsWith("Defense"))
			&& Objects(q, "buildable").Concat(Objects(q, "locked")).Any(i => Name(i) == name));
	static IEnumerable<JsonObject> Items(JsonObject state) => Objects(state, "production").SelectMany(q =>
		Objects(q, "buildable").Select(i => new JsonObject { ["name"] = Name(i), ["available"] = true, ["properties"] = Catalog(state, Name(i))?.DeepClone() })
		.Concat(Objects(q, "locked").Select(i => new JsonObject { ["name"] = Name(i), ["available"] = false, ["properties"] = Catalog(state, Name(i))?.DeepClone(), ["requires"] = i["requires"]?.DeepClone() })))
		.DistinctBy(i => Name(i));
	public static (int X, int Y) Cell(JsonNode? node) => node is JsonArray { Count: >= 2 } c ? (c[0]!.GetValue<int>(), c[1]!.GetValue<int>()) : (0, 0);
	public static JsonArray CellNode((int X, int Y) cell) => new(cell.X, cell.Y);
	static JsonArray Ids(IEnumerable<long> ids) => new([.. ids.Select(id => (JsonNode)JsonValue.Create(id)!)]);
	static double Distance((int X, int Y) a, (int X, int Y) b) => Math.Round(Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2)), 1);
	static double? NearestDistance((int X, int Y) cell, IEnumerable<JsonObject> entries) => entries.Select(e => (double?)Distance(cell, Cell(e["cell"]))).Min();
	static (int X, int Y) Centroid(List<JsonObject> units) => ((int)units.Average(u => Cell(u["cell"]).X), (int)units.Average(u => Cell(u["cell"]).Y));
	public static IEnumerable<(int X, int Y)> LegalCells(JsonObject pending)
	{
		var origin = Cell(pending["gridOrigin"]);
		if (pending["grid"] is JsonArray rows)
		{
			for (var y = 0; y < rows.Count; y++)
			{
				var row = rows[y]?.GetValue<string>() ?? "";
				for (var x = 0; x < row.Length; x++)
					if (row[x] == '+')
						yield return (origin.X + x, origin.Y + y);
			}
		}
		else
			foreach (var cell in pending["validCellsSample"] as JsonArray ?? [])
				yield return Cell(cell);
	}
}
