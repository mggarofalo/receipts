using Application.Commands.Receipt.UploadImage;
using Application.Interfaces.Services;
using Application.Models.Images;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Application.Tests.Commands.Receipt.UploadImage;

public class UploadReceiptImageCommandValidationTests
{
	[Fact]
	public void Constructor_InvalidArguments_Throw()
	{
		((Action)(() => new UploadReceiptImageCommand(Guid.NewGuid(), null!, "image/jpeg", ".jpg"))).Should().Throw<ArgumentNullException>();
		((Action)(() => new UploadReceiptImageCommand(Guid.NewGuid(), [], "image/jpeg", ".jpg"))).Should().Throw<ArgumentException>();
		((Action)(() => new UploadReceiptImageCommand(Guid.NewGuid(), [1], " ", ".jpg"))).Should().Throw<ArgumentException>();
		((Action)(() => new UploadReceiptImageCommand(Guid.NewGuid(), [1], "image/jpeg", " "))).Should().Throw<ArgumentException>();
	}
}

public class UploadReceiptImageCommandHandlerTests
{
	private readonly Mock<IReceiptService> _receipts = new();
	private readonly Mock<IImageStorageService> _storage = new();
	private readonly Mock<IImageProcessingService> _processing = new();
	private UploadReceiptImageCommandHandler Handler => new(_receipts.Object, _storage.Object, _processing.Object);
	private static ReceiptImageSet Set(Guid id, string version) => new($"{id}/set-{version}/original.jpg", $"{id}/set-{version}/processed.png");

	private void ArrangeValid(Guid id, byte[] original, byte[] processed, ReceiptImageSet published)
	{
		_receipts.Setup(x => x.ExistsAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
		_processing.Setup(x => x.PreprocessAsync(original, "image/jpeg", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ImageProcessingResult(processed, 10, 20));
		_storage.Setup(x => x.SaveImageSetAsync(id, original, ".jpg", processed, It.IsAny<CancellationToken>()))
			.ReturnsAsync(published);
	}

	[Fact]
	public async Task Handle_PublishesCompleteSetBeforeDatabaseSwap()
	{
		Guid id = Guid.NewGuid();
		byte[] original = [1], processed = [2];
		ReceiptImageSet published = Set(id, "new");
		bool saved = false;
		ArrangeValid(id, original, processed, published);
		_storage.Setup(x => x.SaveImageSetAsync(id, original, ".jpg", processed, It.IsAny<CancellationToken>()))
			.Callback(() => saved = true).ReturnsAsync(published);
		_receipts.Setup(x => x.ReplaceImagePathsAsync(id, published, It.IsAny<CancellationToken>()))
			.Callback(() => saved.Should().BeTrue()).ReturnsAsync((ReceiptImageSet?)null);

		UploadReceiptImageResult result = await Handler.Handle(
			new UploadReceiptImageCommand(id, original, "image/jpeg", ".jpg"), CancellationToken.None);

		result.Should().BeEquivalentTo(new UploadReceiptImageResult(published.OriginalImagePath, published.ProcessedImagePath));
	}

	[Fact]
	public async Task Handle_HoldsReconciliationLeaseAcrossPublishAndDatabaseSwap()
	{
		Guid id = Guid.NewGuid();
		byte[] original = [1], processed = [2];
		ReceiptImageSet published = Set(id, "new");
		List<string> events = [];
		ArrangeValid(id, original, processed, published);
		Mock<IReceiptImageReconciliationLock> gate = new();
		gate.Setup(x => x.AcquireAsync(It.IsAny<CancellationToken>()))
			.Callback(() => events.Add("acquire"))
			.ReturnsAsync(new RecordingLease(() => events.Add("release")));
		_storage.Setup(x => x.SaveImageSetAsync(id, original, ".jpg", processed, It.IsAny<CancellationToken>()))
			.Callback(() => events.Add("publish")).ReturnsAsync(published);
		_receipts.Setup(x => x.ReplaceImagePathsAsync(id, published, It.IsAny<CancellationToken>()))
			.Callback(() => events.Add("swap")).ReturnsAsync((ReceiptImageSet?)null);
		UploadReceiptImageCommandHandler handler = new(
			_receipts.Object, _storage.Object, _processing.Object, gate.Object,
			NullLogger<UploadReceiptImageCommandHandler>.Instance);

		await handler.Handle(new UploadReceiptImageCommand(id, original, "image/jpeg", ".jpg"), CancellationToken.None);

		events.Should().Equal("acquire", "publish", "swap", "release");
	}

	[Fact]
	public async Task Handle_DatabaseSwapFails_LeavesNewVersionForBackgroundReconciliation()
	{
		Guid id = Guid.NewGuid();
		byte[] original = [1], processed = [2];
		ReceiptImageSet published = Set(id, "orphan");
		ArrangeValid(id, original, processed, published);
		_receipts.Setup(x => x.ReplaceImagePathsAsync(id, published, It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("database unavailable"));

		Func<Task> act = async () => await Handler.Handle(
			new UploadReceiptImageCommand(id, original, "image/jpeg", ".jpg"), CancellationToken.None);

		await act.Should().ThrowAsync<InvalidOperationException>();
		_storage.Verify(x => x.DeleteReceiptImagesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task Handle_SuccessfulSwap_LeavesPreviousVersionForBackgroundReconciliation()
	{
		Guid id = Guid.NewGuid();
		byte[] original = [1], processed = [2];
		ReceiptImageSet previous = Set(id, "previous");
		ReceiptImageSet published = Set(id, "new");
		ArrangeValid(id, original, processed, published);
		_receipts.Setup(x => x.ReplaceImagePathsAsync(id, published, It.IsAny<CancellationToken>())).ReturnsAsync(previous);

		await Handler.Handle(new UploadReceiptImageCommand(id, original, "image/jpeg", ".jpg"), CancellationToken.None);

		_storage.Verify(x => x.DeleteReceiptImagesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task Handle_ValidationFailure_DoesNotTouchStorageOrDatabasePaths()
	{
		Guid id = Guid.NewGuid();
		byte[] bytes = [0];
		_receipts.Setup(x => x.ExistsAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
		_processing.Setup(x => x.PreprocessAsync(bytes, "image/jpeg", It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("invalid image"));

		Func<Task> act = async () => await Handler.Handle(
			new UploadReceiptImageCommand(id, bytes, "image/jpeg", ".jpg"), CancellationToken.None);

		await act.Should().ThrowAsync<InvalidOperationException>();
		_storage.Verify(x => x.SaveImageSetAsync(It.IsAny<Guid>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
		_receipts.Verify(x => x.ReplaceImagePathsAsync(It.IsAny<Guid>(), It.IsAny<ReceiptImageSet>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	private sealed class RecordingLease(Action dispose) : IAsyncDisposable
	{
		public ValueTask DisposeAsync()
		{
			dispose();
			return ValueTask.CompletedTask;
		}
	}
}
