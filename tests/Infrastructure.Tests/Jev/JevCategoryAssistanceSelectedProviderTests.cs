using System.Net;
using Application.Classification;
using Infrastructure.Jev;
using Infrastructure.Services;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.Tests.Jev;

public sealed class JevCategoryAssistanceSelectedProviderTests
{
	private static readonly CategorySuggestionRequest Request = new("purchase", "revision",
		[new("fruit", "Fruit", "Fresh fruit", null), new("apples", "Apples", "Apples", "fruit"),
			new("dairy", "Dairy", "Milk and cheese", null), new("milk", "Milk", "Milk", "dairy")]);
	private const string ValidResponse = """{"model":"jev-1.14.0","answers":{"category":{"type":"choice","choice":"Fruit","confidence":1,"probabilities":{"Fruit":0.9,"Dairy":0.1}},"fits":{"type":"noul","noul":0.9}},"usage":{"input_tokens":100,"output_tokens":10}}""";
	private const string ValidStage2 = """{"model":"jev-1.14.0","answers":{"subcategory":{"type":"choice","choice":"Fruit / Apples","confidence":0.8,"probabilities":{"Fruit / Apples":0.8,"Dairy / Milk":0.2}}},"usage":{"input_tokens":3,"output_tokens":2}}""";

	private static (JevCategoryAssistanceSelectedProvider Provider, Handler Handler,
		Mock<ICategoryAssistanceConnectionBinding> Binding) Create(
		Func<JevOptions> current, Func<HttpRequestMessage, Task<HttpResponseMessage>>? send = null)
	{
		Handler handler = new(send ?? (_ => Task.FromResult(Response())));
		Mock<IOptionsMonitor<JevOptions>> options = new();
		options.SetupGet(x => x.CurrentValue).Returns(current);
		Mock<ICategoryAssistanceConnectionBinding> binding = new(MockBehavior.Strict);
		JevSuggestionProvider transport = new(new HttpClient(handler), options.Object, new());
		return (new(transport, options.Object, binding.Object), handler, binding);
	}

	private static HttpResponseMessage Response(string body = ValidResponse) =>
		new(HttpStatusCode.OK) { Content = new StringContent(body) };

	[Fact]
	public async Task ReadyCapabilityAndBoundSendUseSamePayloadAndCapturedKey()
	{
		JevOptions current = new() { ApiKey = "original-key" };
		Guid generation = Guid.NewGuid();
		var (provider, handler, binding) = Create(() => current, async message =>
		{
			Assert.Equal("original-key", message.Headers.Authorization?.Parameter);
			Assert.Equal(JevTwoStageQuestions.Stage1(Request), await message.Content!.ReadAsByteArrayAsync());
			return Response();
		});
		binding.Setup(x => x.IsCurrentAsync(
				It.Is<CategoryAssistanceConnectionCoordinator.Configuration>(c =>
					c.Provider == "jev" && c.Model == CategorySuggestionPolicy.Model
					&& c.Credential == "original-key" && c.Server.AbsoluteUri == "https://api.typesafe.ai/"),
				generation, It.IsAny<CancellationToken>())).ReturnsAsync(true);

		CategoryAssistanceCapability capability = await provider.GetAsync(default);
		Assert.Equal(CategoryAssistanceCapabilityState.Ready, capability.State);
		Assert.Contains(CategorySuggestionPolicy.Model, capability.Models);
		Assert.True(provider.FitsPayload(Request));
		CategorySuggestionResult result = await provider.SuggestAsync(Request, generation, default);

		Assert.Null(result.Failure);
		Assert.Equal("fruit", result.Suggestion?.CategoryId);
		Assert.Equal(1, handler.Calls);
		binding.VerifyAll();
	}

	[Fact]
	public async Task InvalidGenerationFailsImmediatelyBeforeHttpSend()
	{
		JevOptions current = new() { ApiKey = "original-key" };
		var (provider, handler, binding) = Create(() => current);
		binding.Setup(x => x.IsCurrentAsync(It.IsAny<CategoryAssistanceConnectionCoordinator.Configuration>(),
			It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

		CategorySuggestionResult result = await provider.SuggestAsync(Request, Guid.NewGuid(), default);

		Assert.Equal(SuggestionFailure.Disabled, result.Failure);
		Assert.Null(result.Suggestion);
		Assert.Equal(0, handler.Calls);
		binding.VerifyAll();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task DisableOrRotateCredentialDuringGenerationCheckCannotSend(bool rotate)
	{
		JevOptions current = new() { ApiKey = "original-key" };
		var (provider, handler, binding) = Create(() => current);
		binding.Setup(x => x.IsCurrentAsync(It.IsAny<CategoryAssistanceConnectionCoordinator.Configuration>(),
			It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(() =>
			{
				current = rotate
					? new() { ApiKey = "replacement-key" }
										: new() { Enabled = false, ApiKey = "original-key" };
				return true;
			});

		CategorySuggestionResult result = await provider.SuggestAsync(Request, Guid.NewGuid(), default);

		Assert.Equal(SuggestionFailure.Disabled, result.Failure);
		Assert.Null(result.Suggestion);
		Assert.Equal(0, handler.Calls);
		binding.VerifyAll();
	}

	[Fact]
	public async Task DisabledAndOversizeRequestsNeverCheckGenerationOrSend()
	{
		JevOptions current = new() { Enabled = false, ApiKey = "configured-key" };
		var (provider, handler, binding) = Create(() => current);
		Assert.False(provider.IsAvailable);
		Assert.Equal(CategoryAssistanceCapabilityState.Unavailable, (await provider.GetAsync(default)).State);
		Assert.Equal(SuggestionFailure.Disabled,
			(await provider.SuggestAsync(Request, Guid.NewGuid(), default)).Failure);

		current = new() { ApiKey = "configured-key" };
		CategorySuggestionRequest large = Request with
		{
			Categories = [new("fruit", "Fruit", new string('x', 65500), null), new("dairy", "Dairy", "Dairy", null)]
		};
		Assert.False(provider.FitsPayload(large));
		Assert.Equal(SuggestionFailure.PayloadTooLarge,
			(await provider.SuggestAsync(large, Guid.NewGuid(), default)).Failure);
		Assert.Equal(0, handler.Calls);
		binding.VerifyNoOtherCalls();
	}

	[Fact]
	public async Task ValidNoFitRemainsAbstentionAndHttpFailureRemainsFailure()
	{
		JevOptions current = new() { ApiKey = "key" };
		HttpResponseMessage response = Response(ValidResponse.Replace("\"noul\":0.9", "\"noul\":0.1"));
		var (provider, handler, binding) = Create(() => current, _ => Task.FromResult(response));
		binding.Setup(x => x.IsCurrentAsync(It.IsAny<CategoryAssistanceConnectionCoordinator.Configuration>(),
			It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

		CategorySuggestionResult result = await provider.SuggestAsync(Request, Guid.NewGuid(), default);
		Assert.Null(result.Failure);
		Assert.Null(result.Suggestion?.CategoryId);
		Assert.Equal("no-fit", result.Suggestion?.AbstentionReason);
		Assert.Equal(1, handler.Calls);

		var (failedProvider, failedHandler, failedBinding) = Create(() => current,
			_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
		failedBinding.Setup(x => x.IsCurrentAsync(It.IsAny<CategoryAssistanceConnectionCoordinator.Configuration>(),
			It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
		CategorySuggestionResult failed = await failedProvider.SuggestAsync(Request, Guid.NewGuid(), default);
		Assert.Equal(SuggestionFailure.Unavailable, failed.Failure);
		Assert.Null(failed.Suggestion);
		Assert.Equal(1, failedHandler.Calls);
	}

	[Fact]
	public async Task EmptyGenerationFailsClosedForBothStagesWithoutBindingCheck()
	{
		var (provider, handler, binding) = Create(() => new() { ApiKey = "key" });

		Assert.Equal(SuggestionFailure.Disabled, (await provider.SuggestAsync(Request, Guid.Empty, default)).Failure);
		Assert.Equal(SuggestionFailure.Disabled, (await provider.RefineAsync(Request, Baseline(), Guid.Empty, default)).Failure);
		Assert.Equal(0, handler.Calls);
		binding.VerifyNoOtherCalls();
	}

	[Fact]
	public void PayloadFitRejectsTreesTheWireFormatCannotKeyUniquely()
	{
		var (provider, _, _) = Create(() => new() { ApiKey = "key" });

		Assert.False(provider.FitsPayload(Request with { Categories = [.. Request.Categories, new("fruit-2", "Fruit", "Duplicate name", null)] }));
		Assert.False(provider.FitsPayload(null!));
	}

	[Fact]
	public async Task RefineSendsStageTwoBytesWithCapturedKeyAndAddsTheLeaf()
	{
		JevOptions current = new() { ApiKey = "key" };
		Guid generation = Guid.NewGuid();
		CategorySuggestion baseline = Baseline();
		var (provider, handler, binding) = Create(() => current, async message =>
		{
			Assert.Equal("key", message.Headers.Authorization?.Parameter);
			Assert.Equal(JevTwoStageQuestions.Stage2(Request, ["fruit", "dairy"]), await message.Content!.ReadAsByteArrayAsync());
			return Response(ValidStage2);
		});
		binding.Setup(x => x.IsCurrentAsync(It.Is<CategoryAssistanceConnectionCoordinator.Configuration>(c => c.Credential == "key"),
			generation, It.IsAny<CancellationToken>())).ReturnsAsync(true);

		CategorySuggestionResult result = await provider.RefineAsync(Request, baseline, generation, default);

		Assert.Null(result.Failure);
		Assert.Equal("apples", result.Suggestion!.CategoryId);
		Assert.Equal(["fruit", "apples"], result.Suggestion.Path);
		Assert.Equal(baseline.FitProbability, result.Suggestion.FitProbability);
		Assert.Equal(baseline.RootCandidates, result.Suggestion.RootCandidates);
		Assert.Equal(["apples", "milk"], result.Suggestion.LeafCandidates!.Select(c => c.CategoryId));
		Assert.Equal(103, result.Suggestion.Provenance.InputTokens);
		Assert.Equal(12, result.Suggestion.Provenance.OutputTokens);
		Assert.Equal(1, handler.Calls);
	}

	[Fact]
	public async Task RefineRejectsWrongAnswerAndRevokedBinding()
	{
		JevOptions current = new() { ApiKey = "key" };
		CategorySuggestion baseline = Baseline();
		var (provider, handler, binding) = Create(() => current);
		binding.Setup(x => x.IsCurrentAsync(It.IsAny<CategoryAssistanceConnectionCoordinator.Configuration>(),
			It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
		// The default handler answers with a stage-1 body, which is not a stage-2 answer.
		var malformed = await provider.RefineAsync(Request, baseline, Guid.NewGuid(), default);
		Assert.Equal(SuggestionFailure.InvalidResponse, malformed.Failure);
		Assert.Null(malformed.Suggestion);
		Assert.Equal(1, handler.Calls);
		binding.Reset();
		binding.Setup(x => x.IsCurrentAsync(It.IsAny<CategoryAssistanceConnectionCoordinator.Configuration>(),
			It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
		var revoked = await provider.RefineAsync(Request, baseline, Guid.NewGuid(), default);
		Assert.Equal(SuggestionFailure.Disabled, revoked.Failure);
		Assert.Equal(1, handler.Calls);
	}

	private static CategorySuggestion Baseline() => CategorySuggestionPolicy.DecideStage1(Request,
		JevTwoStageQuestions.ParseStage1(System.Text.Encoding.UTF8.GetBytes(ValidResponse), Request)!, "jev").Suggestion!;

	private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
	{
		public int Calls { get; private set; }
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Calls++;
			return send(request);
		}
	}
}
