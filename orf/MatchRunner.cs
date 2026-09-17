using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Orf;

/// <summary>Full match run: run dir + match.json, Xvfb/game/ffmpeg, agent loops, dashboard, teardown.</summary>
public sealed class MatchRunner(Spec spec, string specPath)
{
	readonly string specDir = Path.GetDirectoryName(Path.GetFullPath(specPath)) ?? ".";

	Process? gameProcess;
	Process? ffmpegProcess;
	Process? xvfbProcess;
	Process? audioFfmpegProcess;
	Task? audioPumpTask;
	string? alsoftConfPath;
	string? displayLockPath;
	int display;

	string DisplayStr => $":{display}";

	public async Task<int> RunAsync(int? portOverride, bool noGame, bool noWeb, CancellationToken ct)
	{
		var repoRoot = Util.FindRepoRoot();
		var runId = $"{DateTime.Now:yyyyMMdd-HHmmss}-{spec.Name}";
		var runDir = Path.Combine(repoRoot, "runs", runId);
		var runStartUtc = DateTime.UtcNow;

		// The spec's display/port are starting points, not reservations: probe upward
		// until something is free so parallel matches never need per-spec editing.
		if (!noGame && !AcquireAnyDisplayLock())
			return 1;

		WebApplication? webApp = null;
		var port = portOverride ?? spec.Web.Port;
		if (!noWeb)
		{
			for (var attempt = 0; ; attempt++, port++)
			{
				try
				{
					webApp = await WebServer.StartAsync(spec, runDir, runId, port);
					break;
				}
				catch (IOException) when (attempt < 15)
				{
					Util.Log("orf", $"port {port} is busy, trying {port + 1}");
				}
			}
		}

		CreateRunDir(runDir, runId, noGame ? null : display, noWeb ? null : port);
		Util.Log("orf", $"run dir: {runDir}");

		var logStdout = Task.CompletedTask;
		var logStderr = Task.CompletedTask;

		try
		{
			if (!noGame)
			{
				EnsureXvfb(spec);

				// Audio: game renders sound via openal-soft's wave backend into a FIFO;
				// ffmpeg (the reader) must be listening before the game opens the write
				// end. Both block on open until the pair meet — that's fine.
				PrepareAudioFifo(runDir);
				audioFfmpegProcess = SpawnAudioFfmpeg(runDir);

				(gameProcess, logStdout, logStderr) = SpawnGame(repoRoot, runDir, spec);
				ffmpegProcess = SpawnFfmpeg(runDir, spec);
			}
			else
			{
				Util.Log("orf", "--no-game: skipping Xvfb/game/ffmpeg spawn");
			}

			if (spec.HasHumans)
				Util.Log("orf", $"human slot(s) open — lobby holds until they join and Ready up. " +
					$"Connect via Multiplayer → Direct IP: {LanAddress()}:{spec.ListenPort}" +
					(string.IsNullOrEmpty(spec.Password) ? "" : $" (password: {spec.Password})"));

			// Agents start once the engine begins exporting state.
			var stopPath = Path.Combine(runDir, "stop");
			var gameJson = Path.Combine(runDir, "state", "game.json");
			while (!File.Exists(gameJson))
			{
				ct.ThrowIfCancellationRequested();
				if (File.Exists(stopPath))
				{
					Util.Log("orf", "stop file detected, shutting down");
					return 0;
				}

				if (gameProcess != null && gameProcess.HasExited)
				{
					Util.Log("orf", $"game process exited (code {gameProcess.ExitCode}) before writing state/game.json — see {runDir}/engine.err");
					return 1;
				}

				Util.Log("orf", "waiting for state/game.json…");
				await Task.Delay(1000, ct);
			}

			Util.Log("orf", "game state detected, starting agent loops");

			using var agentCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			var agentTasks = new List<Task>();
			foreach (var (p, i) in spec.Players.Select((p, i) => (p, i)))
			{
				if (p.IsHuman)
					continue;

				if (p.Controller == "jev")
					agentTasks.Add(new JevLoop(runDir, spec, p).RunAsync(agentCts.Token));
				else if (p.IsSwarm)
				{
					// Swarm player: the coordinator owns the whole lifecycle — bootstrap
					// commander, handoff to specialists, dynamic scaling, shared strategist.
					agentTasks.Add(new SwarmCoordinator(runDir, spec, p, i, specDir).RunAsync(agentCts.Token));
				}
				else
					agentTasks.Add(new AgentLoop(runDir, spec, p, i, specDir).RunAsync(agentCts.Token));
			}

			// Wait for game over (result.json), a stop request, or game process exit.
			var resultPath = Path.Combine(runDir, "result.json");
			while (!File.Exists(resultPath))
			{
				ct.ThrowIfCancellationRequested();
				if (File.Exists(stopPath))
				{
					Util.Log("orf", "stop file detected, ending match");
					break;
				}

				if (gameProcess != null && gameProcess.HasExited)
				{
					Util.Log("orf", $"game process exited (code {gameProcess.ExitCode})");
					break;
				}

				await Task.Delay(1000, ct);
			}

			agentCts.Cancel();
			await Task.WhenAll(agentTasks).WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None).ContinueWith(_ => { });

			// The engine exits itself ~10s after game over (end screen grace); wait
			// for that so the replay is finalized and unlocked before archiving.
			// ffmpeg keeps rolling through the grace so the stream shows the ending.
			if (gameProcess != null && !gameProcess.HasExited)
			{
				Util.Log("orf", "waiting for engine to exit and finalize the replay…");
				await Task.WhenAny(gameProcess.WaitForExitAsync(CancellationToken.None), Task.Delay(TimeSpan.FromSeconds(25)));
				if (!gameProcess.HasExited)
				{
					Util.Log("orf", "engine still running after grace period, killing it");
					try { gameProcess.Kill(entireProcessTree: true); } catch { }
				}
			}

			StopFfmpeg();
			CopyReplay(runDir, runStartUtc);
			PrintSummary(runDir);
			return 0;
		}
		catch (OperationCanceledException)
		{
			Util.Log("orf", "interrupted, shutting down");
			return 130;
		}
		finally
		{
			KillChildren();
			await Task.WhenAny(Task.WhenAll(logStdout, logStderr), Task.Delay(3000));
			if (webApp != null)
				await webApp.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token).ContinueWith(_ => { });
		}
	}

	/// <summary>Best-effort LAN IPv4 for "join at this address" logs (no packets sent).</summary>
	static string LanAddress()
	{
		try
		{
			using var socket = new System.Net.Sockets.Socket(
				System.Net.Sockets.AddressFamily.InterNetwork,
				System.Net.Sockets.SocketType.Dgram,
				System.Net.Sockets.ProtocolType.Udp);
			socket.Connect("8.8.8.8", 53);
			return ((System.Net.IPEndPoint)socket.LocalEndPoint!).Address.ToString();
		}
		catch
		{
			return "<this-machine>";
		}
	}

	void CreateRunDir(string runDir, string runId, int? allocatedDisplay, int? allocatedPort)
	{
		Directory.CreateDirectory(Path.Combine(runDir, "state"));
		Util.WriteAtomic(Path.Combine(runDir, "spec.yaml"), File.ReadAllText(specPath));
		foreach (var p in spec.Players.Where(p => !p.IsHuman))
		{
			Directory.CreateDirectory(Path.Combine(runDir, "orders", p.Slug, "inbox"));
			Directory.CreateDirectory(Path.Combine(runDir, "orders", p.Slug, "results"));
			Directory.CreateDirectory(Path.Combine(runDir, "agents", p.Slug, "turns"));
		}

		var players = new JsonArray();
		foreach (var p in spec.Players)
			players.Add(new JsonObject
			{
				["slug"] = p.Slug,
				["display"] = p.Display,
				["bot"] = p.IsHuman ? "human" : "llm",
				["controller"] = p.Controller,
				["faction"] = p.Faction,
				["spawn"] = p.Spawn,
				["team"] = p.Team,
				["autoManage"] = p.Controller != "jev",
			});

		var match = new JsonObject
		{
			["runId"] = runId,
			["map"] = spec.Map,
			["stateIntervalTicks"] = spec.StateIntervalTicks,
			["options"] = new JsonObject { ["gamespeed"] = "default" },
			["display"] = allocatedDisplay,
			["webPort"] = allocatedPort,
			["players"] = players,
		};

		if (spec.HasHumans)
		{
			match["listenPort"] = spec.ListenPort;
			match["serverName"] = spec.ServerName ?? $"OpenRAFormer {spec.Name}";
			if (!string.IsNullOrEmpty(spec.Password))
				match["password"] = spec.Password;
		}

		Util.WriteAtomic(Path.Combine(runDir, "match.json"), match.ToJsonString(Util.Indented));
	}

	/// <summary>
	/// Per-run engine support dir so parallel game processes don't fight over
	/// ~/.config/openra (Logs, Replays, settings). Game content is shared read-only
	/// via symlink; replays land under the run itself.
	/// </summary>
	static string CreateSupportDir(string runDir)
	{
		var supportDir = Path.Combine(runDir, "support");
		Directory.CreateDirectory(supportDir);

		var sharedContent = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "openra", "Content");
		var contentLink = Path.Combine(supportDir, "Content");
		if (Directory.Exists(sharedContent) && !Directory.Exists(contentLink) && !File.Exists(contentLink))
			File.CreateSymbolicLink(contentLink, sharedContent);

		return supportDir;
	}

	void EnsureXvfb(Spec spec)
	{
		// We hold the display lock, so anything already on this display is debris
		// from a dead run — sweep it and start fresh.
		var socket = $"/tmp/.X11-unix/X{display}";
		if (File.Exists(socket))
		{
			Util.Log("orf", $"stale X server artifacts on {DisplayStr}, sweeping");
			SweepDisplay(display);
			for (var i = 0; i < 20 && File.Exists(socket); i++)
				Thread.Sleep(250);
		}

		Util.Log("orf", $"starting Xvfb on {DisplayStr}");
		var psi = new ProcessStartInfo("Xvfb")
		{
			UseShellExecute = false,
		};
		psi.ArgumentList.Add(DisplayStr);
		psi.ArgumentList.Add("-screen");
		psi.ArgumentList.Add("0");
		psi.ArgumentList.Add($"{spec.Width}x{spec.Height}x24");
		psi.ArgumentList.Add("-nolisten");
		psi.ArgumentList.Add("tcp");

		xvfbProcess = Process.Start(psi);

		// Give the server a moment to create the socket.
		for (var i = 0; i < 20 && !File.Exists(socket); i++)
			Thread.Sleep(250);
	}

	/// <summary>
	/// One orf per X display. The lock file holds the owner's PID; a lock whose owner
	/// is dead means the previous run crashed or was killed — take over and sweep.
	/// Probes upward from the spec's display so parallel launches self-allocate.
	/// </summary>
	bool AcquireAnyDisplayLock()
	{
		for (var candidate = spec.DisplayNumber; candidate < spec.DisplayNumber + 16; candidate++)
		{
			var lockPath = $"/tmp/orf-display-{candidate}.lock";
			if (File.Exists(lockPath)
				&& int.TryParse(File.ReadAllText(lockPath).Trim(), out var ownerPid)
				&& IsAlive(ownerPid))
			{
				Util.Log("orf", $"display :{candidate} is in use by orf pid {ownerPid}, trying :{candidate + 1}");
				continue;
			}

			File.WriteAllText(lockPath, Environment.ProcessId.ToString());
			displayLockPath = lockPath;
			display = candidate;
			return true;
		}

		Util.Log("orf", $"no free display in :{spec.DisplayNumber}..:{spec.DisplayNumber + 15}");
		return false;
	}

	static bool IsAlive(int pid)
	{
		try
		{
			return !Process.GetProcessById(pid).HasExited;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// Kill leftover match processes bound to our display: the Xvfb serving it, any
	/// ffmpeg grabbing it, and any game process whose DISPLAY env points at it.
	/// Only touches processes we can read (same user).
	/// </summary>
	static void SweepDisplay(int display)
	{
		foreach (var procDir in Directory.GetDirectories("/proc"))
		{
			if (!int.TryParse(Path.GetFileName(procDir), out var pid) || pid == Environment.ProcessId)
				continue;

			string cmdline, environ;
			try
			{
				cmdline = File.ReadAllText(Path.Combine(procDir, "cmdline")).Replace('\0', ' ');
				environ = File.ReadAllText(Path.Combine(procDir, "environ")).Replace('\0', '\n');
			}
			catch
			{
				continue; // gone, or another user's process
			}

			var isOurXvfb = cmdline.StartsWith("Xvfb ", StringComparison.Ordinal) && cmdline.Contains($" :{display} ");
			var isOurFfmpeg = cmdline.Contains("x11grab") && cmdline.Contains($"-i :{display} ");
			var isOurGame = cmdline.Contains("bin/OpenRA.dll") && environ.Contains($"\nDISPLAY=:{display}\n");
			if (!isOurXvfb && !isOurFfmpeg && !isOurGame)
				continue;

			Util.Log("orf", $"sweeping stale process {pid}: {cmdline[..Math.Min(80, cmdline.Length)]}");
			try
			{
				var p = Process.GetProcessById(pid);
				p.Kill();
				if (!p.WaitForExit(3000))
					Util.Log("orf", $"pid {pid} did not exit after kill");
			}
			catch
			{
			}
		}
	}

	(Process Game, Task LogOut, Task LogErr) SpawnGame(string repoRoot, string runDir, Spec spec)
	{
		Util.Log("orf", "starting game process");
		var psi = new ProcessStartInfo("dotnet")
		{
			WorkingDirectory = repoRoot,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		psi.ArgumentList.Add("bin/OpenRA.dll");
		psi.ArgumentList.Add("Engine.EngineDir=..");
		psi.ArgumentList.Add($"Engine.SupportDir={CreateSupportDir(runDir)}");
		psi.ArgumentList.Add("Game.Mod=cnc");
		psi.ArgumentList.Add("Graphics.Mode=Windowed");
		psi.ArgumentList.Add($"Graphics.WindowedSize={spec.Width},{spec.Height}");

		psi.Environment["ORF_RUN_DIR"] = runDir;
		psi.Environment["DISPLAY"] = DisplayStr;
		psi.Environment["LIBGL_ALWAYS_SOFTWARE"] = "1";
		psi.Environment["GALLIUM_DRIVER"] = "llvmpipe";

		if (alsoftConfPath != null)
		{
			// Route openal-soft to the wave-writer backend (no sound server needed).
			psi.Environment["ALSOFT_CONF"] = alsoftConfPath;
			psi.ArgumentList.Add("Sound.MusicVolume=0");
		}

		var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start game process");
		var logOut = PumpAsync(process.StandardOutput.BaseStream, Path.Combine(runDir, "engine.log"));
		var logErr = PumpAsync(process.StandardError.BaseStream, Path.Combine(runDir, "engine.err"));
		return (process, logOut, logErr);
	}

	static async Task PumpAsync(Stream source, string path)
	{
		try
		{
			await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
			await source.CopyToAsync(file);
		}
		catch
		{
			// process torn down
		}
	}

	/// <summary>Creates the audio FIFO and the alsoft.conf pointing openal-soft's wave backend at it.</summary>
	void PrepareAudioFifo(string runDir)
	{
		try
		{
			var fifo = Path.Combine(runDir, "audio.fifo");
			if (File.Exists(fifo))
				File.Delete(fifo);

			var mkfifo = Process.Start(new ProcessStartInfo("mkfifo", fifo) { UseShellExecute = false });
			mkfifo?.WaitForExit(5000);
			if (mkfifo == null || mkfifo.ExitCode != 0)
			{
				Util.Log("orf", "mkfifo failed — running without game audio");
				return;
			}

			alsoftConfPath = Path.Combine(runDir, "alsoft.conf");
			File.WriteAllText(alsoftConfPath, $"[general]\ndrivers = wave\nfrequency = 44100\n\n[wave]\nfile = {fifo}\n");
		}
		catch (Exception ex)
		{
			Util.Log("orf", $"audio fifo setup failed ({ex.Message}) — running without game audio");
			alsoftConfPath = null;
		}
	}

	/// <summary>Reads raw WAV from the FIFO, encodes to MP3, and feeds the dashboard broadcaster.</summary>
	Process? SpawnAudioFfmpeg(string runDir)
	{
		if (alsoftConfPath == null)
			return null;

		Util.Log("orf", "starting audio encoder (fifo → mp3 broadcast)");
		var psi = new ProcessStartInfo("ffmpeg")
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = false,
		};

		foreach (var arg in new[]
		{
			"-hide_banner", "-loglevel", "error",
			"-f", "wav", "-i", Path.Combine(runDir, "audio.fifo"),

			// God-view listener sits mid-map, so distant combat mixes quiet;
			// adaptive normalization brings it up without clipping close fights.
			"-af", "dynaudnorm",
			"-c:a", "libmp3lame", "-b:a", "128k",
			"-f", "mp3", "-",
		})
			psi.ArgumentList.Add(arg);

		var process = Process.Start(psi);
		if (process == null)
		{
			Util.Log("orf", "audio ffmpeg failed to start — running without game audio");
			return null;
		}

		var broadcaster = new AudioBroadcaster();
		AudioBroadcaster.Instance = broadcaster;
		audioPumpTask = broadcaster.PumpAsync(process.StandardOutput.BaseStream, CancellationToken.None);
		return process;
	}

	Process SpawnFfmpeg(string runDir, Spec spec)
	{
		Util.Log("orf", "starting ffmpeg x11grab");
		var psi = new ProcessStartInfo("ffmpeg")
		{
			UseShellExecute = false,
			RedirectStandardInput = true, // lets us send 'q' for a clean stop
		};

		foreach (var arg in new[]
		{
			"-hide_banner", "-loglevel", "error", "-y",
			"-f", "x11grab",
			"-video_size", $"{spec.Width}x{spec.Height}",
			"-framerate", spec.Stream.Fps.ToString(),
			"-i", DisplayStr,
			"-vf", $"scale={spec.Stream.Scale}:-1",
			"-q:v", spec.Stream.Quality.ToString(),
			"-update", "1",
			Path.Combine(runDir, "live.jpg"),
		})
			psi.ArgumentList.Add(arg);

		return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg");
	}

	void StopFfmpeg()
	{
		if (ffmpegProcess == null || ffmpegProcess.HasExited)
			return;

		try
		{
			// SIGINT for a clean stop; escalate if it ignores us.
			Process.Start("kill", ["-INT", ffmpegProcess.Id.ToString()])?.WaitForExit(2000);
			if (!ffmpegProcess.WaitForExit(3000))
				ffmpegProcess.Kill(entireProcessTree: true);
		}
		catch
		{
		}
	}

	static void CopyReplay(string runDir, DateTime runStartUtc)
	{
		try
		{
			// Replays land in the per-run support dir now; fall back to the shared
			// location for runs that predate it.
			var replaysDir = Path.Combine(runDir, "support", "Replays");
			if (!Directory.Exists(replaysDir))
				replaysDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "openra", "Replays");
			if (!Directory.Exists(replaysDir))
				return;

			var newest = Directory.GetFiles(replaysDir, "*.orarep", SearchOption.AllDirectories)
				.Select(f => new FileInfo(f))
				.Where(f => f.LastWriteTimeUtc >= runStartUtc)
				.MaxBy(f => f.LastWriteTimeUtc);

			if (newest != null)
			{
				File.Copy(newest.FullName, Path.Combine(runDir, "replay.orarep"), overwrite: true);
				Util.Log("orf", $"archived replay: {newest.Name}");
			}
		}
		catch (Exception ex)
		{
			Util.Log("orf", $"replay archive failed: {ex.Message}");
		}
	}

	void PrintSummary(string runDir)
	{
		Util.Log("orf", "=== match summary ===");
		if (Util.TryReadJson(Path.Combine(runDir, "result.json")) is JsonObject result)
		{
			Util.Log("orf", $"finished at tick {result["finishedAtTick"]} ({result["second"]}s)");
			foreach (var p in result["players"] as JsonArray ?? [])
				Util.Log("orf", $"  {p?["slug"]}: {p?["winState"]}");
		}
		else
		{
			Util.Log("orf", "no result.json (game did not finish normally)");
		}

		Util.Log("orf", $"artifacts: {runDir}");
	}

	void KillChildren()
	{
		foreach (var p in new[] { ffmpegProcess, audioFfmpegProcess, gameProcess, xvfbProcess })
		{
			try
			{
				if (p != null && !p.HasExited)
					p.Kill(entireProcessTree: true);
			}
			catch
			{
			}
		}

		AudioBroadcaster.Instance = null;

		try
		{
			if (displayLockPath != null)
				File.Delete(displayLockPath);
		}
		catch
		{
		}
	}
}
