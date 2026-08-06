using API.Controllers;
using API.Generated.Dtos;
using Application.Interfaces.Services;
using Application.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace Presentation.API.Tests.Controllers;

public class BackupControllerTests : IDisposable
{
	private readonly Mock<IBackupService> _backupServiceMock;
	private readonly Mock<IBackupImportService> _importServiceMock;
	private readonly Mock<ILogger<BackupController>> _loggerMock;
	private readonly BackupController _controller;
	private readonly List<string> _tempFiles = [];

	public BackupControllerTests()
	{
		_backupServiceMock = new Mock<IBackupService>();
		_importServiceMock = new Mock<IBackupImportService>();
		_loggerMock = ControllerTestHelpers.GetLoggerMock<BackupController>();
		_controller = new BackupController(_backupServiceMock.Object, _importServiceMock.Object, _loggerMock.Object);
		_controller.ControllerContext = new ControllerContext
		{
			HttpContext = new DefaultHttpContext()
		};
	}

	[Fact]
	public async Task Export_Success_ReturnsFileStream()
	{
		// Arrange
		string tempFile = Path.GetTempFileName();
		_tempFiles.Add(tempFile);
		await File.WriteAllTextAsync(tempFile, "test-sqlite-content");

		_backupServiceMock.Setup(s => s.ExportToSqliteAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(tempFile);

		// Act
		var result = await _controller.Export(CancellationToken.None);

		// Assert
		FileStreamHttpResult fileResult = result.Result.Should().BeOfType<FileStreamHttpResult>().Subject;
		fileResult.ContentType.Should().Be("application/octet-stream");
		fileResult.FileDownloadName.Should().StartWith("receipts-backup-");
		fileResult.FileDownloadName.Should().EndWith(".db");
	}

	[Fact]
	public async Task Export_ServiceThrows_Returns500()
	{
		// Arrange
		_backupServiceMock.Setup(s => s.ExportToSqliteAsync(It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("Database error"));

		// Act
		var result = await _controller.Export(CancellationToken.None);

		// Assert
		result.Result.Should().BeOfType<StatusCodeHttpResult>()
			.Which.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
	}

	[Fact]
	public async Task Export_CancellationRequested_PropagatesException()
	{
		// Arrange
		_backupServiceMock.Setup(s => s.ExportToSqliteAsync(It.IsAny<CancellationToken>()))
			.ThrowsAsync(new OperationCanceledException());

		// Act
		Func<Task> act = () => _controller.Export(CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	[Fact]
	public async Task ImportBackup_NullFile_ReturnsBadRequest()
	{
		// Act
		Results<Ok<BackupImportResponse>, BadRequest<ProblemDetails>> result = await _controller.ImportBackup(null);

		// Assert
		result.Result.Should().BeOfType<BadRequest<ProblemDetails>>()
			.Which.Value!.Detail.Should().Be("No file was uploaded.");
	}

	[Fact]
	public async Task ImportBackup_EmptyFile_ReturnsBadRequest()
	{
		// Arrange
		Mock<IFormFile> fileMock = new();
		fileMock.Setup(f => f.Length).Returns(0);

		// Act
		Results<Ok<BackupImportResponse>, BadRequest<ProblemDetails>> result = await _controller.ImportBackup(fileMock.Object);

		// Assert
		result.Result.Should().BeOfType<BadRequest<ProblemDetails>>()
			.Which.Value!.Detail.Should().Be("No file was uploaded.");
	}

	[Fact]
	public async Task ImportBackup_OversizedFile_ReturnsBadRequest()
	{
		// Arrange
		Mock<IFormFile> fileMock = new();
		fileMock.Setup(f => f.Length).Returns(101L * 1024 * 1024); // 101 MB
		fileMock.Setup(f => f.FileName).Returns("backup.sqlite");

		// Act
		Results<Ok<BackupImportResponse>, BadRequest<ProblemDetails>> result = await _controller.ImportBackup(fileMock.Object);

		// Assert
		result.Result.Should().BeOfType<BadRequest<ProblemDetails>>()
			.Which.Value!.Detail.Should().Contain("exceeds the maximum allowed size");
	}

	[Fact]
	public async Task ImportBackup_InvalidExtension_ReturnsBadRequest()
	{
		// Arrange
		Mock<IFormFile> fileMock = new();
		fileMock.Setup(f => f.Length).Returns(1024);
		fileMock.Setup(f => f.FileName).Returns("backup.txt");

		// Act
		Results<Ok<BackupImportResponse>, BadRequest<ProblemDetails>> result = await _controller.ImportBackup(fileMock.Object);

		// Assert
		result.Result.Should().BeOfType<BadRequest<ProblemDetails>>()
			.Which.Value!.Detail.Should().Contain("Invalid file extension");
	}

	[Theory]
	[InlineData(".sqlite")]
	[InlineData(".sqlite3")]
	[InlineData(".db")]
	public async Task ImportBackup_ValidExtensions_CallsImportService(string extension)
	{
		// Arrange
		BackupImportResult importResult = new(1, 0, 1, 0, 2, 0, 3, 0, 0, 0, 5, 0, 10, 0, 5, 0, 2, 0);
		_importServiceMock
			.Setup(s => s.ImportFromSqliteAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(importResult);

		Mock<IFormFile> fileMock = new();
		fileMock.Setup(f => f.Length).Returns(1024);
		fileMock.Setup(f => f.FileName).Returns($"backup{extension}");
		fileMock.Setup(f => f.OpenReadStream()).Returns(new MemoryStream());

		// Act
		Results<Ok<BackupImportResponse>, BadRequest<ProblemDetails>> result = await _controller.ImportBackup(fileMock.Object);

		// Assert
		Ok<BackupImportResponse> okResult = Assert.IsType<Ok<BackupImportResponse>>(result.Result);
		BackupImportResponse response = okResult.Value!;
		response.AccountsCreated.Should().Be(1);
		response.CardsCreated.Should().Be(1);
		response.CategoriesCreated.Should().Be(2);
		response.SubcategoriesCreated.Should().Be(3);
		response.ReceiptsCreated.Should().Be(5);
		response.ReceiptItemsCreated.Should().Be(10);
		response.TransactionsCreated.Should().Be(5);
		response.AdjustmentsCreated.Should().Be(2);
		response.TotalCreated.Should().Be(29);
		response.TotalUpdated.Should().Be(0);
	}

	[Fact]
	public async Task ImportBackup_ServiceThrowsInvalidOperation_ReturnsBadRequest()
	{
		// Arrange
		_importServiceMock
			.Setup(s => s.ImportFromSqliteAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("The uploaded file is not a valid SQLite database."));

		Mock<IFormFile> fileMock = new();
		fileMock.Setup(f => f.Length).Returns(1024);
		fileMock.Setup(f => f.FileName).Returns("backup.sqlite");
		fileMock.Setup(f => f.OpenReadStream()).Returns(new MemoryStream());

		// Act
		Results<Ok<BackupImportResponse>, BadRequest<ProblemDetails>> result = await _controller.ImportBackup(fileMock.Object);

		// Assert
		result.Result.Should().BeOfType<BadRequest<ProblemDetails>>()
			.Which.Value!.Detail.Should().Contain("not a valid SQLite database");
	}

	[Fact]
	public async Task ImportBackup_ServiceThrowsFormatException_ReturnsBadRequest()
	{
		// Arrange
		_importServiceMock
			.Setup(s => s.ImportFromSqliteAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new FormatException("Guid should contain 32 digits with 4 dashes."));

		Mock<IFormFile> fileMock = new();
		fileMock.Setup(f => f.Length).Returns(1024);
		fileMock.Setup(f => f.FileName).Returns("backup.sqlite");
		fileMock.Setup(f => f.OpenReadStream()).Returns(new MemoryStream());

		// Act
		Results<Ok<BackupImportResponse>, BadRequest<ProblemDetails>> result = await _controller.ImportBackup(fileMock.Object);

		// Assert
		result.Result.Should().BeOfType<BadRequest<ProblemDetails>>()
			.Which.Value!.Detail.Should().Contain("invalid data");
	}

	[Fact]
	public async Task ImportBackup_ServiceThrowsArgumentException_ReturnsBadRequest()
	{
		// Arrange
		_importServiceMock
			.Setup(s => s.ImportFromSqliteAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new ArgumentException("Requested value 'INVALID' was not found."));

		Mock<IFormFile> fileMock = new();
		fileMock.Setup(f => f.Length).Returns(1024);
		fileMock.Setup(f => f.FileName).Returns("backup.sqlite");
		fileMock.Setup(f => f.OpenReadStream()).Returns(new MemoryStream());

		// Act
		Results<Ok<BackupImportResponse>, BadRequest<ProblemDetails>> result = await _controller.ImportBackup(fileMock.Object);

		// Assert
		result.Result.Should().BeOfType<BadRequest<ProblemDetails>>()
			.Which.Value!.Detail.Should().Contain("invalid data");
	}

	[Fact]
	public async Task ImportBackup_ValidFile_ReturnsCorrectUpsertCounts()
	{
		// Arrange
		BackupImportResult importResult = new(
			AccountsCreated: 2, AccountsUpdated: 1,
			CardsCreated: 2, CardsUpdated: 1,
			CategoriesCreated: 3, CategoriesUpdated: 2,
			SubcategoriesCreated: 5, SubcategoriesUpdated: 3,
			ItemTemplatesCreated: 4, ItemTemplatesUpdated: 1,
			ReceiptsCreated: 10, ReceiptsUpdated: 5,
			ReceiptItemsCreated: 30, ReceiptItemsUpdated: 10,
			TransactionsCreated: 10, TransactionsUpdated: 5,
			AdjustmentsCreated: 3, AdjustmentsUpdated: 2);

		_importServiceMock
			.Setup(s => s.ImportFromSqliteAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(importResult);

		Mock<IFormFile> fileMock = new();
		fileMock.Setup(f => f.Length).Returns(1024);
		fileMock.Setup(f => f.FileName).Returns("backup.db");
		fileMock.Setup(f => f.OpenReadStream()).Returns(new MemoryStream());

		// Act
		Results<Ok<BackupImportResponse>, BadRequest<ProblemDetails>> result = await _controller.ImportBackup(fileMock.Object);

		// Assert
		Ok<BackupImportResponse> okResult = Assert.IsType<Ok<BackupImportResponse>>(result.Result);
		BackupImportResponse response = okResult.Value!;
		response.AccountsCreated.Should().Be(2);
		response.AccountsUpdated.Should().Be(1);
		response.CardsCreated.Should().Be(2);
		response.CardsUpdated.Should().Be(1);
		response.CategoriesCreated.Should().Be(3);
		response.CategoriesUpdated.Should().Be(2);
		response.SubcategoriesCreated.Should().Be(5);
		response.SubcategoriesUpdated.Should().Be(3);
		response.ItemTemplatesCreated.Should().Be(4);
		response.ItemTemplatesUpdated.Should().Be(1);
		response.ReceiptsCreated.Should().Be(10);
		response.ReceiptsUpdated.Should().Be(5);
		response.ReceiptItemsCreated.Should().Be(30);
		response.ReceiptItemsUpdated.Should().Be(10);
		response.TransactionsCreated.Should().Be(10);
		response.TransactionsUpdated.Should().Be(5);
		response.AdjustmentsCreated.Should().Be(3);
		response.AdjustmentsUpdated.Should().Be(2);
		response.TotalCreated.Should().Be(69);
		response.TotalUpdated.Should().Be(30);
	}

	public void Dispose()
	{
		foreach (string path in _tempFiles)
		{
			try
			{
				if (File.Exists(path))
				{
					File.Delete(path);
				}
			}
			catch
			{
				// Best effort cleanup
			}
		}

		GC.SuppressFinalize(this);
	}
}
