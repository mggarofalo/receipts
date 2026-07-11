using Application.Commands.Receipt.UploadImage;
using Application.Interfaces.Services;
using FluentAssertions;
using Moq;

namespace Application.Tests.Commands.Receipt.UploadImage;

public class UploadReceiptImageCommandValidationTests
{
	[Fact]
	public void Constructor_NullImageBytes_ThrowsArgumentNullException()
	{
		// Act
		Action act = () => new UploadReceiptImageCommand(Guid.NewGuid(), null!, "image/jpeg", ".jpg");

		// Assert
		act.Should().Throw<ArgumentNullException>()
			.And.ParamName.Should().Be("imageBytes");
	}

	[Fact]
	public void Constructor_EmptyImageBytes_ThrowsArgumentException()
	{
		// Act
		Action act = () => new UploadReceiptImageCommand(Guid.NewGuid(), [], "image/jpeg", ".jpg");

		// Assert
		act.Should().Throw<ArgumentException>()
			.WithMessage($"*{UploadReceiptImageCommand.ImageBytesCannotBeEmpty}*");
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void Constructor_InvalidContentType_ThrowsArgumentException(string? contentType)
	{
		// Act
		Action act = () => new UploadReceiptImageCommand(Guid.NewGuid(), [0xFF], contentType!, ".jpg");

		// Assert
		act.Should().Throw<ArgumentException>()
			.WithMessage($"*{UploadReceiptImageCommand.ContentTypeCannotBeEmpty}*");
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void Constructor_InvalidFileExtension_ThrowsArgumentException(string? extension)
	{
		// Act
		Action act = () => new UploadReceiptImageCommand(Guid.NewGuid(), [0xFF], "image/jpeg", extension!);

		// Assert
		act.Should().Throw<ArgumentException>()
			.WithMessage($"*{UploadReceiptImageCommand.FileExtensionCannotBeEmpty}*");
	}

	[Fact]
	public void Constructor_ValidArguments_SetsProperties()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		byte[] bytes = [0xFF, 0xD8];

		// Act
		UploadReceiptImageCommand command = new(receiptId, bytes, "image/jpeg", ".jpg");

		// Assert
		command.ReceiptId.Should().Be(receiptId);
		command.ImageBytes.Should().BeSameAs(bytes);
		command.ContentType.Should().Be("image/jpeg");
		command.FileExtension.Should().Be(".jpg");
	}
}

public class UploadReceiptImageCommandHandlerTests
{
	private readonly Mock<IReceiptService> _mockReceiptService;
	private readonly Mock<IImageStorageService> _mockStorageService;
	private readonly Mock<IImageProcessingService> _mockProcessingService;
	private readonly UploadReceiptImageCommandHandler _handler;

	public UploadReceiptImageCommandHandlerTests()
	{
		_mockReceiptService = new Mock<IReceiptService>();
		_mockStorageService = new Mock<IImageStorageService>();
		_mockProcessingService = new Mock<IImageProcessingService>();
		_handler = new UploadReceiptImageCommandHandler(
			_mockReceiptService.Object,
			_mockStorageService.Object,
			_mockProcessingService.Object);
	}

	[Fact]
	public async Task Handle_ValidCommand_ReturnsPaths()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		byte[] imageBytes = [0xFF, 0xD8, 0xFF, 0xE0]; // JPEG magic bytes
		UploadReceiptImageCommand command = new(receiptId, imageBytes, "image/jpeg", ".jpg");

		_mockReceiptService
			.Setup(s => s.ExistsAsync(receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		_mockStorageService
			.Setup(s => s.SaveOriginalAsync(receiptId, imageBytes, ".jpg", It.IsAny<CancellationToken>()))
			.ReturnsAsync($"{receiptId}/original.jpg");

		ImageProcessingResult processingResult = new([0x89, 0x50, 0x4E, 0x47], 100, 200);
		_mockProcessingService
			.Setup(s => s.PreprocessAsync(imageBytes, "image/jpeg", It.IsAny<CancellationToken>()))
			.ReturnsAsync(processingResult);

		_mockStorageService
			.Setup(s => s.SaveProcessedAsync(receiptId, processingResult.ProcessedBytes, It.IsAny<CancellationToken>()))
			.ReturnsAsync($"{receiptId}/processed.png");

		// Act
		UploadReceiptImageResult result = await _handler.Handle(command, CancellationToken.None);

		// Assert
		result.OriginalImagePath.Should().Be($"{receiptId}/original.jpg");
		result.ProcessedImagePath.Should().Be($"{receiptId}/processed.png");

		_mockReceiptService.Verify(
			s => s.UpdateImagePathsAsync(receiptId, $"{receiptId}/original.jpg", $"{receiptId}/processed.png", It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Fact]
	public async Task Handle_ReceiptNotFound_ThrowsKeyNotFoundException()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		byte[] imageBytes = [0xFF, 0xD8, 0xFF, 0xE0];
		UploadReceiptImageCommand command = new(receiptId, imageBytes, "image/jpeg", ".jpg");

		_mockReceiptService
			.Setup(s => s.ExistsAsync(receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		// Act
		Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<KeyNotFoundException>();
	}

	[Fact]
	public async Task Handle_ValidCommand_CallsServicesInOrder()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		byte[] imageBytes = [0xFF, 0xD8];
		UploadReceiptImageCommand command = new(receiptId, imageBytes, "image/png", ".png");

		_mockReceiptService
			.Setup(s => s.ExistsAsync(receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		_mockStorageService
			.Setup(s => s.SaveOriginalAsync(receiptId, imageBytes, ".png", It.IsAny<CancellationToken>()))
			.ReturnsAsync($"{receiptId}/original.png");

		ImageProcessingResult processingResult = new([0x01], 50, 50);
		_mockProcessingService
			.Setup(s => s.PreprocessAsync(imageBytes, "image/png", It.IsAny<CancellationToken>()))
			.ReturnsAsync(processingResult);

		_mockStorageService
			.Setup(s => s.SaveProcessedAsync(receiptId, processingResult.ProcessedBytes, It.IsAny<CancellationToken>()))
			.ReturnsAsync($"{receiptId}/processed.png");

		// Act
		await _handler.Handle(command, CancellationToken.None);

		// Assert
		_mockReceiptService.Verify(s => s.ExistsAsync(receiptId, It.IsAny<CancellationToken>()), Times.Once);
		_mockStorageService.Verify(s => s.SaveOriginalAsync(receiptId, imageBytes, ".png", It.IsAny<CancellationToken>()), Times.Once);
		_mockProcessingService.Verify(s => s.PreprocessAsync(imageBytes, "image/png", It.IsAny<CancellationToken>()), Times.Once);
		_mockStorageService.Verify(s => s.SaveProcessedAsync(receiptId, processingResult.ProcessedBytes, It.IsAny<CancellationToken>()), Times.Once);
		_mockReceiptService.Verify(
			s => s.UpdateImagePathsAsync(receiptId, $"{receiptId}/original.png", $"{receiptId}/processed.png", It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Fact]
	public async Task Handle_ProcessingServiceThrows_PropagatesInvalidOperationException()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		byte[] imageBytes = [0xFF, 0xD8];
		UploadReceiptImageCommand command = new(receiptId, imageBytes, "image/jpeg", ".jpg");

		_mockReceiptService
			.Setup(s => s.ExistsAsync(receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		_mockProcessingService
			.Setup(s => s.PreprocessAsync(imageBytes, "image/jpeg", It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("The uploaded file is not a supported image format."));

		// Act
		Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*not a supported image format*");

		// Validation runs before any write, so nothing is persisted for a rejected upload.
		_mockStorageService.Verify(
			s => s.SaveOriginalAsync(It.IsAny<Guid>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
			Times.Never);
		_mockStorageService.Verify(
			s => s.SaveProcessedAsync(It.IsAny<Guid>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	[Fact]
	public async Task Handle_InvalidImage_DoesNotDeleteExistingReceiptImages()
	{
		// Regression test for the untrusted-input-data-loss finding: a receipt that already has
		// good images must not lose them when a subsequent upload is rejected during validation.
		// The handler must reject BEFORE touching permanent storage and must NEVER call
		// DeleteReceiptImagesAsync (which recursively deletes ALL of the receipt's images).

		// Arrange
		Guid receiptId = Guid.NewGuid();
		byte[] badBytes = [0x00, 0x01, 0x02, 0x03]; // not a real image
		UploadReceiptImageCommand command = new(receiptId, badBytes, "image/png", ".png");

		_mockReceiptService
			.Setup(s => s.ExistsAsync(receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		_mockProcessingService
			.Setup(s => s.PreprocessAsync(badBytes, "image/png", It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException(
				"The uploaded file is not a supported image format. Only JPEG and PNG are accepted."));

		// Act
		Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>();

		// The pre-existing images are preserved: no destructive delete, no overwrite, no DB update.
		_mockStorageService.Verify(
			s => s.DeleteReceiptImagesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
			Times.Never);
		_mockStorageService.Verify(
			s => s.SaveOriginalAsync(It.IsAny<Guid>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
			Times.Never);
		_mockReceiptService.Verify(
			s => s.UpdateImagePathsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	[Fact]
	public async Task Handle_ValidCommand_ValidatesBeforeWritingToStorage()
	{
		// Ordering guard: PreprocessAsync (validation) MUST run before SaveOriginalAsync (the first
		// write to permanent storage), so a rejected upload can never overwrite existing images.
		// Arrange
		Guid receiptId = Guid.NewGuid();
		byte[] imageBytes = [0xFF, 0xD8];
		UploadReceiptImageCommand command = new(receiptId, imageBytes, "image/jpeg", ".jpg");

		bool preprocessCalled = false;

		_mockReceiptService
			.Setup(s => s.ExistsAsync(receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		_mockProcessingService
			.Setup(s => s.PreprocessAsync(imageBytes, "image/jpeg", It.IsAny<CancellationToken>()))
			.Callback(() => preprocessCalled = true)
			.ReturnsAsync(new ImageProcessingResult([0x89, 0x50, 0x4E, 0x47], 100, 100));

		_mockStorageService
			.Setup(s => s.SaveOriginalAsync(receiptId, imageBytes, ".jpg", It.IsAny<CancellationToken>()))
			.Callback(() => preprocessCalled.Should().BeTrue("preprocessing/validation must run before the original is written"))
			.ReturnsAsync($"{receiptId}/original.jpg");

		_mockStorageService
			.Setup(s => s.SaveProcessedAsync(receiptId, It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync($"{receiptId}/processed.png");

		// Act
		await _handler.Handle(command, CancellationToken.None);

		// Assert
		preprocessCalled.Should().BeTrue();
		_mockStorageService.Verify(
			s => s.DeleteReceiptImagesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	[Fact]
	public async Task Handle_StorageWriteFailsAfterValidation_DoesNotDeleteExistingImages()
	{
		// Even when a write fails AFTER a valid upload passes validation, the handler must not
		// recursively delete the receipt's images. Both files are written from validated bytes,
		// so there is no invalid partial state that would justify wiping pre-existing images.

		// Arrange
		Guid receiptId = Guid.NewGuid();
		byte[] imageBytes = [0xFF, 0xD8];
		UploadReceiptImageCommand command = new(receiptId, imageBytes, "image/jpeg", ".jpg");

		_mockReceiptService
			.Setup(s => s.ExistsAsync(receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		_mockProcessingService
			.Setup(s => s.PreprocessAsync(imageBytes, "image/jpeg", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ImageProcessingResult([0x89, 0x50, 0x4E, 0x47], 100, 100));

		_mockStorageService
			.Setup(s => s.SaveOriginalAsync(receiptId, imageBytes, ".jpg", It.IsAny<CancellationToken>()))
			.ThrowsAsync(new IOException("Disk full"));

		// Act
		Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<IOException>()
			.WithMessage("Disk full");

		// Preprocessing (validation) ran first, and no destructive cleanup was performed.
		_mockProcessingService.Verify(
			s => s.PreprocessAsync(imageBytes, "image/jpeg", It.IsAny<CancellationToken>()),
			Times.Once);
		_mockStorageService.Verify(
			s => s.DeleteReceiptImagesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}
}
