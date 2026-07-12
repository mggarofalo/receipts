using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.IntegrationTests.Services;

// Exercises the REAL LocalImageStorageService (RECEIPTS-806) against a real temp directory — no mock,
// no Postgres. Validates the temp-then-rename atomic write introduced to replace the previous in-place
// File.WriteAllBytesAsync(finalPath, ...) (FileMode.Create = truncate-then-write):
//
//   * a successful (re-)upload replaces the file and returns the correct relative path, and
//   * a re-upload whose write/promote fails leaves the pre-existing image byte-for-byte intact
//     (never truncated/corrupted) and leaves no orphaned temp file behind.
//
// This is filesystem-only, so it needs no PostgresFixture; it uses an isolated temp dir under
// Path.GetTempPath() and cleans it up on dispose.
[Trait("Category", "Integration")]
public sealed class LocalImageStorageServiceTests : IDisposable
{
	private readonly string _root;
	private readonly LocalImageStorageService _service;

	public LocalImageStorageServiceTests()
	{
		_root = Path.Combine(Path.GetTempPath(), "receipts-806-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_root);

		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["ImageStorage:Path"] = _root,
			})
			.Build();

		_service = new LocalImageStorageService(configuration);
	}

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_root))
			{
				Directory.Delete(_root, recursive: true);
			}
		}
		catch (IOException)
		{
			// Best-effort test cleanup — a leftover temp dir must not fail the suite.
		}
		catch (UnauthorizedAccessException)
		{
			// Best-effort test cleanup — a leftover temp dir must not fail the suite.
		}
	}

	private static byte[] Bytes(byte fill, int length)
	{
		byte[] bytes = new byte[length];
		Array.Fill(bytes, fill);
		return bytes;
	}

	private string ReceiptDir(Guid receiptId) => Path.Combine(_root, receiptId.ToString());

	private IReadOnlyList<string> TempFilesIn(Guid receiptId)
	{
		string dir = ReceiptDir(receiptId);
		return Directory.Exists(dir)
			? Directory.EnumerateFiles(dir, "*.tmp").ToList()
			: [];
	}

	[Fact]
	public async Task SaveOriginalAsync_ReUploadSameExtension_ReplacesFileAndReturnsRelativePath()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		byte[] first = Bytes(0x11, 256);
		byte[] second = Bytes(0x22, 128);

		// Act — save, then re-upload with the same extension.
		string firstPath = await _service.SaveOriginalAsync(receiptId, first, ".png", CancellationToken.None);
		string secondPath = await _service.SaveOriginalAsync(receiptId, second, ".png", CancellationToken.None);

		// Assert — relative path is stable and correct, and the file holds the new bytes.
		string expected = Path.Combine(receiptId.ToString(), "original.png");
		firstPath.Should().Be(expected);
		secondPath.Should().Be(expected);

		string absolute = Path.Combine(_root, secondPath);
		byte[] onDisk = await File.ReadAllBytesAsync(absolute);
		onDisk.Should().Equal(second, "a successful re-upload replaces the original in place");

		TempFilesIn(receiptId).Should().BeEmpty("the temp file is renamed into place, never left behind");
	}

	[Fact]
	public async Task SaveOriginalAsync_WriteCancelledMidReUpload_LeavesExistingOriginalIntact()
	{
		// Arrange — an existing, good original.
		Guid receiptId = Guid.NewGuid();
		byte[] original = Bytes(0x11, 256);
		await _service.SaveOriginalAsync(receiptId, original, ".png", CancellationToken.None);

		// Act — a re-upload whose write is forced to fail (cancelled token). The old in-place write
		// opened the existing original with FileMode.Create (truncating it) before writing; the atomic
		// temp-then-rename never touches the existing file unless the whole write succeeds.
		byte[] replacement = Bytes(0x22, 512);
		using CancellationTokenSource cts = new();
		await cts.CancelAsync();

		Func<Task> act = async () =>
			await _service.SaveOriginalAsync(receiptId, replacement, ".png", cts.Token);

		// Assert — the call fails and the pre-existing original is byte-for-byte intact.
		await act.Should().ThrowAsync<OperationCanceledException>();

		string absolute = Path.Combine(_root, receiptId.ToString(), "original.png");
		byte[] onDisk = await File.ReadAllBytesAsync(absolute);
		onDisk.Should().Equal(original, "a failed re-upload must not truncate or corrupt the existing original");

		TempFilesIn(receiptId).Should().BeEmpty("a failed write must not leave an orphaned temp file");
	}

	[Fact]
	public async Task SaveOriginalAsync_PromoteFails_CleansUpTempAndPropagates()
	{
		// Arrange — occupy the final promote target with a *directory* so the atomic
		// File.Move(temp -> original.png) fails AFTER the temp file has already been written. This is
		// the "point at a path that will fail" failure mode: it deterministically drives the catch/
		// cleanup path that an in-place FileMode.Create write never exercised.
		Guid receiptId = Guid.NewGuid();
		string dir = ReceiptDir(receiptId);
		string blockingDir = Path.Combine(dir, "original.png");
		Directory.CreateDirectory(blockingDir);
		// Make it non-empty so the rename fails identically across platforms.
		await File.WriteAllBytesAsync(Path.Combine(blockingDir, "marker"), Bytes(0x33, 8));

		// Act
		Func<Task> act = async () =>
			await _service.SaveOriginalAsync(receiptId, Bytes(0x44, 64), ".png", CancellationToken.None);

		// Assert — the failure propagates, the temp file is cleaned up, and the blocking dir is intact.
		await act.Should().ThrowAsync<Exception>();

		TempFilesIn(receiptId).Should().BeEmpty("a failed promote must delete the temp file it created");
		Directory.Exists(blockingDir).Should().BeTrue("the failed write must not disturb what already occupied the path");
	}

	[Fact]
	public async Task SaveProcessedAsync_ReUpload_ReplacesFileAndReturnsRelativePath()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		byte[] first = Bytes(0x55, 200);
		byte[] second = Bytes(0x66, 64);

		// Act
		string firstPath = await _service.SaveProcessedAsync(receiptId, first, CancellationToken.None);
		string secondPath = await _service.SaveProcessedAsync(receiptId, second, CancellationToken.None);

		// Assert
		string expected = Path.Combine(receiptId.ToString(), "processed.png");
		firstPath.Should().Be(expected);
		secondPath.Should().Be(expected);

		byte[] onDisk = await File.ReadAllBytesAsync(Path.Combine(_root, secondPath));
		onDisk.Should().Equal(second, "a successful re-save replaces the processed image in place");

		TempFilesIn(receiptId).Should().BeEmpty("the temp file is renamed into place, never left behind");
	}

	[Fact]
	public async Task SaveProcessedAsync_WriteCancelled_LeavesExistingProcessedIntact()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		byte[] original = Bytes(0x55, 200);
		await _service.SaveProcessedAsync(receiptId, original, CancellationToken.None);

		// Act — re-save with a cancelled token.
		using CancellationTokenSource cts = new();
		await cts.CancelAsync();

		Func<Task> act = async () =>
			await _service.SaveProcessedAsync(receiptId, Bytes(0x66, 400), cts.Token);

		// Assert
		await act.Should().ThrowAsync<OperationCanceledException>();

		byte[] onDisk = await File.ReadAllBytesAsync(Path.Combine(_root, receiptId.ToString(), "processed.png"));
		onDisk.Should().Equal(original, "a failed re-save must not truncate or corrupt the existing processed image");

		TempFilesIn(receiptId).Should().BeEmpty("a failed write must not leave an orphaned temp file");
	}
}
