using Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Infrastructure.Services;

public class DatabaseMigratorService(
	IDbContextFactory<ApplicationDbContext> contextFactory,
	ILogger<DatabaseMigratorService> logger) : IDatabaseMigratorService
{
	public async Task MigrateAsync()
	{
		await using ApplicationDbContext dbContext = await contextFactory.CreateDbContextAsync();
		NpgsqlConnection? connection = dbContext.Database.IsNpgsql()
			? (NpgsqlConnection)dbContext.Database.GetDbConnection()
			: null;
		if (connection is not null)
		{
			connection.Notice += LogMigrationNotice;
		}
		try
		{
			await dbContext.Database.MigrateAsync();
		}
		finally
		{
			if (connection is not null)
			{
				connection.Notice -= LogMigrationNotice;
			}
		}
	}

	private void LogMigrationNotice(object sender, NpgsqlNoticeEventArgs args)
	{
		// PostgreSQL emits successful data-precondition counts as notices. Surface
		// them in the deploy log instead of requiring provider debug logging.
		logger.LogInformation("Database migration notice: {Message}", args.Notice.MessageText);
	}
}
