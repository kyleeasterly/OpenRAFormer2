using System.Text.Json.Nodes;
using NUnit.Framework;
using Orf;
using static OrfTests.JevTests;

namespace OrfTests;

[TestFixture]
public sealed class JevV2Tests
{
	static JsonObject Observation()
	{
		var state = State();
		state["units"]![0]!["hpPercent"] = 100;
		state["ruleCatalog"]!["Minigunner"]!["weapons"]![0]!["targets"] = "Ground,Water";
		return state;
	}

	static (JevPolicyV2 Policy, JevTactics Tactics) Policy()
	{
		var config = new JevSpec { PolicyVersion = 2 };
		var tactics = new JevTactics();
		return (new(config, new JevPolicy(config), tactics), tactics);
	}

	[Test]
	public void ReinforcementsDoNotJoinAWaveAlreadyCommittedAcrossTheMap()
	{
		var (policy, tactics) = Policy();
		var state = Observation();
		policy.Prepare(state, [], []);
		tactics.Groups["group0"].Phase = "advance";
		state["units"]![0]!["cell"] = new JsonArray(70,70);
		((JsonArray)state["units"]!).Add(new JsonObject { ["id"] = 3L, ["name"] = "Minigunner", ["cell"] = new JsonArray(12,10), ["idle"] = true });
		var frame = policy.Prepare(state, [], []);
		Assert.That(tactics.Groups["group0"].Members, Is.EqualTo(new long[] { 2 }));
		Assert.That(tactics.Groups["group1"].Members, Is.EqualTo(new long[] { 3 }));
		Assert.That(frame.Questions.ContainsKey("group1"), Is.True);
		Assert.That(frame.Questions.ContainsKey("squad0"), Is.False);
	}

	[Test]
	public void AReplacementUnitDoesNotRetainALegacyQuestionForADeadGroup()
	{
		var (policy, tactics) = Policy();
		var state = Observation();
		policy.Prepare(state, [], []);
		state["units"]![0]!["id"] = 3L;
		var frame = policy.Prepare(state, [], []);
		Assert.That(tactics.Groups.ContainsKey("group0"), Is.False);
		Assert.That(frame.Questions.ContainsKey("group0"), Is.False);
		Assert.That(frame.Questions.ContainsKey("group1"), Is.True);
	}

	[Test]
	public void ArtilleryAndCaptureSpecialistsAreSeparateFromFrontlineTroops()
	{
		var (policy, tactics) = Policy();
		var state = Observation();
		state["ruleCatalog"]!["Artillery"] = JsonNode.Parse("""{"cost":900,"weapons":[{"rangeCells":11,"targets":"Ground"}]}""");
		state["ruleCatalog"]!["Engineer"] = JsonNode.Parse("""{"cost":500,"canCapture":true,"weapons":[]}""");
		foreach (var (id, name) in new[] { (3L,"Artillery"), (4L,"Engineer") })
			((JsonArray)state["units"]!).Add(new JsonObject { ["id"] = id, ["name"] = name, ["cell"] = new JsonArray(12,10), ["idle"] = true });
		policy.Prepare(state, [], []);
		Assert.That(tactics.Groups.Values.Select(g => g.Role), Is.EquivalentTo(new[] { "front", "artillery", "capture" }));
	}

	[Test]
	public void LocalTargetIsUsedOnlyWhenTheGroupChoosesToEngage()
	{
		var (policy, _) = Policy();
		var state = Observation();
		var frame = policy.Prepare(state, [], []);
		var response = Response(frame, new() { ["group0_target"] = "target90" });
		var channel = new PlayerOrderChannel(Path.GetTempPath(), "jev-v2-test");
		Assert.That(((JsonArray)policy.Apply(frame, response, state, channel)["orders"]!).Count, Is.Zero);
		frame = policy.Prepare(state, [], []);
		response = Response(frame, new() { ["group0_target"] = "target90", ["group0"] = "engage" });
		var trace = policy.Apply(frame, response, state, channel);
		Assert.That(trace["orders"]![0]!["targetActorId"]!.GetValue<long>(), Is.EqualTo(90));
	}

	[Test]
	public void FarAwaySightingsAreNotLocalCombatTargets()
	{
		var (policy, _) = Policy();
		var state = Observation();
		state["visibleEnemies"]![0]!["cell"] = new JsonArray(90,90);
		Assert.That(policy.Prepare(state, [], []).Questions.ContainsKey("group0_target"), Is.False);
	}

	[Test]
	public void RefineryChoiceIncludesDockingRoutesAndPatchSize()
	{
		var (policy, _) = Policy();
		var state = Observation();
		state["pendingPlacement"]![0]!["item"] = "Tiberium Refinery";
		state["spatialEconomy"] = JsonNode.Parse("""
		{"refineries":{"Tiberium Refinery":{"freeUnit":"Harvester"}},"patches":[{"id":"large","visibleCells":30,"density":150},{"id":"small","visibleCells":2,"density":3}],"placementSites":[
		 {"item":"Tiberium Refinery","cell":[5,5],"dockCell":[5,7],"openDockNeighbors":3,"patches":[{"id":"large","travelCells":2,"visibleCells":30,"density":150}]},
		 {"item":"Tiberium Refinery","cell":[7,5],"dockCell":[7,7],"openDockNeighbors":1,"patches":[{"id":"small","travelCells":1,"visibleCells":2,"density":3}]}]}
		""");
		var frame = policy.Prepare(state, [], []);
		var criteria = frame.Questions["placement0"]!["criteria"]!;
		Assert.That(criteria["c5_5"]!["patches"]![0]!["travelCells"]!.GetValue<int>(), Is.EqualTo(2));
		Assert.That(criteria["c7_5"]!["patches"]![0]!["id"]!.GetValue<string>(), Is.EqualTo("small"));
		Assert.That(policy.RequestState["economy"]!["visibleResourcePatches"]![1]!["visibleCells"]!.GetValue<int>(), Is.EqualTo(2));
		Assert.That(policy.RequestState["game"]!["spatialEconomy"], Is.Null);
	}

	[Test]
	public void CampaignLaunchOwnsPreparingGroupsBeforeTheirLocalOrders()
	{
		var (policy, tactics) = Policy();
		var state = Observation();
		var frame = policy.Prepare(state, [], []);
		var response = Response(frame, new() { ["campaign"] = "launch", ["group0"] = "engage", ["group0_target"] = "target90" });
		var trace = policy.Apply(frame, response, state, new PlayerOrderChannel(Path.GetTempPath(), "jev-v2-test"));
		Assert.That(((JsonArray)trace["orders"]!).Count, Is.EqualTo(1));
		Assert.That(trace["orders"]![0]!["type"]!.GetValue<string>(), Is.EqualTo("attack_move"));
		Assert.That(tactics.Groups["group0"].Phase, Is.EqualTo("advance"));
		Assert.That(tactics.Campaign, Is.EqualTo("launch"));
		Assert.That(policy.Prepare(state, [], []).Questions.ContainsKey("campaign"), Is.False);
	}

	[Test]
	public void CampaignScoutIsDetachedFromThePreparingForce()
	{
		var (policy, tactics) = Policy();
		var state = Observation();
		var recruit = state["units"]![0]!.DeepClone(); recruit["id"] = 3L;
		state["units"]!.AsArray().Add(recruit);
		var frame = policy.Prepare(state, [], []);
		policy.Apply(frame, Response(frame, new() { ["campaign"] = "scout2" }), state, new PlayerOrderChannel(Path.GetTempPath(), "jev-v2-test"));
		Assert.That(tactics.Groups["group0"].Members, Is.EqualTo(new long[] { 3 }));
		Assert.That(tactics.Groups["group1"].Members, Is.EqualTo(new long[] { 2 }));
		Assert.That(tactics.Groups["group1"].Phase, Is.EqualTo("scout"));
		Assert.That(tactics.Campaign, Is.EqualTo("scout"));
	}

	[Test]
	public void PreparingGroupsCannotBypassCampaignHoldOrFollowDeployedReinforcements()
	{
		var (policy, tactics) = Policy();
		var state = Observation();
		var frame = policy.Prepare(state, [], []);
		Assert.That(frame.Actions["group0"].ContainsKey("advance"), Is.False);
		tactics.Groups["group0"].Committed = true;
		tactics.Groups["group0"].Phase = "advance";
		frame = policy.Prepare(state, [], []);
		Assert.That(frame.Actions["group0"].ContainsKey("advance"), Is.True);
	}

	[TestCase("Minigunner", 4)]
	[TestCase("Turret", 6)]
	public void NewArmedThreatCanInterruptBuildingFireBeforeOrdinaryHoldExpires(string threat, int range)
	{
		var config = new JevSpec { PolicyVersion = 2, CommandHoldSeconds = 30 };
		var oldOrder = """{"type":"attack","actorIds":[2],"targetActorId":91}""";
		var memory = new JevMemory { Assignments = new() { ["group0"] = new JevAssignment(oldOrder, 3) } };
		var tactics = new JevTactics();
		var policy = new JevPolicyV2(config, new JevPolicy(config, memory), tactics);
		var state = Observation();
		state["visibleEnemies"]![0]!["name"] = threat;
		state["ruleCatalog"]![threat] = new JsonObject { ["cost"] = 500L, ["weapons"] = new JsonArray(new JsonObject { ["rangeCells"] = range, ["targets"] = "Ground" }) };
		state["visibleEnemies"]!.AsArray().Add(new JsonObject { ["id"] = 91L, ["name"] = "Barracks", ["cell"] = new JsonArray(16,10), ["isBuilding"] = true });
		var frame = policy.Prepare(state, [], []);
		var trace = policy.Apply(frame, Response(frame, new() { ["alert_group0"] = "target90", ["campaign"] = "launch", ["group0"] = "engage", ["group0_target"] = "target91" }), state,
			new PlayerOrderChannel(Path.GetTempPath(), "jev-v2-test"));
		Assert.That(trace["orders"]!.AsArray().Count, Is.EqualTo(1));
		Assert.That(trace["orders"]![0]!["targetActorId"]!.GetValue<long>(), Is.EqualTo(90));
		Assert.That(tactics.Groups["group0"].Phase, Is.EqualTo("engage"));
		Assert.That(policy.Prepare(state, [], []).Questions.ContainsKey("alert_group0"), Is.False, "unchanged threats do not repeatedly interrupt");
	}

	[Test]
	public void OversizedPlacementChoicesAreSampledWithMatchingExecutableActions()
	{
		var (policy, _) = Policy();
		var state = Observation();
		state["pendingPlacement"]![0]!["item"] = "Tiberium Refinery";
		state["pendingPlacement"]![0]!["gridOrigin"] = new JsonArray(0,0);
		state["pendingPlacement"]![0]!["grid"] = new JsonArray([.. Enumerable.Range(0, 16).Select(_ => (JsonNode)JsonValue.Create(new string('+', 16))!)]);
		var sites = new JsonArray();
		for (var i = 0; i < 128; i++)
			sites.Add(new JsonObject { ["item"] = "Tiberium Refinery", ["cell"] = new JsonArray(i % 16,i / 16),
				["dockCell"] = new JsonArray(i % 16,i / 16 + 2), ["routeDescription"] = new string('x', 600),
				["patches"] = new JsonArray() });
		state["spatialEconomy"] = new JsonObject { ["refineries"] = new JsonObject { ["Tiberium Refinery"] = new JsonObject() }, ["placementSites"] = sites };
		var frame = policy.Prepare(state, [], []);
		JsonObject Request() => new() { ["model"] = "jev-test", ["state"] = policy.RequestState.DeepClone(), ["questions"] = frame.Questions.DeepClone() };
		Assert.That(JevClient.Batches(Request(), 48000).All(b => b.ToJsonString().Length <= 48000), Is.True);
		var choices = ((JsonObject)frame.Questions["placement0"]!["criteria"]!).Select(c => c.Key).ToList();
		Assert.That(choices.Count, Is.LessThan(129));
		Assert.That(choices, Does.Contain("wait"));
		Assert.That(frame.Actions["placement0"].Keys, Is.EquivalentTo(choices.Where(k => k != "wait")));
		Assert.That(policy.ReduceRequestBudget(), Is.True);
		frame = policy.Prepare(state, [], []);
		Assert.That(JevClient.Batches(Request(), 36000).All(b => b.ToJsonString().Length <= 36000), Is.True);
	}

	[Test]
	public void RepairToggleIsNeitherRepeatedNorAppliedAfterRepairStateChanges()
	{
		var (policy, _) = Policy();
		var state = Observation();
		state["buildings"]![0]!["hpPercent"] = 40;
		state["buildings"]![0]!["repairing"] = false;
		var frame = policy.Prepare(state, [], []);
		var order = frame.Actions["maintenance"]["repair1"].Order;
		state["buildings"]![0]!["repairing"] = true;
		var channel = new PlayerOrderChannel(Path.GetTempPath(), "jev-v2-test");
		Assert.That(channel.Validate(order, state), Does.Contain("repair state changed"));
		Assert.That(policy.Prepare(state, [], []).Questions.ContainsKey("maintenance"), Is.False);
	}

	[Test]
	public void ExpiredAnswerCannotCommitOperationOrGroupManeuver()
	{
		var (policy, tactics) = Policy();
		var state = Observation();
		var frame = policy.Prepare(state, [], []);
		var response = Response(frame, new() { ["operation"] = "objective0", ["campaign"] = "launch", ["alert_group0"] = "target90" });
		state = (JsonObject)state.DeepClone(); state["tick"] = 1000;
		var trace = policy.Apply(frame, response, state, new PlayerOrderChannel(Path.GetTempPath(), "jev-v2-test"));
		Assert.That(((JsonArray)trace["orders"]!).Count, Is.Zero);
		Assert.That(tactics.OperationCell, Is.Null);
		Assert.That(tactics.Groups["group0"].Phase, Is.EqualTo("assemble"));
		Assert.That(tactics.Groups["group0"].Committed, Is.False);
		Assert.That(tactics.Groups["group0"].ThreatIds, Is.Empty);
	}
}
