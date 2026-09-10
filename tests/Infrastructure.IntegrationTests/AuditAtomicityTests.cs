using System.Data.Common;
using Domain;
using Domain.Core;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Mapping;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class AuditAtomicityTests(PostgresFixture fixture)
{
	[Theory]
	[InlineData(false, 1)]
	[InlineData(false, 2)]
	[InlineData(true, 1)]
	[InlineData(true, 2)]
	public async Task OrdinaryReceiptWrite_AuditInsertFailure_RollsBackEveryBusinessRow(bool update, int count)
	{
		Guid[] ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
		if (update)
		{
			await using ApplicationDbContext seed = fixture.CreateDbContext();
			seed.Receipts.AddRange(ids.Select(id => new ReceiptEntity { Id = id, Location = "Before", Date = new(2024, 1, 1) }));
			await seed.SaveChangesAsync();
		}
		RejectAuditInsert failure = new();
		DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions())
			.AddInterceptors(failure).Options;
		ReceiptService service = new(new ReceiptRepository(new Factory(options)), new ReceiptMapper());
		List<Receipt> edits = [.. ids.Select(id => new Receipt(id, "After", new(2024, 2, 1), new Money(2)))];
		Func<Task> save = update
			? () => service.UpdateAsync(edits, CancellationToken.None)
			: async () => await service.CreateAsync(edits, CancellationToken.None);

		await save.Should().ThrowAsync<DbUpdateException>();

		failure.Fired.Should().BeTrue("the failure must occur at the mandatory audit insert");
		await using ApplicationDbContext read = fixture.CreateDbContext();
		List<ReceiptEntity> stored = await read.Receipts.Where(row => ids.Contains(row.Id)).ToListAsync();
		if (update)
		{
			stored.Should().HaveCount(count);
			stored.Should().OnlyContain(row => row.Location == "Before" && row.Date == new DateOnly(2024, 1, 1) && row.TaxAmount == 0,
				"a failed audit must roll back all edited business fields");
		}
		else
		{
			stored.Should().BeEmpty("a failed audit must not leave committed business rows");
		}
		string[] auditIds = ids.Select(id => id.ToString()).ToArray();
		(await read.AuditLogs.CountAsync(row => auditIds.Contains(row.EntityId))).Should().Be(update ? count : 0,
			"failed writes must not leave additional audit rows either");
	}

	[Fact]
	public async Task CallerTransactionRollback_AfterAuditFailure_LeavesNoBusinessOrAuditRows()
	{
		Guid id = Guid.NewGuid();
		RejectAuditInsert failure = new();
		DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions())
			.AddInterceptors(failure).Options;
		await using (ApplicationDbContext write = new(options))
		{
			await using var transaction = await write.Database.BeginTransactionAsync();
			write.Receipts.Add(new ReceiptEntity { Id = id, Location = "Caller transaction", Date = new(2024, 1, 1) });
			Func<Task> save = () => write.SaveChangesAsync();
			await save.Should().ThrowAsync<DbUpdateException>();
			await transaction.RollbackAsync();
		}

		failure.Fired.Should().BeTrue();
		await using ApplicationDbContext read = fixture.CreateDbContext();
		(await read.Receipts.AnyAsync(row => row.Id == id)).Should().BeFalse();
		(await read.AuditLogs.AnyAsync(row => row.EntityId == id.ToString())).Should().BeFalse();
	}

	private sealed class RejectAuditInsert : DbCommandInterceptor
	{
		public bool Fired { get; private set; }

		public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
		{
			if (command.CommandText.Contains("INSERT INTO audit.\"AuditLogs\"", StringComparison.Ordinal)
				|| command.CommandText.Contains("INSERT INTO \"audit\".\"AuditLogs\"", StringComparison.Ordinal))
			{
				Fired = true;
				throw new InvalidOperationException("Injected audit insert failure");
			}
			return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
		}
	}

	private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
		public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
	}
}
