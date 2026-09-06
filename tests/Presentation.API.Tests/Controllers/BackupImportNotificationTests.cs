using API.Controllers;
using API.Generated.Dtos;
using API.Services;
using Application.Interfaces.Services;
using Application.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Presentation.API.Tests.Controllers;

public class BackupImportNotificationTests : IDisposable
{
	private readonly Mock<IBackupService> _export = new();
	private readonly Mock<IBackupImportService> _import = new();
	private readonly Mock<IEntityChangeNotifier> _notifier = new();
	private readonly ServiceProvider _services;
	private readonly BackupController _controller;

	public BackupImportNotificationTests()
	{
		ServiceCollection services = new();
		services.AddSingleton(_export.Object);
		services.AddSingleton(_import.Object);
		services.AddSingleton(_notifier.Object);
		services.AddSingleton<ILogger<BackupController>>(NullLogger<BackupController>.Instance);
		_services = services.BuildServiceProvider();
		_controller = ActivatorUtilities.CreateInstance<BackupController>(_services);
		_controller.ControllerContext = new() { HttpContext = new DefaultHttpContext() };
	}

	[Theory]
	[InlineData(0, false)]
	[InlineData(2, false)]
	[InlineData(0, true)]
	public async Task Import_EnqueuesOnceOnlyAfterServiceSuccess_EvenWithZeroCountsOrPostCommitCancellation(int created, bool cancelAfterCommit)
	{
		using CancellationTokenSource request = new();
		_controller.HttpContext.RequestAborted = request.Token;
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
		List<string> order = [];
		_import.Setup(service => service.ImportFromSqliteAsync(It.IsAny<Stream>(), request.Token))
			.Returns(async () =>
			{
				entered.SetResult();
				await committed.Task;
				order.Add("service returned committed result");
				if (cancelAfterCommit)
				{
					request.Cancel();
				}

				return Result(created);
			});
		_notifier.Setup(notifier => notifier.NotifyAllChanged("backup-import", "updated"))
			.Callback(() => order.Add("notification enqueued"))
			.Returns(Task.CompletedTask);
		using MemoryStream contents = new([1, 2, 3]);

		Task<Results<Ok<BackupImportResponse>, BadRequest<ProblemDetails>>> operation = _controller.ImportBackup(Upload(contents));
		await entered.Task;
		_notifier.VerifyNoOtherCalls();
		committed.SetResult();
		var response = await operation;

		response.Result.Should().BeOfType<Ok<BackupImportResponse>>().Which.Value!.TotalCreated.Should().Be(created);
		order.Should().Equal("service returned committed result", "notification enqueued");
		_notifier.Verify(notifier => notifier.NotifyAllChanged("backup-import", "updated"), Times.Once);
		_notifier.VerifyNoOtherCalls();
	}

	[Theory]
	[InlineData("missing")]
	[InlineData("empty")]
	[InlineData("oversized")]
	[InlineData("extension")]
	public async Task Import_ValidationRejectionDoesNotCallServiceOrNotify(string invalid)
	{
		Mock<IFormFile> file = new();
		file.Setup(upload => upload.Length).Returns(invalid == "empty" ? 0 : invalid == "oversized" ? 101L * 1024 * 1024 : 3);
		file.Setup(upload => upload.FileName).Returns(invalid == "extension" ? "backup.txt" : "backup.db");

		var response = await _controller.ImportBackup(invalid == "missing" ? null : file.Object);

		response.Result.Should().BeOfType<BadRequest<ProblemDetails>>();
		_import.VerifyNoOtherCalls();
		_notifier.VerifyNoOtherCalls();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Import_PrecommitFailureOrCancellationDoesNotNotify(bool cancelled)
	{
		_import.Setup(service => service.ImportFromSqliteAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(cancelled ? new OperationCanceledException() : new InvalidOperationException("Malformed backup"));
		using MemoryStream contents = new([1, 2, 3]);

		if (cancelled)
		{
			Func<Task> operation = () => _controller.ImportBackup(Upload(contents));
			await operation.Should().ThrowAsync<OperationCanceledException>();
		}
		else
		{
			var response = await _controller.ImportBackup(Upload(contents));
			response.Result.Should().BeOfType<BadRequest<ProblemDetails>>();
		}
		_notifier.VerifyNoOtherCalls();
	}

	[Theory]
	[InlineData("success")]
	[InlineData("failure")]
	[InlineData("cancelled")]
	public async Task Export_NeverEmitsAnImportNotification(string outcome)
	{
		string path = Path.Combine(Path.GetTempPath(), $"backup-notification-{Guid.NewGuid():N}.db");
		try
		{
			if (outcome == "success")
			{
				await File.WriteAllBytesAsync(path, [1, 2, 3]);
				_export.Setup(service => service.ExportToSqliteAsync(It.IsAny<CancellationToken>())).ReturnsAsync(path);
				var response = await _controller.Export(CancellationToken.None);
				FileStreamHttpResult stream = response.Result.Should().BeOfType<FileStreamHttpResult>().Subject;
				await stream.FileStream.DisposeAsync();
			}
			else if (outcome == "cancelled")
			{
				_export.Setup(service => service.ExportToSqliteAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
				Func<Task> operation = () => _controller.Export(CancellationToken.None);
				await operation.Should().ThrowAsync<OperationCanceledException>();
			}
			else
			{
				_export.Setup(service => service.ExportToSqliteAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("Export failed"));
				var response = await _controller.Export(CancellationToken.None);
				response.Result.Should().BeOfType<StatusCodeHttpResult>().Which.StatusCode.Should().Be(500);
			}
			_notifier.VerifyNoOtherCalls();
		}
		finally
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
	}

	private static FormFile Upload(Stream stream) => new(stream, 0, stream.Length, "file", "backup.db");
	private static BackupImportResult Result(int created) => new(created, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
	public void Dispose() => _services.Dispose();
}
