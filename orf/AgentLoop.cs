using System.Text.Json.Nodes;

namespace Orf;

/// <summary>
/// One agent playing one player slot. Runs a turn every turnIntervalSeconds against
/// R/state/&lt;slug&gt;.json and writes orders to R/orders/&lt;slug&gt;/inbox/ plus artifacts
/// under R/agents/&lt;slug&gt;/. Exceptions never propagate out of the loop.
/// </summary>
public sealed class AgentLoop
{
	readonly string runDir;
	readonly Spec spec;
	readonly PlayerSpec player;
	readonly ProviderSpec provider;
	readonly int index;
	readonly string systemPrompt;
	readonly LlmClient? llm;

	// Swarm mode: this loop drives one specialist role of the player rather than
	// the whole player. Role loops share the player's inbox (with a role prefix on
	// filenames), publish their status as a dashboard module lane, and coordinate
	// through the team board + the shared strategist's advice.
	readonly RoleSpec? role;
	readonly bool ownsAdvisor;
	readonly HashSet<string>? allowedOrders;
	string Prefix => role == null ? "" : role.Name + "-";
	string Lane => role == null ? player.Slug : $"{player.Slug}/{role.Name}";

	readonly HashSet<string> seenResults = [];
	readonly List<List<JsonObject>> history = [];

	long lastTick = -1;
	int turnCount;
	int seqCounter;
	long totalPromptTokens;
	double totalCostUsd;
	readonly List<string> goals = [];
	readonly Dictionary<string, bool> checklist = [];
	readonly HashSet<string> exploredSpawns = [];
	long totalCompletionTokens;
	readonly Queue<string> actionLog = new();

	// Agent-authored memory. `facts` is an append-only notepad (remember) that
	// survives the rolling history window — the cure for re-deriving the same
	// wrong conclusion every turn. `groups` are named unit sets the harness keeps
	// pruned against live state, so a commander never issues orders to the dead.
	// `intents` is the agent's own one-line rationale per turn, replayed back as
	// continuity that provider `reasoning` fields do not survive.
	readonly List<string> facts = [];
	readonly Dictionary<string, List<long>> groups = new(StringComparer.OrdinalIgnoreCase);
	readonly Queue<string> intents = new();

	// Provider failover. The lane, not the model, is what usually loses a match:
	// a degraded endpoint answers slowly or returns HTTP-200 empty bodies, and the
	// agent silently stops playing. Count consecutive bad turns and switch model.
	int modelIndex;
	int consecutiveFailures;
	string ActiveModel
	{
		get
		{
			var primary = role?.Model ?? player.Model;
			return modelIndex <= 0 || modelIndex > player.FallbackModels.Count
				? primary
				: player.FallbackModels[modelIndex - 1];
		}
	}

	/// <summary>Records the outcome of one provider turn and rotates the model after
	/// enough consecutive failures. Returns true when the lane switched.</summary>
	/// <summary>
	/// Copies tool calls for replay in history, replacing arguments that are not a
	/// JSON object with "{}". Providers validate the whole transcript: one empty
	/// arguments string (MissingNo, 2026-10-11) made every later request a 400,
	/// and since failed turns never advance the history it could not age out.
	/// </summary>
	static JsonArray SanitizedToolCalls(JsonArray toolCalls)
	{
		var copy = (JsonArray)toolCalls.DeepClone();
		foreach (var call in copy.OfType<JsonObject>())
		{
			if (call["function"] is not JsonObject fn)
				continue;

			var valid = false;
			try
			{
				valid = JsonNode.Parse(fn["arguments"]?.GetValue<string>() ?? "") is JsonObject;
			}
			catch (Exception)
			{
			}

			if (!valid)
				fn["arguments"] = "{}";
		}

		return copy;
	}

	bool NoteTurnOutcome(bool ok)
	{
		if (ok)
		{
			consecutiveFailures = 0;
			return false;
		}

		if (++consecutiveFailures < Math.Max(1, player.FallbackAfterFailures)
			|| modelIndex >= player.FallbackModels.Count)
			return false;

		var from = ActiveModel;
		modelIndex++;
		consecutiveFailures = 0;
		Util.Log(Lane, $"provider failover: '{from}' failed {player.FallbackAfterFailures}x in a row — switching to '{ActiveModel}'");
		AppendErrorLog($"failover: {from} -> {ActiveModel}");

		// The history carries provider-shaped tool_call ids; a fresh model should
		// not have to reconcile another model's transcript.
		history.Clear();
		return true;
	}

	// Standing plans the agent wrote for itself, executed between its turns.
	// The executor runs on its own loop, so everything it shares with the driver
	// loop (inbox submission, action log, in-flight set) is guarded by planLock.
	readonly PlanExecutor planExecutor = new();
	readonly object planLock = new();
	readonly Dictionary<string, HashSet<string>> inFlightByFile = [];
	int planSeqCounter;
	string? lastError;
	string summaryLine = "";
	string seqStr = "000000";

	// Demand system (swarm): sequence for requests this role has posted, and the
	// set of inbox submissions still awaiting engine results (anti-double-order).
	int requestCounter;
	readonly Dictionary<string, string> pendingSubmissions = [];

	// Optional add-on: over-the-shoulder advisor (runs its own async loop).
	readonly AdvisorLoop? advisorLoop;

	// Cadence telemetry for the dashboard: when a provider request is in flight,
	// when the last response landed, and a rolling window of full-turn latencies.
	DateTime? requestStartedAtUtc;
	DateTime? lastResponseAtUtc;
	double? lastTurnSeconds;
	readonly Queue<double> turnLatencies = new();
	const int LatencyWindow = 10;

	public AgentLoop(string runDir, Spec spec, PlayerSpec player, int index, string specDir,
		RoleSpec? role = null, AdvisorLoop? sharedAdvisor = null)
	{
		this.runDir = runDir;
		this.spec = spec;
		this.player = player;
		this.index = index;
		this.role = role;

		var providerKey = role?.Provider ?? player.Provider;
		if (!spec.Providers.TryGetValue(providerKey, out provider!))
			throw new InvalidOperationException($"'{Lane}' references unknown provider '{providerKey}'");

		if (role != null && role.Orders.Count > 0)
			allowedOrders = [.. role.Orders];

		var promptFile = role?.PromptFile ?? player.PromptFile;
		var promptPath = promptFile != null
			? Path.GetFullPath(Path.Combine(specDir, promptFile))
			: Util.AssetPath(Path.Combine("prompts", "system-default.md"));
		systemPrompt = File.ReadAllText(promptPath);

		// Shared game knowledge (unit counters, armor classes) appended to every
		// prompt variant so instruction A/Bs stay orthogonal to game facts.
		var guidePath = Util.AssetPath(Path.Combine("prompts", "strategy-guide.md"));
		if (File.Exists(guidePath))
			systemPrompt += "\n\n" + File.ReadAllText(guidePath);

		if (!provider.IsTest)
		{
			var apiKey = Environment.GetEnvironmentVariable(provider.ApiKeyEnv);
			if (string.IsNullOrEmpty(apiKey))
				throw new InvalidOperationException($"Provider '{providerKey}' requires env var {provider.ApiKeyEnv} to be set");
			llm = new LlmClient(provider.BaseUrl, apiKey, role?.TimeoutSeconds ?? player.TimeoutSeconds,
				msg => AppendErrorLog($"retry: {msg}"));
		}

		// Advisor/strategist: swarm role loops receive one shared instance (started
		// by the runner); a classic driver creates and owns its own.
		if (sharedAdvisor != null)
		{
			advisorLoop = sharedAdvisor;
			ownsAdvisor = false;
		}
		else if (role == null && player.Advisor != null && !provider.IsTest)
		{
			advisorLoop = new AdvisorLoop(runDir, spec, player, specDir);
			ownsAdvisor = true;
		}

		seqCounter = ScanExistingSeq();
	}

	string AgentDir => Path.Combine(runDir, "agents", player.Slug);
	string InboxDir => Path.Combine(runDir, "orders", player.Slug, "inbox");
	string ResultsDir => Path.Combine(runDir, "orders", player.Slug, "results");

	int EffectiveInterval => role != null ? role.TurnIntervalSeconds : spec.TurnIntervalSeconds;

	public async Task RunAsync(CancellationToken ct)
	{
		// The advisor rides alongside on the same cancellation token; the driver
		// loop never waits on it, so a slow (or dead) advisor cannot stall play.
		// Shared (swarm) advisors are started by the runner, not by role loops.
		if (ownsAdvisor)
			_ = advisorLoop?.RunAsync(ct);

		// The plan executor runs on its OWN loop, deliberately: the driver loop is
		// blocked inside the provider call for 8-40 s at a time, which is exactly
		// the window a human uses to place a refinery and queue five rifles. The
		// agent's standing plan must keep running while the agent is thinking.
		if (role == null && !provider.IsTest)
			_ = PlanLoopAsync(ct);

		try
		{
			await Task.Delay(index * (EffectiveInterval > 0 ? 1500 : 300), ct);

			while (!ct.IsCancellationRequested)
			{
				try
				{
					if (!await StepAsync(ct))
					{
						Util.Log(Lane, "agent loop finished");
						return;
					}
				}
				catch (OperationCanceledException) when (ct.IsCancellationRequested)
				{
					return;
				}
				catch (Exception ex)
				{
					RecordError(ex);
				}

				if (spec.MaxTurnsPerPlayer > 0 && turnCount >= spec.MaxTurnsPerPlayer)
				{
					Util.Log(Lane, $"reached maxTurnsPerPlayer ({spec.MaxTurnsPerPlayer})");
					return;
				}

				// turnIntervalSeconds <= 0 means "as fast as the provider allows": poll for
				// fresh engine state (written 1/s) and take the next turn immediately.
				// StepAsync's tick gate prevents duplicate turns on stale state.
				var interval = EffectiveInterval > 0
					? TimeSpan.FromSeconds(EffectiveInterval)
					: TimeSpan.FromMilliseconds(250);

				// Token-budget pacing from the provider's rate-limit headers (see
				// LlmClient.UpdatePace): a lane that drains the key's hourly budget
				// in 40 minutes loses the last 20 to a hard stall.
				var pace = TimeSpan.FromSeconds(llm?.PaceDelaySeconds ?? 0);
				await Task.Delay(pace > interval ? pace : interval, ct);
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	/// <summary>One scheduler step. Returns false when the loop should stop (defeat / win decided).</summary>
	async Task<bool> StepAsync(CancellationToken ct)
	{
		var statePath = Path.Combine(runDir, "state", $"{player.Slug}.json");
		if (!File.Exists(statePath))
		{
			Util.Log(Lane, "no state file yet, skipping turn");
			return true;
		}

		var state = await ReadStateWithRetryAsync(statePath, ct);
		if (state == null)
		{
			Util.Log(Lane, "state file unreadable, skipping turn");
			return true;
		}

		var tick = state["tick"]?.GetValue<long>() ?? -1;
		if (tick == lastTick)
			return !IsDecided(state); // stale state; also stop if game already decided for us

		lastTick = tick;

		var results = CollectNewResults();
		await TakeTurnAsync(state, results, ct);

		return !IsDecided(state);
	}

	/// <summary>
	/// Carries out the agent's standing build plan and production rules at state
	/// rate (~1 Hz), independently of the provider round trip. This is what lets a
	/// commander who thinks every 8-40 seconds still open at human tempo — the
	/// plan is the agent's own, written by tool call; only the clockwork is ours.
	/// </summary>
	async Task PlanLoopAsync(CancellationToken ct)
	{
		var statePath = Path.Combine(runDir, "state", $"{player.Slug}.json");
		var lastPlanTick = -1L;

		try
		{
			while (!ct.IsCancellationRequested)
			{
				await Task.Delay(1000, ct);

				try
				{
					if (!File.Exists(statePath))
						continue;

					var state = await ReadStateWithRetryAsync(statePath, ct);
					if (state == null)
						continue;

					var tick = state["tick"]?.GetValue<long>() ?? -1;
					if (tick == lastPlanTick)
						continue;

					lastPlanTick = tick;
					if (state["you"]?["defeated"]?.GetValue<bool>() == true)
						return;

					JsonArray orders;
					lock (planLock)
						orders = planExecutor.Tick(state, InFlightKeys());

					if (orders.Count > 0)
						SubmitOrders(state, orders, $"plan-{++planSeqCounter:D6}");
				}
				catch (OperationCanceledException)
				{
					return;
				}
				catch (Exception ex)
				{
					AppendErrorLog($"plan executor: {ex.Message}");
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	/// <summary>Item-level keys for orders submitted but not yet confirmed by the engine.</summary>
	HashSet<string> InFlightKeys()
	{
		var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var set in inFlightByFile.Values)
			keys.UnionWith(set);

		return keys;
	}

	static HashSet<string> OrderKeys(JsonArray orders)
	{
		var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var o in orders.OfType<JsonObject>())
		{
			var type = o["type"]?.GetValue<string>();
			var item = o["item"]?.GetValue<string>();
			if (item == null)
				continue;

			if (type == "start_production")
				keys.Add("start:" + item);
			else if (type == "place_building")
				keys.Add("place:" + item);
		}

		return keys;
	}

	/// <summary>Writes one order batch to the engine inbox. Shared by both loops.</summary>
	void SubmitOrders(JsonObject state, JsonArray orders, string seq)
	{
		lock (planLock)
		{
			Util.WriteAtomic(Path.Combine(InboxDir, $"{seq}.json"),
				new JsonObject { ["orders"] = orders.DeepClone() }.ToJsonString(Util.Indented));

			RecordActions(state, orders);
			planExecutor.NoteSubmitted(orders, state["tick"]?.GetValue<long>() ?? 0);

			pendingSubmissions[seq + ".json"] = string.Join("; ", orders.OfType<JsonObject>()
				.Select(o => $"{o["type"]?.GetValue<string>()}{(o["item"] != null ? " " + o["item"]!.GetValue<string>() : "")}"));
			inFlightByFile[seq + ".json"] = OrderKeys(orders);
		}
	}

	bool IsDecided(JsonObject state)
	{
		if (state["you"]?["defeated"]?.GetValue<bool>() == true)
			return true;

		var game = Util.TryReadJson(Path.Combine(runDir, "state", "game.json"));
		var winState = (game?["players"] as JsonArray)?
			.OfType<JsonObject>()
			.FirstOrDefault(p => p["slug"]?.GetValue<string>() == player.Slug)?["winState"]?.GetValue<string>();

		return winState is "Won" or "Lost";
	}

	static async Task<JsonObject?> ReadStateWithRetryAsync(string path, CancellationToken ct)
	{
		for (var attempt = 0; attempt < 2; attempt++)
		{
			try
			{
				return JsonNode.Parse(await File.ReadAllTextAsync(path, ct)) as JsonObject;
			}
			catch (Exception) when (attempt == 0)
			{
				await Task.Delay(250, ct);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception)
			{
				return null;
			}
		}

		return null;
	}

	List<JsonObject> CollectNewResults()
	{
		var found = new List<JsonObject>();
		if (!Directory.Exists(ResultsDir))
			return found;

		foreach (var file in Directory.GetFiles(ResultsDir, "*.json").Order())
		{
			var name = Path.GetFileName(file);

			// Swarm: results carry the same role-prefixed name as the inbox file
			// that produced them; each role only consumes its own.
			if (role != null && !name.StartsWith(Prefix, StringComparison.Ordinal))
				continue;

			if (!seenResults.Add(name))
				continue;

			lock (planLock)
			{
				pendingSubmissions.Remove(name);
				inFlightByFile.Remove(name);
			}

			if (Util.TryReadJson(file) is JsonObject result)
				found.Add(result);
		}

		return found;
	}

	/// <summary>Maps every currently known production item name to its queue's base name
	/// (Building, Infantry, Vehicle, …) from the state's queue listings.</summary>
	static Dictionary<string, string> ItemQueueIndex(JsonObject state)
	{
		var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var q in state["production"] as JsonArray ?? [])
		{
			if (q is not JsonObject queue || queue["queue"]?.GetValue<string>() is not { } queueName)
				continue;

			var baseName = queueName.Split('.')[0];
			foreach (var b in queue["buildable"] as JsonArray ?? [])
				if (NameOf(b) is { } item)
					index.TryAdd(item, baseName);

			if (NameOf(queue["current"]) is { } current)
				index.TryAdd(current, baseName);

			// NOTE: "queued" entries are plain strings, not objects — indexing them
			// as objects threw and silently ate a whole turn's orders (2026-08-23).
			foreach (var w in queue["queued"] as JsonArray ?? [])
				if (NameOf(w) is { } waiting)
					index.TryAdd(waiting, baseName);
		}

		return index;
	}

	/// <summary>Item name from either shape: {"name": "Barracks", ...} or plain "Barracks".</summary>
	static string? NameOf(JsonNode? node) => node switch
	{
		JsonObject o => o["name"]?.GetValue<string>(),
		JsonValue v => v.TryGetValue<string>(out var s) ? s : null,
		_ => null,
	};

	/// <summary>Executes exactly one turn against the given state. Also used by the offline agent-turn command.</summary>
	public async Task TakeTurnAsync(JsonObject state, List<JsonObject> results, CancellationToken ct)
	{
		turnCount++;
		seqStr = Prefix + (++seqCounter).ToString("D6");
		var turnDir = Path.Combine(AgentDir, "turns", seqStr);
		Directory.CreateDirectory(turnDir);
		Util.WriteAtomic(Path.Combine(turnDir, "state.json"), state.ToJsonString(Util.Indented));

		var turnStartedUtc = DateTime.UtcNow;
		JsonArray orders;
		try
		{
			orders = provider.IsTest
				? TestTurn(state, turnDir)
				: await LlmTurnAsync(state, results, turnDir, ct);

			lastError = null;
		}
		catch (Exception ex)
		{
			requestStartedAtUtc = null;
			NoteTurnOutcome(false);

			// A 400 means the request itself was rejected. History is the only part
			// that carries over between turns, so drop it rather than resend the
			// same poisoned transcript forever.
			if (ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.BadRequest } && history.Count > 0)
			{
				history.Clear();
				AppendErrorLog("400 from provider: cleared rolling history so the next turn starts clean");
			}

			WriteStatus();
			throw;
		}

		// Swarm lane discipline: a role may only issue its whitelisted order types,
		// and production orders only for its whitelisted queues (this is what stops
		// the unit chief from building a Barracks). Dropped counts surface in the
		// log line so leaks are visible.
		var dropped = 0;
		if (role != null && orders.Count > 0)
		{
			var itemQueues = role.Queues.Count > 0 ? ItemQueueIndex(state) : [];
			var kept = new JsonArray();
			foreach (var o in orders.OfType<JsonObject>())
			{
				var type = o["type"]?.GetValue<string>() ?? "";
				if (allowedOrders != null && !allowedOrders.Contains(type))
				{
					dropped++;
					continue;
				}

				if (role.Queues.Count > 0 && type is "start_production" or "cancel_production"
					&& o["item"]?.GetValue<string>() is { } item
					&& itemQueues.TryGetValue(item, out var queue)
					&& !role.Queues.Contains(queue))
				{
					dropped++;
					continue;
				}

				// Mass-before-attack gate: no lone-unit death marches across the map.
				// Attacks near home (defense) always pass; far attack_moves need mass.
				if (role.MinAttackGroup > 0 && type == "attack_move"
					&& o["cell"] is JsonArray { Count: >= 2 } target
					&& state["map"]?["yourSpawnCell"] is JsonArray { Count: >= 2 } home)
				{
					var dx = (target[0]?.GetValue<int>() ?? 0) - (home[0]?.GetValue<int>() ?? 0);
					var dy = (target[1]?.GetValue<int>() ?? 0) - (home[1]?.GetValue<int>() ?? 0);
					var unitCount = (o["actorIds"] as JsonArray)?.Count ?? 0;
					if (dx * dx + dy * dy > 30 * 30 && unitCount < role.MinAttackGroup)
					{
						dropped++;
						continue;
					}
				}

				kept.Add(o.DeepClone());
			}

			orders = kept;
		}

		lastTurnSeconds = (DateTime.UtcNow - turnStartedUtc).TotalSeconds;
		turnLatencies.Enqueue(lastTurnSeconds.Value);
		while (turnLatencies.Count > LatencyWindow)
			turnLatencies.Dequeue();

		if (orders.Count > 0)
			SubmitOrders(state, orders, seqStr);

		Util.WriteAtomic(Path.Combine(turnDir, "orders.json"), new JsonObject { ["orders"] = orders.DeepClone() }.ToJsonString(Util.Indented));
		WriteStatus();
		Util.Log(Lane, $"turn {turnCount} seq {seqStr}: {summaryLine}"
			+ (dropped > 0 ? $" [{dropped} out-of-lane orders dropped]" : ""));
	}

	JsonArray TestTurn(JsonObject state, string turnDir)
	{
		var (orders, summary) = TestAgent.Decide(state);
		summaryLine = Truncate(summary, 160);
		return orders;
	}

	async Task<JsonArray> LlmTurnAsync(JsonObject state, List<JsonObject> results, string turnDir, CancellationToken ct)
	{
		var userMsg = new JsonObject { ["role"] = "user", ["content"] = BuildUserContent(state, results) };

		var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = systemPrompt } };
		foreach (var exchange in history)
			foreach (var m in exchange)
				messages.Add(m.DeepClone());
		messages.Add(userMsg.DeepClone());

		var payload = new JsonObject
		{
			["model"] = ActiveModel,
			["messages"] = messages,
			["temperature"] = role?.Temperature ?? player.Temperature,
			["max_tokens"] = role?.MaxTokens ?? player.MaxTokens,
			["tools"] = ToolSchema.Tools(swarm: role != null),
			["tool_choice"] = "auto",
		};

		if ((role?.ReasoningEffort ?? player.ReasoningEffort) is string effort)
			payload["reasoning_effort"] = effort;

		Util.WriteAtomic(Path.Combine(turnDir, "request.json"), payload.ToJsonString(Util.Indented));

		// Publish the in-flight window so the dashboard can show a live
		// "thinking for N seconds" indicator while we wait on the provider.
		requestStartedAtUtc = DateTime.UtcNow;
		WriteStatus();

		string raw;
		try
		{
			raw = await llm!.ChatAsync(payload, ct);
		}
		finally
		{
			lastResponseAtUtc = DateTime.UtcNow;
			requestStartedAtUtc = null;
		}

		Util.WriteAtomic(Path.Combine(turnDir, "response.json"), raw);

		var response = JsonNode.Parse(raw) as JsonObject
			?? throw new InvalidOperationException("Provider returned non-object JSON");

		if (response["usage"] is JsonObject usage)
		{
			totalPromptTokens += usage["prompt_tokens"]?.GetValue<long>() ?? 0;
			totalCompletionTokens += usage["completion_tokens"]?.GetValue<long>() ?? 0;

			// DeepInfra reports exact spend as estimated_cost; Nous Portal as cost.
			if (usage["estimated_cost"] is JsonValue cost)
				totalCostUsd += cost.GetValue<double>();
			else if (usage["cost"] is JsonValue nousCost)
				totalCostUsd += nousCost.GetValue<double>();
		}

		var message = response["choices"]?[0]?["message"] as JsonObject
			?? throw new InvalidOperationException("Provider response has no choices[0].message");

		var content = message["content"]?.GetValue<string>();

		// Merge orders from all issue_orders tool calls; none = pass.
		var orders = new JsonArray();
		var toolMessages = new List<JsonObject>();
		var textFallbackUsed = false;

		// Some models (e.g. Llama 3.1 on certain endpoints) emit their native
		// function-call template as plain text instead of structured tool_calls:
		//   <function=issue_orders>{"orders": [...]}<function>
		// Rescue those: extract the JSON payload so the model can still play.
		if (message["tool_calls"] is not JsonArray { Count: > 0 } && !string.IsNullOrEmpty(content))
		{
			var m = System.Text.RegularExpressions.Regex.Match(
				content, @"<function=issue_orders>\s*(\{.*?\})\s*(?:</?function>?|$)",
				System.Text.RegularExpressions.RegexOptions.Singleline);
			if (m.Success)
			{
				try
				{
					var args = JsonNode.Parse(m.Groups[1].Value);
					foreach (var order in args?["orders"] as JsonArray ?? [])
						if (order != null)
							orders.Add(order.DeepClone());

					textFallbackUsed = orders.Count > 0;
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					AppendErrorLog($"text-form tool call unparseable (treated as pass): {ex.Message}");
				}
			}
		}
		// Parallel tool calls arrive in no meaningful order: MissingNo emitted
		// issue_orders on group "ball" before the set_group that created it, so the
		// orders were rejected. Run set_group first; otherwise keep the model's order.
		var calls = (message["tool_calls"] as JsonArray ?? [])
			.OfType<JsonObject>()
			.OrderBy(c => c["function"]?["name"]?.GetValue<string>() == "set_group" ? 0 : 1)
			.ToList();
		foreach (var call in calls)
		{

			var toolName = call["function"]?["name"]?.GetValue<string>();
			var toolResult = $"{{\"ok\": false, \"reason\": \"unknown tool '{toolName}'\"}}";
			try
			{
				var args = JsonNode.Parse(call["function"]?["arguments"]?.GetValue<string>() ?? "{}");
				switch (toolName)
				{
					case "issue_orders":
					{
						if (args?["intent"]?.GetValue<string>() is { } intent && intent.Trim().Length > 0)
							RecordIntent(state, intent.Trim());

						var batch = new JsonArray();
						foreach (var order in args?["orders"] as JsonArray ?? [])
							if (order != null)
								batch.Add(order.DeepClone());

						// Validate here, before the model is told anything. The old loop
						// answered every tool call with {"submitted": true} — including
						// orders the harness then dropped — so lanes retried forever
						// against a success they never got. Now the answer is the truth.
						var (kept, rejected, notes) = ValidateOrders(state, batch);
						foreach (var o in kept.OfType<JsonObject>().ToList())
						{
							kept.Remove(o);
							orders.Add(o);
						}

						var report = new JsonObject { ["accepted"] = orders.Count, ["seq"] = seqStr };
						if (rejected.Count > 0)
						{
							var arr = new JsonArray();
							foreach (var r in rejected)
								arr.Add(r);
							report["rejected"] = arr;
						}

						if (notes.Count > 0)
						{
							var arr = new JsonArray();
							foreach (var n in notes)
								arr.Add(n);
							report["notes"] = arr;
						}

						toolResult = report.ToJsonString();
						break;
					}

					case "set_build_plan":
						toolResult = planExecutor.SetPlan(args?["steps"] as JsonArray);
						PersistPlanning();
						break;

					case "set_production_rules":
						toolResult = planExecutor.SetRules(args?["rules"] as JsonArray);
						PersistPlanning();
						break;

					case "set_group":
					{
						var name = args?["name"]?.GetValue<string>()?.Trim();
						if (string.IsNullOrEmpty(name))
						{
							toolResult = "{\"ok\": false, \"reason\": \"missing group name\"}";
							break;
						}

						var ids = (args?["actorIds"] as JsonArray ?? [])
							.OfType<JsonValue>()
							.Select(v => v.TryGetValue<long>(out var id) ? id : -1)
							.Where(id => id >= 0)
							.Distinct()
							.ToList();

						if (ids.Count == 0)
						{
							groups.Remove(name);
							toolResult = $"{{\"ok\": true, \"deleted\": \"{name}\"}}";
						}
						else
						{
							var live = LiveActorIds(state);
							var alive = ids.Where(live.Contains).ToList();
							groups[name] = alive;
							PersistPlanning();
							toolResult = $"{{\"ok\": true, \"group\": \"{name}\", \"members\": {alive.Count}, \"dropped_dead\": {ids.Count - alive.Count}}}";
						}

						break;
					}

					case "remember":
					{
						var fact = args?["fact"]?.GetValue<string>()?.Trim();
						if (string.IsNullOrEmpty(fact))
						{
							toolResult = "{\"ok\": false, \"reason\": \"empty fact\"}";
							break;
						}

						fact = Truncate(fact, 220);
						if (facts.Any(f => string.Equals(f, fact, StringComparison.OrdinalIgnoreCase)))
						{
							toolResult = "{\"ok\": true, \"note\": \"already recorded\"}";
							break;
						}

						facts.Add(fact);
						while (facts.Count > 24)
							facts.RemoveAt(0);

						PersistPlanning();
						toolResult = $"{{\"ok\": true, \"facts\": {facts.Count}}}";
						break;
					}

					case "set_goals":
						goals.Clear();
						foreach (var g in (args?["goals"] as JsonArray ?? []).Take(8))
							if (g is JsonValue v && v.TryGetValue<string>(out var goal) && goal.Length > 0)
								goals.Add(goal.Length > 200 ? goal[..200] : goal);
						PersistPlanning();
						toolResult = "{\"ok\": true}";
						break;

					case "post_request" when role != null:
					{
						var id = $"{role.Name}-{++requestCounter}";
						var requestsDir = Path.Combine(AgentDir, "requests");
						Directory.CreateDirectory(requestsDir);
						Util.WriteAtomic(Path.Combine(requestsDir, $"{id}.json"), new JsonObject
						{
							["id"] = id,
							["from"] = role.Name,
							["to"] = args?["to"]?.GetValue<string>() ?? "any",
							["need"] = Truncate(args?["need"]?.GetValue<string>() ?? "", 200),
							["why"] = Truncate(args?["why"]?.GetValue<string>() ?? "", 200),
							["priority"] = args?["priority"]?.GetValue<string>() ?? "normal",
							["status"] = "open",
							["postedAtUtc"] = DateTime.UtcNow.ToString("o"),
						}.ToJsonString(Util.Indented));
						toolResult = $"{{\"ok\": true, \"id\": \"{id}\"}}";
						break;
					}

					case "resolve_request" when role != null:
					{
						toolResult = "{\"ok\": false, \"reason\": \"unknown request id\"}";
						var reqId = args?["id"]?.GetValue<string>() ?? "";
						var reqPath = Path.Combine(AgentDir, "requests", $"{Path.GetFileName(reqId)}.json");
						if (File.Exists(reqPath) && Util.TryReadJson(reqPath) is JsonObject req)
						{
							req["status"] = "done";
							req["note"] = Truncate(args?["note"]?.GetValue<string>() ?? "", 200);
							req["resolvedBy"] = role.Name;
							req["resolvedAtUtc"] = DateTime.UtcNow.ToString("o");
							Util.WriteAtomic(reqPath, req.ToJsonString(Util.Indented));
							toolResult = "{\"ok\": true}";
						}

						break;
					}

					case "complete_checklist_item":
						toolResult = "{\"ok\": false, \"reason\": \"unknown checklist cell\"}";
						if (args?["cell"] is JsonArray { Count: 2 } cell)
						{
							var key = $"{cell[0]},{cell[1]}";
							if (checklist.ContainsKey(key))
							{
								// Honesty gate: the engine tells us whether this player has
								// actually explored the spawn. No scouting, no credit.
								if (!exploredSpawns.Contains(key))
								{
									toolResult = "{\"ok\": false, \"reason\": \"rejected: you have not explored that spawn area yet — scout it first\"}";
								}
								else
								{
									checklist[key] = true;
									PersistPlanning();
									toolResult = "{\"ok\": true, \"cleared\": \"" + key + "\"}";
								}
							}
						}

						break;
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// Broad by design: models emit duplicate-key JSON ("Key: id" — v3's
				// army lane lost whole turns to it), which throws ArgumentException,
				// not JsonException. One bad tool call must never eat the turn.
				AppendErrorLog($"tool call arguments unparseable (treated as pass): {ex.Message}");
				toolResult = "{\"ok\": false, \"reason\": \"arguments were not valid JSON\"}";
			}

			// Protocol correctness: every tool_call gets a tool-role result message.
			toolMessages.Add(new JsonObject
			{
				["role"] = "tool",
				["tool_call_id"] = call["id"]?.GetValue<string>() ?? "",
				["content"] = toolResult,
			});
		}

		// A turn that came back with nothing at all is a provider glitch, not a
		// decision — that shape (HTTP 200, null content) once ate 108 turns of a match.
		// Plain text with no tool call is a (poor) decision by a live model: counting
		// it as a failure failed MissingNo over after three "nothing to do" turns.
		if (NoteTurnOutcome(toolMessages.Count > 0 || textFallbackUsed || !string.IsNullOrWhiteSpace(content)))
			return orders;

		// Rolling history: keep the last 6 exchange pairs, each a protocol-correct group.
		var assistantMsg = new JsonObject { ["role"] = "assistant", ["content"] = content };
		if (message["tool_calls"] is JsonArray tcs && tcs.Count > 0)
			assistantMsg["tool_calls"] = SanitizedToolCalls(tcs);

		var group = new List<JsonObject> { userMsg, assistantMsg };
		group.AddRange(toolMessages);
		history.Add(group);

		// Each exchange embeds a full state snapshot — history length is the single
		// biggest prompt-size lever. Role lanes default to 2 (v3's 6 ballooned every
		// lane to ~15k tokens and 25-40s turns).
		var maxHistory = role != null
			? role.HistoryTurns > 0 ? role.HistoryTurns : 2
			: player.HistoryTurns > 0 ? player.HistoryTurns : 6;
		while (history.Count > maxHistory)
			history.RemoveAt(0);

		summaryLine = textFallbackUsed
			? $"(text-form tool call rescued: {orders.Count} orders)"
			: !string.IsNullOrWhiteSpace(content)
			? Truncate(content.ReplaceLineEndings(" ").Trim(), 160)
			: orders.Count > 0 ? $"(tool call: {orders.Count} orders)" : "(pass)";

		return orders;
	}

	// Rolling factual log of the agent's own issued orders. The 6-exchange message
	// history only spans seconds at fast cadence; this digest spans minutes, so the
	// agent can see it already ordered/cancelled something before doing it again.
	/// <summary>Every actor id the player currently owns (units and buildings).</summary>
	static HashSet<long> LiveActorIds(JsonObject state)
	{
		var live = new HashSet<long>();
		foreach (var key in (string[])["units", "buildings"])
			foreach (var a in state[key] as JsonArray ?? [])
				if ((a as JsonObject)?["id"]?.GetValue<long>() is { } id)
					live.Add(id);

		return live;
	}

	/// <summary>Items the engine says are not buildable yet, mapped to the prerequisites they need.</summary>
	static Dictionary<string, string> LockedIndex(JsonObject state)
	{
		var locked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var q in state["production"] as JsonArray ?? [])
			foreach (var l in (q as JsonObject)?["locked"] as JsonArray ?? [])
			{
				if (l is not JsonObject item || item["name"]?.GetValue<string>() is not { } name)
					continue;

				var requires = string.Join(", ", (item["requires"] as JsonArray ?? [])
					.OfType<JsonValue>()
					.Select(v => v.TryGetValue<string>(out var s) ? s : null)
					.Where(s => s != null));

				locked.TryAdd(name, requires.Length > 0 ? requires : "unmet prerequisites");
			}

		return locked;
	}

	/// <summary>
	/// Resolves group references to live actor ids, drops dead ids, and refuses
	/// orders that provably cannot work — so the model learns this turn instead of
	/// two turns later, and never burns a turn commanding corpses.
	/// </summary>
	(JsonArray Kept, List<string> Rejected, List<string> Notes) ValidateOrders(JsonObject state, JsonArray batch)
	{
		var kept = new JsonArray();
		var rejected = new List<string>();
		var notes = new List<string>();
		if (batch.Count == 0)
			return (kept, rejected, notes);

		var live = LiveActorIds(state);
		var locked = LockedIndex(state);

		foreach (var o in batch.OfType<JsonObject>().ToList())
		{
			batch.Remove(o);
			var type = o["type"]?.GetValue<string>() ?? "";

			// Models routinely try to call the planning tools as order types. Absorb
			// it instead of spending the turn teaching them the difference.
			if (type == "set_group")
			{
				var gname = (o["name"] ?? o["group"])?.GetValue<string>()?.Trim();
				var gids = (o["actorIds"] as JsonArray ?? [])
					.OfType<JsonValue>()
					.Select(v => v.TryGetValue<long>(out var gid) ? gid : -1)
					.Where(id => id >= 0)
					.ToList();

				if (!string.IsNullOrEmpty(gname) && gids.Count > 0)
				{
					groups[gname] = gids.Where(LiveActorIds(state).Contains).ToList();
					PersistPlanning();
					notes.Add($"set_group is a separate tool, not an order type — handled it for you: '{gname}' now has {groups[gname].Count} members.");
				}

				continue;
			}

			// Models mix up the singular and plural forms constantly, and the engine
			// rejects the mismatch with "missing 'actorIds'" — two wasted turns in the
			// first live test, on the very first order of the match. Normalise instead.
			if (o["actorIds"] is not JsonArray && o["actorId"] is JsonValue single
				&& type is "deploy" or "move" or "attack_move" or "attack" or "capture"
					or "guard" or "stop" or "enter" or "harvest" or "set_stance" or "scatter"
				&& single.TryGetValue<long>(out var soloId))
			{
				o["actorIds"] = new JsonArray(soloId);
			}
			else if (o["actorId"] is not JsonValue && type is "sell" or "repair" or "set_rally" or "unload"
				&& o["actorIds"] is JsonArray { Count: >= 1 } many
				&& many[0] is JsonValue first && first.TryGetValue<long>(out var firstId))
			{
				o["actorId"] = firstId;
			}

			// Group -> live ids. A named group is the whole point: it cannot go stale.
			if (o["group"]?.GetValue<string>() is { } groupName)
			{
				o.Remove("group");
				if (!groups.TryGetValue(groupName, out var members))
				{
					rejected.Add($"{type}: no group named '{groupName}' — create it with set_group first");
					continue;
				}

				members.RemoveAll(id => !live.Contains(id));
				if (members.Count == 0)
				{
					rejected.Add($"{type}: group '{groupName}' has no surviving members — it was wiped out");
					continue;
				}

				var arr = new JsonArray();
				foreach (var id in members)
					arr.Add(id);
				o["actorIds"] = arr;
			}
			else if (o["actorIds"] is JsonArray ids)
			{
				var alive = ids.OfType<JsonValue>()
					.Select(v => v.TryGetValue<long>(out var id) ? id : -1)
					.Where(live.Contains)
					.ToList();

				if (alive.Count == 0)
				{
					rejected.Add($"{type}: none of those units are alive any more — check the units table");
					continue;
				}

				var arr = new JsonArray();
				foreach (var id in alive)
					arr.Add(id);
				o["actorIds"] = arr;
			}

			// `guard` protects an ACTOR, not a cell. The model guessed both ways and
			// wrote two contradictory facts into its own notepad trying to work it out.
			if (type is "guard" or "attack" or "capture" && o["targetActorId"] is not JsonValue)
			{
				rejected.Add($"{type}: needs 'targetActorId' (the actor to {(type == "guard" ? "protect" : "target")}). "
					+ (type == "guard"
						? "To hold ground instead, use attack_move to that cell, or set_stance defend."
						: "The target must be visible to you — check visibleEnemies."));
				continue;
			}

			// Placement is only legal for a structure that has finished building and
			// is sitting in pendingPlacement. Models guess at this constantly; telling
			// them now beats a rejection two turns later.
			if (type == "place_building" && o["item"]?.GetValue<string>() is { } placeItem)
			{
				var ready = (state["pendingPlacement"] as JsonArray ?? [])
					.OfType<JsonObject>()
					.Select(p => p["item"]?.GetValue<string>())
					.Where(i => i != null)
					.ToList();

				if (!ready.Any(i => string.Equals(i, placeItem, StringComparison.OrdinalIgnoreCase)))
				{
					rejected.Add(ready.Count > 0
						? $"place_building {placeItem}: not finished yet. Ready to place right now: {string.Join(", ", ready)}."
						: $"place_building {placeItem}: nothing is waiting for placement — you can only place a structure once it has finished building.");
					continue;
				}
			}

			// The prerequisite trap that cost a whole match: refuse the order and
			// name the missing building, rather than letting it fail silently.
			if (type == "start_production" && o["item"]?.GetValue<string>() is { } item
				&& locked.TryGetValue(item, out var requires))
			{
				rejected.Add($"start_production {item}: NOT BUILDABLE YET — requires {requires}. Build that first; do not retry this order.");
				continue;
			}

			kept.Add(o);
		}

		return (kept, rejected, notes);
	}

	void RecordIntent(JsonObject state, string intent)
	{
		var sec = state["second"]?.GetValue<long>() ?? 0;
		intents.Enqueue($"t={sec / 60}:{sec % 60:D2} {Truncate(intent, 180)}");
		while (intents.Count > 8)
			intents.Dequeue();
	}

	void RecordActions(JsonObject state, JsonArray orders)
	{
		var sec = state["second"]?.GetValue<long>() ?? 0;
		var t = $"{sec / 60}:{sec % 60:D2}";
		foreach (var o in orders.OfType<JsonObject>())
		{
			var type = o["type"]?.GetValue<string>() ?? "?";
			var item = o["item"]?.GetValue<string>();
			var cell = o["cell"] is JsonArray c ? $" -> [{c[0]},{c[1]}]" : "";
			var count = o["count"]?.GetValue<int>() is int n and > 1 ? $" x{n}" : "";
			var actors = o["actorIds"] is JsonArray a ? $" ({a.Count} units)" : "";
			actionLog.Enqueue($"t={t} {type}{(item != null ? " " + item : "")}{count}{cell}{actors}");
		}

		while (actionLog.Count > 15)
			actionLog.Dequeue();
	}

	string BuildUserContent(JsonObject state, List<JsonObject> results)
	{
		var markdown = spec.StateFormat == "markdown";
		var text = markdown
			? $"Current game state:\n{StateMarkdown.Render(state)}"
			: $"Current game state:\n{state.ToJsonString()}";

		if (results.Count > 0)
		{
			if (markdown)
			{
				text += $"\n\nRecent order results:\n{StateMarkdown.RenderResults(results)}";
			}
			else
			{
				var arr = new JsonArray();
				foreach (var r in results)
					arr.Add(r.DeepClone());
				text += $"\n\nRecent order results:\n{arr.ToJsonString()}";
			}
		}

		// The opportunity ledger. Earlier prompts stacked three separate "do not
		// repeat yourself" warnings and agents read them as "do nothing" — 16-31%
		// of turns issued no orders while thousands of credits sat idle. State the
		// unspent capacity as a checklist instead: it is much harder to argue that
		// there is nothing to do when the nothing is itemised.
		var opportunities = OpportunityLedger(state);
		if (opportunities != null)
			text += opportunities;

		// Snapshot everything the executor loop also mutates.
		string? executed;
		List<string> actionsSnapshot;
		List<string> inFlightSnapshot;
		lock (planLock)
		{
			executed = planExecutor.DrainActivity();
			actionsSnapshot = [.. actionLog];
			inFlightSnapshot = [.. pendingSubmissions.Values];
		}

		if (executed != null)
			text += executed;

		if (player.RecentActions && actionsSnapshot.Count > 0)
			text += "\n\nYour recent actions (for continuity — repeating one is fine if the situation calls for it):\n"
				+ string.Join("\n", actionsSnapshot.Select(a => "  " + a));

		if (intents.Count > 0)
			text += "\n\nYour recent intents (what you said you were doing — stay on plan or say why it changed):\n"
				+ string.Join("\n", intents.Select(i => "  " + i));

		if (inFlightSnapshot.Count > 0)
			text += "\n\nIn flight (submitted, engine confirmation pending — these are already handled, so spend this turn on something else rather than waiting):\n"
				+ string.Join("\n", inFlightSnapshot.TakeLast(5).Select(p => "  " + p));

		if (facts.Count > 0)
			text += "\n\nCONFIRMED FACTS (you verified these yourself — treat them as settled, do not re-litigate):\n"
				+ string.Join("\n", facts.Select(f => "  - " + f));

		if (groups.Count > 0)
		{
			var live = LiveActorIds(state);
			var lines = groups.Select(g =>
			{
				g.Value.RemoveAll(id => !live.Contains(id));
				return $"  {g.Key}: {g.Value.Count} alive";
			});
			text += "\n\nYOUR GROUPS (command them with \"group\": \"<name>\"):\n" + string.Join("\n", lines);
		}

		var plan = planExecutor.Render();
		if (plan != null)
			text += plan;

		// The elimination checklist only concerns roles that can actually attack;
		// build/econ specialists shouldn't burn tokens reasoning about it.
		var attackCapable = allowedOrders == null || allowedOrders.Contains("attack_move");
		if (attackCapable)
		{
			SyncChecklistFromState(state);

			if (checklist.Count > 0)
			{
				var items = checklist.Select(kv => $"  spawn [{kv.Key}] — {(kv.Value ? "CLEARED" : "PENDING")}");
				text += "\n\nElimination checklist (enemy start positions; mark cleared with complete_checklist_item once you confirm the base there is destroyed):\n"
					+ string.Join("\n", items);
			}
		}

		text += goals.Count > 0
			? "\n\nYour long-term goals (update with set_goals):\n" + string.Join("\n", goals.Select(g => "  - " + g))
			: "\n\nYou have no long-term goals set. Use set_goals to record your strategic plan.";

		var board = TeamBoard();
		if (board != null)
			text += "\n\nTEAM BOARD — your fellow specialists' current goals (coordinate, don't duplicate):\n" + board;

		var requests = RequestsDigest();
		if (requests != null)
			text += requests;

		// Advisor module: inject the latest review (sticky until superseded) so
		// slow, deep guidance persists across the driver's fast turns. Role lanes
		// get it capped — a 16k-token memo repeated into six prompts was a major
		// part of v3's token bloat.
		var advice = advisorLoop?.CurrentAdvice;
		if (advice != null)
		{
			if (role != null && advice.Length > 1500)
				advice = advice[..1500] + " …[truncated]";

			text += "\n\nADVISOR GUIDANCE — a deeper model reviewed your recent turns; weigh this seriously:\n" + advice;
		}

		return text;
	}

	/// <summary>
	/// Itemises everything the player owns that is currently doing nothing:
	/// idle queues, unspent cash, idle combat units, buildings waiting to be
	/// placed, thinning tiberium. This is the anti-passivity mechanism — a
	/// concrete list of unused capacity, rendered every turn.
	/// </summary>
	static string? OpportunityLedger(JsonObject state)
	{
		var lines = new List<string>();

		var cash = state["you"]?["cash"]?.GetValue<long>() ?? 0;

		// Losing the Construction Yard takes the whole Building queue with it: no
		// structures, ever again, including the Power Plant needed to dig out of a
		// deficit. In the 2026-08-24 match the agent played six more minutes without
		// once noticing. It is the only truly unrecoverable state, so it leads.
		var hasBuildQueue = (state["production"] as JsonArray ?? [])
			.OfType<JsonObject>()
			.Any(q => q["queue"]?.GetValue<string>()?.StartsWith("Building", StringComparison.Ordinal) == true);

		var units = (state["units"] as JsonArray ?? []).OfType<JsonObject>().ToList();
		var mcv = units.FirstOrDefault(u =>
			u["name"]?.GetValue<string>()?.Contains("Construction Vehicle", StringComparison.OrdinalIgnoreCase) == true);

		if (!hasBuildQueue)
		{
			lines.Add(mcv != null
				? $"*** CRITICAL: you have NO Construction Yard — you cannot build any structure. You DO have an MCV (id {mcv["id"]}). `deploy` it RIGHT NOW. Nothing else matters this turn. ***"
				: "*** CRITICAL: your Construction Yard is GONE — you cannot build any structure, ever, until you replace it. Buy a Mobile Construction Vehicle ($3000, from the Weapons Factory/Airstrip — it has no tech prerequisite) and `deploy` it. Do this before anything else; every turn you spend attacking instead is a turn closer to losing. ***");
		}
		else if (mcv != null && mcv["idle"]?.GetValue<bool>() == true)
		{
			lines.Add($"You have an idle MCV (id {mcv["id"]}) — `deploy` it to add a second Construction Yard, or move it to a new tiberium field first and expand.");
		}
		var idleQueues = new List<string>();
		foreach (var q in state["production"] as JsonArray ?? [])
		{
			if (q is not JsonObject queue || queue["busy"]?.GetValue<bool>() == true)
				continue;

			var name = queue["queue"]?.GetValue<string>()?.Split('.')[0] ?? "?";
			var affordable = (queue["buildable"] as JsonArray ?? [])
				.OfType<JsonObject>()
				.Where(b => (b["cost"]?.GetValue<long>() ?? 0) <= cash)
				.Select(b => $"{b["name"]?.GetValue<string>()} ${b["cost"]?.GetValue<long>()}")
				.Take(4)
				.ToList();

			if (affordable.Count > 0)
				idleQueues.Add($"{name} queue is IDLE — you can afford: {string.Join("; ", affordable)}");
		}

		lines.AddRange(idleQueues);

		// Only call out floating cash when something can actually be bought with it.
		// Crying wolf while the build queue is legitimately busy teaches the model to
		// ignore the whole block, which is how the old prompt lost its authority.
		var anythingAffordable = (state["production"] as JsonArray ?? [])
			.OfType<JsonObject>()
			.SelectMany(q => (q["buildable"] as JsonArray ?? []).OfType<JsonObject>())
			.Any(b => (b["cost"]?.GetValue<long>() ?? 0) <= cash);

		if (cash >= 2000 && anythingAffordable)
			lines.Add($"${cash} is sitting unspent. Money in the bank wins nothing — convert it into army, economy or defence this turn.");
		else if (cash >= 4000)
			lines.Add($"${cash} banked with nothing affordable to add right now — your build plan is the bottleneck. Consider revising set_build_plan, or spend the turn scouting and positioning.");

		var pending = (state["pendingPlacement"] as JsonArray ?? [])
			.OfType<JsonObject>()
			.Select(p => p["item"]?.GetValue<string>())
			.Where(i => i != null)
			.ToList();

		if (pending.Count > 0)
			lines.Add($"FINISHED AND WAITING FOR YOU TO PLACE IT: {string.Join(", ", pending)} — place it now or it is dead capital.");

		var idleUnits = (state["units"] as JsonArray ?? [])
			.OfType<JsonObject>()
			.Where(u => u["idle"]?.GetValue<bool>() == true
				&& u["name"]?.GetValue<string>()?.Contains("Harvester", StringComparison.OrdinalIgnoreCase) != true)
			.Select(u => u["id"]?.GetValue<long>() ?? 0)
			.ToList();

		if (idleUnits.Count > 0)
			lines.Add($"{idleUnits.Count} combat unit(s) standing idle with no orders: {string.Join(", ", idleUnits.Take(20))}. Units do nothing between your turns unless you tell them to.");

		var harvesters = (state["units"] as JsonArray ?? [])
			.OfType<JsonObject>()
			.Count(u => u["name"]?.GetValue<string>()?.Contains("Harvester", StringComparison.OrdinalIgnoreCase) == true);

		var income = state["you"]?["incomePerMinute"]?.GetValue<long>() ?? 0;
		if (harvesters > 0 && income < 700)
			lines.Add($"Income is only {income}/min with {harvesters} harvester(s) — your tiberium patch may be exhausted. Check exploredResources and move or expand to a fresh field.");

		var provided = state["you"]?["powerProvided"]?.GetValue<long>() ?? 0;
		var drained = state["you"]?["powerDrained"]?.GetValue<long>() ?? 0;
		if (drained > provided)
			lines.Add($"POWER DEFICIT ({drained} drained vs {provided} provided) — production is slowed and defences are offline until you build a Power Plant.");

		// Two matches running, this agent has destroyed zero enemy buildings because
		// it never found them. Scouts die and are never replaced, and it attacks
		// anyway. Make the blindness impossible to overlook.
		var knownEnemy = (state["lastKnownEnemyBuildings"] as JsonArray)?.Count ?? 0;
		var visible = (state["visibleEnemies"] as JsonArray)?.Count ?? 0;
		if (knownEnemy == 0 && visible == 0)
		{
			var spawnCell = (state["enemySpawns"] as JsonArray ?? [])
				.OfType<JsonObject>()
				.FirstOrDefault(s => s["explored"]?.GetValue<bool>() != true)?["cell"] as JsonArray;

			// JsonNode.ToString() pretty-prints arrays across lines; render it inline.
			var target = spawnCell is { Count: >= 2 } ? $"[{spawnCell[0]},{spawnCell[1]}]" : null;

			lines.Add(target != null
				? $"YOU ARE BLIND: you have never seen a single enemy building. You cannot kill a base you have not found, and an army sent at an unscouted spawn dies for nothing. Send one cheap unit to {target} now, and replace it every time a scout dies."
				: "YOU ARE BLIND: no enemy units or buildings are known to you. Send a cheap scout out before committing your army anywhere.");
		}

		if (lines.Count == 0)
			return null;

		return "\n\nOPPORTUNITIES — unused capacity you own right now. Every line here is a turn you are wasting; clear them before doing anything speculative:\n  "
			+ string.Join("\n  ", lines);
	}

	void SyncChecklistFromState(JsonObject state)
	{
		foreach (var spawn in state["enemySpawns"] as JsonArray ?? [])
		{
			if (spawn is not JsonObject s || s["cell"] is not JsonArray { Count: 2 } cell)
				continue;

			var key = $"{cell[0]},{cell[1]}";
			checklist.TryAdd(key, false);
			if (s["explored"]?.GetValue<bool>() == true)
				exploredSpawns.Add(key);
		}
	}

	void PersistPlanning()
	{
		var goalsArr = new JsonArray();
		foreach (var g in goals)
			goalsArr.Add(g);

		// Swarm roles publish goals to the shared team board (one file per role, no
		// locking needed) so every specialist sees what its teammates are working on.
		if (role != null)
		{
			var boardDir = Path.Combine(AgentDir, "board");
			Directory.CreateDirectory(boardDir);
			Util.WriteAtomic(Path.Combine(boardDir, $"{role.Name}.json"), new JsonObject
			{
				["role"] = role.Name,
				["goals"] = goalsArr,
				["updatedAtUtc"] = DateTime.UtcNow.ToString("o"),
			}.ToJsonString(Util.Indented));
			return;
		}

		var checklistArr = new JsonArray();
		foreach (var (key, done) in checklist)
			checklistArr.Add(new JsonObject { ["cell"] = key, ["done"] = done });

		var factsArr = new JsonArray();
		foreach (var f in facts)
			factsArr.Add(f);

		var groupsObj = new JsonObject();
		foreach (var (name, members) in groups)
		{
			var arr = new JsonArray();
			foreach (var id in members)
				arr.Add(id);
			groupsObj[name] = arr;
		}

		Util.WriteAtomic(Path.Combine(AgentDir, "planning.json"), new JsonObject
		{
			["goals"] = goalsArr,
			["checklist"] = checklistArr,
			["facts"] = factsArr,
			["groups"] = groupsObj,
			["buildPlan"] = planExecutor.PlanCount,
			["productionRules"] = planExecutor.RuleCount,
		}.ToJsonString(Util.Indented));
	}

	/// <summary>Demand-system digest: open requests addressed to this role (act on them,
	/// resolve_request when done) plus this role's own still-open asks.</summary>
	string? RequestsDigest()
	{
		if (role == null)
			return null;

		var requestsDir = Path.Combine(AgentDir, "requests");
		if (!Directory.Exists(requestsDir))
			return null;

		var forMe = new List<string>();
		var mine = new List<string>();
		foreach (var file in Directory.GetFiles(requestsDir, "*.json").Order())
		{
			if (Util.TryReadJson(file) is not JsonObject r || r["status"]?.GetValue<string>() != "open")
				continue;

			var from = r["from"]?.GetValue<string>() ?? "?";
			var to = r["to"]?.GetValue<string>() ?? "any";
			var line = $"  [{r["id"]?.GetValue<string>()}] ({r["priority"]?.GetValue<string>()}) {r["need"]?.GetValue<string>()}"
				+ (r["why"]?.GetValue<string>() is { Length: > 0 } why ? $" — {why}" : "");

			if (from == role.Name)
				mine.Add($"{line} -> waiting on {to}");
			else if (to == role.Name || to == "any")
				forMe.Add($"{line} (from {from})");
		}

		var text = "";
		if (forMe.Count > 0)
			text += "\n\nOPEN REQUESTS FOR YOU — teammates need these from you; act and resolve_request when done:\n"
				+ string.Join("\n", forMe.TakeLast(8));
		if (mine.Count > 0)
			text += "\n\nYour own open requests (don't repost; they're visible to the team):\n"
				+ string.Join("\n", mine.TakeLast(5));

		return text.Length > 0 ? text : null;
	}

	/// <summary>Team board digest for swarm prompts: every other role's current goals.</summary>
	string? TeamBoard()
	{
		if (role == null)
			return null;

		var boardDir = Path.Combine(AgentDir, "board");
		if (!Directory.Exists(boardDir))
			return null;

		var lines = new List<string>();
		foreach (var file in Directory.GetFiles(boardDir, "*.json").Order())
		{
			var name = Path.GetFileNameWithoutExtension(file);
			if (name == role.Name)
				continue;

			if (Util.TryReadJson(file) is not JsonObject entry || entry["goals"] is not JsonArray g || g.Count == 0)
				continue;

			lines.Add($"  [{name}]");
			foreach (var goal in g)
				lines.Add($"    - {goal?.GetValue<string>()}");
		}

		return lines.Count > 0 ? string.Join("\n", lines) : null;
	}

	void WriteStatus()
	{
		// Swarm roles publish as dashboard module lanes (same shape the advisor
		// uses, so the existing UI renders them with zero changes) and refresh a
		// light player-level aggregate for the card header. Token/cost counters
		// live ONLY in the module files — the dashboard sums drivers + modules,
		// so an aggregate carrying them too would double-count.
		if (role != null)
		{
			var modulesDir = Path.Combine(AgentDir, "modules");
			Directory.CreateDirectory(modulesDir);
			Util.WriteAtomic(Path.Combine(modulesDir, $"{role.Name}.json"), new JsonObject
			{
				["name"] = role.Name,
				["model"] = role.Model ?? player.Model,
				["reasoningEffort"] = role.ReasoningEffort ?? player.ReasoningEffort,
				["requestStartedAtUtc"] = requestStartedAtUtc?.ToString("o"),
				["lastResponseAtUtc"] = lastResponseAtUtc?.ToString("o"),
				["lastTurnSeconds"] = lastTurnSeconds,
				["avgTurnSeconds"] = turnLatencies.Count > 0 ? Math.Round(turnLatencies.Average(), 1) : null,
				["cycles"] = turnCount,
				["totalPromptTokens"] = totalPromptTokens,
				["totalCompletionTokens"] = totalCompletionTokens,
				["totalCostUsd"] = totalCostUsd,
				["summaryLine"] = summaryLine,
				["lastError"] = lastError,
			}.ToJsonString(Util.Indented));

			var latest = new List<string>();
			foreach (var file in Directory.GetFiles(modulesDir, "*.json").Order())
				if (Util.TryReadJson(file) is JsonObject m && m["summaryLine"]?.GetValue<string>() is { Length: > 0 } s)
					latest.Add($"{m["name"]?.GetValue<string>()}: {Truncate(s, 60)}");

			Util.WriteAtomic(Path.Combine(AgentDir, "status.json"), new JsonObject
			{
				["seq"] = seqStr,
				["lastTurnAtUtc"] = DateTime.UtcNow.ToString("o"),
				["lastResponseAtUtc"] = lastResponseAtUtc?.ToString("o"),
				["model"] = player.Model,
				["provider"] = player.Provider,
				["summaryLine"] = string.Join(" | ", latest),
				["swarm"] = true,
			}.ToJsonString(Util.Indented));
			return;
		}

		var status = new JsonObject
		{
			["turn"] = turnCount,
			["seq"] = seqStr,
			["lastTurnAtUtc"] = DateTime.UtcNow.ToString("o"),
			["requestStartedAtUtc"] = requestStartedAtUtc?.ToString("o"),
			["lastResponseAtUtc"] = lastResponseAtUtc?.ToString("o"),
			["lastTurnSeconds"] = lastTurnSeconds,
			["avgTurnSeconds"] = turnLatencies.Count > 0 ? Math.Round(turnLatencies.Average(), 1) : null,
			["latencyWindow"] = turnLatencies.Count,
			["rateLimited429s"] = llm?.RateLimited429s ?? 0,
			["paceDelaySeconds"] = Math.Round(llm?.PaceDelaySeconds ?? 0, 1),
			["paceNote"] = llm?.PaceNote,
			["model"] = ActiveModel,
			["modelFallbackIndex"] = modelIndex,
			["provider"] = player.Provider,
			["summaryLine"] = summaryLine,
			["totalPromptTokens"] = totalPromptTokens,
			["totalCompletionTokens"] = totalCompletionTokens,
			["totalCostUsd"] = totalCostUsd,
			["goals"] = new JsonArray([.. goals.Select(g => (JsonNode)g)]),
			["checklist"] = new JsonArray([.. checklist.Select(kv =>
				(JsonNode)new JsonObject { ["cell"] = kv.Key, ["done"] = kv.Value })]),
			["lastError"] = lastError,
		};

		Util.WriteAtomic(Path.Combine(AgentDir, "status.json"), status.ToJsonString(Util.Indented));
	}

	void RecordError(Exception ex)
	{
		lastError = ex.Message;
		AppendErrorLog($"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
		try
		{
			WriteStatus();
		}
		catch
		{
			// never let error reporting take the loop down
		}

		Util.Log(Lane, $"turn error: {ex.Message}");
	}

	void AppendErrorLog(string message)
	{
		try
		{
			Directory.CreateDirectory(AgentDir);
			File.AppendAllText(Path.Combine(AgentDir, "errors.log"), $"[{DateTime.UtcNow:o}] {message}\n");
		}
		catch
		{
		}
	}

	int ScanExistingSeq()
	{
		var max = 0;

		void Scan(IEnumerable<string> names)
		{
			foreach (var name in names)
			{
				var stem = Path.GetFileNameWithoutExtension(name);
				if (role != null)
				{
					if (!stem.StartsWith(Prefix, StringComparison.Ordinal))
						continue;
					stem = stem[Prefix.Length..];
				}

				if (int.TryParse(stem, out var n) && n > max)
					max = n;
			}
		}

		if (Directory.Exists(InboxDir))
			Scan(Directory.GetFiles(InboxDir, "*.json"));
		if (Directory.Exists(ResultsDir))
			Scan(Directory.GetFiles(ResultsDir, "*.json"));

		var turnsDir = Path.Combine(AgentDir, "turns");
		if (Directory.Exists(turnsDir))
			Scan(Directory.GetDirectories(turnsDir));

		return max;
	}

	static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
