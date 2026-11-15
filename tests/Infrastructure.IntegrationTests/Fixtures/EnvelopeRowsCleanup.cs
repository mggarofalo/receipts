using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Fixtures;

/// <summary>
/// Hard-deletes the envelope rows a test added to a shared fixture. A restore of a pre-envelope backup
/// requires a destination without envelopes, and other tests in the same class restore one.
/// </summary>
internal static class EnvelopeRowsCleanup
{
	public static async Task RemoveAllAsync(PostgresFixture fixture)
	{
		await using ApplicationDbContext db = fixture.CreateDbContext();
		await db.ReceiptItems.IgnoreQueryFilters().Where(row => row.EnvelopeId != null)
			.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.EnvelopeId, (Guid?)null));
		await db.Receipts.IgnoreQueryFilters().Where(row => row.PaidFromEnvelopeId != null)
			.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.PaidFromEnvelopeId, (Guid?)null));
		await db.ItemTypeEnvelopeDefaults.IgnoreQueryFilters().ExecuteDeleteAsync();
		await db.YnabEnvelopeMappings.IgnoreQueryFilters().ExecuteDeleteAsync();
		await db.Envelopes.IgnoreQueryFilters().ExecuteDeleteAsync();
	}
}
