using FluentAssertions;
using Infrastructure.Services;

namespace Infrastructure.Tests.Services;

public class EmbeddingModelOptionsTests
{
	[Fact]
	public void ResolveModelDirectory_ModelPathSet_ReturnsItVerbatim()
	{
		// Arrange — this is the path containers set via Embeddings__ModelPath.
		EmbeddingModelOptions options = new() { ModelPath = "/data/models/BgeLargeEnV15" };

		// Act
		string directory = options.ResolveModelDirectory();

		// Assert
		directory.Should().Be("/data/models/BgeLargeEnV15");
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void ResolveModelDirectory_ModelPathMissing_FallsBackToPerMachineCache(string? modelPath)
	{
		// Arrange
		EmbeddingModelOptions options = new() { ModelPath = modelPath };

		// Act
		string directory = options.ResolveModelDirectory();

		// Assert — a rooted, per-machine location, not something relative to the current
		// directory, so every clone and worktree shares one download.
		directory.Should().NotBeNullOrWhiteSpace();
		Path.IsPathRooted(directory).Should().BeTrue();
		directory.Should().EndWith(Path.Combine("Receipts", "models", "BgeLargeEnV15"));
	}

	[Fact]
	public void BuildDownloadUri_PinsTheRevision_AndDoesNotUseAMutableBranchRef()
	{
		// Arrange
		EmbeddingModelOptions options = new();
		EmbeddingModelFile model = EmbeddingModelOptions.Files.Single(f => f.FileName == "model.onnx");

		// Act
		Uri uri = options.BuildDownloadUri(model);

		// Assert — addressing "main" would let an upstream re-upload silently swap the model
		// and invalidate every embedding already stored.
		uri.ToString().Should().Be(
			"https://huggingface.co/BAAI/bge-large-en-v1.5/resolve/" +
			EmbeddingModelOptions.Revision +
			"/onnx/model.onnx");
		uri.ToString().Should().NotContain("/resolve/main/");
	}

	[Fact]
	public void BuildDownloadUri_BaseUrlHasTrailingSlash_DoesNotDoubleUpSeparators()
	{
		// Arrange
		EmbeddingModelOptions options = new() { BaseUrl = "https://mirror.example.com/bge/" };
		EmbeddingModelFile vocab = EmbeddingModelOptions.Files.Single(f => f.FileName == "vocab.txt");

		// Act
		Uri uri = options.BuildDownloadUri(vocab);

		// Assert
		uri.ToString().Should().Be($"https://mirror.example.com/bge/{EmbeddingModelOptions.Revision}/vocab.txt");
	}

	[Fact]
	public void Files_DeclareBothArtifacts_WithDigestsAndSizes()
	{
		// Assert — the size doubles as a cheap truncation guard on startup, and the digest is
		// what makes a torn or substituted download detectable.
		EmbeddingModelOptions.Files.Should().HaveCount(2);
		EmbeddingModelOptions.Files.Select(f => f.FileName)
			.Should().BeEquivalentTo(["model.onnx", "vocab.txt"]);

		foreach (EmbeddingModelFile file in EmbeddingModelOptions.Files)
		{
			file.SizeBytes.Should().BePositive();
			file.Sha256.Should().MatchRegex("^[0-9a-f]{64}$");
			file.RemotePath.Should().NotBeNullOrWhiteSpace();
		}
	}

	[Fact]
	public void AutoDownload_DefaultsToEnabled()
	{
		new EmbeddingModelOptions().AutoDownload.Should().BeTrue();
	}
}
