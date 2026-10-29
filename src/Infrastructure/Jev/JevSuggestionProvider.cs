using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Application.Classification;
using Microsoft.Extensions.Options;

namespace Infrastructure.Jev;

/// <summary>One attempt, no fallback; transport failures are distinct from valid abstention.</summary>
public sealed class JevSuggestionProvider(HttpClient client, IOptionsMonitor<JevOptions> options, JevSuggestionConcurrency concurrency) : ICategorySuggestionProvider
{
	public bool IsAvailable => options.CurrentValue.Enabled && options.CurrentValue.InferenceEnabled && !string.IsNullOrWhiteSpace(options.CurrentValue.ApiKey);

	public async Task<CategorySuggestionResult> SuggestAsync(CategorySuggestionRequest request, CancellationToken cancellationToken)
				=> await SuggestCoreAsync(request, null, requireLegacyGate: true, cancellationToken);

	internal Task<CategorySuggestionResult> SuggestBoundAsync(CategorySuggestionRequest request,
		Func<string, CancellationToken, Task<bool>> isCurrent, CancellationToken cancellationToken)
				=> SuggestCoreAsync(request, isCurrent, requireLegacyGate: false, cancellationToken);

	internal async Task<CategorySuggestionResult> VerifySelectedBoundAsync(CategorySuggestionRequest request,
		CategorySuggestion baseline, Func<string, CancellationToken, Task<bool>> isCurrent, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		JevOptions current = options.CurrentValue;
		string? key = current.ApiKey;
		if (!current.Enabled || string.IsNullOrWhiteSpace(key))
		{
			return CategorySuggestionResult.Failed(SuggestionFailure.Disabled);
		}

		JevSelectedPathQuestions.Prepared prepared;
		try { prepared = JevSelectedPathQuestions.Prepare(request, baseline); }
		catch (ArgumentException) { return CategorySuggestionResult.Failed(SuggestionFailure.InvalidInput); }
		byte[] payload = prepared.Payload;
		if (payload.Length > CategorySuggestionPolicy.MaximumPayloadBytes)
		{
			return CategorySuggestionResult.Failed(SuggestionFailure.PayloadTooLarge);
		}

		if (!await concurrency.Gate.WaitAsync(0, ct))
		{
			return CategorySuggestionResult.Failed(SuggestionFailure.Busy);
		}

		try
		{
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
			timeout.CancelAfter(TimeSpan.FromSeconds(10));
			using HttpRequestMessage message = new(HttpMethod.Post, "https://api.typesafe.ai/v1/systemone");
			message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
			message.Content = new ByteArrayContent(payload);
			message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
			if (!await isCurrent(key, timeout.Token))
			{
				return CategorySuggestionResult.Failed(SuggestionFailure.Disabled);
			}

			JevOptions beforeSend = options.CurrentValue;
			ct.ThrowIfCancellationRequested();
			if (!beforeSend.Enabled || beforeSend.ApiKey != key)
			{
				return CategorySuggestionResult.Failed(SuggestionFailure.Disabled);
			}

			using HttpResponseMessage response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
			if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
			{
				return CategorySuggestionResult.Failed(SuggestionFailure.Authentication);
			}

			if (response.StatusCode == HttpStatusCode.TooManyRequests)
			{
				return CategorySuggestionResult.Failed(SuggestionFailure.RateLimited);
			}

			if (!response.IsSuccessStatusCode)
			{
				return CategorySuggestionResult.Failed(SuggestionFailure.Unavailable);
			}

			await response.Content.LoadIntoBufferAsync(1048576, timeout.Token);
			var answer = prepared.ParseResponse(await response.Content.ReadAsByteArrayAsync(timeout.Token));
			if (answer is null || answer.InputTokens > 9007199254740991 - baseline.Provenance.InputTokens ||
				answer.OutputTokens > 9007199254740991 - baseline.Provenance.OutputTokens)
			{
				return CategorySuggestionResult.Failed(SuggestionFailure.InvalidResponse);
			}

			ct.ThrowIfCancellationRequested();
			JevOptions latest = options.CurrentValue;
			if (!latest.Enabled || latest.ApiKey != key || !await isCurrent(key, timeout.Token))
			{
				return CategorySuggestionResult.Failed(SuggestionFailure.Disabled);
			}

			ct.ThrowIfCancellationRequested();
			var provenance = baseline.Provenance with
			{
				InputTokens = baseline.Provenance.InputTokens + answer.InputTokens,
				OutputTokens = baseline.Provenance.OutputTokens + answer.OutputTokens
			};
			return new(baseline with
			{
				CategoryId = answer.Probability >= 0.7 ? baseline.CategoryId : null,
				Path = answer.Probability >= 0.7 ? baseline.Path : [],
				AbstentionReason = answer.Probability >= 0.7 ? null : "uncertain-selected-path",
				SelectedPathSupport = answer.Probability,
				Provenance = provenance
			}, null);
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return CategorySuggestionResult.Failed(SuggestionFailure.Timeout); }
		catch (HttpRequestException) { return CategorySuggestionResult.Failed(SuggestionFailure.Unavailable); }
		catch (IOException) { return CategorySuggestionResult.Failed(SuggestionFailure.Unavailable); }
		finally { concurrency.Gate.Release(); }
	}

	private async Task<CategorySuggestionResult> SuggestCoreAsync(CategorySuggestionRequest request,
				Func<string, CancellationToken, Task<bool>>? isCurrent, bool requireLegacyGate,
				CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		JevOptions current = options.CurrentValue;
		bool enabled = current.Enabled && (!requireLegacyGate || current.InferenceEnabled);
		string? key = current.ApiKey;
		if (!enabled)
		{
			return CategorySuggestionResult.Failed(SuggestionFailure.Disabled);
		}

		if (string.IsNullOrWhiteSpace(key))
		{
			return CategorySuggestionResult.Failed(SuggestionFailure.MissingCredential);
		}

		if (request is null || request.Categories is null)
		{
			return CategorySuggestionResult.Failed(SuggestionFailure.InvalidInput);
		}
		// Snapshot the caller's collection before validation and serialization.
		request = request with { Categories = request.Categories.ToArray() };
		if (!CategorySuggestionPolicy.IsValid(request))
		{
			return CategorySuggestionResult.Failed(SuggestionFailure.InvalidInput);
		}

		byte[] payload = JevSuggestionQuestions.Serialize(request);
		if (payload.Length > CategorySuggestionPolicy.MaximumPayloadBytes)
		{
			return CategorySuggestionResult.Failed(SuggestionFailure.PayloadTooLarge);
		}

		if (!await concurrency.Gate.WaitAsync(0, cancellationToken))
		{
			return CategorySuggestionResult.Failed(SuggestionFailure.Busy);
		}

		try
		{
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(TimeSpan.FromSeconds(10));
			using HttpRequestMessage message = new(HttpMethod.Post, "https://api.typesafe.ai/v1/systemone");
			message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
			message.Content = new ByteArrayContent(payload);
			message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
			if (isCurrent is not null && !await isCurrent(key, timeout.Token))
			{
				return CategorySuggestionResult.Failed(SuggestionFailure.Disabled);
			}
			JevOptions beforeSend = options.CurrentValue;
			cancellationToken.ThrowIfCancellationRequested();
			if (!beforeSend.Enabled || (requireLegacyGate && !beforeSend.InferenceEnabled) || beforeSend.ApiKey != key)
			{
				return CategorySuggestionResult.Failed(SuggestionFailure.Disabled);
			}
			using HttpResponseMessage response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
			if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
			{
				return CategorySuggestionResult.Failed(SuggestionFailure.Authentication);
			}

			if (response.StatusCode == HttpStatusCode.TooManyRequests)
			{
				return CategorySuggestionResult.Failed(SuggestionFailure.RateLimited);
			}

			if (!response.IsSuccessStatusCode)
			{
				return CategorySuggestionResult.Failed(SuggestionFailure.Unavailable);
			}

			await response.Content.LoadIntoBufferAsync(1048576, timeout.Token);
			using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(timeout.Token));
			JsonElement root = document.RootElement;
			JsonElement answers = root.GetProperty("answers");
			JsonElement fits = answers.GetProperty("fits");
			int expectedChoices = 1 + request.Categories.Count(x => request.Categories.Any(y => y.ParentId == x.Id));
			if (answers.ValueKind != JsonValueKind.Object || answers.EnumerateObject().Count() != expectedChoices + 1 ||
				fits.GetProperty("type").GetString() != "noul")
			{
				return CategorySuggestionResult.Failed(SuggestionFailure.InvalidResponse);
			}
			Dictionary<string, CategoryChoiceJudgment> choices = new(StringComparer.Ordinal);
			foreach (JsonProperty answer in answers.EnumerateObject())
			{
				if (answer.Name == "fits")
				{
					continue;
				}

				JsonElement choice = answer.Value;
				if (choice.GetProperty("type").GetString() != "choice")
				{
					return CategorySuggestionResult.Failed(SuggestionFailure.InvalidResponse);
				}

				Dictionary<string, double> probabilities = choice.GetProperty("probabilities").EnumerateObject()
					.ToDictionary(property => property.Name, property => property.Value.GetDouble(), StringComparer.Ordinal);
				choices.Add(answer.Name, new(choice.GetProperty("choice").GetString()!, choice.GetProperty("confidence").GetDouble(), probabilities));
			}
			CategoryHierarchyJudgment judgment = new(root.GetProperty("model").GetString()!, request.TaxonomyRevision,
					fits.GetProperty("noul").GetDouble(), choices,
	root.GetProperty("usage").GetProperty("input_tokens").GetInt64(), root.GetProperty("usage").GetProperty("output_tokens").GetInt64());
			JevOptions latest = options.CurrentValue;
			cancellationToken.ThrowIfCancellationRequested();
			if (!latest.Enabled || (requireLegacyGate && !latest.InferenceEnabled) || latest.ApiKey != key)
			{
				return CategorySuggestionResult.Failed(SuggestionFailure.Disabled);
			}

			return CategorySuggestionPolicy.Decide(request, judgment, "jev");
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return CategorySuggestionResult.Failed(SuggestionFailure.Timeout); }
		catch (HttpRequestException) { return CategorySuggestionResult.Failed(SuggestionFailure.Unavailable); }
		catch (IOException) { return CategorySuggestionResult.Failed(SuggestionFailure.Unavailable); }
		catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
		{ return CategorySuggestionResult.Failed(SuggestionFailure.InvalidResponse); }
		finally { concurrency.Gate.Release(); }
	}
}

/// <summary>Singleton across typed-client instances; excess work is rejected rather than queued.</summary>
public sealed class JevSuggestionConcurrency : IDisposable
{
	internal SemaphoreSlim Gate { get; } = new(1, 1);
	public void Dispose() => Gate.Dispose();
}
