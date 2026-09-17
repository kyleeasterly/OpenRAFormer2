using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Orf;

/// <summary>Native System One HTTP transport. The scheduler retries against fresh state.</summary>
public sealed class JevClient
{
	static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
	readonly HttpClient http;
	readonly string endpoint;
	readonly string apiKey;
	readonly int timeoutMilliseconds;

	public JevClient(string baseUrl, string apiKey, int timeoutMilliseconds, HttpClient? http = null)
	{
		this.http = http ?? SharedHttp;
		endpoint = baseUrl.TrimEnd('/') + "/systemone";
		this.apiKey = apiKey;
		this.timeoutMilliseconds = timeoutMilliseconds;
	}

	public async Task<JsonObject> EvaluateAsync(JsonObject request, CancellationToken ct)
	{
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
		deadline.CancelAfter(timeoutMilliseconds);
		using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
		message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
		message.Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
		using var response = await http.SendAsync(message, deadline.Token);
		if (!response.IsSuccessStatusCode)
		{
			var retryAfter = response.Headers.RetryAfter?.Delta
				?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
			string? errorType = null;
			try
			{
				var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(deadline.Token)) as JsonObject;
				if (body?["detail"] is JsonObject detail && detail["error_type"]?.ToString() == "max_tokens_exceeded")
					errorType = "max_tokens_exceeded";
			}
			catch (System.Text.Json.JsonException) { }
			throw new JevApiException((int)response.StatusCode, retryAfter, errorType);
		}

		return JsonNode.Parse(await response.Content.ReadAsStringAsync(deadline.Token)) as JsonObject
			?? throw new InvalidDataException("TypeSafe returned a non-object response");
	}

	/// <summary>Schema guarantees do not replace checking the transport contract.</summary>
	public static void ValidateAnswers(JsonObject questions, JsonObject response)
	{
		if (response["answers"] is not JsonObject answers)
			throw new InvalidDataException("TypeSafe response has no answers");
		foreach (var (id, question) in questions)
		{
			var type = question?["type"]?.GetValue<string>();
			if (answers[id] is not JsonObject answer || answer["type"]?.GetValue<string>() != type)
				throw new InvalidDataException($"Missing or mismatched TypeSafe answer: {id}");
			if (type == "noul")
			{
				Probability(answer["noul"]);
				continue;
			}

			Probability(answer["confidence"]);
			if (answer["probabilities"] is not JsonObject probabilities || probabilities.Count == 0)
				throw new InvalidDataException($"Missing probabilities: {id}");
			var sum = probabilities.Sum(kv => Probability(kv.Value));
			if (Math.Abs(sum - 1) > 0.02)
				throw new InvalidDataException($"Probabilities do not sum to one: {id}");
			if (type == "choice")
			{
				var criteria = (JsonObject)question!["criteria"]!;
				var choice = answer["choice"]?.GetValue<string>() ?? "";
				if (!criteria.ContainsKey(choice) || !criteria.Select(kv => kv.Key).ToHashSet().SetEquals(probabilities.Select(kv => kv.Key)))
					throw new InvalidDataException($"TypeSafe returned an unknown choice: {id}");
			}
			else if (type == "score")
			{
				var count = ((JsonArray)question!["criteria"]!).Count;
				var score = JevPolicy.Real(answer["score"]);
				if (!double.IsFinite(score) || score < 0 || score > count - 1)
					throw new InvalidDataException($"TypeSafe returned an invalid score: {id}");
			}
		}
	}

	static double Probability(JsonNode? node)
	{
		var value = JevPolicy.Real(node);
		if (!double.IsFinite(value) || value < 0 || value > 1)
			throw new InvalidDataException("TypeSafe returned an invalid probability");
		return value;
	}
}

public sealed class JevApiException(int status, TimeSpan? retryAfter, string? errorType = null)
	: Exception($"TypeSafe HTTP {status}" + (errorType == null ? "" : ": " + errorType))
{
	public int Status { get; } = status;
	public string? ErrorType { get; } = errorType;
	public TimeSpan? RetryAfter { get; } = retryAfter;
	public bool Retryable => Status is 408 or 429 or >= 500;
}
