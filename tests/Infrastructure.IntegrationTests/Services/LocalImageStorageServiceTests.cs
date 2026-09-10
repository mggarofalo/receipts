using Application.Models.Images;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
public sealed class LocalImageStorageServiceTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "receipts-964-" + Guid.NewGuid().ToString("N"));
	private readonly IConfiguration _configuration;
	private readonly LocalImageStorageService _service;

	public LocalImageStorageServiceTests()
	{
		Directory.CreateDirectory(_root);
		_configuration = new ConfigurationBuilder().AddInMemoryCollection(
			new Dictionary<string, string?> { ["ImageStorage:Path"] = _root }).Build();
		_service = new LocalImageStorageService(_configuration);
	}

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); }
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
	}

	[Fact]
	public async Task SaveImageSetAsync_PublishesImmutableCompleteVersion()
	{
		Guid id = Guid.NewGuid();
		byte[] original = [1, 2, 3];
		byte[] processed = [4, 5, 6];

		ReceiptImageSet set = await _service.SaveImageSetAsync(id, original, ".jpg", processed, CancellationToken.None);

		Path.GetDirectoryName(set.OriginalImagePath).Should().Be(Path.GetDirectoryName(set.ProcessedImagePath));
		Path.GetFileName(Path.GetDirectoryName(set.OriginalImagePath)).Should().StartWith("set-");
		(await File.ReadAllBytesAsync(Path.Combine(_root, set.OriginalImagePath))).Should().Equal(original);
		(await File.ReadAllBytesAsync(Path.Combine(_root, set.ProcessedImagePath))).Should().Equal(processed);
		Directory.EnumerateDirectories(Path.Combine(_root, id.ToString()), ".staging-*").Should().BeEmpty();
	}

	[Fact]
	public async Task SaveImageSetAsync_ProcessedWriteFails_PreservesPriorSetAndPublishesNoPartialVersion()
	{
		Guid id = Guid.NewGuid();
		ReceiptImageSet prior = await _service.SaveImageSetAsync(id, [1], ".jpg", [2], CancellationToken.None);
		int writes = 0;
		LocalImageStorageService failing = new(_configuration, async (path, bytes, ct) =>
		{
			if (++writes == 2)
			{
				throw new IOException("processed write failed");
			}

			await File.WriteAllBytesAsync(path, bytes, ct);
		});

		Func<Task> act = async () => await failing.SaveImageSetAsync(id, [3], ".jpg", [4], CancellationToken.None);

		await act.Should().ThrowAsync<IOException>();
		(await File.ReadAllBytesAsync(Path.Combine(_root, prior.OriginalImagePath))).Should().Equal([1]);
		(await File.ReadAllBytesAsync(Path.Combine(_root, prior.ProcessedImagePath))).Should().Equal([2]);
		Directory.EnumerateDirectories(Path.Combine(_root, id.ToString())).Should().ContainSingle();
		Directory.EnumerateDirectories(Path.Combine(_root, id.ToString()), ".staging-*").Should().BeEmpty();
	}

	[Fact]
	public async Task SaveImageSetAsync_ConcurrentSaves_NeverMixVariants()
	{
		Guid id = Guid.NewGuid();
		Task<ReceiptImageSet>[] saves = Enumerable.Range(1, 12)
			.Select(value => _service.SaveImageSetAsync(id, [(byte)value], ".jpg", [(byte)(value + 100)], CancellationToken.None))
			.ToArray();

		ReceiptImageSet[] sets = await Task.WhenAll(saves);

		sets.Select(x => Path.GetDirectoryName(x.OriginalImagePath)).Should().OnlyHaveUniqueItems();
		foreach (ReceiptImageSet set in sets)
		{
			byte original = (await File.ReadAllBytesAsync(Path.Combine(_root, set.OriginalImagePath))).Single();
			byte processed = (await File.ReadAllBytesAsync(Path.Combine(_root, set.ProcessedImagePath))).Single();
			processed.Should().Be((byte)(original + 100));
			Path.GetDirectoryName(set.OriginalImagePath).Should().Be(Path.GetDirectoryName(set.ProcessedImagePath));
		}
	}

	[Fact]
	public async Task SaveImageSetAsync_ExtensionTraversal_IsRejectedBeforeCreatingReceiptDirectory()
	{
		Guid id = Guid.NewGuid();

		Func<Task> act = async () => await _service.SaveImageSetAsync(
			id, [1], $"{Path.DirectorySeparatorChar}outside", [2], CancellationToken.None);

		await act.Should().ThrowAsync<ArgumentException>();
		Directory.Exists(Path.Combine(_root, id.ToString())).Should().BeFalse();
	}

	[Fact]
	public async Task CleanupUnreferencedAsync_LegacyLayoutPreservesReferencedFileWithoutDeletingVersionDirectory()
	{
		Guid id = Guid.NewGuid();
		ReceiptImageSet current = await _service.SaveImageSetAsync(id, [1], ".jpg", [2], CancellationToken.None);
		string receiptDirectory = Path.Combine(_root, id.ToString());
		string referencedLegacy = Path.Combine(receiptDirectory, "original.jpg");
		string orphanLegacy = Path.Combine(receiptDirectory, "processed.png");
		await File.WriteAllBytesAsync(referencedLegacy, [3]);
		await File.WriteAllBytesAsync(orphanLegacy, [4]);
		File.SetLastWriteTimeUtc(referencedLegacy, DateTime.UtcNow.AddHours(-3));
		File.SetLastWriteTimeUtc(orphanLegacy, DateTime.UtcNow.AddHours(-3));

		await _service.CleanupUnreferencedAsync(
			new HashSet<string> { Path.Combine(id.ToString(), "original.jpg"), current.OriginalImagePath, current.ProcessedImagePath },
			DateTimeOffset.UtcNow.AddHours(-1), CancellationToken.None);

		File.Exists(referencedLegacy).Should().BeTrue();
		File.Exists(orphanLegacy).Should().BeFalse();
		File.Exists(Path.Combine(_root, current.OriginalImagePath)).Should().BeTrue();
	}

	[Fact]
	public async Task CleanupUnreferencedAsync_ReparsePointSet_IsSkippedWithoutTouchingTarget()
	{
		Guid id = Guid.NewGuid();
		string external = Path.Combine(_root, "external");
		Directory.CreateDirectory(external);
		string marker = Path.Combine(external, "keep.txt");
		await File.WriteAllTextAsync(marker, "keep");
		string receiptDirectory = Path.Combine(_root, id.ToString());
		Directory.CreateDirectory(receiptDirectory);
		string link = Path.Combine(receiptDirectory, "set-linked");
		Directory.CreateSymbolicLink(link, external);

		ImageCleanupResult result = await _service.CleanupUnreferencedAsync(
			new HashSet<string>(), DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);

		File.Exists(marker).Should().BeTrue();
		Directory.Exists(link).Should().BeTrue();
		result.FailedEntries.Should().Be(1, "skipped unsafe entries are observable for retry/operations");
	}

	[Fact]
	public async Task CleanupUnreferencedAsync_PreservesReferencedAndRecent_RemovesOldOrphansAndRetriesIdempotently()
	{
		Guid liveId = Guid.NewGuid();
		Guid purgedId = Guid.NewGuid();
		ReceiptImageSet live = await _service.SaveImageSetAsync(liveId, [1], ".jpg", [2], CancellationToken.None);
		ReceiptImageSet oldOrphan = await _service.SaveImageSetAsync(liveId, [3], ".jpg", [4], CancellationToken.None);
		ReceiptImageSet recent = await _service.SaveImageSetAsync(liveId, [5], ".jpg", [6], CancellationToken.None);
		ReceiptImageSet purged = await _service.SaveImageSetAsync(purgedId, [7], ".jpg", [8], CancellationToken.None);
		DateTime old = DateTime.UtcNow.AddHours(-3);
		Directory.SetLastWriteTimeUtc(Path.Combine(_root, Path.GetDirectoryName(oldOrphan.OriginalImagePath)!), old);
		Directory.SetLastWriteTimeUtc(Path.Combine(_root, Path.GetDirectoryName(purged.OriginalImagePath)!), old);

		ImageCleanupResult result = await _service.CleanupUnreferencedAsync(
			new HashSet<string> { live.OriginalImagePath, live.ProcessedImagePath },
			DateTimeOffset.UtcNow.AddHours(-1), CancellationToken.None);

		result.Should().Be(new ImageCleanupResult(2, 1));
		File.Exists(Path.Combine(_root, live.OriginalImagePath)).Should().BeTrue();
		File.Exists(Path.Combine(_root, recent.OriginalImagePath)).Should().BeTrue();
		File.Exists(Path.Combine(_root, oldOrphan.OriginalImagePath)).Should().BeFalse();
		Directory.Exists(Path.Combine(_root, purgedId.ToString())).Should().BeFalse();
		(await _service.CleanupUnreferencedAsync(
			new HashSet<string> { live.OriginalImagePath, live.ProcessedImagePath },
			DateTimeOffset.UtcNow.AddHours(-1), CancellationToken.None)).Should().Be(new ImageCleanupResult(0, 0));
	}
}
