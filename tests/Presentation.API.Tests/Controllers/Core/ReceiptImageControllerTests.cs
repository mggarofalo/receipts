using API.Controllers.Core;
using API.Generated.Dtos;
using Application.Commands.Receipt.UploadImage;
using FluentAssertions;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using Moq;

namespace Presentation.API.Tests.Controllers.Core;

public class ReceiptImageControllerTests
{
	private readonly Mock<IMediator> _mediatorMock;
	private readonly ReceiptImageController _controller;

	public ReceiptImageControllerTests()
	{
		_mediatorMock = new Mock<IMediator>();
		Mock<ILogger<ReceiptImageController>> loggerMock = ControllerTestHelpers.GetLoggerMock<ReceiptImageController>();
		_controller = new ReceiptImageController(_mediatorMock.Object, loggerMock.Object);
	}

	[Fact]
	public async Task UploadImage_NullFile_ReturnsBadRequest()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();

		// Act
		var result = await _controller.UploadImage(receiptId, null);

		// Assert
		result.Result.Should().BeOfType<BadRequest<ProblemDetails>>()
			.Which.Value!.Detail.Should().Be("No file was uploaded.");
	}

	[Fact]
	public async Task UploadImage_EmptyFile_ReturnsBadRequest()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		Mock<IFormFile> fileMock = new();
		fileMock.Setup(f => f.Length).Returns(0);

		// Act
		var result = await _controller.UploadImage(receiptId, fileMock.Object);

		// Assert
		result.Result.Should().BeOfType<BadRequest<ProblemDetails>>()
			.Which.Value!.Detail.Should().Be("No file was uploaded.");
	}

	[Fact]
	public async Task UploadImage_OversizedFile_ReturnsBadRequest()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		Mock<IFormFile> fileMock = new();
		fileMock.Setup(f => f.Length).Returns(21 * 1024 * 1024); // 21 MB
		fileMock.Setup(f => f.ContentType).Returns("image/jpeg");

		// Act
		var result = await _controller.UploadImage(receiptId, fileMock.Object);

		// Assert
		result.Result.Should().BeOfType<BadRequest<ProblemDetails>>()
			.Which.Value!.Detail.Should().Contain("maximum allowed size");
	}

	[Theory]
	[InlineData("image/gif")]
	[InlineData("image/bmp")]
	[InlineData("image/tiff")]
	[InlineData("application/octet-stream")]
	[InlineData("text/plain")]
	public async Task UploadImage_UnsupportedContentType_Returns415(string contentType)
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		Mock<IFormFile> fileMock = new();
		fileMock.Setup(f => f.Length).Returns(1024);
		fileMock.Setup(f => f.ContentType).Returns(contentType);

		// Act
		var result = await _controller.UploadImage(receiptId, fileMock.Object);

		// Assert
		result.Result.Should().BeOfType<StatusCodeHttpResult>()
			.Which.StatusCode.Should().Be(StatusCodes.Status415UnsupportedMediaType);
	}

	[Fact]
	public async Task UploadImage_HeicContentType_Returns415()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		Mock<IFormFile> fileMock = new();
		fileMock.Setup(f => f.Length).Returns(1024);
		fileMock.Setup(f => f.ContentType).Returns("image/heic");

		// Act
		var result = await _controller.UploadImage(receiptId, fileMock.Object);

		// Assert
		result.Result.Should().BeOfType<StatusCodeHttpResult>()
			.Which.StatusCode.Should().Be(StatusCodes.Status415UnsupportedMediaType);
	}

	[Fact]
	public async Task UploadImage_ReceiptNotFound_ReturnsNotFound()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		IFormFile file = CreateMockFormFile("test.jpg", "image/jpeg", 1024);

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<UploadReceiptImageCommand>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new KeyNotFoundException($"Receipt {receiptId} not found."));

		// Act
		var result = await _controller.UploadImage(receiptId, file);

		// Assert
		result.Result.Should().BeOfType<NotFound>();
	}

	[Fact]
	public async Task UploadImage_InvalidImage_ReturnsBadRequest()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		IFormFile file = CreateMockFormFile("test.jpg", "image/jpeg", 1024);

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<UploadReceiptImageCommand>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("The uploaded file is not a supported image format."));

		// Act
		var result = await _controller.UploadImage(receiptId, file);

		// Assert
		result.Result.Should().BeOfType<BadRequest<ProblemDetails>>()
			.Which.Value!.Detail.Should().Contain("not a supported image format");
	}

	[Fact]
	public async Task UploadImage_ValidJpeg_ReturnsOkWithPaths()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		IFormFile file = CreateMockFormFile("receipt.jpg", "image/jpeg", 2048);

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<UploadReceiptImageCommand>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new UploadReceiptImageResult($"{receiptId}/original.jpg", $"{receiptId}/processed.png"));

		// Act
		var result = await _controller.UploadImage(receiptId, file);

		// Assert
		Ok<UploadReceiptImageResponse> okResult = result.Result.Should().BeOfType<Ok<UploadReceiptImageResponse>>().Subject;
		okResult.Value!.OriginalImagePath.Should().Be($"{receiptId}/original.jpg");
		okResult.Value.ProcessedImagePath.Should().Be($"{receiptId}/processed.png");
	}

	[Fact]
	public async Task UploadImage_ValidPng_ReturnsOkWithPaths()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		IFormFile file = CreateMockFormFile("receipt.png", "image/png", 4096);

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<UploadReceiptImageCommand>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new UploadReceiptImageResult($"{receiptId}/original.png", $"{receiptId}/processed.png"));

		// Act
		var result = await _controller.UploadImage(receiptId, file);

		// Assert
		Ok<UploadReceiptImageResponse> okResult = result.Result.Should().BeOfType<Ok<UploadReceiptImageResponse>>().Subject;
		okResult.Value!.OriginalImagePath.Should().Be($"{receiptId}/original.png");
		okResult.Value.ProcessedImagePath.Should().Be($"{receiptId}/processed.png");
	}

	[Fact]
	public async Task UploadImage_FileWithNoExtension_FallsBackToContentTypeExtension()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		Mock<IFormFile> fileMock = new();
		fileMock.Setup(f => f.Length).Returns(1024);
		fileMock.Setup(f => f.ContentType).Returns("image/png");
		fileMock.Setup(f => f.FileName).Returns("noextension");
		byte[] fakeBytes = new byte[1024];
		fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
			.Callback<Stream, CancellationToken>((stream, _) => stream.Write(fakeBytes, 0, fakeBytes.Length))
			.Returns(Task.CompletedTask);

		_mediatorMock
			.Setup(m => m.Send(
				It.Is<UploadReceiptImageCommand>(c => c.FileExtension == ".png"),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(new UploadReceiptImageResult($"{receiptId}/original.png", $"{receiptId}/processed.png"));

		// Act
		var result = await _controller.UploadImage(receiptId, fileMock.Object);

		// Assert
		Ok<UploadReceiptImageResponse> okResult = result.Result.Should().BeOfType<Ok<UploadReceiptImageResponse>>().Subject;
		okResult.Value.Should().NotBeNull();

		_mediatorMock.Verify(
			m => m.Send(It.Is<UploadReceiptImageCommand>(c => c.FileExtension == ".png"), It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Fact]
	public async Task UploadImage_FileWithNoExtensionJpeg_FallsBackToJpg()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		Mock<IFormFile> fileMock = new();
		fileMock.Setup(f => f.Length).Returns(1024);
		fileMock.Setup(f => f.ContentType).Returns("image/jpeg");
		fileMock.Setup(f => f.FileName).Returns("noextension");
		byte[] fakeBytes = new byte[1024];
		fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
			.Callback<Stream, CancellationToken>((stream, _) => stream.Write(fakeBytes, 0, fakeBytes.Length))
			.Returns(Task.CompletedTask);

		_mediatorMock
			.Setup(m => m.Send(
				It.Is<UploadReceiptImageCommand>(c => c.FileExtension == ".jpg"),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(new UploadReceiptImageResult($"{receiptId}/original.jpg", $"{receiptId}/processed.png"));

		// Act
		var result = await _controller.UploadImage(receiptId, fileMock.Object);

		// Assert
		_mediatorMock.Verify(
			m => m.Send(It.Is<UploadReceiptImageCommand>(c => c.FileExtension == ".jpg"), It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Fact]
	public async Task UploadImage_FileSizeExactlyAtLimit_IsAccepted()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		long exactLimit = 20 * 1024 * 1024;
		IFormFile file = CreateMockFormFile("receipt.jpg", "image/jpeg", (int)exactLimit);

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<UploadReceiptImageCommand>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new UploadReceiptImageResult($"{receiptId}/original.jpg", $"{receiptId}/processed.png"));

		// Act
		var result = await _controller.UploadImage(receiptId, file);

		// Assert
		result.Result.Should().BeOfType<Ok<UploadReceiptImageResponse>>();
	}

	[Fact]
	public async Task UploadImage_SendsCorrectCommandToMediator()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		IFormFile file = CreateMockFormFile("scan.jpg", "image/jpeg", 512);

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<UploadReceiptImageCommand>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new UploadReceiptImageResult($"{receiptId}/original.jpg", $"{receiptId}/processed.png"));

		// Act
		await _controller.UploadImage(receiptId, file);

		// Assert
		_mediatorMock.Verify(m => m.Send(
			It.Is<UploadReceiptImageCommand>(c =>
				c.ReceiptId == receiptId &&
				c.ContentType == "image/jpeg" &&
				c.FileExtension == ".jpg" &&
				c.ImageBytes.Length == 512),
			It.IsAny<CancellationToken>()),
			Times.Once);
	}

	private static IFormFile CreateMockFormFile(string fileName, string contentType, int size)
	{
		Mock<IFormFile> fileMock = new();
		fileMock.Setup(f => f.Length).Returns(size);
		fileMock.Setup(f => f.ContentType).Returns(contentType);
		fileMock.Setup(f => f.FileName).Returns(fileName);

		byte[] content = new byte[size];
		fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
			.Callback<Stream, CancellationToken>((stream, _) => stream.Write(content, 0, content.Length))
			.Returns(Task.CompletedTask);

		return fileMock.Object;
	}
}
