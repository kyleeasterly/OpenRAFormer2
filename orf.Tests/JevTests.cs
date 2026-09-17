using System.Net;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Orf;

namespace OrfTests;

[TestFixture]
public sealed class JevTests
{
	string directory = null!;

	[SetUp]
	public void Setup() => directory = Path.Combine(Path.GetTempPath(), "orf-test-" + Guid.NewGuid());

	[TearDown]
	public void Cleanup()
	{
		if (Directory.Exists(directory)) Directory.Delete(directory, true);
	}

	internal static JsonObject State() => (JsonObject)JsonNode.Parse("""
	{
	  "tick": 100, "second": 4,
	  "you": {"cash": 900, "powerProvided": 100, "powerDrained": 40},
	  "map": {"width": 100, "height": 100, "yourSpawnCell": [10,10]},
	  "buildings": [{"id":1,"name":"Construction Yard","cell":[10,10]}],
	  "units": [{"id":2,"name":"Minigunner","cell":[12,10],"idle":true}],
	  "visibleEnemies": [{"id":90,"name":"Minigunner","cell":[15,10]}],
	  "enemySpawns": [{"cell":[80,80],"explored":false}],
	  "production": [
	    {"queue":"Building.GDI","current":null,"queued":[],"buildable":[{"name":"Power Plant","cost":600}]},
	    {"queue":"Infantry.GDI","current":null,"queued":[],"buildable":[{"name":"Minigunner","cost":600}]}
	  ],
	  "ruleCatalog": {
	    "Power Plant":{"isBuilding":true,"cost":600,"power":100},
	    "Minigunner":{"cost":600,"weapons":[{"rangeCells":4}]}
	  },
	  "pendingPlacement": [{"item":"Barracks","gridOrigin":[5,5],"grid":["+.+",".+."]}]
	}
	""")!;

	internal static JsonObject Response(JevFrame frame, Dictionary<string, string>? choices = null)
	{
		var answers = new JsonObject();
		foreach (var (id, q) in frame.Questions)
		{
			var probabilities = new JsonObject();
			if (q!["type"]!.GetValue<string>() == "score")
			{
				for (var i = 0; i < ((JsonArray)q["criteria"]!).Count; i++) probabilities[i.ToString()] = i == 2 ? 1 : 0;
				answers[id] = new JsonObject { ["type"] = "score", ["score"] = 2, ["confidence"] = 1, ["probabilities"] = probabilities };
			}
			else
			{
				var options = (JsonObject)q["criteria"]!;
				var selected = choices?.GetValueOrDefault(id) ?? options.First().Key;
				foreach (var key in options.Select(kv => kv.Key)) probabilities[key] = key == selected ? 1 : 0;
				answers[id] = new JsonObject { ["type"] = "choice", ["choice"] = selected, ["confidence"] = 1, ["probabilities"] = probabilities };
			}
		}
		return new JsonObject { ["answers"] = answers };
	}

	[Test]
	public void BatchesDecisionsAndOffersOnlyLegalPlacementCells()
	{
		var policy = new JevPolicy(new());
		var frame = policy.Prepare(State(), [], []);
		Assert.That(frame.Questions.Count, Is.GreaterThan(5));
		Assert.That(frame.Actions["placement0"].Values.Select(a => JevPolicy.Cell(a.Order["cell"])),
			Is.EquivalentTo(new[] { (5,5), (7,5), (6,6) }));
		Assert.That(policy.RequestState["game"]?["pendingPlacement"]?[0]?["grid"], Is.Null);
	}

	[Test]
	public void ParallelPurchasesCannotSpendTheSameCash()
	{
		var state = State();
		var policy = new JevPolicy(new());
		var frame = policy.Prepare(state, [], []);
		var response = Response(frame, new() { ["production0"] = "a0", ["production1"] = "a0" });
		var result = policy.Apply(frame, response, state, new(directory, "p"));
		Assert.That(((JsonArray)result["orders"]!).Count, Is.EqualTo(1));
		Assert.That(result["remainingBudget"]!.GetValue<long>(), Is.EqualTo(300));
		Assert.That(result.ToJsonString(), Does.Contain("budget reserved"));
	}

	[Test]
	public void ExpiredResponsesCannotChangeObjectivesOrIssueOrders()
	{
		var state = State();
		var policy = new JevPolicy(new() { MaxResponseAgeTicks = 25 });
		var frame = policy.Prepare(state, [], []);
		var latest = (JsonObject)state.DeepClone();
		latest["tick"] = 200;
		var result = policy.Apply(frame, Response(frame, new() { ["focus"] = "pressure", ["production0"] = "a0" }), latest, new(directory, "p"));
		Assert.That(((JsonArray)result["orders"]!).Count, Is.Zero);
		Assert.That(policy.Memory.Focus, Is.EqualTo("develop"));
	}

	[Test]
	public void JevCanSaveForItsCapitalGoalWhileAnotherQueueWantsToSpend()
	{
		var state = State();
		var memory = new JevMemory { Goal = "Power Plant", GoalCount = 1, GoalAt = 4 };
		var policy = new JevPolicy(new(), memory);
		var frame = policy.Prepare(state, [], []);
		Assert.That(frame.ReserveItem, Is.EqualTo("Power Plant"));
		var result = policy.Apply(frame, Response(frame, new() { ["budget"] = "reserve", ["production1"] = "a0" }), state, new(directory, "p"));
		Assert.That(((JsonArray)result["orders"]!).Count, Is.Zero);
		Assert.That(result.ToJsonString(), Does.Contain("budget reserved"));
	}

	[Test]
	public void NewCostsAreUsedWhenRevalidatingAnOldProposal()
	{
		var state = State();
		var policy = new JevPolicy(new());
		var frame = policy.Prepare(state, [], []);
		var latest = (JsonObject)state.DeepClone();
		latest["production"]![0]!["buildable"]![0]!["cost"] = 1000;
		var result = policy.Apply(frame, Response(frame, new() { ["production0"] = "a0" }), latest, new(directory, "p"));
		Assert.That(((JsonArray)result["orders"]!).Count, Is.Zero);
	}

	[Test]
	public void ObservedGoalCompletionAllowsANewObjective()
	{
		var state = State();
		var memory = new JevMemory { Goal = "Power Plant", GoalCount = 1, GoalAt = 4 };
		var policy = new JevPolicy(new(), memory);
		Assert.That(policy.Prepare(state, [], []).Questions.ContainsKey("goal"), Is.False);
		((JsonArray)state["buildings"]!).Add(new JsonObject { ["id"] = 3L, ["name"] = "Power Plant", ["cell"] = new JsonArray(5,5) });
		Assert.That(policy.Prepare(state, [], []).Questions.ContainsKey("goal"), Is.True);
		Assert.That(memory.Goal, Is.Null);
	}

	[Test]
	public void ChoosingNoCapitalGoalIsRetainedUntilReconsideration()
	{
		var state = State();
		var policy = new JevPolicy(new());
		var frame = policy.Prepare(state, [], []);
		policy.Apply(frame, Response(frame), state, new(directory, "p"));
		state["second"] = 5;
		Assert.That(policy.Prepare(state, [], []).Questions.ContainsKey("goal"), Is.False);
		state["second"] = 24;
		Assert.That(policy.Prepare(state, [], []).Questions.ContainsKey("goal"), Is.True);
	}

	[Test]
	public void CaptureSendsOnlyACapableMemberToAVisibleCapturableTarget()
	{
		var state = State();
		((JsonArray)state["units"]!).Add(new JsonObject { ["id"] = 3L, ["name"] = "Engineer", ["cell"] = new JsonArray(13,10), ["idle"] = true });
		state["ruleCatalog"]!["Engineer"] = new JsonObject { ["canCapture"] = true };
		state["ruleCatalog"]!["Barracks"] = new JsonObject { ["capturable"] = true };
		((JsonArray)state["visibleEnemies"]!).Add(new JsonObject { ["id"] = 91L, ["name"] = "Barracks", ["cell"] = new JsonArray(16,10) });
		var policy = new JevPolicy(new());
		var frame = policy.Prepare(state, [], []);
		Assert.That(frame.Actions["squad0"].ContainsKey("capture90"), Is.False);
		var result = policy.Apply(frame, Response(frame, new() { ["squad0"] = "capture91" }), state, new(directory, "p"));
		Assert.That(result["orders"]![0]!["actorIds"]!.ToJsonString(), Is.EqualTo("[3]"));
		Assert.That(result["orders"]![0]!["type"]!.GetValue<string>(), Is.EqualTo("capture"));
	}

	[Test]
	public void StaleTargetsAndNewlyBlockedPlacementsAreRejected()
	{
		var state = State();
		var policy = new JevPolicy(new());
		var frame = policy.Prepare(state, [], []);
		var latest = (JsonObject)state.DeepClone();
		latest["visibleEnemies"] = new JsonArray();
		latest["pendingPlacement"]![0]!["grid"] = new JsonArray("...", "...");
		var result = policy.Apply(frame, Response(frame, new() { ["squad0"] = "attack90", ["placement0"] = "c5_5" }), latest, new(directory, "p"));
		Assert.That(((JsonArray)result["orders"]!).Count, Is.Zero);
		Assert.That(result.ToJsonString(), Does.Contain("target no longer visible"));
		Assert.That(result.ToJsonString(), Does.Contain("placement no longer available"));
	}

	[Test]
	public void DeadActorsAreRemovedBeforeSubmission()
	{
		var channel = new PlayerOrderChannel(directory, "p");
		var order = (JsonObject)JsonNode.Parse("""{"type":"move","actorIds":[2,999],"cell":[20,20]}""")!;
		Assert.That(channel.Validate(order, State()), Is.Null);
		Assert.That(order["actorIds"]!.ToJsonString(), Is.EqualTo("[2]"));
	}

	[Test]
	public void AcknowledgementsDoNotReleaseReservationsBeforeObservedExecution()
	{
		var channel = new PlayerOrderChannel(directory, "p");
		var state = State();
		var order = (JsonObject)JsonNode.Parse("""{"type":"start_production","item":"Power Plant","count":1}""")!;
		channel.Submit("000001", new JsonArray(order.DeepClone()), state);
		Assert.That(channel.ReservedCash, Is.EqualTo(600));
		Assert.That(channel.Validate(order, state), Does.Contain("awaiting"));
		Util.WriteAtomic(Path.Combine(channel.ResultsDir, "000001.json"), """{"tick":110,"results":[{"index":0,"status":"ok"}]}""");
		state["tick"] = 125;
		Assert.That(channel.Collect(state).Count, Is.EqualTo(1));
		Assert.That(channel.ReservedCash, Is.EqualTo(600));
		state["tick"] = 175;
		Assert.That(channel.Collect(state).Count, Is.Zero);
		Assert.That(channel.ReservedCash, Is.Zero);
	}

	[Test]
	public void StableSquadsPruneDeadMembersAndIncludeNewUnits()
	{
		var policy = new JevPolicy(new());
		var state = State();
		policy.Prepare(state, [], []);
		state["units"]![0]!["id"] = 3;
		policy.Prepare(state, [], []);
		Assert.That(policy.Memory.Squads["squad0"], Is.EqualTo(new long[] { 3 }));
	}

	[Test]
	public void RepeatedCommandsPreserveActiveAssignments()
	{
		var policy = new JevPolicy(new());
		var state = State();
		var channel = new PlayerOrderChannel(directory, "p");
		var frame = policy.Prepare(state, [], []);
		var result = policy.Apply(frame, Response(frame, new() { ["squad0"] = "attack90" }), state, channel);
		Assert.That(((JsonArray)result["orders"]!).Count, Is.EqualTo(1));
		state["second"] = 5;
		frame = policy.Prepare(state, [], []);
		result = policy.Apply(frame, Response(frame, new() { ["squad0"] = "regroup" }), state, channel);
		Assert.That(((JsonArray)result["orders"]!).Count, Is.Zero);
	}

	[Test]
	public void UnknownChoicesAndMissingAnswersFailClosed()
	{
		var frame = new JevPolicy(new()).Prepare(State(), [], []);
		var response = Response(frame);
		response["answers"]!["focus"]!["choice"] = "invented";
		Assert.Throws<InvalidDataException>(() => JevClient.ValidateAnswers(frame.Questions, response));
		response = Response(frame);
		((JsonObject)response["answers"]!).Remove("focus");
		Assert.Throws<InvalidDataException>(() => JevClient.ValidateAnswers(frame.Questions, response));
	}

	[Test]
	public void JevCannotSilentlyEnableAnLlmAdvisor()
	{
		Directory.CreateDirectory(directory);
		var path = Path.Combine(directory, "spec.yaml");
		File.WriteAllText(path, """
		name: invalid
		players:
		  - slug: p
		    controller: jev
		    provider: typesafe
		    advisor: { model: some-llm }
		providers:
		  typesafe: { baseUrl: 'https://api.typesafe.ai/v1' }
		""");
		Assert.Throws<InvalidOperationException>(() => Spec.Load(path));
	}

	sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
	}

	[Test]
	public async Task NativeClientUsesBearerAuthAndDoesNotRetryOldObservations()
	{
		var calls = 0;
		using var http = new HttpClient(new Handler((request, _) =>
		{
			calls++;
			Assert.That(request.RequestUri!.AbsolutePath, Is.EqualTo("/v1/systemone"));
			Assert.That(request.Headers.Authorization!.Scheme, Is.EqualTo("Bearer"));
			var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
			response.Headers.RetryAfter = new(TimeSpan.FromSeconds(2));
			return Task.FromResult(response);
		}));
		var client = new JevClient("https://example.invalid/v1", "test-only", 500, http);
		try { await client.EvaluateAsync(new(), CancellationToken.None); Assert.Fail("Expected rate limit"); }
		catch (JevApiException e) { Assert.That(e.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(2))); }
		Assert.That(calls, Is.EqualTo(1));
	}

	[Test]
	public async Task NativeBatchesShareOneObservationAndCombineOnlyValidatedAnswers()
	{
		var request = JsonNode.Parse("""{"model":"jev-test","state":{"cash":100},"questions":{"a":{"type":"noul","instructions":"A?"},"b":{"type":"noul","instructions":"B?"}}}""")!.AsObject();
		var single = request.DeepClone().AsObject(); single["questions"]!.AsObject().Remove("b");
		var budget = single.ToJsonString().Length;
		var records = 0;
		using var http = new HttpClient(new Handler(async (message, ct) =>
		{
			var sent = JsonNode.Parse(await message.Content!.ReadAsStringAsync(ct))!.AsObject();
			Assert.That(sent["state"]!.ToJsonString(), Is.EqualTo(request["state"]!.ToJsonString()));
			Assert.That(sent.ToJsonString().Length, Is.LessThanOrEqualTo(budget));
			var id = sent["questions"]!.AsObject().Single().Key;
			return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonObject
			{
				["model"] = "jev-test", ["answers"] = new JsonObject { [id] = new JsonObject { ["type"] = "noul", ["noul"] = 0.8 } },
				["usage"] = new JsonObject { ["input_tokens"] = 7, ["output_tokens"] = 3 }
			}.ToJsonString()) };
		}));
		var client = new JevClient("https://example.invalid/v1", "test-only", 500, http);
		var result = await client.EvaluateBatchedAsync(request, budget, (_, _, _) => records++, CancellationToken.None);
		Assert.That(records, Is.EqualTo(2));
		Assert.That(result["answers"]!.AsObject().Count, Is.EqualTo(2));
		Assert.That(result["usage"]!["input_tokens"]!.GetValue<long>(), Is.EqualTo(14));
		Assert.That(result["batchCount"]!.GetValue<int>(), Is.EqualTo(2));
		Assert.Throws<InvalidOperationException>(() => JevClient.Batches(request, 5));
	}

	[Test]
	public async Task NativeSizeErrorIsIdentifiedWithoutEchoingArbitraryErrorContent()
	{
		using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
		{
			Content = new StringContent("""{"detail":{"error_type":"max_tokens_exceeded","echo":"private-request-content"}}""")
		})));
		var client = new JevClient("https://example.invalid/v1", "test-only", 500, http);
		try { await client.EvaluateAsync(new(), CancellationToken.None); Assert.Fail("Expected size error"); }
		catch (JevApiException e)
		{
			Assert.That(e.ErrorType, Is.EqualTo("max_tokens_exceeded"));
			Assert.That(e.Retryable, Is.False);
			Assert.That(e.Message, Does.Not.Contain("private-request-content"));
		}
	}

	[Test]
	public async Task MatchEndingDuringRequestDiscardsTheResponse()
	{
		var state = State();
		var frame = new JevPolicy(new()).Prepare(state, [], []);
		using var http = new HttpClient(new Handler((_, _) =>
		{
			Util.WriteAtomic(Path.Combine(directory, "result.json"), "{}");
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(Response(frame, new() { ["production0"] = "a0" }).ToJsonString())
			});
		}));
		var player = new PlayerSpec { Slug = "p", Controller = "jev", Provider = "typesafe" };
		var spec = new Spec { Players = [player], Providers = new() { ["typesafe"] = new ProviderSpec { BaseUrl = "https://example.invalid/v1" } } };
		await new JevLoop(directory, spec, player, new JevClient("https://example.invalid/v1", "test-only", 500, http)).TakeTurnAsync(state, CancellationToken.None);
		Assert.That(Directory.Exists(Path.Combine(directory, "orders", "p", "inbox")), Is.False);
		Assert.That(File.ReadAllText(Path.Combine(directory, "agents", "p", "turns", "000001", "decisions.json")), Does.Contain("match ended"));
	}

	[Test]
	public void NativeClientHonorsRequestDeadline()
	{
		using var http = new HttpClient(new Handler(async (_, ct) =>
		{
			await Task.Delay(10000, ct);
			return new HttpResponseMessage(HttpStatusCode.OK);
		}));
		var client = new JevClient("https://example.invalid/v1", "test-only", 20, http);
		Assert.CatchAsync<OperationCanceledException>(async () => await client.EvaluateAsync(new(), CancellationToken.None));
	}
}
