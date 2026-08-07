using FluentAssertions;
using Infrastructure.Services;

namespace Infrastructure.Tests.Services;

/// <summary>
/// Covers the startup check that decides whether the 1.34 GB download can be skipped.
/// Uses a synthetic file set so the assertions run against bytes we can actually create.
/// </summary>
public class EmbeddingModelProvisioningServiceTests : IDisposable
{
	private static readonly IReadOnlyList<EmbeddingModelFile> TestFiles =
	[
		new("model.onnx", "onnx/model.onnx", 4L, "0000000000000000000000000000000000000000000000000000000000000000"),
		new("vocab.txt", "vocab.txt", 3L, "1111111111111111111111111111111111111111111111111111111111111111"),
	];

	private readonly string _directory =
		Path.Combine(Path.GetTempPath(), "receipts-onnx-tests", Guid.NewGuid().ToString("N"));

	public EmbeddingModelProvisioningServiceTests() => Directory.CreateDirectory(_directory);

	[Fact]
	public void IsProvisioned_NothingOnDisk_ReturnsFalse()
	{
		EmbeddingModelProvisioningService.IsProvisioned(_directory, TestFiles).Should().BeFalse();
	}

	[Fact]
	public void IsProvisioned_FilesPresentButNoMarker_ReturnsFalse()
	{
		// Arrange — an interrupted run can leave correct-looking files behind; without the
		// marker we have never confirmed their digests, so they must not be trusted.
		WriteFiles();

		// Act / Assert
		EmbeddingModelProvisioningService.IsProvisioned(_directory, TestFiles).Should().BeFalse();
	}

	[Fact]
	public void IsProvisioned_MarkerNamesADifferentRevision_ReturnsFalse()
	{
		// Arrange — this is what makes bumping the pinned revision re-provision.
		WriteFiles();
		WriteMarker("0000000000000000000000000000000000000000");

		// Act / Assert
		EmbeddingModelProvisioningService.IsProvisioned(_directory, TestFiles).Should().BeFalse();
	}

	[Fact]
	public void IsProvisioned_FileTruncated_ReturnsFalse()
	{
		// Arrange — a half-written file with a valid marker is the dangerous case: it would
		// otherwise be handed to InferenceSession and crash on load.
		WriteFiles();
		WriteMarker(EmbeddingModelOptions.Revision);
		File.WriteAllText(Path.Combine(_directory, "model.onnx"), "x");

		// Act / Assert
		EmbeddingModelProvisioningService.IsProvisioned(_directory, TestFiles).Should().BeFalse();
	}

	[Fact]
	public void IsProvisioned_OneFileMissing_ReturnsFalse()
	{
		// Arrange
		WriteFiles();
		WriteMarker(EmbeddingModelOptions.Revision);
		File.Delete(Path.Combine(_directory, "vocab.txt"));

		// Act / Assert
		EmbeddingModelProvisioningService.IsProvisioned(_directory, TestFiles).Should().BeFalse();
	}

	[Fact]
	public void IsProvisioned_MarkerMatchesAndFilesIntact_ReturnsTrue()
	{
		// Arrange
		WriteFiles();
		WriteMarker(EmbeddingModelOptions.Revision);

		// Act / Assert
		EmbeddingModelProvisioningService.IsProvisioned(_directory, TestFiles).Should().BeTrue();
	}

	[Fact]
	public void IsProvisioned_MarkerHasSurroundingWhitespace_StillMatches()
	{
		// Arrange — tolerate a trailing newline from a hand-staged air-gapped install.
		WriteFiles();
		File.WriteAllText(
			Path.Combine(_directory, EmbeddingModelOptions.MarkerFileName),
			$"  {EmbeddingModelOptions.Revision}\r\n");

		// Act / Assert
		EmbeddingModelProvisioningService.IsProvisioned(_directory, TestFiles).Should().BeTrue();
	}

	[Fact]
	public void IsProvisioned_DirectoryDoesNotExist_ReturnsFalseRatherThanThrowing()
	{
		string missing = Path.Combine(_directory, "nope");

		EmbeddingModelProvisioningService.IsProvisioned(missing, TestFiles).Should().BeFalse();
	}

	private void WriteFiles()
	{
		File.WriteAllText(Path.Combine(_directory, "model.onnx"), "ABCD");
		File.WriteAllText(Path.Combine(_directory, "vocab.txt"), "abc");
	}

	private void WriteMarker(string revision) =>
		File.WriteAllText(Path.Combine(_directory, EmbeddingModelOptions.MarkerFileName), revision);

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_directory))
			{
				Directory.Delete(_directory, recursive: true);
			}
		}
		catch (IOException)
		{
			// Best effort — a leftover temp directory must not fail the run.
		}

		GC.SuppressFinalize(this);
	}
}
