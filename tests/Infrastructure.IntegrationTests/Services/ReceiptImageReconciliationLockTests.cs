using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class ReceiptImageReconciliationLockTests(PostgresFixture fixture)
{
	[Fact]
	public async Task LockService_SecondLeaseWaitsForFirstLeaseToReleaseStableTransactionLock()
	{
		ReceiptImageReconciliationLockService service = new(new ContextFactory(fixture));
		IAsyncDisposable firstLease = await service.AcquireAsync(CancellationToken.None);

		Task<IAsyncDisposable> secondAcquisition = service.AcquireAsync(CancellationToken.None);
		await Task.Delay(150);
		secondAcquisition.IsCompleted.Should().BeFalse(
			"backup import, upload, and cleanup must serialize on the same lock");

		await firstLease.DisposeAsync();
		await using IAsyncDisposable secondLease = await secondAcquisition.WaitAsync(TimeSpan.FromSeconds(5));
	}

	private sealed class ContextFactory(PostgresFixture database) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => database.CreateDbContext();
	}
}
