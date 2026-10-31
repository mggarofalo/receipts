namespace Application.Interfaces.Services;

public interface IBackupService
{
	/// <summary>
	/// Exports all domain data from the database to a portable SQLite file.
	/// Returns the path to the generated SQLite file.
	/// </summary>
	Task<string> ExportToSqliteAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Exports the same portable SQLite file wrapped in gzip, for download and upload.
	/// Returns the path to the generated <c>.db.gz</c> file.
	/// </summary>
	Task<string> ExportCompressedAsync(CancellationToken cancellationToken = default);
}
