using System.Net;
using Application.Classification;
using Infrastructure.Jev;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.Tests.Jev;

public class JevSuggestionProviderBoundaryTests
{
	private static readonly CategorySuggestionRequest Request = new("purchase", "revision", [new("category", "Category", "Purchases", null)]);
	private const string ValidResponse = """{"model":"jev-1.14.0","answers":{"category":{"type":"choice","choice":"Category","confidence":1,"probabilities":{"Category":1}},"fits":{"type":"noul","noul":0.9}},"usage":{"input_tokens":100,"output_tokens":10}}""";
	private static readonly Func<string, CancellationToken, Task<bool>> Current = (_, _) => Task.FromResult(true);

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public async Task MissingCredentialWithOptInIsUnavailableWithoutDispatch(string? key)
	{
		JevOptions options = new() { ApiKey = key };
		Handler handler = new(() => new(HttpStatusCode.OK));
		JevSuggestionProvider provider = Provider(handler, () => options);
		CategorySuggestionResult result = await provider.SuggestBoundAsync(Request, Current, default);
		Assert.Equal(SuggestionFailure.MissingCredential, result.Failure);
		Assert.Null(result.Suggestion);
		Assert.Equal(0, handler.Calls);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task DisableOrCredentialChangeDuringDispatchDiscardsOtherwiseValidResult(bool rotateKey)
	{
		JevOptions options = new() { ApiKey = "first-key" };
		Handler handler = new(() =>
		{
			options = rotateKey ? new() { ApiKey = "changed-key" } : new() { Enabled = false, ApiKey = "first-key" };
			return new(HttpStatusCode.OK) { Content = new StringContent(ValidResponse) };
		});
		CategorySuggestionResult result = await Provider(handler, () => options).SuggestBoundAsync(Request, Current, default);
		Assert.Equal(SuggestionFailure.Disabled, result.Failure);
		Assert.Null(result.Suggestion);
		Assert.Equal(1, handler.Calls);
	}

	[Fact]
	public async Task OversizeProviderBodyFailsWithoutSuggestionOrRetry()
	{
		Handler handler = new(() => new(HttpStatusCode.OK) { Content = new StringContent(ValidResponse + new string(' ', 1048576)) });
		CategorySuggestionResult result = await Provider(handler, () => new() { ApiKey = "test-key" }).SuggestBoundAsync(Request, Current, default);
		Assert.NotNull(result.Failure);
		Assert.Null(result.Suggestion);
		Assert.Equal(1, handler.Calls);
	}

	[Fact]
	public async Task LargeValidResponseIsBufferedAndAcceptedOnce()
	{
		Handler handler = new(() => new(HttpStatusCode.OK) { Content = new StringContent(ValidResponse + new string(' ', 150000)) });
		CategorySuggestionResult result = await Provider(handler, () => new() { ApiKey = "test-key" }).SuggestBoundAsync(Request, Current, default);
		Assert.Null(result.Failure);
		Assert.Equal("category", result.Suggestion!.CategoryId);
		Assert.Equal(1, handler.Calls);
	}

	private static JevSuggestionProvider Provider(Handler handler, Func<JevOptions> current)
	{
		Mock<IOptionsMonitor<JevOptions>> options = new();
		options.SetupGet(value => value.CurrentValue).Returns(current);
		return new(new HttpClient(handler), options.Object, new());
	}

	private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
	{
		public int Calls { get; private set; }
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Calls++;
			return Task.FromResult(response());
		}
	}
}
