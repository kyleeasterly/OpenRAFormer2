using System.Text.Json.Nodes;
using Orf;

const string Usage =
	"""
	orf — OpenRAFormer2 orchestrator

	Usage:
	  orf run --spec <path> [--port N] [--no-game] [--no-web]
	      Run a full match: run dir + match.json, Xvfb/game/ffmpeg (unless --no-game),
	      agent loops, and the web dashboard (unless --no-web).

	  orf agent-turn --spec <path> --slug <slug> --state <fixture.json> --outdir <dir>
	      Execute exactly one agent turn offline against a state fixture. Writes the
	      inbox order file and turn artifacts under <dir>.

	  orf advisor-once --spec <path> --slug <slug> --rundir <dir>
	      Run one advisor review cycle offline against an existing run dir's turn
	      history (the player must have an advisor in the spec). Writes advice and
	      module status under <dir>/agents/<slug>/.

	  orf plan-test --state <fixture.json> [--plan "Item:placement,Item:placement"]
	                [--rules "Item:count,Item:count"]
	      Dry-run the plan executor against a state fixture: prints the orders it
	      would submit and the plan/status text the agent would see. No network.

	  orf launch --spec <path> [--node http://host:5100]
	      Validate a spec locally, then start it as a detached match on the target
	      node's hub (default: this machine's hub). No shell access needed on the
	      node — hubs are the runner control plane.

	  orf fleet [--node http://host:5100]...
	      One line per node (localhost + --node/ORF_NODES): host, cores, load,
	      and its live matches.

	  orf stop [runId] [--node http://host:5100]
	      Request a clean shutdown of a running match by writing runs/<id>/stop.
	      Without runId, targets the newest local unfinished run. With --node,
	      stops the match on that node (runId required).

	  orf hub [--port N] [--node http://host:5100]...
	      Landing page over all runs on one fixed port (default 5100): live score
	      graphs for running matches, proxied dashboards under /r/<runId>/. --node
	      (repeatable; or ORF_NODES=host:port,host:port) federates peer machines'
	      hubs so one page shows the whole fleet. Only open dashboards stream video.
	""";

if (args.Length == 0)
{
	Console.WriteLine(Usage);
	return 1;
}

try
{
	switch (args[0])
	{
		case "run":
			return await RunCommand(args[1..]);
		case "agent-turn":
			return await AgentTurnCommand(args[1..]);
		case "advisor-once":
			return await AdvisorOnceCommand(args[1..]);
		case "plan-test":
			return PlanTestCommand(args[1..]);
		case "launch":
			return await LaunchCommand(args[1..]);
		case "fleet":
			return await FleetCommand(args[1..]);
		case "stop":
			return await StopCommand(args[1..]);
		case "hub":
			return await HubCommand(args[1..]);
		case "-h" or "--help" or "help":
			Console.WriteLine(Usage);
			return 0;
		default:
			Console.Error.WriteLine($"Unknown command '{args[0]}'\n");
			Console.WriteLine(Usage);
			return 1;
	}
}
catch (Exception ex)
{
	Console.Error.WriteLine($"orf: {ex.Message}");
	return 1;
}

static async Task<int> RunCommand(string[] args)
{
	string? specPath = null;
	int? port = null;
	var noGame = false;
	var noWeb = false;

	for (var i = 0; i < args.Length; i++)
	{
		switch (args[i])
		{
			case "--spec":
				specPath = Expect(args, ref i, "--spec");
				break;
			case "--port":
				port = int.Parse(Expect(args, ref i, "--port"));
				break;
			case "--no-game":
				noGame = true;
				break;
			case "--no-web":
				noWeb = true;
				break;
			default:
				throw new ArgumentException($"Unknown option '{args[i]}' for 'run'");
		}
	}

	if (specPath == null)
		throw new ArgumentException("run requires --spec <path>");

	specPath = Util.ResolveInputPath(specPath);
	var spec = Spec.Load(specPath);

	using var cts = new CancellationTokenSource();
	Console.CancelKeyPress += (_, e) =>
	{
		e.Cancel = true;
		Util.Log("orf", "Ctrl+C received");
		cts.Cancel();
	};

	// `dotnet run` swallows console SIGINT, and a plain `kill` (SIGTERM) would skip
	// every finally block — either way children leak. Catch both and cancel instead.
	using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
		System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx =>
		{
			ctx.Cancel = true;
			Util.Log("orf", "SIGTERM received");
			cts.Cancel();
		});
	using var sigint = System.Runtime.InteropServices.PosixSignalRegistration.Create(
		System.Runtime.InteropServices.PosixSignal.SIGINT, ctx =>
		{
			ctx.Cancel = true;
			cts.Cancel();
		});

	return await new MatchRunner(spec, specPath).RunAsync(port, noGame, noWeb, cts.Token);
}

static async Task<int> AdvisorOnceCommand(string[] args)
{
	string? specPath = null, slug = null, runDir = null;

	for (var i = 0; i < args.Length; i++)
	{
		switch (args[i])
		{
			case "--spec":
				specPath = Expect(args, ref i, "--spec");
				break;
			case "--slug":
				slug = Expect(args, ref i, "--slug");
				break;
			case "--rundir":
				runDir = Expect(args, ref i, "--rundir");
				break;
			default:
				throw new ArgumentException($"Unknown option '{args[i]}' for 'advisor-once'");
		}
	}

	if (specPath == null || slug == null || runDir == null)
		throw new ArgumentException("advisor-once requires --spec, --slug and --rundir");

	specPath = Util.ResolveInputPath(specPath);
	var spec = Spec.Load(specPath);
	var player = spec.Players.FirstOrDefault(p => p.Slug == slug)
		?? throw new ArgumentException($"No player with slug '{slug}' in spec");
	if (player.Advisor == null)
		throw new ArgumentException($"Player '{slug}' has no advisor in the spec");

	var specDir = Path.GetDirectoryName(Path.GetFullPath(specPath)) ?? ".";
	var advisor = new AdvisorLoop(Path.GetFullPath(runDir), spec, player, specDir);

	if (!await advisor.RunOnceAsync(CancellationToken.None))
	{
		Util.Log("orf", $"no completed turns found under {runDir}/agents/{slug}/turns");
		return 1;
	}

	Util.Log("orf", $"advisor-once complete; see {runDir}/agents/{slug}/advice and modules");
	return 0;
}

static async Task<int> AgentTurnCommand(string[] args)
{
	string? specPath = null, slug = null, statePath = null, outDir = null;

	for (var i = 0; i < args.Length; i++)
	{
		switch (args[i])
		{
			case "--spec":
				specPath = Expect(args, ref i, "--spec");
				break;
			case "--slug":
				slug = Expect(args, ref i, "--slug");
				break;
			case "--state":
				statePath = Expect(args, ref i, "--state");
				break;
			case "--outdir":
				outDir = Expect(args, ref i, "--outdir");
				break;
			default:
				throw new ArgumentException($"Unknown option '{args[i]}' for 'agent-turn'");
		}
	}

	if (specPath == null || slug == null || statePath == null || outDir == null)
		throw new ArgumentException("agent-turn requires --spec, --slug, --state and --outdir");

	specPath = Util.ResolveInputPath(specPath);
	statePath = Util.ResolveInputPath(statePath);
	var spec = Spec.Load(specPath);
	var player = spec.Players.FirstOrDefault(p => p.Slug == slug)
		?? throw new ArgumentException($"No player with slug '{slug}' in spec");

	var state = JsonNode.Parse(File.ReadAllText(statePath)) as JsonObject
		?? throw new ArgumentException($"State fixture '{statePath}' is not a JSON object");

	outDir = Path.GetFullPath(outDir);
	Directory.CreateDirectory(outDir);

	var specDir = Path.GetDirectoryName(Path.GetFullPath(specPath)) ?? ".";
	var index = spec.Players.IndexOf(player);
	if (player.Controller == "jev")
		await new JevLoop(outDir, spec, player).TakeTurnAsync(state, CancellationToken.None);
	else
		await new AgentLoop(outDir, spec, player, index, specDir).TakeTurnAsync(state, [], CancellationToken.None);

	Util.Log("orf", $"agent-turn complete; artifacts under {outDir}");
	return 0;
}

static async Task<int> HubCommand(string[] args)
{
	var port = 5100;
	var nodes = new List<string>();
	for (var i = 0; i < args.Length; i++)
	{
		if (args[i] == "--port")
			port = int.Parse(Expect(args, ref i, "--port"));
		else if (args[i] == "--node")
			nodes.Add(Expect(args, ref i, "--node").TrimEnd('/'));
		else
			throw new ArgumentException($"Unknown option '{args[i]}' for 'hub'");
	}

	// Peer hubs can also come from the environment so a service definition or
	// shell profile can pin the fleet without editing commands.
	var envNodes = Environment.GetEnvironmentVariable("ORF_NODES");
	if (!string.IsNullOrWhiteSpace(envNodes))
		nodes.AddRange(envNodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(n => n.TrimEnd('/')));

	using var cts = new CancellationTokenSource();
	Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
	using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
		System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; cts.Cancel(); });

	return await Hub.RunAsync(port, nodes, cts.Token);
}

static async Task<int> LaunchCommand(string[] args)
{
	string? specPath = null;
	var node = "http://127.0.0.1:5100";

	for (var i = 0; i < args.Length; i++)
	{
		switch (args[i])
		{
			case "--spec":
				specPath = Expect(args, ref i, "--spec");
				break;
			case "--node":
				node = Expect(args, ref i, "--node").TrimEnd('/');
				break;
			default:
				throw new ArgumentException($"Unknown option '{args[i]}' for 'launch'");
		}
	}

	if (specPath == null)
		throw new ArgumentException("launch requires --spec");

	specPath = Util.ResolveInputPath(specPath);
	var spec = Spec.Load(specPath); // validate before shipping it anywhere

	using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
	var response = await http.PostAsync($"{node}/api/launch",
		new StringContent(File.ReadAllText(specPath), System.Text.Encoding.UTF8, "text/yaml"));
	var body = await response.Content.ReadAsStringAsync();
	if (!response.IsSuccessStatusCode)
		throw new InvalidOperationException($"launch failed on {node}: {(int)response.StatusCode} {body}");

	Util.Log("orf", $"launched '{spec.Name}' on {node}: {body}");
	return 0;
}

static async Task<int> FleetCommand(string[] args)
{
	var nodes = new List<string> { "http://127.0.0.1:5100" };
	for (var i = 0; i < args.Length; i++)
	{
		if (args[i] == "--node")
			nodes.Add(Expect(args, ref i, "--node").TrimEnd('/'));
		else
			throw new ArgumentException($"Unknown option '{args[i]}' for 'fleet'");
	}

	var envNodes = Environment.GetEnvironmentVariable("ORF_NODES");
	if (!string.IsNullOrWhiteSpace(envNodes))
		nodes.AddRange(envNodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(n => n.TrimEnd('/')));

	using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
	foreach (var node in nodes.Distinct())
	{
		try
		{
			var info = JsonNode.Parse(await http.GetStringAsync($"{node}/api/node")) as JsonObject;
			var runs = JsonNode.Parse(await http.GetStringAsync($"{node}/api/runs?localOnly=1")) as JsonArray ?? [];
			var live = runs.OfType<JsonObject>().Where(r => r["live"]?.GetValue<bool>() == true).ToList();
			Console.WriteLine($"{node}  [{info?["host"]}]  cores={info?["cores"]}  load1={info?["load1"]}  live={live.Count}");
			foreach (var run in live)
				Console.WriteLine($"    {run["runId"]}  {run["players"]}p");
		}
		catch (Exception ex)
		{
			Console.WriteLine($"{node}  UNREACHABLE ({ex.Message})");
		}
	}

	return 0;
}

static async Task<int> StopCommand(string[] args)
{
	string? nodeArg = null;
	var rest = new List<string>();
	for (var i = 0; i < args.Length; i++)
	{
		if (args[i] == "--node")
			nodeArg = Expect(args, ref i, "--node").TrimEnd('/');
		else
			rest.Add(args[i]);
	}

	args = [.. rest];

	if (nodeArg != null)
	{
		if (args.Length == 0)
			throw new ArgumentException("stop --node requires an explicit runId");

		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
		var response = await http.PostAsync($"{nodeArg}/api/stop/{args[0]}", null);
		if (!response.IsSuccessStatusCode)
			throw new InvalidOperationException($"stop failed on {nodeArg}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");

		Util.Log("orf", $"stop requested for {args[0]} on {nodeArg}");
		return 0;
	}

	var runsDir = Path.Combine(Util.FindRepoRoot(), "runs");
	string? runDir = null;

	if (args.Length > 0)
	{
		runDir = Path.Combine(runsDir, args[0]);
		if (!Directory.Exists(runDir))
			throw new ArgumentException($"No run dir '{runDir}'");
	}
	else
	{
		runDir = Directory.GetDirectories(runsDir)
			.Where(d => !File.Exists(Path.Combine(d, "result.json")))
			.OrderByDescending(Path.GetFileName)
			.FirstOrDefault()
			?? throw new ArgumentException("No unfinished run found to stop");
	}

	File.WriteAllText(Path.Combine(runDir, "stop"), $"requested {DateTime.UtcNow:o}\n");
	Util.Log("orf", $"stop requested for {Path.GetFileName(runDir)} (takes effect within ~1s if that match is live)");
	return 0;
}

static string Expect(string[] args, ref int i, string flag)
{
	if (i + 1 >= args.Length)
		throw new ArgumentException($"{flag} requires a value");
	return args[++i];
}

static int PlanTestCommand(string[] args)
{
	string? statePath = null, planArg = null, rulesArg = null;
	for (var i = 0; i < args.Length; i++)
	{
		switch (args[i])
		{
			case "--state":
				statePath = Expect(args, ref i, "--state");
				break;
			case "--plan":
				planArg = Expect(args, ref i, "--plan");
				break;
			case "--rules":
				rulesArg = Expect(args, ref i, "--rules");
				break;
		}
	}

	if (statePath == null)
	{
		Console.Error.WriteLine("plan-test requires --state <fixture.json>");
		return 1;
	}

	if (JsonNode.Parse(File.ReadAllText(statePath)) is not JsonObject state)
	{
		Console.Error.WriteLine("state fixture is not a JSON object");
		return 1;
	}

	var executor = new PlanExecutor();

	var steps = new JsonArray();
	foreach (var part in (planArg ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
	{
		var bits = part.Split(':');
		steps.Add(new JsonObject
		{
			["item"] = bits[0].Trim(),
			["placement"] = bits.Length > 1 ? bits[1].Trim() : "near_base",
		});
	}

	var rules = new JsonArray();
	foreach (var part in (rulesArg ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
	{
		var bits = part.Split(':');
		rules.Add(new JsonObject
		{
			["item"] = bits[0].Trim(),
			["maintain"] = bits.Length > 1 && int.TryParse(bits[1], out var n) ? n : 1,
		});
	}

	Console.WriteLine("set_build_plan       -> " + executor.SetPlan(steps));
	Console.WriteLine("set_production_rules -> " + executor.SetRules(rules));

	var orders = executor.Tick(state, new HashSet<string>());
	Console.WriteLine($"\nORDERS THE EXECUTOR WOULD SUBMIT ({orders.Count}):");
	foreach (var o in orders)
		Console.WriteLine("  " + o!.ToJsonString());

	Console.WriteLine("\nWHAT THE AGENT WOULD SEE:");
	Console.WriteLine(executor.Render() ?? "  (no plan)");
	Console.WriteLine(executor.DrainActivity() ?? "  (no activity)");
	return 0;
}
