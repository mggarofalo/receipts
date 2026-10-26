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
	public void PayloadContainsIndependentRootAndBranchChoices()
	{
		JsonNode expected = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "jev-suggestion-v4.json")))!;
		JsonNode state = expected["state"]!;
		SuggestionCategory[] categories = state["categories"]!.AsArray().Select(node =>
		{
			string[] path = node!["path"]!.Deserialize<string[]>()!;
			return new SuggestionCategory(node["id"]!.GetValue<string>(), node["name"]!.GetValue<string>(), node["definition"]!.GetValue<string>(), path.Length > 1 ? path[^2] : null);
		}).ToArray();
		CategorySuggestionRequest request = new(state["receiptLine"]!["untrustedDescription"]!.GetValue<string>(), state["taxonomyRevision"]!.GetValue<string>(), categories);
		JsonNode actual = JsonNode.Parse(JevSuggestionQuestions.Serialize(request))!;
		Assert.Equal("jev-1.14.0", actual["model"]!.GetValue<string>());
		Assert.Equal(categories.Count(x => x.ParentId is null), actual["questions"]!["category"]!["criteria"]!.AsObject().Count);
		Assert.Equal(1 + categories.Count(x => categories.Any(y => y.ParentId == x.Id)) + 1, actual["questions"]!.AsObject().Count);
		Assert.NotNull(actual["questions"]!["fits"]);
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
		CategorySuggestionRequest request = Request with { Categories = [new("parent", "Parent", new string('x', 65500), null)] };
		Assert.Equal(SuggestionFailure.PayloadTooLarge, (await Provider(handler).SuggestAsync(request, default)).Failure);
		Assert.Equal(0, handler.Calls);
	}

	[Fact]
	public async Task LargeHierarchyIsCompleteAndUsesOneRequest()
	{
		SuggestionCategory[] roots = Enumerable.Range(0, 68)
				.Select(i => new SuggestionCategory($"r{i}", $"Root {i}", $"General purchases of type {i}", null)).ToArray();
		SuggestionCategory[] children = Enumerable.Range(0, 241)
				.Select(i => new SuggestionCategory($"c{i}", $"Child {i}", $"Specific purchases of type {i}", $"r{i % 68}")).ToArray();
		CategorySuggestionRequest request = Request with { Categories = [.. roots, .. children] };
		byte[] payload = JevSuggestionQuestions.Serialize(request);
		JsonNode questions = JsonNode.Parse(payload)!["questions"]!;
		Assert.InRange(payload.Length, 1, CategorySuggestionPolicy.MaximumPayloadBytes);
		Assert.Equal(70, questions.AsObject().Count);
		Assert.Equal(68, questions["category"]!["criteria"]!.AsObject().Count);
		Assert.Equal(309, questions.AsObject().Where(x => x.Key != "fits")
				.Sum(x => x.Value!["criteria"]!.AsObject().Count) - 68);
		string response = ResponseFor(questions, "r0", "c0");
		Handler handler = new((_, _) => Task.FromResult(Response(response)));
		CategorySuggestionResult result = await Provider(handler).SuggestAsync(request, default);
		Assert.Null(result.Failure);
		Assert.Equal("c0", result.Suggestion!.CategoryId);
		Assert.Equal(new[] { "r0", "c0" }, result.Suggestion.Path);
		Assert.Equal("local_choice", result.Suggestion.ProbabilityScope);
		Assert.Equal(1, handler.Calls);
	}

	[Fact]
	public async Task MissingExtraAndMalformedBranchAnswersFail()
	{
		CategorySuggestionRequest request = Request with { Categories = [new("r", "Root", "Broad", null), new("c", "Child", "Specific", "r")] };
		JsonNode questions = JsonNode.Parse(JevSuggestionQuestions.Serialize(request))!["questions"]!;
		JsonNode valid = JsonNode.Parse(ResponseFor(questions, "r", "c"))!;
		var bad = new List<JsonNode>();
		JsonNode missing = valid.DeepClone(); missing["answers"]!.AsObject().Remove("branch_0"); bad.Add(missing);
		JsonNode extra = valid.DeepClone(); extra["answers"]!["branch_extra"] = valid["answers"]!["branch_0"]!.DeepClone(); bad.Add(extra);
		JsonNode distribution = valid.DeepClone(); distribution["answers"]!["branch_0"]!["probabilities"]!["unknown"] = 0.1; bad.Add(distribution);
		JsonNode choice = valid.DeepClone(); choice["answers"]!["branch_0"]!["choice"] = "unknown"; bad.Add(choice);
		foreach (JsonNode body in bad)
		{
			Handler handler = new((_, _) => Task.FromResult(Response(body.ToJsonString())));
			Assert.Equal(SuggestionFailure.InvalidResponse, (await Provider(handler).SuggestAsync(request, default)).Failure);
		}
	}

	private static string ResponseFor(JsonNode questions, string root, string child)
	{
		var answers = new Dictionary<string, object>();
		foreach (var (key, node) in questions.AsObject())
		{
			if (key == "fits")
			{
				continue;
			}

			string[] ids = node!["criteria"]!.AsObject().Select(x => x.Key).ToArray();
			string selected = key == "category" ? root : ids.Contains(child) && ids.Contains(root) ? child : ids[0];
			answers[key] = new
			{
				type = "choice",
				choice = selected,
				confidence = 0.8,
				probabilities = ids.ToDictionary(id => id, id => id == selected ? 1.0 : 0.0)
			};
		}
		answers["fits"] = new { type = "noul", noul = 0.9 };
		return JsonSerializer.Serialize(new
		{
			model = CategorySuggestionPolicy.Model,
			answers,
			usage = new { input_tokens = 100L, output_tokens = 10L }
		});
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
