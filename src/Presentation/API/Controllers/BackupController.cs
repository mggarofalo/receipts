using API.Generated.Dtos;
using Application.Interfaces.Services;
using Application.Models;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers;

[ApiVersion("1.0")]
[ApiController]
[Route("api/backup")]
[Authorize(Policy = "RequireAdmin")]
public class BackupController(
	IBackupService backupService,
	IBackupImportService importService,
	ILogger<BackupController> logger) : ControllerBase
{
	private const long MaxFileSizeBytes = 100 * 1024 * 1024; // 100 MB

	private static readonly HashSet<string> AllowedExtensions =
	[
		".sqlite",
		".sqlite3",
		".db",
	];

	[HttpPost("export")]
	[EndpointSummary("Export database to a portable SQLite file")]
	[EndpointDescription("Generates a SQLite database containing all domain data (accounts, categories, receipts, items, transactions, adjustments, item templates), YNAB configuration (budget selection and account/category mappings and current sync records), and normalized descriptions. Receipt image file paths are included, but the image binaries themselves are stored outside the database and must be backed up separately. User accounts and authentication settings are excluded. Audit logs (AuditLogs/AuthAuditLogs), the YNAB sync history (YnabSyncEvents), and the re-fetchable YNAB delta cursor (YnabServerKnowledge) are deliberately excluded as activity history or regenerable data rather than restorable state. Admin-only. Soft-deleted records are excluded.")]
	public async Task<Results<FileStreamHttpResult, StatusCodeHttpResult>> Export(CancellationToken cancellationToken)
	{
		string? filePath = null;
		try
		{
			filePath = await backupService.ExportToSqliteAsync(cancellationToken);

			FileStream stream;
			try
			{
				stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.None,
					bufferSize: 81920, FileOptions.DeleteOnClose | FileOptions.SequentialScan);
			}
			catch
			{
				if (System.IO.File.Exists(filePath))
				{
					System.IO.File.Delete(filePath);
				}

				throw;
			}

			string fileName = $"receipts-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db";
			return TypedResults.Stream(stream, "application/octet-stream", fileName);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogError(ex, "Failed to export database backup");
			return TypedResults.StatusCode(StatusCodes.Status500InternalServerError);
		}
	}

	[HttpPost("import")]
	[RequestSizeLimit(100 * 1024 * 1024)]
	[EndpointSummary("Import data from a SQLite backup file")]
	[EndpointDescription("Accepts a SQLite database file exported by the backup endpoint, reads all entity tables, and upserts each row into the current database. Backups from older export versions are accepted; tables and columns they lack are treated as absent. Requires the Admin role.")]
	public async Task<Results<Ok<BackupImportResponse>, BadRequest<ProblemDetails>>> ImportBackup(
		IFormFile? file)
	{
		if (file is null || file.Length == 0)
		{
			return ApiProblem.BadRequest("No file was uploaded.");
		}

		if (file.Length > MaxFileSizeBytes)
		{
			return ApiProblem.BadRequest($"File size exceeds the maximum allowed size of {MaxFileSizeBytes / (1024 * 1024)} MB.");
		}

		string extension = Path.GetExtension(file.FileName).ToLowerInvariant();
		if (!AllowedExtensions.Contains(extension))
		{
			return ApiProblem.BadRequest($"Invalid file extension '{extension}'. Allowed: {string.Join(", ", AllowedExtensions)}");
		}

		try
		{
			await using Stream stream = file.OpenReadStream();
			BackupImportResult result = await importService.ImportFromSqliteAsync(stream, HttpContext.RequestAborted);

			logger.LogInformation(
				"Backup import completed: {TotalCreated} created, {TotalUpdated} updated",
				result.TotalCreated, result.TotalUpdated);

			return TypedResults.Ok(new BackupImportResponse
			{
				AccountsCreated = result.AccountsCreated,
				AccountsUpdated = result.AccountsUpdated,
				CardsCreated = result.CardsCreated,
				CardsUpdated = result.CardsUpdated,
				CategoriesCreated = result.CategoriesCreated,
				CategoriesUpdated = result.CategoriesUpdated,
				SubcategoriesCreated = result.SubcategoriesCreated,
				SubcategoriesUpdated = result.SubcategoriesUpdated,
				ItemTemplatesCreated = result.ItemTemplatesCreated,
				ItemTemplatesUpdated = result.ItemTemplatesUpdated,
				ReceiptsCreated = result.ReceiptsCreated,
				ReceiptsUpdated = result.ReceiptsUpdated,
				ReceiptItemsCreated = result.ReceiptItemsCreated,
				ReceiptItemsUpdated = result.ReceiptItemsUpdated,
				TransactionsCreated = result.TransactionsCreated,
				TransactionsUpdated = result.TransactionsUpdated,
				AdjustmentsCreated = result.AdjustmentsCreated,
				AdjustmentsUpdated = result.AdjustmentsUpdated,
				TotalCreated = result.TotalCreated,
				TotalUpdated = result.TotalUpdated,
			});
		}
		catch (InvalidOperationException ex)
		{
			logger.LogWarning(ex, "Backup import failed: {Message}", ex.Message);
			return ApiProblem.BadRequest(ex.Message);
		}
		catch (Exception ex) when (ex is FormatException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
		{
			logger.LogWarning(ex, "Backup import failed due to malformed data: {Message}", ex.Message);
			return ApiProblem.BadRequest($"The backup file contains invalid data: {ex.Message}");
		}
	}
}
