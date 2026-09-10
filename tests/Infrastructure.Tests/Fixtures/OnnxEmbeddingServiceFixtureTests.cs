using FluentAssertions;
using Infrastructure.Services;

namespace Infrastructure.Tests.Fixtures;

public class OnnxEmbeddingServiceFixtureTests
{
	[Fact]
	public void ConfiguredModelPath_WhenArtifactsAreMissing_ReportsTheConfiguredDirectory()
	{
		string configuredPath = Path.Combine(
			Path.GetTempPath(),
			"receipts-missing-model",
			Guid.NewGuid().ToString("N"));

		Action create = () => _ = new OnnxEmbeddingServiceFixture(configuredPath);

		create.Should().Throw<InvalidOperationException>()
			.WithMessage($"*{configuredPath}*");
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void UnconfiguredModelPath_PreservesDefaultCacheResolution(string? configuredPath)
	{
		string expected = new EmbeddingModelOptions().ResolveModelDirectory();

		string resolved = OnnxEmbeddingServiceFixture.ResolveModelDirectory(configuredPath);

		resolved.Should().Be(expected);
	}
}
