using System.Text.Json.Nodes;
using static Orf.JevPolicy;

namespace Orf;

/// <summary>File-based player order transport and observation-time validation, independent of model APIs.</summary>
public sealed class PlayerOrderChannel(string runDir, string slug, int stateIntervalTicks = 25)
{
	sealed record Submission(JsonArray Orders, long Tick, long Cost)
	{
		public long? AcknowledgedTick { get; set; }
	}

	readonly Dictionary<string, Submission> pending = [];
	readonly HashSet<string> seenResults = [];
	readonly Dictionary<string, long> cooldowns = [];
	public string InboxDir => Path.Combine(runDir, "orders", slug, "inbox");
	public string ResultsDir => Path.Combine(runDir, "orders", slug, "results");
	public long ReservedCash => pending.Values.Sum(p => p.Cost);
	public JsonArray PendingOrders => new([.. pending.Values.SelectMany(p => p.Orders).Select(o => o!.DeepClone())]);

	public JsonArray Collect(JsonObject state)
	{
		var results = new JsonArray();
		var tick = Number(state, "tick");
		if (Directory.Exists(ResultsDir))
			foreach (var file in Directory.GetFiles(ResultsDir, "*.json").Order(StringComparer.Ordinal))
			{
				if (seenResults.Contains(file) || Util.TryReadJson(file) is not JsonObject result)
					continue;
				seenResults.Add(file);
				results.Add(result);
				if (pending.TryGetValue(Path.GetFileNameWithoutExtension(file), out var submission))
				{
					submission.AcknowledgedTick = Number(result, "tick");
					if (Objects(result, "results").All(r => Text(r, "status") == "rejected"))
						pending.Remove(Path.GetFileNameWithoutExtension(file));
				}
			}

		foreach (var (seq, submission) in pending.ToArray())
		{
			// Acknowledgement precedes engine execution. Wait for two observation
			// intervals before releasing reservations, so stale queues cannot duplicate purchases.
			if (submission.AcknowledgedTick is { } ack && tick >= ack + Math.Max(10, stateIntervalTicks * 2))
				pending.Remove(seq);
			else if (tick - submission.Tick > 250)
			{
				pending.Remove(seq);
				results.Add(new JsonObject { ["seq"] = seq, ["status"] = "unconfirmed", ["reason"] = "No engine acknowledgement within 10 game seconds; reconcile against observed state" });
			}
		}
		return results;
	}

	public void Submit(string seq, JsonArray orders, JsonObject state)
	{
		if (orders.Count == 0)
			return;
		var cost = orders.OfType<JsonObject>().Sum(o => OrderCost(state, o));
		Util.WriteAtomic(Path.Combine(InboxDir, seq + ".json"), new JsonObject { ["orders"] = orders.DeepClone() }.ToJsonString());
		pending[seq] = new((JsonArray)orders.DeepClone(), Number(state, "tick"), cost);
		foreach (var order in orders.OfType<JsonObject>())
			cooldowns[Key(order)] = Number(state, "tick");
	}

	public string? Validate(JsonObject order, JsonObject state)
	{
		var tick = Number(state, "tick");
		var type = Text(order, "type");
		if (Bool(state["you"], "defeated"))
			return "player defeated";
		if (pending.Values.Any(s => s.Orders.OfType<JsonObject>().Any(o => Key(o) == Key(order))))
			return "matching order still awaiting engine observation";
		if (cooldowns.TryGetValue(Key(order), out var at) && tick - at < stateIntervalTicks)
			return "matching order was just submitted";

		var own = Objects(state, "units").Concat(Objects(state, "buildings")).Select(a => Number(a, "id")).ToHashSet();
		if (order["actorIds"] is JsonArray ids)
		{
			var surviving = ids.Select(i => i!.GetValue<long>()).Where(own.Contains).Distinct().ToArray();
			if (surviving.Length == 0)
				return "actors no longer owned or alive";
			order["actorIds"] = new JsonArray([.. surviving.Select(id => (JsonNode)JsonValue.Create(id)!)]);
		}
		if (order["actorId"] != null && !own.Contains(Number(order, "actorId")))
			return "actor no longer owned or alive";
		if (order["targetActorId"] != null)
		{
			var target = Number(order, "targetActorId");
			var valid = type == "guard" ? own.Contains(target) : Objects(state, "visibleEnemies").Any(a => Number(a, "id") == target);
			if (!valid)
				return "target no longer visible or valid for this order";
		}

		if (order["cell"] is JsonArray cell)
		{
			var (x, y) = Cell(cell);
			if (x < 0 || y < 0 || x >= Number(state["map"], "width") || y >= Number(state["map"], "height"))
				return "cell outside map";
		}
		if (type == "start_production")
		{
			var name = Text(order, "item");
			var queue = Objects(state, "production").FirstOrDefault(q => Objects(q, "buildable").Any(i => Name(i) == name));
			if (queue == null)
				return "item no longer buildable";
			var lane = Text(queue, "queue");
			if ((lane.StartsWith("Building") || lane.StartsWith("Support") || lane.StartsWith("Defense")) && queue["current"] is JsonObject
				|| (queue["queued"] as JsonArray)?.Count > 0)
				return "queue already committed";
			if (Number(order, "count") is < 1 or > 10)
				return "invalid batch count";
		}
		if (type == "place_building")
		{
			var placement = Objects(state, "pendingPlacement").FirstOrDefault(p => Text(p, "item") == Text(order, "item"));
			if (placement == null || !LegalCells(placement).Contains(Cell(order["cell"])))
				return "building or legal placement no longer available";
		}
		return null;
	}

	static string Key(JsonObject order) => Text(order, "type") is "start_production" or "place_building"
		? Text(order, "type") + ":" + Text(order, "item")
		: order.ToJsonString();
	public static long OrderCost(JsonObject state, JsonObject order) => Text(order, "type") == "start_production"
		? Cost(state, Text(order, "item")) * Number(order, "count") : 0;
	static long Cost(JsonObject state, string name) => Objects(state, "production").SelectMany(q => Objects(q, "buildable"))
		.Where(i => Name(i) == name).Select(i => Number(i, "cost")).FirstOrDefault();
}
