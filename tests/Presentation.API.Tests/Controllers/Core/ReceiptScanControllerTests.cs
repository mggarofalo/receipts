// CS0612: ProposedReceiptResponse.Payments is intentionally exercised here for back-compat
// regression coverage until RECEIPTS-658 deletes the field.
#pragma warning disable CS0612

using System.Text.Json;
using API.Configuration;
using API.Controllers.Core;
using API.Generated.Dtos;
using Application.Commands.Receipt.Scan;
using Application.Exceptions;
using Application.Models.Ocr;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using DtoConfidenceLevel = global::API.Generated.Dtos.ConfidenceLevel;

namespace Presentation.API.Tests.Controllers.Core;

public class ReceiptScanControllerTests
{
	private readonly Mock<IMediator> _mediatorMock;
	private readonly ReceiptScanController _controller;

	public ReceiptScanControllerTests()
	{
		_mediatorMock = new Mock<IMediator>();
		Mock<ILogger<ReceiptScanController>> loggerMock = ControllerTestHelpers.GetLoggerMock<ReceiptScanController>();
		_controller = new ReceiptScanController(_mediatorMock.Object, loggerMock.Object);
	}

	[Fact]
	public async Task ScanReceipt_NullFile_ReturnsBadRequest()
	{
		// Act
		Results<Ok<ProposedReceiptResponse>, BadRequest<string>, StatusCodeHttpResult, UnprocessableEntity<string>> actual = await _controller.ScanReceipt(null);

		// Assert
		actual.Result.Should().BeOfType<BadRequest<string>>()
			.Which.Value.Should().Be("No file was uploaded.");
	}

	[Fact]
	public async Task ScanReceipt_EmptyFile_ReturnsBadRequest()
	{
		// Arrange
		Mock<IFormFile> fileMock = new();
		fileMock.Setup(f => f.Length).Returns(0);

		// Act
		Results<Ok<ProposedReceiptResponse>, BadRequest<string>, StatusCodeHttpResult, UnprocessableEntity<string>> actual = await _controller.ScanReceipt(fileMock.Object);

		// Assert
		actual.Result.Should().BeOfType<BadRequest<string>>()
			.Which.Value.Should().Be("No file was uploaded.");
	}

	[Fact]
	public async Task ScanReceipt_OversizedFile_ReturnsBadRequest()
	{
		// Arrange
		Mock<IFormFile> fileMock = new();
		fileMock.Setup(f => f.Length).Returns(21 * 1024 * 1024); // 21 MB
		fileMock.Setup(f => f.ContentType).Returns("image/jpeg");

		// Act
		Results<Ok<ProposedReceiptResponse>, BadRequest<string>, StatusCodeHttpResult, UnprocessableEntity<string>> actual = await _controller.ScanReceipt(fileMock.Object);

		// Assert
		actual.Result.Should().BeOfType<BadRequest<string>>()
			.Which.Value.Should().Contain("maximum allowed size");
	}

	[Fact]
	public async Task ScanReceipt_ValidPdf_ReturnsOkWithProposal()
	{
		// Arrange
		IFormFile file = CreateMockFormFile("receipt.pdf", "application/pdf", 4096);

		ParsedReceipt parsedReceipt = new(
			FieldConfidence<string>.High("COSTCO"),
			FieldConfidence<DateOnly>.Medium(new DateOnly(2026, 4, 10)),
			[],
			FieldConfidence<decimal>.Low(0m),
			[],
			FieldConfidence<decimal>.High(42.99m),
			FieldConfidence<string?>.None()
		);

		ScanReceiptResult scanResult = new(parsedReceipt);

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<ScanReceiptCommand>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(scanResult);

		// Act
		Results<Ok<ProposedReceiptResponse>, BadRequest<string>, StatusCodeHttpResult, UnprocessableEntity<string>> actual = await _controller.ScanReceipt(file);

		// Assert
		actual.Result.Should().BeOfType<Ok<ProposedReceiptResponse>>();
	}

	[Fact]
	public async Task ScanReceipt_PdfContentType_SendsCorrectContentTypeToMediator()
	{
		// Arrange
		IFormFile file = CreateMockFormFile("receipt.pdf", "application/pdf", 2048);

		ParsedReceipt parsedReceipt = new(
			FieldConfidence<string>.Low("Unknown"),
			FieldConfidence<DateOnly>.Low(DateOnly.FromDateTime(DateTime.Today)),
			[],
			FieldConfidence<decimal>.Low(0m),
			[],
			FieldConfidence<decimal>.Low(0m),
			FieldConfidence<string?>.None()
		);

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<ScanReceiptCommand>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ScanReceiptResult(parsedReceipt));

		// Act
		await _controller.ScanReceipt(file);

		// Assert
		_mediatorMock.Verify(m => m.Send(
			It.Is<ScanReceiptCommand>(c =>
				c.ContentType == "application/pdf" &&
				c.ImageBytes.Length == 2048),
			It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Theory]
	[InlineData("image/gif")]
	[InlineData("image/bmp")]
	[InlineData("image/tiff")]
	[InlineData("application/octet-stream")]
	[InlineData("text/plain")]
	[InlineData("image/heic")]
	public async Task ScanReceipt_UnsupportedContentType_Returns415(string contentType)
	{
		// Arrange
		Mock<IFormFile> fileMock = new();
		fileMock.Setup(f => f.Length).Returns(1024);
		fileMock.Setup(f => f.ContentType).Returns(contentType);

		// Act
		Results<Ok<ProposedReceiptResponse>, BadRequest<string>, StatusCodeHttpResult, UnprocessableEntity<string>> actual = await _controller.ScanReceipt(fileMock.Object);

		// Assert
		actual.Result.Should().BeOfType<StatusCodeHttpResult>()
			.Which.StatusCode.Should().Be(StatusCodes.Status415UnsupportedMediaType);
	}

	[Fact]
	public async Task ScanReceipt_OcrReturnsNoText_Returns422()
	{
		// Arrange
		IFormFile file = CreateMockFormFile("receipt.jpg", "image/jpeg", 1024);

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<ScanReceiptCommand>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new OcrNoTextException("OCR returned no readable text from the image."));

		// Act
		Results<Ok<ProposedReceiptResponse>, BadRequest<string>, StatusCodeHttpResult, UnprocessableEntity<string>> actual = await _controller.ScanReceipt(file);

		// Assert
		actual.Result.Should().BeOfType<UnprocessableEntity<string>>()
			.Which.Value.Should().Contain("could not be read");
	}

	[Fact]
	public async Task ScanReceipt_ProcessingFails_Returns422()
	{
		// Arrange
		IFormFile file = CreateMockFormFile("receipt.jpg", "image/jpeg", 1024);

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<ScanReceiptCommand>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("The uploaded file is not a supported image format."));

		// Act
		Results<Ok<ProposedReceiptResponse>, BadRequest<string>, StatusCodeHttpResult, UnprocessableEntity<string>> actual = await _controller.ScanReceipt(file);

		// Assert
		actual.Result.Should().BeOfType<UnprocessableEntity<string>>()
			.Which.Value.Should().Contain("not a supported image format");
	}

	[Fact]
	public async Task ScanReceipt_ValidJpeg_ReturnsOkWithProposal()
	{
		// Arrange
		IFormFile file = CreateMockFormFile("receipt.jpg", "image/jpeg", 2048);

		ParsedReceipt parsedReceipt = new(
			FieldConfidence<string>.High("WALMART"),
			FieldConfidence<DateOnly>.Medium(new DateOnly(2026, 3, 15)),
			[
				new ParsedReceiptItem(
					FieldConfidence<string?>.None(),
					FieldConfidence<string>.High("MILK 2%"),
					FieldConfidence<decimal>.High(1m),
					FieldConfidence<decimal>.High(3.49m),
					FieldConfidence<decimal>.High(3.49m))
				{
					TaxCode = FieldConfidence<string?>.High("N"),
				}
			],
			FieldConfidence<decimal>.Medium(3.49m),
			[
				new ParsedTaxLine(
					FieldConfidence<string>.Medium("TAX"),
					FieldConfidence<decimal>.High(0.25m))
			],
			FieldConfidence<decimal>.High(3.74m),
			FieldConfidence<string?>.Low("VISA")
		)
		{
			StoreAddress = FieldConfidence<string?>.High("9 BENTON RD, TRAVELERS REST SC 29690"),
			StorePhone = FieldConfidence<string?>.High("864-834-7179"),
			Payments =
			[
				new ParsedPayment(
					FieldConfidence<string?>.High("VISA"),
					FieldConfidence<decimal?>.High(3.74m),
					FieldConfidence<string?>.High("1234")),
			],
			ReceiptId = FieldConfidence<string?>.High("7QKKG1XDWPD"),
			StoreNumber = FieldConfidence<string?>.High("05487"),
			TerminalId = FieldConfidence<string?>.High("54731105"),
		};

		ScanReceiptResult scanResult = new(parsedReceipt);

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<ScanReceiptCommand>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(scanResult);

		// Act
		Results<Ok<ProposedReceiptResponse>, BadRequest<string>, StatusCodeHttpResult, UnprocessableEntity<string>> actual = await _controller.ScanReceipt(file);

		// Assert
		Ok<ProposedReceiptResponse> okResult = actual.Result.Should().BeOfType<Ok<ProposedReceiptResponse>>().Subject;
		ProposedReceiptResponse response = okResult.Value!;

		response.StoreName.Should().Be("WALMART");
		response.StoreNameConfidence.Should().Be(DtoConfidenceLevel.High);
		response.StoreAddress.Should().Be("9 BENTON RD, TRAVELERS REST SC 29690");
		response.StoreAddressConfidence.Should().Be(DtoConfidenceLevel.High);
		response.StorePhone.Should().Be("864-834-7179");
		response.StorePhoneConfidence.Should().Be(DtoConfidenceLevel.High);
		response.Date.Should().Be(new DateOnly(2026, 3, 15));
		response.DateConfidence.Should().Be(DtoConfidenceLevel.Medium);
		response.Items.Should().HaveCount(1);
		response.Items.First().Description.Should().Be("MILK 2%");
		response.Items.First().TotalPrice.Should().Be(3.49d);
		response.Items.First().TaxCode.Should().Be("N");
		response.Items.First().TaxCodeConfidence.Should().Be(DtoConfidenceLevel.High);
		response.Subtotal.Should().Be(3.49d);
		response.TaxLines.Should().HaveCount(1);
		response.TaxLines.First().Label.Should().Be("TAX");
		response.TaxLines.First().Amount.Should().Be(0.25d);
		response.Total.Should().Be(3.74d);
		response.TotalConfidence.Should().Be(DtoConfidenceLevel.High);
		response.PaymentMethod.Should().Be("VISA");
		response.Payments.Should().HaveCount(1);
		response.Payments.First().Method.Should().Be("VISA");
		response.Payments.First().Amount.Should().Be(3.74d);
		response.Payments.First().LastFour.Should().Be("1234");
		response.Payments.First().LastFourConfidence.Should().Be(DtoConfidenceLevel.High);
		response.ReceiptId.Should().Be("7QKKG1XDWPD");
		response.ReceiptIdConfidence.Should().Be(DtoConfidenceLevel.High);
		response.StoreNumber.Should().Be("05487");
		response.StoreNumberConfidence.Should().Be(DtoConfidenceLevel.High);
		response.TerminalId.Should().Be("54731105");
		response.TerminalIdConfidence.Should().Be(DtoConfidenceLevel.High);
	}

	[Fact]
	public async Task ScanReceipt_DroppedPageCount_PropagatesToResponse()
	{
		// Arrange — RECEIPTS-637: when the handler reports dropped pages, the
		// controller must surface the count on ProposedReceiptResponse so the
		// client can render a warning banner.
		IFormFile file = CreateMockFormFile("multi.pdf", "application/pdf", 4096);

		ParsedReceipt parsedReceipt = new(
			FieldConfidence<string>.High("WALMART"),
			FieldConfidence<DateOnly>.High(new DateOnly(2026, 3, 15)),
			[],
			FieldConfidence<decimal>.High(40m),
			[],
			FieldConfidence<decimal>.High(40m),
			FieldConfidence<string?>.None()
		);

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<ScanReceiptCommand>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ScanReceiptResult(parsedReceipt, DroppedPageCount: 2));

		// Act
		Results<Ok<ProposedReceiptResponse>, BadRequest<string>, StatusCodeHttpResult, UnprocessableEntity<string>> actual = await _controller.ScanReceipt(file);

		// Assert
		ProposedReceiptResponse response = actual.Result.Should().BeOfType<Ok<ProposedReceiptResponse>>().Subject.Value!;
		response.DroppedPageCount.Should().Be(2);
	}

	[Fact]
	public async Task ScanReceipt_NoDroppedPages_ResponseDroppedPageCountIsZero()
	{
		// Arrange — image scan: no pages dropped, count must be 0.
		IFormFile file = CreateMockFormFile("receipt.jpg", "image/jpeg", 1024);

		ParsedReceipt parsedReceipt = new(
			FieldConfidence<string>.High("WALMART"),
			FieldConfidence<DateOnly>.High(new DateOnly(2026, 3, 15)),
			[],
			FieldConfidence<decimal>.High(10m),
			[],
			FieldConfidence<decimal>.High(10m),
			FieldConfidence<string?>.None()
		);

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<ScanReceiptCommand>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ScanReceiptResult(parsedReceipt));

		// Act
		Results<Ok<ProposedReceiptResponse>, BadRequest<string>, StatusCodeHttpResult, UnprocessableEntity<string>> actual = await _controller.ScanReceipt(file);

		// Assert
		ProposedReceiptResponse response = actual.Result.Should().BeOfType<Ok<ProposedReceiptResponse>>().Subject.Value!;
		response.DroppedPageCount.Should().Be(0);
	}

	[Fact]
	public async Task ScanReceipt_MultiPayment_ReturnsAllPaymentsInResponse()
	{
		// Arrange — split-tender receipt (gift card + card). The response must carry both
		// payments so downstream code can reconcile per-tender amounts.
		IFormFile file = CreateMockFormFile("receipt.jpg", "image/jpeg", 2048);

		ParsedReceipt parsedReceipt = new(
			FieldConfidence<string>.High("WALMART"),
			FieldConfidence<DateOnly>.High(new DateOnly(2026, 3, 15)),
			[],
			FieldConfidence<decimal>.High(40m),
			[],
			FieldConfidence<decimal>.High(40m),
			FieldConfidence<string?>.High("GIFT CARD")
		)
		{
			Payments =
			[
				new ParsedPayment(
					FieldConfidence<string?>.High("GIFT CARD"),
					FieldConfidence<decimal?>.High(10m),
					FieldConfidence<string?>.None()),
				new ParsedPayment(
					FieldConfidence<string?>.High("VISA"),
					FieldConfidence<decimal?>.High(30m),
					FieldConfidence<string?>.High("1234")),
			],
		};

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<ScanReceiptCommand>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ScanReceiptResult(parsedReceipt));

		// Act
		Results<Ok<ProposedReceiptResponse>, BadRequest<string>, StatusCodeHttpResult, UnprocessableEntity<string>> actual = await _controller.ScanReceipt(file);

		// Assert
		ProposedReceiptResponse response = actual.Result.Should().BeOfType<Ok<ProposedReceiptResponse>>().Subject.Value!;
		response.Payments.Should().HaveCount(2);
		response.Payments.ElementAt(0).Method.Should().Be("GIFT CARD");
		response.Payments.ElementAt(0).Amount.Should().Be(10d);
		response.Payments.ElementAt(0).LastFour.Should().BeNull();
		response.Payments.ElementAt(0).LastFourConfidence.Should().Be(DtoConfidenceLevel.None);
		response.Payments.ElementAt(1).Method.Should().Be("VISA");
		response.Payments.ElementAt(1).Amount.Should().Be(30d);
		response.Payments.ElementAt(1).LastFour.Should().Be("1234");
	}

	[Fact]
	public async Task ScanReceipt_ValidPng_ReturnsOkWithProposal()
	{
		// Arrange
		IFormFile file = CreateMockFormFile("receipt.png", "image/png", 4096);

		ParsedReceipt parsedReceipt = new(
			FieldConfidence<string>.Medium("STORE"),
			FieldConfidence<DateOnly>.Low(DateOnly.FromDateTime(DateTime.Today)),
			[],
			FieldConfidence<decimal>.Low(0m),
			[],
			FieldConfidence<decimal>.Low(0m),
			FieldConfidence<string?>.None()
		);

		ScanReceiptResult scanResult = new(parsedReceipt);

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<ScanReceiptCommand>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(scanResult);

		// Act
		Results<Ok<ProposedReceiptResponse>, BadRequest<string>, StatusCodeHttpResult, UnprocessableEntity<string>> actual = await _controller.ScanReceipt(file);

		// Assert
		actual.Result.Should().BeOfType<Ok<ProposedReceiptResponse>>();
	}

	[Fact]
	public async Task ScanReceipt_FileSizeExactlyAtLimit_IsAccepted()
	{
		// Arrange
		long exactLimit = 20 * 1024 * 1024;
		IFormFile file = CreateMockFormFile("receipt.jpg", "image/jpeg", (int)exactLimit);

		ParsedReceipt parsedReceipt = new(
			FieldConfidence<string>.Low("Unknown"),
			FieldConfidence<DateOnly>.Low(DateOnly.FromDateTime(DateTime.Today)),
			[],
			FieldConfidence<decimal>.Low(0m),
			[],
			FieldConfidence<decimal>.Low(0m),
			FieldConfidence<string?>.None()
		);

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<ScanReceiptCommand>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ScanReceiptResult(parsedReceipt));

		// Act
		Results<Ok<ProposedReceiptResponse>, BadRequest<string>, StatusCodeHttpResult, UnprocessableEntity<string>> actual = await _controller.ScanReceipt(file);

		// Assert
		actual.Result.Should().BeOfType<Ok<ProposedReceiptResponse>>();
	}

	[Fact]
	public async Task ScanReceipt_SendsCorrectCommandToMediator()
	{
		// Arrange
		IFormFile file = CreateMockFormFile("scan.jpg", "image/jpeg", 512);

		ParsedReceipt parsedReceipt = new(
			FieldConfidence<string>.Low("Unknown"),
			FieldConfidence<DateOnly>.Low(DateOnly.FromDateTime(DateTime.Today)),
			[],
			FieldConfidence<decimal>.Low(0m),
			[],
			FieldConfidence<decimal>.Low(0m),
			FieldConfidence<string?>.None()
		);

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<ScanReceiptCommand>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ScanReceiptResult(parsedReceipt));

		// Act
		await _controller.ScanReceipt(file);

		// Assert
		_mediatorMock.Verify(m => m.Send(
			It.Is<ScanReceiptCommand>(c =>
				c.ContentType == "image/jpeg" &&
				c.ImageBytes.Length == 512),
			It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Fact]
	public async Task ScanReceipt_ResponseSerializesConfidenceFieldsAsLowercase()
	{
		// RECEIPTS-660: confidence fields must serialize as lowercase strings ("high",
		// "medium", "low", "none") to match the OpenAPI contract. Prior to the
		// DtoSplitter rewriter, NSwag emitted a per-property [JsonConverter] that
		// produced PascalCase ("High"/"Medium"/...), tripping OpenAPI response
		// validation and the wizard's CHIP_CONFIG[confidence] lookup.
		IFormFile file = CreateMockFormFile("receipt.jpg", "image/jpeg", 1024);

		ParsedReceipt parsedReceipt = new(
			FieldConfidence<string>.High("WALMART"),
			FieldConfidence<DateOnly>.Medium(new DateOnly(2026, 3, 15)),
			[
				new ParsedReceiptItem(
					FieldConfidence<string?>.None(),
					FieldConfidence<string>.High("MILK 2%"),
					FieldConfidence<decimal>.High(1m),
					FieldConfidence<decimal>.High(3.49m),
					FieldConfidence<decimal>.High(3.49m))
			],
			FieldConfidence<decimal>.Medium(3.49m),
			[],
			FieldConfidence<decimal>.High(3.74m),
			FieldConfidence<string?>.Low("VISA")
		);

		_mediatorMock
			.Setup(m => m.Send(It.IsAny<ScanReceiptCommand>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ScanReceiptResult(parsedReceipt));

		Results<Ok<ProposedReceiptResponse>, BadRequest<string>, StatusCodeHttpResult, UnprocessableEntity<string>> actual = await _controller.ScanReceipt(file);

		ProposedReceiptResponse response = actual.Result.Should().BeOfType<Ok<ProposedReceiptResponse>>().Subject.Value!;

		// Round-trip through the controller-configured serializer to capture the
		// exact wire format clients receive.
		string json = JsonSerializer.Serialize(response, GetConfiguredJsonOptions());

		json.Should().Contain("\"storeNameConfidence\":\"high\"");
		json.Should().Contain("\"dateConfidence\":\"medium\"");
		json.Should().Contain("\"subtotalConfidence\":\"medium\"");
		json.Should().Contain("\"totalConfidence\":\"high\"");
		json.Should().Contain("\"paymentMethodConfidence\":\"low\"");

		// Hard guard: nothing should escape as PascalCase.
		json.Should().NotContain("\"High\"");
		json.Should().NotContain("\"Medium\"");
		json.Should().NotContain("\"Low\"");
		json.Should().NotContain("\"None\"");
	}

	private static JsonSerializerOptions GetConfiguredJsonOptions()
	{
		ServiceCollection services = new();
		services.AddApplicationServices(new ConfigurationBuilder().Build());
		ServiceProvider provider = services.BuildServiceProvider();
		JsonOptions mvcJsonOptions = provider.GetRequiredService<IOptions<JsonOptions>>().Value;
		return mvcJsonOptions.JsonSerializerOptions;
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
