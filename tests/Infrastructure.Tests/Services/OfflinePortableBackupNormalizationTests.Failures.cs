using Application.Normalization;
using FluentAssertions;
using Infrastructure.Services;
using Xunit;

namespace Infrastructure.Tests.Services;

public sealed partial class OfflinePortableBackupNormalizationTests
{
	[Fact]
	public async Task Apply_PriorOfflineOutputAsNewSource_ExplainsHowToReviseAgainstOriginal()
	{
		string source = await CreateAsync(9);
		string manifest = await SaveAsync(await ManifestAsync(source, true));
		await service.ApplyAsync(source, manifest, Output);
		byte[] original = await File.ReadAllBytesAsync(source), output = await File.ReadAllBytesAsync(Output);
		string revised = await SaveAsync(await ManifestAsync(Output, false));
		string next = Path.Combine(directory, "C.db");

		Func<Task> apply = () => service.ApplyAsync(Output, revised, next);

		await apply.Should().ThrowAsync<InvalidDataException>().WithMessage("*prior offline output*original source A*new output path*");
		Directory.GetFileSystemEntries(directory, "C.db*").Should().BeEmpty();
		(await File.ReadAllBytesAsync(source)).Should().Equal(original);
		(await File.ReadAllBytesAsync(Output)).Should().Equal(output);
	}

	[Fact]
	public async Task Apply_CancellationAfterCopy_RemovesOwnedArtifactsAndPreservesOriginal()
	{
		string source = await CreateAsync();
		string manifest = await SaveAsync(await ManifestAsync(source, true));
		byte[] original = await File.ReadAllBytesAsync(source);
		using var cancellation = new CancellationTokenSource();
		var inspection = new PostCopyInspection(path =>
		{
			File.Exists(path).Should().BeTrue("cancellation must happen after the copy exists");
			cancellation.Cancel();
		});
		var subject = new PortableBackupNormalizationService(inspection);

		Func<Task> apply = () => subject.ApplyAsync(source, manifest, Output, cancellation.Token);

		await apply.Should().ThrowAsync<OperationCanceledException>();
		inspection.PostCopyCalls.Should().Be(1);
		Directory.GetFileSystemEntries(directory, "B.db*").Should().BeEmpty();
		(await File.ReadAllBytesAsync(source)).Should().Equal(original);
	}

	[Theory]
	[InlineData("manifest")]
	[InlineData("lineage")]
	[InlineData("output")]
	public async Task Apply_PublicationObstacleAfterCopy_PreservesObstacleAndRemovesOwnedArtifacts(string obstacle)
	{
		string source = await CreateAsync();
		string manifest = await SaveAsync(await ManifestAsync(source, true));
		byte[] original = await File.ReadAllBytesAsync(source);
		string blocked = obstacle switch { "manifest" => Output + ".manifest.json", "lineage" => Output + ".lineage.json", _ => Output };
		var inspection = new PostCopyInspection(path =>
		{
			File.Exists(path).Should().BeTrue();
			Directory.CreateDirectory(blocked);
			File.WriteAllText(Path.Combine(blocked, "preexisting.txt"), "preserve this obstacle");
		});
		var subject = new PortableBackupNormalizationService(inspection);

		Func<Task> apply = () => subject.ApplyAsync(source, manifest, Output);

		var failure = await apply.Should().ThrowAsync<Exception>();
		failure.Which.Should().Match<Exception>(error => error is IOException || error is UnauthorizedAccessException,
			"publishing over a directory must fail with a filesystem error");
		inspection.PostCopyCalls.Should().Be(1);
		Directory.GetFileSystemEntries(directory, "B.db*").Should().Equal(blocked);
		(await File.ReadAllTextAsync(Path.Combine(blocked, "preexisting.txt"))).Should().Be("preserve this obstacle");
		(await File.ReadAllBytesAsync(source)).Should().Equal(original);
	}

	private sealed class PostCopyInspection(Action<string> afterCopy) : IOfflinePortableBackupInspection
	{
		private readonly OfflinePortableBackupInspection inner = new();
		public int PostCopyCalls { get; private set; }
		public async Task<OfflineBackupInspection> InspectAsync(string sourcePath, CancellationToken cancellationToken = default)
		{
			if (sourcePath.EndsWith(".tmp", StringComparison.Ordinal))
			{
				PostCopyCalls++;
				afterCopy(sourcePath);
			}
			return await inner.InspectAsync(sourcePath, cancellationToken);
		}
		public void VerifyBaseline(OfflineBackupInspection baseline, OfflineBackupInspection candidate) => inner.VerifyBaseline(baseline, candidate);
		public void ValidateManifest(OfflineNormalizationManifest manifest, OfflineBackupInspection source, bool requireReviewed, DateTimeOffset now) => inner.ValidateManifest(manifest, source, requireReviewed, now);
	}
}
