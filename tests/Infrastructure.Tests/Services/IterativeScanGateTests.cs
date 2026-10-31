using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace Infrastructure.Tests.Services;

public class IterativeScanGateTests
{
	private readonly Mock<ILogger> _logger = new();

	[Theory]
	[InlineData("0.8.0", true)]
	[InlineData("0.8.7", true)]
	[InlineData("0.10.0", true)]
	[InlineData("1.0", true)]
	[InlineData("0.7.4", false)]
	[InlineData("0.5.1", false)]
	[InlineData(null, false)]
	[InlineData("", false)]
	[InlineData("dev", false)]
	public void Supports_RequiresPgvectorZeroPointEight(string? version, bool expected)
	{
		IterativeScanGate.Supports(version).Should().Be(expected);
	}

	[Fact]
	public async Task IsSupported_ReadsTheVersionOncePerGate()
	{
		IterativeScanGate gate = new();
		int reads = 0;
		Task<string?> Read(CancellationToken _) { reads++; return Task.FromResult<string?>("0.8.7"); }

		bool first = await gate.IsSupportedAsync(Read, _logger.Object, CancellationToken.None);
		bool second = await gate.IsSupportedAsync(Read, _logger.Object, CancellationToken.None);

		first.Should().BeTrue();
		second.Should().BeTrue();
		reads.Should().Be(1);
		VerifyWarnings(Times.Never());
	}

	[Fact]
	public async Task IsSupported_BelowZeroPointEightSkipsTheSettingAndWarnsOnce()
	{
		IterativeScanGate gate = new();
		int reads = 0;
		Task<string?> Read(CancellationToken _) { reads++; return Task.FromResult<string?>("0.7.4"); }

		bool[] answers = [
			await gate.IsSupportedAsync(Read, _logger.Object, CancellationToken.None),
			await gate.IsSupportedAsync(Read, _logger.Object, CancellationToken.None),
			await gate.IsSupportedAsync(Read, _logger.Object, CancellationToken.None)];

		answers.Should().AllBeEquivalentTo(false);
		reads.Should().Be(1);
		VerifyWarnings(Times.Once());
	}

	[Fact]
	public async Task IsSupported_MissingExtensionCountsAsUnsupported()
	{
		IterativeScanGate gate = new();

		bool supported = await gate.IsSupportedAsync(_ => Task.FromResult<string?>(null), _logger.Object, CancellationToken.None);

		supported.Should().BeFalse();
		VerifyWarnings(Times.Once());
	}

	[Fact]
	public async Task IsSupported_FailedReadIsNotCached()
	{
		IterativeScanGate gate = new();
		int reads = 0;
		Task<string?> Read(CancellationToken _) => ++reads == 1
			? Task.FromException<string?>(new InvalidOperationException("connection reset"))
			: Task.FromResult<string?>("0.8.7");

		Func<Task> first = () => gate.IsSupportedAsync(Read, _logger.Object, CancellationToken.None);
		await first.Should().ThrowAsync<InvalidOperationException>();
		bool second = await gate.IsSupportedAsync(Read, _logger.Object, CancellationToken.None);

		second.Should().BeTrue();
		reads.Should().Be(2);
	}

	[Fact]
	public async Task IsSupported_ConcurrentFirstCallsWarnOnce()
	{
		IterativeScanGate gate = new();
		TaskCompletionSource<string?> version = new(TaskCreationOptions.RunContinuationsAsynchronously);

		Task<bool>[] calls = Enumerable.Range(0, 8)
			.Select(_ => gate.IsSupportedAsync(_ => version.Task, _logger.Object, CancellationToken.None)).ToArray();
		version.SetResult("0.7.0");
		bool[] answers = await Task.WhenAll(calls);

		answers.Should().AllBeEquivalentTo(false);
		VerifyWarnings(Times.Once());
	}

	private void VerifyWarnings(Times times) => _logger.Verify(
		logger => logger.Log(
			LogLevel.Warning,
			It.IsAny<EventId>(),
			It.IsAny<It.IsAnyType>(),
			It.IsAny<Exception>(),
			It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
		times);
}
