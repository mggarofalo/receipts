using System.Net;
using System.Net.Http.Headers;
using Application.Classification;
using Microsoft.Extensions.Options;

namespace Infrastructure.Jev;

/// <summary>
/// One attempt per stage, no fallback; transport failures are distinct from valid abstention. Each send is separately
/// authorized by the caller's durable claim, which <c>isCurrent</c> rechecks immediately before sending.
/// </summary>
public sealed class JevSuggestionProvider(HttpClient client, IOptionsMonitor<JevOptions> options, JevSuggestionConcurrency concurrency)
{
	private static readonly Uri Endpoint = new("https://api.typesafe.ai/v1/systemone");

	internal Task<CategorySuggestionResult> SuggestBoundAsync(CategorySuggestionRequest request,
		Func<string, CancellationToken, Task<bool>> isCurrent, CancellationToken ct)
	{
		if (Snapshot(request) is not { } snapshot) { return Task.FromResult(CategorySuggestionResult.Failed(SuggestionFailure.InvalidInput)); }

		return SendAsync(JevTwoStageQuestions.Stage1(snapshot), isCurrent,
			body => JevTwoStageQuestions.ParseStage1(body, snapshot) is { } judgment
				? Billed(CategorySuggestionPolicy.DecideStage1(snapshot, judgment, "jev"), judgment)
				: CategorySuggestionResult.Failed(SuggestionFailure.InvalidResponse), ct);
	}

	internal Task<CategorySuggestionResult> RefineBoundAsync(CategorySuggestionRequest request, CategorySuggestion baseline,
		Func<string, CancellationToken, Task<bool>> isCurrent, CancellationToken ct)
	{
		if (Snapshot(request) is not { } snapshot || !CategorySuggestionPolicy.IsBaseline(snapshot, baseline))
		{
			return Task.FromResult(CategorySuggestionResult.Failed(SuggestionFailure.InvalidInput));
		}

		IReadOnlyList<string> roots = CategorySuggestionPolicy.Stage2RootIds(baseline);
		return SendAsync(JevTwoStageQuestions.Stage2(snapshot, roots), isCurrent,
			body => JevTwoStageQuestions.ParseStage2(body, snapshot, roots) is { } judgment
				? Billed(CategorySuggestionPolicy.DecideStage2(snapshot, baseline, judgment), judgment)
				: CategorySuggestionResult.Failed(SuggestionFailure.InvalidResponse), ct);
	}

	// A parsed response was billed whatever the policy decides, so its usage travels with every outcome.
	private static CategorySuggestionResult Billed(CategorySuggestionResult result, CategoryStageJudgment judgment) =>
		result with { Usage = new(judgment.InputTokens, judgment.OutputTokens) };

	// Snapshot the caller's collection before validation and serialization.
	private static CategorySuggestionRequest? Snapshot(CategorySuggestionRequest? request)
	{
		if (request?.Categories is null) { return null; }
		request = request with { Categories = request.Categories.ToArray() };
		return CategorySuggestionPolicy.IsValid(request) ? request : null;
	}

	private async Task<CategorySuggestionResult> SendAsync(byte[] payload, Func<string, CancellationToken, Task<bool>> isCurrent,
		Func<byte[], CategorySuggestionResult> decide, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		JevOptions current = options.CurrentValue;
		string? key = current.ApiKey;
		if (!current.Enabled) { return CategorySuggestionResult.Failed(SuggestionFailure.Disabled); }
		if (string.IsNullOrWhiteSpace(key)) { return CategorySuggestionResult.Failed(SuggestionFailure.MissingCredential); }
		if (payload.Length > CategorySuggestionPolicy.MaximumPayloadBytes) { return CategorySuggestionResult.Failed(SuggestionFailure.PayloadTooLarge); }
		if (!await concurrency.Gate.WaitAsync(0, ct)) { return CategorySuggestionResult.Failed(SuggestionFailure.Busy); }

		try
		{
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
			timeout.CancelAfter(TimeSpan.FromSeconds(10));
			using HttpRequestMessage message = new(HttpMethod.Post, Endpoint);
			message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
			message.Content = new ByteArrayContent(payload);
			message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
			if (!await isCurrent(key, timeout.Token)) { return CategorySuggestionResult.Failed(SuggestionFailure.Disabled); }

			JevOptions beforeSend = options.CurrentValue;
			ct.ThrowIfCancellationRequested();
			if (!beforeSend.Enabled || beforeSend.ApiKey != key) { return CategorySuggestionResult.Failed(SuggestionFailure.Disabled); }

			using HttpResponseMessage response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
			if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
			{
				return CategorySuggestionResult.Failed(SuggestionFailure.Authentication);
			}

			if (response.StatusCode == HttpStatusCode.TooManyRequests) { return CategorySuggestionResult.Failed(SuggestionFailure.RateLimited); }
			if (!response.IsSuccessStatusCode) { return CategorySuggestionResult.Failed(SuggestionFailure.Unavailable); }

			await response.Content.LoadIntoBufferAsync(JevTwoStageQuestions.MaximumResponseBytes, timeout.Token);
			CategorySuggestionResult result = decide(await response.Content.ReadAsByteArrayAsync(timeout.Token));
			ct.ThrowIfCancellationRequested();
			JevOptions latest = options.CurrentValue;
			if (!latest.Enabled || latest.ApiKey != key || !await isCurrent(key, timeout.Token))
			{
				return CategorySuggestionResult.Failed(SuggestionFailure.Disabled, result.Usage);
			}

			ct.ThrowIfCancellationRequested();
			return result;
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return CategorySuggestionResult.Failed(SuggestionFailure.Timeout); }
		catch (HttpRequestException) { return CategorySuggestionResult.Failed(SuggestionFailure.Unavailable); }
		catch (IOException) { return CategorySuggestionResult.Failed(SuggestionFailure.Unavailable); }
		finally { concurrency.Gate.Release(); }
	}
}

/// <summary>Singleton across typed-client instances; excess work is rejected rather than queued.</summary>
public sealed class JevSuggestionConcurrency : IDisposable
{
	internal SemaphoreSlim Gate { get; } = new(1, 1);
	public void Dispose() => Gate.Dispose();
}
