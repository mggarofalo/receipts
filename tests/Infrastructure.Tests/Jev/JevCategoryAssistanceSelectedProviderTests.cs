using System.Net;
using System.Text.Json.Nodes;
using Application.Classification;
using Infrastructure.Jev;
using Infrastructure.Services;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.Tests.Jev;

public sealed class JevCategoryAssistanceSelectedProviderTests
{
	private static readonly CategorySuggestionRequest Request = new("purchase", "revision",
		[new("category", "Category", "Purchases", null)]);
	private const string ValidResponse = """{"model":"jev-1.14.0","answers":{"category":{"type":"choice","choice":"category","confidence":1,"probabilities":{"category":1}},"fits":{"type":"noul","noul":0.9}},"usage":{"input_tokens":100,"output_tokens":10}}""";

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
			Assert.True(JsonNode.DeepEquals(JsonNode.Parse(JevSuggestionQuestions.Serialize(Request)),
				JsonNode.Parse(await message.Content!.ReadAsStringAsync())));
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
		Assert.Equal("category", result.Suggestion?.CategoryId);
		Assert.Equal(1, handler.Calls);
		binding.VerifyAll();
	}

	[Fact]
	public async Task InvalidGenerationFailsImmediatelyBeforeHttpSend()
	{
		JevOptions current = new() { InferenceEnabled = true, ApiKey = "original-key" };
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
		JevOptions current = new() { InferenceEnabled = true, ApiKey = "original-key" };
		var (provider, handler, binding) = Create(() => current);
		binding.Setup(x => x.IsCurrentAsync(It.IsAny<CategoryAssistanceConnectionCoordinator.Configuration>(),
			It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(() =>
			{
				current = rotate
					? new() { InferenceEnabled = true, ApiKey = "replacement-key" }
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

		current = new() { ApiKey = "configured-key", InferenceEnabled = true };
		CategorySuggestionRequest large = Request with
		{
			Categories = [new("category", "Category", new string('x', 65500), null)]
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
		JevOptions current = new() { InferenceEnabled = true, ApiKey = "key" };
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
