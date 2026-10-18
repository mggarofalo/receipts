using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

/// <summary>Serializes duplicate acceptance with writes to the receipt facts it acknowledges.</summary>
internal static class ReceiptFactLock
{
	internal static void Acquire(ApplicationDbContext context, IEnumerable<Guid> receiptIds)
	{
		if (context.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL")
		{
			return;
		}
		RequireTransaction(context);
		Guid[] ids = [.. receiptIds.Distinct().Order()];
		context.Database.SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM \"receipts\".\"Receipts\" WHERE \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR UPDATE").ToList();
	}

	internal static async Task AcquireAsync(ApplicationDbContext context, IEnumerable<Guid> receiptIds, CancellationToken cancellationToken)
	{
		if (context.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL")
		{
			return;
		}
		RequireTransaction(context);
		Guid[] ids = [.. receiptIds.Distinct().Order()];
		await context.Database.SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM \"receipts\".\"Receipts\" WHERE \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR UPDATE").ToListAsync(cancellationToken);
	}

	private static void RequireTransaction(ApplicationDbContext context)
	{
		if (context.Database.CurrentTransaction is null)
		{
			throw new InvalidOperationException("Duplicate receipt fact locks require an active transaction.");
		}
	}
}
