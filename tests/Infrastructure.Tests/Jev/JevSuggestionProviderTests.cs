using System.Net;
using System.Text.Json.Nodes;
using Application.Classification;
using Infrastructure.Jev;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.Tests.Jev;

public class JevSuggestionProviderTests
{
	private static readonly CategorySuggestionRequest Request = new("gala apples", "revision-1",
	[
		new("fruit", "Fruit", "Fresh or dried fruit", null),
		new("apples", "Apples", "Apples", "fruit"),
		new("citrus", "Citrus", "Oranges and lemons", "fruit"),
		new("dairy", "Dairy", "Dairy", null),
		new("milk", "Milk", "Milk", "dairy")
	], "Corner Market");

	private const string ValidStage1 = """{"model":"jev-1.14.0","answers":{"category":{"type":"choice","choice":"Fruit","confidence":0.8,"probabilities":{"Fruit":0.9,"Dairy":0.1}},"fits":{"type":"noul","noul":0.9}},"usage":{"input_tokens":100,"output_tokens":10}}""";
	private const string ValidStage2 = """{"model":"jev-1.14.0","answers":{"subcategory":{"type":"choice","choice":"Fruit / Apples","confidence":0.7,"probabilities":{"Fruit / Apples":0.8,"Fruit / Citrus":0.1,"Dairy / Milk":0.1}}},"usage":{"input_tokens":60,"output_tokens":4}}""";

	private static readonly Func<string, CancellationToken, Task<bool>> Current = (_, _) => Task.FromResult(true);

	private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
	{
		public int Calls { get; private set; }
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; return send(request, cancellationToken); }
	}

	private static JevSuggestionProvider Provider(Handler handler, bool enabled = true, JevSuggestionConcurrency? concurrency = null)
	{
		Mock<IOptionsMonitor<JevOptions>> options = new();
		options.SetupGet(value => value.CurrentValue).Returns(new JevOptions { ApiKey = "test-key", Enabled = enabled });
		return new(new HttpClient(handler), options.Object, concurrency ?? new());
	}

	private static HttpResponseMessage Response(string body = ValidStage1) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

	private static Task<CategorySuggestionResult> Suggest(Handler handler, CategorySuggestionRequest? request = null, CancellationToken ct = default) =>
		Provider(handler).SuggestBoundAsync(request ?? Request, Current, ct);

	private static CategorySuggestion Baseline() =>
		CategorySuggestionPolicy.DecideStage1(Request, JevTwoStageQuestions.ParseStage1(System.Text.Encoding.UTF8.GetBytes(ValidStage1), Request)!, "jev").Suggestion!;

	[Fact]
	public async Task StageOnePostsTheExactProductionBytesWithAuthenticationAndReturnsABaseline()
	{
		byte[]? sent = null;
		Handler handler = new(async (message, token) =>
		{
			Assert.Equal(HttpMethod.Post, message.Method);
			Assert.Equal("https://api.typesafe.ai/v1/systemone", message.RequestUri!.AbsoluteUri);
			Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
			Assert.Equal("test-key", message.Headers.Authorization.Parameter);
			Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
			sent = await message.Content.ReadAsByteArrayAsync(token);
			return Response();
		});

		CategorySuggestionResult actual = await Suggest(handler);

		Assert.Equal(JevTwoStageQuestions.Stage1(Request), sent);
		Assert.Null(actual.Failure);
		Assert.Equal("fruit", actual.Suggestion!.CategoryId);
		Assert.Equal(["fruit", "dairy"], actual.Suggestion.RootCandidates!.Select(c => c.CategoryId));
		Assert.Equal(CategorySuggestionPolicy.Version, actual.Suggestion.Provenance.QuestionPolicy);
		Assert.Equal("jev", actual.Suggestion.Provenance.Provider);
		Assert.True(CategorySuggestionPolicy.IsBaseline(Request, actual.Suggestion));
		Assert.Equal(1, handler.Calls);
	}

	[Fact]
	public async Task StageTwoPostsTheBaselineRootsOptionsAndReturnsTheFinalSuggestion()
	{
		CategorySuggestion baseline = Baseline();
		byte[]? sent = null;
		Handler handler = new(async (message, token) =>
		{
			sent = await message.Content!.ReadAsByteArrayAsync(token);
			return Response(ValidStage2);
		});

		CategorySuggestionResult actual = await Provider(handler).RefineBoundAsync(Request, baseline, Current, default);

		Assert.Equal(JevTwoStageQuestions.Stage2(Request, ["fruit", "dairy"]), sent);
		Assert.Null(actual.Failure);
		Assert.Equal("apples", actual.Suggestion!.CategoryId);
		Assert.Equal(["fruit", "apples"], actual.Suggestion.Path);
		Assert.Equal(["apples", "citrus", "milk"], actual.Suggestion.LeafCandidates!.Select(c => c.CategoryId));
		Assert.Equal(160, actual.Suggestion.Provenance.InputTokens);
		Assert.Equal(14, actual.Suggestion.Provenance.OutputTokens);
	}

	[Fact]
	public async Task RefineWithoutAValidBaselineIsInvalidInputWithoutSending()
	{
		Handler handler = new((_, _) => Task.FromResult(Response(ValidStage2)));
		CategorySuggestion baseline = Baseline();

		foreach (CategorySuggestion invalid in new[] { baseline with { RootCandidates = null }, baseline with { CategoryId = "dairy", Path = ["dairy"] },
			baseline with { LeafCandidates = [new("apples", 1)] } })
		{
			Assert.Equal(SuggestionFailure.InvalidInput, (await Provider(handler).RefineBoundAsync(Request, invalid, Current, default)).Failure);
		}

		Assert.Equal(0, handler.Calls);
	}

	[Fact]
	public async Task StageOneResponseIsNotAcceptedAsStageTwo()
	{
		Handler handler = new((_, _) => Task.FromResult(Response(ValidStage1)));
		Assert.Equal(SuggestionFailure.InvalidResponse, (await Provider(handler).RefineBoundAsync(Request, Baseline(), Current, default)).Failure);
	}

	[Fact]
	public async Task DisabledProviderDoesNotSend()
	{
		Handler handler = new((_, _) => Task.FromResult(Response()));
		Assert.Equal(SuggestionFailure.Disabled, (await Provider(handler, enabled: false).SuggestBoundAsync(Request, Current, default)).Failure);
		Assert.Equal(0, handler.Calls);
	}

	[Fact]
	public async Task InvalidRequestIsRejectedBeforeSending()
	{
		Handler handler = new((_, _) => Task.FromResult(Response()));
		CategorySuggestionRequest deep = Request with { Categories = [.. Request.Categories, new("gala", "Gala", "Gala apples", "apples")] };

		Assert.Equal(SuggestionFailure.InvalidInput, (await Suggest(handler, deep)).Failure);
		Assert.Equal(SuggestionFailure.InvalidInput, (await Suggest(handler, Request with { Categories = null! })).Failure);
		Assert.Equal(0, handler.Calls);
	}

	[Fact]
	public async Task StaleBindingBeforeSendDoesNotSendAndAfterResponseDiscardsTheResult()
	{
		Handler handler = new((_, _) => Task.FromResult(Response()));
		int checks = 0;

		CategorySuggestionResult before = await Provider(handler).SuggestBoundAsync(Request, (_, _) => Task.FromResult(false), default);
		Assert.Equal(SuggestionFailure.Disabled, before.Failure);
		Assert.Equal(0, handler.Calls);

		CategorySuggestionResult after = await Provider(handler).SuggestBoundAsync(Request, (key, _) =>
		{
			Assert.Equal("test-key", key);
			return Task.FromResult(++checks == 1);
		}, default);
		Assert.Equal(SuggestionFailure.Disabled, after.Failure);
		Assert.Null(after.Suggestion);
		Assert.Equal(1, handler.Calls);
		Assert.Equal(2, checks);
	}

	[Theory]
	[InlineData(401, SuggestionFailure.Authentication)]
	[InlineData(403, SuggestionFailure.Authentication)]
	[InlineData(429, SuggestionFailure.RateLimited)]
	[InlineData(500, SuggestionFailure.Unavailable)]
	[InlineData(302, SuggestionFailure.Unavailable)]
	public async Task HttpErrorsAreOneAttemptFailures(int status, SuggestionFailure expected)
	{
		Handler handler = new((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
		CategorySuggestionResult actual = await Suggest(handler);
		Assert.Equal(expected, actual.Failure);
		Assert.Null(actual.Suggestion);
		Assert.Equal(1, handler.Calls);
	}

	[Fact]
	public async Task TransportExceptionsAreUnavailable()
	{
		Assert.Equal(SuggestionFailure.Unavailable, (await Suggest(new((_, _) => throw new HttpRequestException("synthetic")))).Failure);
		Assert.Equal(SuggestionFailure.Unavailable, (await Suggest(new((_, _) => throw new IOException("synthetic")))).Failure);
	}

	[Theory]
	[InlineData("{}")]
	[InlineData("not-json")]
	[InlineData("{\"model\":null}")]
	public async Task MalformedResponsesAreFailures(string body)
	{
		Handler handler = new((_, _) => Task.FromResult(Response(body)));
		Assert.Equal(SuggestionFailure.InvalidResponse, (await Suggest(handler)).Failure);
	}

	[Fact]
	public async Task ProviderDriftUnknownCategoryAndBadUsageAreFailures()
	{
		string[] bodies = [ValidStage1.Replace("jev-1.14.0", "other-model"), ValidStage1.Replace("\"choice\":\"Fruit\"", "\"choice\":\"Unknown\""),
			ValidStage1.Replace("\"input_tokens\":100", "\"wrong_tokens\":100"), ValidStage1.Replace("\"input_tokens\":100", "\"input_tokens\":-1"),
			ValidStage1.Replace("\"confidence\":0.8", "\"confidence\":2"), ValidStage1.Replace("\"Dairy\":0.1", "\"Dairy\":0.2")];
		foreach (string body in bodies)
		{
			Handler handler = new((_, _) => Task.FromResult(Response(body)));
			CategorySuggestionResult actual = await Suggest(handler);
			Assert.Equal(SuggestionFailure.InvalidResponse, actual.Failure);
			Assert.Null(actual.Suggestion);
		}
	}

	[Fact]
	public async Task ValidNoFitIsAnAbstentionWithUsage()
	{
		Handler handler = new((_, _) => Task.FromResult(Response(ValidStage1.Replace("\"noul\":0.9", "\"noul\":0.1"))));
		CategorySuggestionResult actual = await Suggest(handler);
		Assert.Null(actual.Failure);
		Assert.Null(actual.Suggestion!.CategoryId);
		Assert.Equal("no-fit", actual.Suggestion.AbstentionReason);
		Assert.Equal(100, actual.Suggestion.Provenance.InputTokens);
	}

	[Fact]
	public async Task OversizePayloadIsRejectedWithoutTruncatingTaxonomyOrSending()
	{
		Handler handler = new((_, _) => Task.FromResult(Response()));
		CategorySuggestionRequest request = Request with { Categories = [new("fruit", "Fruit", new string('x', 65500), null), new("dairy", "Dairy", "Dairy", null)] };
		Assert.Equal(SuggestionFailure.PayloadTooLarge, (await Suggest(handler, request)).Failure);
		Assert.Equal(0, handler.Calls);
	}

	[Fact]
	public async Task CallerSnapshotIsIsolatedFromLaterMutation()
	{
		List<SuggestionCategory> categories = [.. Request.Categories];
		CategorySuggestionRequest request = Request with { Categories = categories };
		Handler handler = new((_, _) =>
		{
			categories.Clear();
			return Task.FromResult(Response());
		});

		CategorySuggestionResult actual = await Suggest(handler, request);

		Assert.Null(actual.Failure);
		Assert.Equal("fruit", actual.Suggestion!.CategoryId);
	}

	[Fact]
	public async Task TimeoutIsFailureAndCallerCancellationPropagates()
	{
		Handler timeout = new((_, _) => throw new TaskCanceledException());
		Assert.Equal(SuggestionFailure.Timeout, (await Suggest(timeout)).Failure);
		using CancellationTokenSource cancellation = new();
		Handler handler = new((_, _) => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); });
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Suggest(handler, ct: cancellation.Token));
	}

	[Fact]
	public async Task ConcurrentClientInstancesShareLimitAndReleaseAfterCompletion()
	{
		TaskCompletionSource<HttpResponseMessage> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Handler handler = new((_, _) => pending.Task);
		using JevSuggestionConcurrency concurrency = new();
		Task<CategorySuggestionResult> first = Provider(handler, concurrency: concurrency).SuggestBoundAsync(Request, Current, default);
		CategorySuggestionResult second = await Provider(handler, concurrency: concurrency).RefineBoundAsync(Request, Baseline(), Current, default);
		Assert.Equal(SuggestionFailure.Busy, second.Failure);
		pending.SetResult(Response());
		Assert.Null((await first).Failure);
		Assert.Null((await Provider(new Handler((_, _) => Task.FromResult(Response())), concurrency: concurrency).SuggestBoundAsync(Request, Current, default)).Failure);
		Assert.Equal(1, handler.Calls);
	}

	[Fact]
	public void StageTwoRequestCarriesOnlyTheSubcategoryQuestion()
	{
		JsonNode payload = JsonNode.Parse(JevTwoStageQuestions.Stage2(Request, ["fruit", "dairy"]))!;
		Assert.Equal(["subcategory"], payload["questions"]!.AsObject().Select(p => p.Key));
		Assert.Equal("Corner Market", payload["state"]!["purchase"]!["store"]!.GetValue<string>());
	}
}
