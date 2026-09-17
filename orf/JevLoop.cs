using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Orf.JevPolicy;

namespace Orf;

/// <summary>One in-flight request per player, newest observation after failures, no LLM fallback.</summary>
public sealed class JevLoop
{
	readonly string runDir;
	readonly Spec spec;
	readonly PlayerSpec player;
	readonly JevClient client;
	readonly PlayerOrderChannel channel;
	readonly JevPolicy policy;
	readonly JevPolicyV2? policyV2;
	readonly Queue<double> latencies = [];
	int sequence;
	int turns;
	int failures;
	long inputTokens;
	long outputTokens;
	DateTime? requestAt;
	DateTime? responseAt;
	double lastSeconds;
	string? error;
	string summary = "Waiting for player state";
	string AgentDir => Path.Combine(runDir, "agents", player.Slug);
	string StatePath => Path.Combine(runDir, "state", player.Slug + ".json");

	public JevLoop(string runDir, Spec spec, PlayerSpec player, JevClient? client = null)
	{
		this.runDir = runDir;
		this.spec = spec;
		this.player = player;
		var provider = spec.ProviderFor(player);
		var key = Environment.GetEnvironmentVariable(provider.ApiKeyEnv);
		if (client == null && string.IsNullOrWhiteSpace(key))
			throw new InvalidOperationException($"Provider '{player.Provider}' requires {provider.ApiKeyEnv}");
		this.client = client ?? new JevClient(provider.BaseUrl, key!, player.Jev.RequestTimeoutMilliseconds);
		channel = new(runDir, player.Slug, spec.StateIntervalTicks);
		var memory = Util.TryReadJson(Path.Combine(AgentDir, "memory.json"))?.Deserialize<JevMemory>();
		policy = new(player.Jev, memory);
		if (player.Jev.PolicyVersion == 2)
			policyV2 = new(player.Jev, policy, Util.TryReadJson(Path.Combine(AgentDir, "tactics.json"))?.Deserialize<JevTactics>());
		var turnDir = Path.Combine(AgentDir, "turns");
		if (Directory.Exists(turnDir))
			sequence = Directory.GetDirectories(turnDir).Select(Path.GetFileName)
				.Select(n => int.TryParse(n, out var i) ? i : 0).DefaultIfEmpty().Max();
	}

	public async Task RunAsync(CancellationToken ct)
	{
		long lastTick = -1;
		try
		{
			while (!ct.IsCancellationRequested)
			{
				if (File.Exists(Path.Combine(runDir, "result.json")))
					break;
				if (Util.TryReadJson(StatePath) is not JsonObject state || Number(state, "tick") <= lastTick)
				{
					await Task.Delay(100, ct);
					continue;
				}
				if (Bool(state["you"], "defeated"))
					break;
				lastTick = Number(state, "tick");
				var start = Stopwatch.StartNew();
				var wait = TimeSpan.Zero;
				try
				{
					await TakeTurnAsync(state, ct);
					failures = 0;
				}
				catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
				catch (Exception ex)
				{
					failures++;
					error = ex is OperationCanceledException ? "TypeSafe request deadline exceeded" : ex.Message;
					requestAt = null;
					WriteStatus();
					File.AppendAllText(Path.Combine(AgentDir, "errors.log"), $"{DateTime.UtcNow:o} {error}\n");
					Util.Log(player.Slug, error);
					var reduced = ex is JevApiException { ErrorType: "max_tokens_exceeded" } && policyV2?.ReduceRequestBudget() == true;
					if (ex is JevApiException { Retryable: false } && !reduced)
						break;
					wait = ex is JevApiException { RetryAfter: { } retry } ? retry
						: TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(failures, 5))) + Random.Shared.NextDouble());
				}
				if (spec.MaxTurnsPerPlayer > 0 && turns >= spec.MaxTurnsPerPlayer)
					break;
				var cadence = TimeSpan.FromMilliseconds(player.Jev.IntervalMilliseconds) - start.Elapsed;
				await Task.Delay(wait > cadence ? wait : cadence > TimeSpan.Zero ? cadence : TimeSpan.Zero, ct);
			}
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
		finally
		{
			requestAt = null;
			WriteStatus();
		}
	}

	public async Task TakeTurnAsync(JsonObject state, CancellationToken ct)
	{
		var seq = (++sequence).ToString("D6");
		var turnDir = Path.Combine(AgentDir, "turns", seq);
		var results = channel.Collect(state);
		foreach (var r in results)
			policy.Memory.Note("Engine: " + r!.ToJsonString());
		var frame = policyV2?.Prepare(state, results, channel.PendingOrders) ?? policy.Prepare(state, results, channel.PendingOrders);
		if (frame.Questions.Count == 0)
			return;
		var payload = new JsonObject { ["model"] = player.Model, ["state"] = policyV2?.RequestState ?? policy.RequestState, ["questions"] = frame.Questions };
		var request = payload.ToJsonString();
		// Conservative character guard. Exact billed tokens remain in usage; do
		// not silently truncate questions or emit unmapped choices on oversized input.
		if (request.Length > 110000)
			throw new InvalidOperationException("Jev request exceeds the experiment's 110k-character guard; reduce squads or placement options");
		Util.WriteAtomic(Path.Combine(turnDir, "state.json"), state.ToJsonString());
		Util.WriteAtomic(Path.Combine(turnDir, "request.json"), request);
		Util.WriteAtomic(Path.Combine(turnDir, "results.json"), results.ToJsonString());
		requestAt = DateTime.UtcNow;
		turns++;
		WriteStatus();
		var stopwatch = Stopwatch.StartNew();
		JsonObject response;
		try
		{
			response = await client.EvaluateAsync(payload, ct);
		}
		finally
		{
			requestAt = null;
			responseAt = DateTime.UtcNow;
			lastSeconds = stopwatch.Elapsed.TotalSeconds;
		}
		Util.WriteAtomic(Path.Combine(turnDir, "response.json"), response.ToJsonString());
		inputTokens += Number(response["usage"], "input_tokens");
		outputTokens += Number(response["usage"], "output_tokens");
		latencies.Enqueue(lastSeconds);
		if (latencies.Count > 30)
			latencies.Dequeue();
		var latest = Util.TryReadJson(StatePath) as JsonObject ?? state;
		var trace = File.Exists(Path.Combine(runDir, "result.json"))
			? new JsonObject { ["orders"] = new JsonArray(), ["decisions"] = new JsonArray(), ["discarded"] = "match ended while evaluating" }
			: policyV2?.Apply(frame, response, latest, channel) ?? policy.Apply(frame, response, latest, channel);
		var orders = (JsonArray)trace["orders"]!;
		channel.Submit(seq, orders, latest);
		Util.WriteAtomic(Path.Combine(turnDir, "orders.json"), new JsonObject { ["orders"] = orders.DeepClone() }.ToJsonString());
		Util.WriteAtomic(Path.Combine(turnDir, "decisions.json"), trace.ToJsonString());
		Util.WriteAtomic(Path.Combine(AgentDir, "memory.json"), JsonSerializer.Serialize(policy.Memory));
		if (policyV2 != null)
			Util.WriteAtomic(Path.Combine(AgentDir, "tactics.json"), JsonSerializer.Serialize(policyV2.Tactics));
		summary = string.Join("; ", orders.OfType<JsonObject>().Select(o => Text(o, "type") + " " + Text(o, "item")));
		if (summary.Length == 0)
			summary = Text(trace, "discarded") is { Length: > 0 } discarded ? discarded : "Continuing assignments / saving";
		error = null;
		WriteStatus();
		if (orders.Count > 0 || turns % 10 == 0)
			Util.Log(player.Slug, $"Jev #{seq} t={Number(latest, "second")} {lastSeconds * 1000:0}ms: {summary}");
	}

	void WriteStatus()
	{
		var goals = new JsonArray("Focus: " + policy.Memory.Focus);
		if (policy.Memory.Goal != null)
			goals.Add("Objective: " + policy.Memory.Goal);
		if (policyV2 != null)
			goals.Add("Operation: " + policyV2.Tactics.Operation);
		Util.WriteAtomic(Path.Combine(AgentDir, "status.json"), new JsonObject
		{
			["controller"] = "jev", ["turn"] = turns, ["seq"] = sequence.ToString("D6"),
			["policyVersion"] = player.Jev.PolicyVersion,
			["model"] = player.Model, ["provider"] = player.Provider,
			["lastTurnAtUtc"] = responseAt?.ToString("o"), ["requestStartedAtUtc"] = requestAt?.ToString("o"),
			["lastResponseAtUtc"] = responseAt?.ToString("o"), ["lastTurnSeconds"] = lastSeconds,
			["avgTurnSeconds"] = latencies.Count > 0 ? latencies.Average() : null,
			["summaryLine"] = summary, ["goals"] = goals, ["lastError"] = error,
			["totalPromptTokens"] = inputTokens, ["totalCompletionTokens"] = outputTokens,
			["totalCostUsd"] = inputTokens * player.Jev.InputUsdPerMillionTokens / 1000000,
			["costIsEstimate"] = true, ["consecutiveFailures"] = failures,
			["reservedCash"] = channel.ReservedCash
		}.ToJsonString());
	}
}
