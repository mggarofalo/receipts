using Common;
using Domain.Envelopes;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Envelopes;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Repositories;

// ReceiptRepository.UpdateAsync applies the receipt's paid-from choice to its items, so it runs in one transaction
// and takes the normalization write gate and then the receipt row lock, like the budget-choice endpoints.
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class ReceiptRepositoryUpdateLockTests(PostgresFixture server) : IsolatedPostgresTest(server)
{
	[Fact]
	public async Task UpdateAsync_WaitsForAConcurrentReceiptRowLock_ThenCommitsTheEditAndItsChoiceCascade()
	{
		// Arrange
		(ReceiptEntity receipt, Guid itemId, Guid envelope) = await SeedAsync();
		await using NpgsqlConnection holder = new(fixture.ConnectionString);
		await holder.OpenAsync();
		await using NpgsqlTransaction held = await holder.BeginTransactionAsync();
		await using (NpgsqlCommand rowLock = new("SELECT 1 FROM receipts.\"Receipts\" WHERE \"Id\" = @id FOR UPDATE", holder, held))
		{
			rowLock.Parameters.AddWithValue("id", receipt.Id);
			await rowLock.ExecuteNonQueryAsync();
		}
		using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));

		// Act
		Task update = Repository().UpdateAsync([Edited(receipt, envelope)], deadline.Token);
		await WaitUntilBlockedByAsync(fixture.ConnectionString, holder, update, deadline.Token);

		// Assert — nothing is visible while the row lock is held.
		update.IsCompleted.Should().BeFalse("the update waits for the receipt row lock");
		await using (ApplicationDbContext during = fixture.CreateDbContext())
		{
			(await during.Receipts.AsNoTracking().SingleAsync(row => row.Id == receipt.Id)).Location.Should().Be("Before");
			(await during.ReceiptItems.AsNoTracking().SingleAsync(row => row.Id == itemId)).EnvelopeId.Should().BeNull();
		}

		await held.CommitAsync();
		await update.WaitAsync(deadline.Token);

		await using ApplicationDbContext verify = fixture.CreateDbContext();
		ReceiptEntity saved = await verify.Receipts.AsNoTracking().SingleAsync(row => row.Id == receipt.Id);
		saved.Location.Should().Be("After");
		saved.PaidFromEnvelopeId.Should().Be(envelope);
		ReceiptItemEntity item = await verify.ReceiptItems.AsNoTracking().SingleAsync(row => row.Id == itemId);
		item.EnvelopeId.Should().Be(envelope, "the receipt's paid-from choice applies to its items in the same commit");
		item.EnvelopeSource.Should().Be(EnvelopeSource.Receipt);
	}

	[Fact]
	public async Task UpdateAsync_TakesTheWriteGateBeforeTheReceiptRowLock()
	{
		// Arrange — hold the normalization write gate; the waiting update must not hold the receipt row meanwhile.
		(ReceiptEntity receipt, Guid itemId, Guid envelope) = await SeedAsync();
		await using NpgsqlConnection holder = new(fixture.ConnectionString);
		await holder.OpenAsync();
		await using NpgsqlTransaction held = await holder.BeginTransactionAsync();
		await using (NpgsqlCommand gate = new("SELECT pg_advisory_xact_lock(1380143188, 1313821261)", holder, held))
		{
			await gate.ExecuteNonQueryAsync();
		}
		using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));

		// Act
		Task update = Repository().UpdateAsync([Edited(receipt, envelope)], deadline.Token);
		await WaitUntilBlockedByAsync(fixture.ConnectionString, holder, update, deadline.Token);

		// Assert
		await using (NpgsqlConnection probe = new(fixture.ConnectionString))
		{
			await probe.OpenAsync();
			await using NpgsqlTransaction probing = await probe.BeginTransactionAsync();
			await using NpgsqlCommand noWait = new("SELECT 1 FROM receipts.\"Receipts\" WHERE \"Id\" = @id FOR UPDATE NOWAIT", probe, probing);
			noWait.Parameters.AddWithValue("id", receipt.Id);
			Func<Task> rowFree = () => noWait.ExecuteNonQueryAsync();
			await rowFree.Should().NotThrowAsync("an update waiting for the gate has not locked the receipt yet");
			await probing.RollbackAsync();
		}

		await held.CommitAsync();
		await update.WaitAsync(deadline.Token);
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		(await verify.ReceiptItems.AsNoTracking().SingleAsync(row => row.Id == itemId)).EnvelopeId.Should().Be(envelope);
	}

	private ReceiptRepository Repository() => new(new FixtureDbContextFactory(fixture));

	private static ReceiptEntity Edited(ReceiptEntity receipt, Guid envelope) => new()
	{
		Id = receipt.Id,
		Location = "After",
		Date = receipt.Date,
		TaxAmount = receipt.TaxAmount,
		TaxAmountCurrency = receipt.TaxAmountCurrency,
		PaidFromEnvelopeId = envelope,
	};

	private async Task<(ReceiptEntity Receipt, Guid ItemId, Guid Envelope)> SeedAsync()
	{
		EnvelopeEntity envelope = new() { Id = Guid.NewGuid(), Name = "Groceries", IsActive = true };
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		receipt.Location = "Before";
		receipt.TaxAmountCurrency = Currency.USD;
		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
		item.EnvelopeId = null;
		item.EnvelopeSource = EnvelopeSource.Default;
		await using ApplicationDbContext seed = fixture.CreateDbContext();
		seed.Envelopes.Add(envelope);
		seed.Receipts.Add(receipt);
		seed.ReceiptItems.Add(item);
		await seed.SaveChangesAsync();
		return (receipt, item.Id, envelope.Id);
	}

	/// <summary>Polls until some session is blocked by the holder, failing fast if the operation finishes first.</summary>
	private static async Task WaitUntilBlockedByAsync(string connectionString, NpgsqlConnection holder, Task operation, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection monitor = new(connectionString);
		await monitor.OpenAsync(cancellationToken);
		while (true)
		{
			await using NpgsqlCommand blocked = new(
				"SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE @holder = ANY(pg_blocking_pids(pid)))", monitor);
			blocked.Parameters.AddWithValue("holder", holder.ProcessID);
			if ((bool)(await blocked.ExecuteScalarAsync(cancellationToken))!)
			{
				return;
			}
			if (operation.IsCompleted)
			{
				await operation;
				throw new InvalidOperationException("The operation completed without waiting for the held lock.");
			}
			await Task.Delay(20, cancellationToken);
		}
	}

	private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}
}
