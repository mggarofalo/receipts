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
			await RunEnvelopeBackfillAsync(dbContext);
		}
		finally
		{
			if (connection is not null)
			{
				connection.Notice -= LogMigrationNotice;
			}
		}
	}

	// The envelope data step runs once, after the schema exists, in its own transaction.
	private async Task RunEnvelopeBackfillAsync(ApplicationDbContext dbContext)
	{
		await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = dbContext.Database.IsRelational()
			? await dbContext.Database.BeginTransactionAsync() : null;
		EnvelopeBackfillResult? result = await EnvelopeBackfill.RunIfPendingAsync(dbContext, TimeProvider.System, CancellationToken.None);
		if (transaction is not null)
		{
			await transaction.CommitAsync();
		}
		if (result is not null)
		{
			logger.LogInformation("Envelope backfill: {Items} historical items, {Resolved} with an envelope, {Envelopes} envelopes, {Defaults} item-type defaults.",
				result.Items, result.ItemsWithEnvelope, result.Envelopes, result.Defaults);
		}
	}

	private void LogMigrationNotice(object sender, NpgsqlNoticeEventArgs args)
	{
		// PostgreSQL emits successful data-precondition counts as notices. Surface
		// them in the deploy log instead of requiring provider debug logging.
		logger.LogInformation("Database migration notice: {Message}", args.Notice.MessageText);
	}
}
