using System.Text;
using System.Text.Json.Nodes;

namespace Orf;

/// <summary>Kestrel minimal-API dashboard: static index, SSE live feed, MJPEG stream.</summary>
public static class WebServer
{
	public static async Task<WebApplication> StartAsync(Spec spec, string runDir, string runId, int port)
	{
		var wwwroot = Util.AssetPath("wwwroot");

		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			ContentRootPath = AppContext.BaseDirectory,
			WebRootPath = wwwroot,
			EnvironmentName = "Production",
		});
		builder.Logging.ClearProviders();
		builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

		var app = builder.Build();

		app.MapGet("/", () => Results.File(Path.Combine(wwwroot, "index.html"), "text/html; charset=utf-8"));
		app.UseStaticFiles();

		// Baked web renderer assets (utility --llm-export-web-assets writes here).
		var webAssets = Path.Combine(Util.FindRepoRoot(), "webassets");
		if (Directory.Exists(webAssets))
			app.UseStaticFiles(new StaticFileOptions
			{
				FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webAssets),
				RequestPath = "/webassets",
			});

		// Observer actor feed: pushes each new actors.json frame (~8 Hz, written
		// by the engine) as SSE for the browser renderer.
		app.MapGet("/api/actors", async context =>
		{
			context.Response.Headers.ContentType = "text/event-stream";
			context.Response.Headers.CacheControl = "no-cache";
			var ct = context.RequestAborted;
			var actorsPath = Path.Combine(runDir, "state", "actors.json");
			var lastWrite = DateTime.MinValue;

			try
			{
				await context.Response.Body.FlushAsync(ct);
				while (!ct.IsCancellationRequested)
				{
					if (File.Exists(actorsPath))
					{
						var write = File.GetLastWriteTimeUtc(actorsPath);
						if (write != lastWrite)
						{
							lastWrite = write;
							string? frame = null;
							try
							{
								frame = File.ReadAllText(actorsPath);
							}
							catch (IOException)
							{
								// mid-write; retry next poll
							}

							if (frame != null && frame.StartsWith('{') && frame.EndsWith('}'))
							{
								await context.Response.WriteAsync($"data: {frame}\n\n", ct);
								await context.Response.Body.FlushAsync(ct);
							}
						}
					}

					await Task.Delay(100, ct);
				}
			}
			catch (Exception) when (ct.IsCancellationRequested)
			{
				// client went away
			}
			catch (IOException)
			{
			}
		});

		app.MapGet("/api/live", async context =>
		{
			context.Response.Headers.ContentType = "text/event-stream";
			context.Response.Headers.CacheControl = "no-cache";
			var ct = context.RequestAborted;

			try
			{
				while (!ct.IsCancellationRequested)
				{
					var payload = BuildLivePayload(spec, runDir, runId);
					await context.Response.WriteAsync($"data: {payload}\n\n", ct);
					await context.Response.Body.FlushAsync(ct);
					await Task.Delay(1000, ct);
				}
			}
			catch (Exception) when (ct.IsCancellationRequested || !context.Response.HasStarted)
			{
				// client went away
			}
			catch (IOException)
			{
			}
		});

		app.MapGet("/audio", async context =>
		{
			var broadcaster = AudioBroadcaster.Instance;
			if (broadcaster == null)
			{
				context.Response.StatusCode = 404;
				return;
			}

			context.Response.ContentType = "audio/mpeg";
			context.Response.Headers.CacheControl = "no-store";
			var ct = context.RequestAborted;
			var channel = broadcaster.Subscribe();

			try
			{
				await context.Response.Body.FlushAsync(ct);
				await foreach (var chunk in channel.Reader.ReadAllAsync(ct))
				{
					await context.Response.Body.WriteAsync(chunk, ct);
					await context.Response.Body.FlushAsync(ct);
				}
			}
			catch (Exception) when (ct.IsCancellationRequested)
			{
				// client went away
			}
			catch (IOException)
			{
			}
			finally
			{
				broadcaster.Unsubscribe(channel);
			}
		});

		app.MapGet("/stream", async context =>
		{
			context.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
			var ct = context.RequestAborted;
			var livePath = Path.Combine(runDir, "live.jpg");
			var lastWrite = DateTime.MinValue;

			try
			{
				// Flush headers immediately so clients see a 200 while waiting for the first frame.
				await context.Response.Body.FlushAsync(ct);

				while (!ct.IsCancellationRequested)
				{
					var info = new FileInfo(livePath);
					if (info.Exists && info.LastWriteTimeUtc != lastWrite)
					{
						byte[] bytes;
						try
						{
							bytes = await File.ReadAllBytesAsync(livePath, ct);
						}
						catch (IOException)
						{
							await Task.Delay(80, ct);
							continue;
						}

						if (bytes.Length > 0)
						{
							lastWrite = info.LastWriteTimeUtc;
							var header = Encoding.ASCII.GetBytes(
								$"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {bytes.Length}\r\n\r\n");
							await context.Response.Body.WriteAsync(header, ct);
							await context.Response.Body.WriteAsync(bytes, ct);
							await context.Response.Body.WriteAsync("\r\n"u8.ToArray(), ct);
							await context.Response.Body.FlushAsync(ct);
						}
					}

					await Task.Delay(1000 / Math.Max(1, spec.Stream.Fps), ct);
				}
			}
			catch (OperationCanceledException)
			{
			}
			catch (IOException)
			{
			}
		});

		app.MapGet("/frame.jpg", () =>
		{
			var livePath = Path.Combine(runDir, "live.jpg");
			try
			{
				return File.Exists(livePath)
					? Results.Bytes(File.ReadAllBytes(livePath), "image/jpeg")
					: Results.NotFound();
			}
			catch (IOException)
			{
				return Results.NotFound();
			}
		});

		// Agent turn history. Slugs/seqs are validated before touching the filesystem.
		var validSlugs = spec.Players.Select(p => p.Slug).ToHashSet();

		app.MapGet("/api/agents/{slug}/turns", (string slug) =>
		{
			if (!validSlugs.Contains(slug))
				return Results.NotFound();

			var turnsDir = Path.Combine(runDir, "agents", slug, "turns");
			if (!Directory.Exists(turnsDir))
				return Results.Json(Array.Empty<object>());

			// Newest-first by mtime, not name: swarm turn dirs are role-prefixed, so
			// ordinal name order would group by role instead of the actual timeline.
			var turns = Directory.GetDirectories(turnsDir)
				.Select(d => new DirectoryInfo(d))
				.OrderByDescending(d => d.LastWriteTimeUtc)
				.Take(1000)
				.Select(d => new { seq = d.Name, atUtc = d.LastWriteTimeUtc.ToString("o") })
				.ToList();

			return Results.Json(turns);
		});

		app.MapGet("/api/agents/{slug}/turns/{seq}", (string slug, string seq) =>
		{
			// Classic seqs are 6 digits; swarm seqs are role-prefixed (boot-000023).
			if (!validSlugs.Contains(slug) || seq.Length is 0 or > 40
				|| !seq.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
				return Results.NotFound();

			var turnDir = Path.Combine(runDir, "agents", slug, "turns", seq);
			if (!Directory.Exists(turnDir))
				return Results.NotFound();

			static string? ReadOrNull(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

			// Files are already JSON; compose them verbatim rather than reparsing.
			var request = ReadOrNull(Path.Combine(turnDir, "request.json")) ?? "null";
			var response = ReadOrNull(Path.Combine(turnDir, "response.json")) ?? "null";
			var orders = ReadOrNull(Path.Combine(turnDir, "orders.json")) ?? "null";
			var decisions = ReadOrNull(Path.Combine(turnDir, "decisions.json")) ?? "null";
			var results = ReadOrNull(Path.Combine(turnDir, "results.json")) ?? "null";
			return Results.Content($"{{\"request\":{request},\"response\":{response},\"orders\":{orders},\"decisions\":{decisions},\"results\":{results}}}", "application/json");
		});

		var historyPath = Path.Combine(runDir, "history.jsonl");

		app.MapGet("/api/history", () =>
		{
			try
			{
				if (!File.Exists(historyPath))
					return Results.Content("[]", "application/json");

				// Drop unparseable rows: older runs suffered a writer-leak bug (one
				// extra writer per failed port-bind attempt) whose concurrent appends
				// could tear lines; one bad row must not blank the whole graph.
				var lines = File.ReadLines(historyPath).TakeLast(7200).Where(l =>
				{
					try
					{
						return System.Text.Json.Nodes.JsonNode.Parse(l) != null;
					}
					catch (System.Text.Json.JsonException)
					{
						return false;
					}
				});
				return Results.Content("[" + string.Join(",", lines) + "]", "application/json");
			}
			catch
			{
				return Results.Content("[]", "application/json");
			}
		});

		await app.StartAsync();

		// Points history: one row per engine tick advance, persisted so the graph
		// survives page reloads and is analyzable after the match. Started ONLY
		// after the port bind succeeded: StartAsync throwing on a busy port used
		// to leak one immortal writer per retry (torn lines, multiplied rows).
		_ = Task.Run(async () =>
		{
			long lastTick = -1;
			while (true)
			{
				try
				{
					var game = Util.TryReadJson(Path.Combine(runDir, "state", "game.json"));
					var tick = game?["tick"]?.GetValue<long>() ?? -1;
					if (game != null && tick != lastTick)
					{
						lastTick = tick;
						var points = new System.Text.Json.Nodes.JsonObject();
						foreach (var p in game["players"] as System.Text.Json.Nodes.JsonArray ?? [])
							if (p is System.Text.Json.Nodes.JsonObject po && po["slug"]?.GetValue<string>() is string slug)
								points[slug] = (po["armyValue"]?.GetValue<long>() ?? 0) + (po["assetsValue"]?.GetValue<long>() ?? 0);

						var row = new System.Text.Json.Nodes.JsonObject
						{
							["second"] = game["second"]?.GetValue<long>() ?? 0,
							["points"] = points
						};
						File.AppendAllText(historyPath, row.ToJsonString() + "\n");
					}
				}
				catch
				{
					// state file mid-write or missing; retry next second
				}

				await Task.Delay(1000);
			}
		});
		Util.Log("orf", $"dashboard: http://0.0.0.0:{port}/");
		return app;
	}

	static string BuildLivePayload(Spec spec, string runDir, string runId)
	{
		var players = new JsonArray();
		foreach (var p in spec.Players)
		{
			// Build queues for the observer cards: current item + progress + waiting list.
			JsonArray? production = null;
			if (Util.TryReadJson(Path.Combine(runDir, "state", $"{p.Slug}.json")) is JsonObject st
				&& st["production"] is JsonArray prod)
			{
				production = [];
				foreach (var q in prod.OfType<JsonObject>())
					production.Add(new JsonObject
					{
						["queue"] = q["queue"]?.DeepClone(),
						["current"] = q["current"]?.DeepClone(),
						["queued"] = q["queued"]?.DeepClone(),
					});
			}

			// Add-on modules (advisor etc.): each publishes agents/<slug>/modules/<name>.json;
			// the dashboard renders one cadence lane per module.
			JsonArray? modules = null;
			var modulesDir = Path.Combine(runDir, "agents", p.Slug, "modules");
			if (Directory.Exists(modulesDir))
			{
				modules = [];
				foreach (var file in Directory.GetFiles(modulesDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
					if (Util.TryReadJson(file) is JsonObject module)
						modules.Add(module);
			}

			players.Add(new JsonObject
			{
				["slug"] = p.Slug,
				["display"] = p.Display,
				["provider"] = p.Provider,
				["controller"] = p.Controller,
				["model"] = p.Model,
				["faction"] = p.Faction,
				["status"] = Util.TryReadJson(Path.Combine(runDir, "agents", p.Slug, "status.json")),
				["modules"] = modules,
				["production"] = production,
			});
		}

		var payload = new JsonObject
		{
			["runId"] = runId,
			["nowUtc"] = DateTime.UtcNow.ToString("o"),
			["game"] = Util.TryReadJson(Path.Combine(runDir, "state", "game.json")),
			["players"] = players,
			["finished"] = Util.TryReadJson(Path.Combine(runDir, "result.json")),
		};

		return payload.ToJsonString();
	}
}
