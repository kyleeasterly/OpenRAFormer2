using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Orf;

/// <summary>Minimal OpenAI-compatible chat completions client over raw HttpClient.</summary>
public sealed class LlmClient(string baseUrl, string apiKey, int timeoutSeconds = 120, Action<string>? onRetry = null)
{
	static readonly HttpClient Http = CreateHttp();

	// HttpClient sends no User-Agent by default, and Nous's Cloudflare front now
	// answers UA-less requests with a 403 HTML challenge (observed 2026-10-11).
	static HttpClient CreateHttp()
	{
		var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
		http.DefaultRequestHeaders.UserAgent.ParseAdd("orf/1.0 (OpenRAFormer2)");
		return http;
	}

	readonly TimeSpan requestTimeout = TimeSpan.FromSeconds(timeoutSeconds);
	const int MaxRetries = 3;
	const int MaxRateLimitRetries = 8;

	readonly string endpoint = baseUrl.TrimEnd('/') + "/chat/completions";

	/// <summary>Total 429 responses seen, for status displays.</summary>
	public long RateLimited429s { get; private set; }

	/// <summary>
	/// Seconds the caller should wait before its next request so the key's token
	/// budget lasts, derived from the provider's x-ratelimit-* headers on the last
	/// success. Zero when the provider sends no such headers.
	/// </summary>
	public double PaceDelaySeconds { get; private set; }

	/// <summary>Human-readable budget snapshot behind PaceDelaySeconds, for status.</summary>
	public string? PaceNote { get; private set; }

	/// <summary>
	/// POSTs the payload; retries on 429/5xx/network errors and returns the raw
	/// response body. 429s get more attempts with longer, jittered waits — dozens
	/// of agents share one API key, and synchronized short backoffs just re-collide
	/// (observed 2026-08-22: a multi-minute fleet-wide stall against Nous).
	/// </summary>
	public async Task<string> ChatAsync(JsonObject payload, CancellationToken ct)
	{
		var body = payload.ToJsonString();
		Exception? last = null;
		var rateLimited = 0;

		for (var attempt = 0; attempt <= MaxRetries + rateLimited && rateLimited <= MaxRateLimitRetries; attempt++)
		{
			if (attempt > 0)
			{
				var delay = last is RateLimitException
					? TimeSpan.FromSeconds(5 * rateLimited + Random.Shared.NextDouble() * 10)
					: TimeSpan.FromSeconds(Math.Pow(2, attempt - rateLimited));
				onRetry?.Invoke($"attempt {attempt} failed ({Truncate(last?.Message ?? "?", 120)}); retrying in {delay.TotalSeconds:0.0}s");
				await Task.Delay(delay, ct);
			}

			using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			timeoutCts.CancelAfter(requestTimeout);

			try
			{
				using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
				request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
				request.Content = new StringContent(body, Encoding.UTF8, "application/json");

				using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token);
				var text = await response.Content.ReadAsStringAsync(timeoutCts.Token);

				if (response.IsSuccessStatusCode)
				{
					// Some providers (observed: OpenRouter/Stealth 2026-08-23) return
					// HTTP 200 with an empty message and native_finish_reason
					// "network_error" when their upstream flakes. Treating that as a
					// completed turn left an agent braindead for a whole match —
					// retry it like any other transport failure.
					if (IsEmptyProviderGlitch(text))
					{
						last = new HttpRequestException($"provider returned empty message (upstream network_error) from {endpoint}");
						continue;
					}

					UpdatePace(response);
					return text;
				}

				if (response.StatusCode == HttpStatusCode.TooManyRequests)
				{
					rateLimited++;
					RateLimited429s++;
					last = new RateLimitException($"429 from {endpoint}: {Truncate(text, 200)}");
					continue;
				}

				var error = new HttpRequestException($"{(int)response.StatusCode} {response.StatusCode} from {endpoint}: {Truncate(text, 500)}", null, response.StatusCode);
				if ((int)response.StatusCode < 500)
					throw error;

				last = error;
			}
			catch (HttpRequestException ex) when (ex.StatusCode is not { } code || (int)code >= 500)
			{
				// Transport failures and 5xx retry; 4xx propagate (the request itself is wrong).
				last = ex;
			}
			catch (OperationCanceledException) when (!ct.IsCancellationRequested)
			{
				last = new TimeoutException($"Request to {endpoint} timed out after {requestTimeout.TotalSeconds}s");
			}
		}

		throw last ?? new HttpRequestException($"Request to {endpoint} failed");
	}

	/// <summary>True when a 200 response carries no usable message: null/empty content
	/// AND no tool calls (a legit "pass" always has at least text). Unparseable bodies
	/// are NOT treated as glitches — downstream error handling reports those better.</summary>
	static bool IsEmptyProviderGlitch(string body)
	{
		try
		{
			if (JsonNode.Parse(body) is not JsonObject obj)
				return false;

			if (obj["choices"]?[0] is not JsonObject choice || choice["message"] is not JsonObject message)
				return false;

			var content = message["content"]?.GetValue<string>();
			var hasToolCalls = message["tool_calls"] is JsonArray { Count: > 0 };
			return string.IsNullOrEmpty(content) && !hasToolCalls;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// Paces lanes against the key's token windows. Nous (2026-10-11) gives a free
	/// key 720k tokens/min and 30.24M tokens/hour in fixed windows; three v7 lanes
	/// at ~25k-token prompts burn through the minute window in seconds and would
	/// drain the hour in ~40 minutes, freezing every lane mid-match. Every lane on
	/// a key sees the same key-wide numbers, so all of them slow down together.
	/// Hour window: spread what remains evenly until reset — when the burn is ahead
	/// of that pace (ratio f below 1), wait proportionally longer. Minute window:
	/// when nearly spent, wait for its reset instead of colliding into a 429.
	/// </summary>
	void UpdatePace(HttpResponseMessage response)
	{
		var limitHour = Header(response, "x-ratelimit-limit-tokens-1h");
		var remainingHour = Header(response, "x-ratelimit-remaining-tokens-1h");
		var resetHour = Header(response, "x-ratelimit-reset-tokens-1h");
		var limitMinute = Header(response, "x-ratelimit-limit-tokens");
		var remainingMinute = Header(response, "x-ratelimit-remaining-tokens");
		var resetMinute = Header(response, "x-ratelimit-reset-tokens");

		double delay = 0;
		var notes = new List<string>();

		if (limitHour is > 0 && remainingHour is { } rh && resetHour is > 1)
		{
			var pace = rh / resetHour.Value / (limitHour.Value / 3600.0);
			if (pace < 1)
				delay = Math.Max(delay, Math.Min(90, 30 * (1 / Math.Max(pace, 0.05) - 1)));
			notes.Add($"hour {rh / 1e6:0.0}M left, reset {resetHour / 60:0}m, pace {pace:0.00}");
		}

		if (limitMinute is > 0 && remainingMinute is { } rm && resetMinute is { } resetM)
		{
			if (rm < limitMinute.Value * 0.15)
				delay = Math.Max(delay, Math.Min(60, resetM + Random.Shared.NextDouble() * 3));
			notes.Add($"minute {rm / 1e3:0}k left");
		}

		PaceDelaySeconds = delay;
		PaceNote = notes.Count > 0 ? string.Join(", ", notes) + (delay > 0 ? $" -> wait {delay:0.0}s" : "") : null;
	}

	static double? Header(HttpResponseMessage response, string name) =>
		response.Headers.TryGetValues(name, out var values)
			&& double.TryParse(values.FirstOrDefault(), System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out var v)
			? v
			: null;

	static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>Distinguishes 429s so the retry loop can wait longer with jitter.</summary>
public sealed class RateLimitException(string message) : Exception(message);
