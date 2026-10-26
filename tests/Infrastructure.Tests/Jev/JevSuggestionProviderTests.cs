using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Classification;
using Infrastructure.Jev;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.Tests.Jev;

public class JevSuggestionProviderTests
{
	private static readonly CategorySuggestionRequest Request = new("test purchase", "revision-1", [new("parent", "Parent", "All purchases", null)]);
	private const string ValidResponse = """{"model":"jev-1.14.0","answers":{"category":{"type":"choice","choice":"parent","confidence":0.8,"probabilities":{"parent":1}},"fits":{"type":"noul","noul":0.9}},"usage":{"input_tokens":100,"output_tokens":10}}""";

	private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
	{
		public int Calls { get; private set; }
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; return send(request, cancellationToken); }
	}
	private static JevSuggestionProvider Provider(Handler handler, bool enabled = true, JevSuggestionConcurrency? concurrency = null)
	{
		Mock<IOptionsMonitor<JevOptions>> options = new();
		options.SetupGet(value => value.CurrentValue).Returns(new JevOptions { ApiKey = "test-key", InferenceEnabled = enabled });
		return new(new HttpClient(handler), options.Object, concurrency ?? new());
	}
	private static HttpResponseMessage Response(string body = ValidResponse) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

	[Fact]
	public void FrozenEvaluatedPayloadHasExactSemanticParity()
	{
		JsonNode expected = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "jev-suggestion-v4.json")))!;
		JsonNode state = expected["state"]!;
		SuggestionCategory[] categories = state["categories"]!.AsArray().Select(node =>
		{
			string[] path = node!["path"]!.Deserialize<string[]>()!;
			return new SuggestionCategory(node["id"]!.GetValue<string>(), node["name"]!.GetValue<string>(), node["definition"]!.GetValue<string>(), path.Length > 1 ? path[^2] : null);
		}).ToArray();
		CategorySuggestionRequest request = new(state["receiptLine"]!["untrustedDescription"]!.GetValue<string>(), state["taxonomyRevision"]!.GetValue<string>(), categories);
		Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(JevSuggestionQuestions.Serialize(request))));
	}

	[Fact]
	public async Task KeyAloneDoesNotEnableInference()
	{
		Handler handler = new((_, _) => Task.FromResult(Response()));
		JevSuggestionProvider provider = Provider(handler, false);
		Assert.False(provider.IsAvailable);
		Assert.Equal(SuggestionFailure.Disabled, (await provider.SuggestAsync(Request, default)).Failure);
		Assert.Equal(0, handler.Calls);
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
		CategorySuggestionResult actual = await Provider(handler).SuggestAsync(Request, default);
		Assert.Equal(expected, actual.Failure);
		Assert.Null(actual.Suggestion);
		Assert.Equal(1, handler.Calls);
	}

	[Theory]
	[InlineData("{}")]
	[InlineData("not-json")]
	[InlineData("{\"model\":null}")]
	public async Task MalformedResponsesAreFailures(string body)
	{
		Handler handler = new((_, _) => Task.FromResult(Response(body)));
		Assert.Equal(SuggestionFailure.InvalidResponse, (await Provider(handler).SuggestAsync(Request, default)).Failure);
	}

	[Fact]
	public async Task ProviderDriftUnknownCategoryAndMissingUsageAreFailures()
	{
		string[] bodies = [ValidResponse.Replace("jev-1.14.0", "other-model"), ValidResponse.Replace("\"choice\":\"parent\"", "\"choice\":\"unknown\""), ValidResponse.Replace("\"input_tokens\":100", "\"wrong_tokens\":100"), ValidResponse.Replace("\"input_tokens\":100", "\"input_tokens\":-1"), ValidResponse.Replace("\"confidence\":0.8", "\"confidence\":2")];
		foreach (string body in bodies)
		{
			Handler handler = new((_, _) => Task.FromResult(Response(body)));
			CategorySuggestionResult actual = await Provider(handler).SuggestAsync(Request, default);
			Assert.Equal(SuggestionFailure.InvalidResponse, actual.Failure);
			Assert.Null(actual.Suggestion);
		}
	}

	[Fact]
	public async Task ValidNoFitIsAnAbstentionWithUsage()
	{
		Handler handler = new((_, _) => Task.FromResult(Response(ValidResponse.Replace("\"noul\":0.9", "\"noul\":0.1"))));
		CategorySuggestionResult actual = await Provider(handler).SuggestAsync(Request, default);
		Assert.Null(actual.Failure);
		Assert.Null(actual.Suggestion!.CategoryId);
		Assert.Equal("no-fit", actual.Suggestion.AbstentionReason);
		Assert.Equal(100, actual.Suggestion.Provenance.InputTokens);
	}

	[Fact]
	public async Task PostsBoundedPayloadWithAuthenticationAndReturnsValidatedProvenance()
	{
		Handler handler = new(async (message, token) =>
		{
			Assert.Equal(HttpMethod.Post, message.Method);
			Assert.Equal("https://api.typesafe.ai/v1/systemone", message.RequestUri!.AbsoluteUri);
			Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
			Assert.Equal("test-key", message.Headers.Authorization.Parameter);
			Assert.True(JsonNode.DeepEquals(JsonNode.Parse(JevSuggestionQuestions.Serialize(Request)), JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))));
			return Response();
		});
		CategorySuggestionResult actual = await Provider(handler).SuggestAsync(Request, default);
		Assert.Null(actual.Failure);
		Assert.Equal("parent", actual.Suggestion!.CategoryId);
		Assert.Equal(100, actual.Suggestion.Provenance.InputTokens);
	}

	[Fact]
	public async Task OversizePayloadIsRejectedWithoutTruncatingTaxonomyOrSending()
	{
		Handler handler = new((_, _) => Task.FromResult(Response()));
		CategorySuggestionRequest request = Request with { Categories = [new("parent", "Parent", new string('x', 7000), null)] };
		Assert.Equal(SuggestionFailure.PayloadTooLarge, (await Provider(handler).SuggestAsync(request, default)).Failure);
		Assert.Equal(0, handler.Calls);
	}

	[Fact]
	public async Task TimeoutIsFailureAndCallerCancellationPropagates()
	{
		Handler timeout = new((_, _) => throw new TaskCanceledException());
		Assert.Equal(SuggestionFailure.Timeout, (await Provider(timeout).SuggestAsync(Request, default)).Failure);
		using CancellationTokenSource cancellation = new();
		Handler handler = new((_, _) => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); });
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(handler).SuggestAsync(Request, cancellation.Token));
	}

	[Fact]
	public async Task ConcurrentClientInstancesShareLimitAndReleaseAfterCompletion()
	{
		TaskCompletionSource<HttpResponseMessage> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Handler handler = new((_, _) => pending.Task);
		using JevSuggestionConcurrency concurrency = new();
		Task<CategorySuggestionResult> first = Provider(handler, concurrency: concurrency).SuggestAsync(Request, default);
		CategorySuggestionResult second = await Provider(handler, concurrency: concurrency).SuggestAsync(Request, default);
		Assert.Equal(SuggestionFailure.Busy, second.Failure);
		pending.SetResult(Response());
		Assert.Null((await first).Failure);
		Assert.Null((await Provider(new Handler((_, _) => Task.FromResult(Response())), concurrency: concurrency).SuggestAsync(Request, default)).Failure);
		Assert.Equal(1, handler.Calls);
	}
}
