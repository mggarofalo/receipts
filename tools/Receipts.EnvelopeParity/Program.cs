using Application.Envelopes;
using Infrastructure;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

// Read-only check of the envelope backfill invariant. Prints counts only, never names or amounts.
if (args.Length != 0)
{
	Console.Error.WriteLine("Usage: (no arguments). Set RECEIPTS_MIGRATION_CONNECTION securely in the process environment.");
	return 1;
}
string connection = Environment.GetEnvironmentVariable("RECEIPTS_MIGRATION_CONNECTION")
	?? throw new InvalidOperationException("RECEIPTS_MIGRATION_CONNECTION is required.");
var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection, npgsql => npgsql.UseVector()).Options;
await using var db = new ApplicationDbContext(options);
await using var snapshot = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");
EnvelopeParityReport report = await EnvelopeParityReader.CheckAsync(db, CancellationToken.None);
Console.WriteLine($"items {report.Items}; envelopes {report.Envelopes}; budgets {report.Budgets}; " +
	$"mapped by node path {report.ItemsMappedByOldPath}; mapped by envelope path {report.ItemsMappedByNewPath}; mismatches {report.Mismatches}");
return report.Holds ? 0 : 2;
