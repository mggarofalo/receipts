using System.Net;
using Application.Classification;
using FluentAssertions;
using Infrastructure.Jev;
using Microsoft.Extensions.Options;
using Moq;
namespace Infrastructure.Tests.Services;

public class JevConnectionTests
{
	private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
	{
		public int Calls { get; private set; }
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{ Calls++; return send(request, cancellationToken); }
	}
	private static (JevConnection Connection, Handler Handler, Mock<IOptionsMonitor<JevOptions>> Options) Create(JevOptions options, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null)
	{
		Handler handler = new(send ?? ((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"models\":[{\"name\":\"jev-latest\",\"description\":\"model\",\"release_date\":\"2026-09-15\"}]}") })));
		Mock<IOptionsMonitor<JevOptions>> monitor = new();
		monitor.SetupGet(value => value.CurrentValue).Returns(options);
		return (new JevConnection(new HttpClient(handler), monitor.Object), handler, monitor);
	}
	[Theory]
	[InlineData(false, "fake", ProviderConnectionState.Disabled)]
	[InlineData(true, null, ProviderConnectionState.Missing)]
	public async Task UnconfiguredMakesZeroRequests(bool enabled, string? key, ProviderConnectionState expected)
	{
		var (connection, handler, _) = Create(new() { Enabled = enabled, ApiKey = key });
		connection.GetStatus().State.Should().Be(expected);
		(await connection.CheckAsync(default)).State.Should().Be(expected);
		handler.Calls.Should().Be(0);
	}
	[Fact]
	public async Task PassiveReadNeverChecksAndSuccessDoesNotPersistIdentity()
	{
		var (connection, handler, _) = Create(new() { ApiKey = "fake-secret" });
		connection.GetStatus().State.Should().Be(ProviderConnectionState.ConfiguredUnverified);
		handler.Calls.Should().Be(0);
		ProviderConnectionStatus actual = await connection.CheckAsync(default);
		actual.Models.Should().Equal("jev-latest");
		actual.State.Should().Be(ProviderConnectionState.Connected);
		connection.GetStatus().State.Should().Be(ProviderConnectionState.ConfiguredUnverified);
	}
	[Theory]
	[InlineData(401, ProviderConnectionState.Invalid, "authenticationRejected")]
	[InlineData(403, ProviderConnectionState.Invalid, "authenticationRejected")]
	[InlineData(429, ProviderConnectionState.Unavailable, "rateLimited")]
	[InlineData(500, ProviderConnectionState.Unavailable, "providerUnavailable")]
	public async Task FailuresAreRedacted(int status, ProviderConnectionState state, string reason)
	{
		var (connection, _, _) = Create(new() { ApiKey = "secret" }, (request, _) =>
		{
			request.Method.Should().Be(HttpMethod.Get);
			request.RequestUri!.AbsoluteUri.Should().Be("https://api.typesafe.ai/v1/models");
			request.Headers.Authorization!.Parameter.Should().Be("secret");
			return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("secret provider error") });
		});
		ProviderConnectionStatus actual = await connection.CheckAsync(default);
		actual.Should().BeEquivalentTo(new ProviderConnectionStatus(state, reason, []));
	}
	[Theory]
	[InlineData("broken")]
	[InlineData("{}")]
	[InlineData("{\"models\":[{}]}")]
	public async Task MalformedCatalogIsUnavailable(string body)
	{
		var (connection, _, _) = Create(new() { ApiKey = "secret" }, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
		(await connection.CheckAsync(default)).State.Should().Be(ProviderConnectionState.Unavailable);
	}
	[Fact]
	public async Task RotationDiscardsInflightIdentity()
	{
		Mock<IOptionsMonitor<JevOptions>>? monitor = null;
		var (connection, _, options) = Create(new() { ApiKey = "old-secret" }, (_, _) =>
		{
			monitor!.SetupGet(value => value.CurrentValue).Returns(new JevOptions { ApiKey = "new-secret" });
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"models\":[]}") });
		});
		monitor = options;
		(await connection.CheckAsync(default)).State.Should().Be(ProviderConnectionState.ConfiguredUnverified);
	}
	[Fact]
	public async Task ProviderTimeoutIsUnavailable()
	{
		var (connection, _, _) = Create(new() { ApiKey = "secret" }, (_, _) => throw new OperationCanceledException());
		ProviderConnectionStatus actual = await connection.CheckAsync(default);
		actual.State.Should().Be(ProviderConnectionState.Unavailable);
		actual.Reason.Should().Be("timeout");
	}
	[Fact]
	public async Task CallerCancellationPropagates()
	{
		var (connection, _, _) = Create(new() { ApiKey = "secret" }, (_, token) => Task.FromCanceled<HttpResponseMessage>(token));
		using CancellationTokenSource source = new(); source.Cancel();
		await FluentActions.Awaiting(() => connection.CheckAsync(source.Token)).Should().ThrowAsync<OperationCanceledException>();
	}
}
