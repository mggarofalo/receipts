using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.Entities.Audit;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Tests.Services;

public sealed class BackupBootstrapTests
{
	[Theory]
	[InlineData("pristine", true)]
	[InlineData("template edit", false)]
	[InlineData("category edit", false)]
	[InlineData("trash", false)]
	[InlineData("missing seed", false)]
	[InlineData("receipt", false)]
	[InlineData("audit", false)]
	[InlineData("seed run", false)]
	[InlineData("history edit", false)]
	public async Task ProofRequiresCompleteUnmodifiedBusinessBootstrap(string scenario, bool expected)
	{
		var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
		await using (var setup = new ApplicationDbContext(options))
		{
			await setup.Database.EnsureCreatedAsync();
			setup.AuditingEnabled = false;
			switch (scenario)
			{
				case "template edit": (await setup.ItemTemplates.FirstAsync()).DefaultItemCode = ""; break;
				case "category edit": (await setup.Categories.FirstAsync()).Description = "edited"; break;
				case "trash": (await setup.Categories.FirstAsync()).DeletedAt = DateTimeOffset.UtcNow; break;
				case "missing seed": setup.ItemTemplates.Remove(await setup.ItemTemplates.FirstAsync()); break;
				case "receipt": setup.Receipts.Add(new ReceiptEntity { Id = Guid.NewGuid(), Location = "User data" }); break;
				case "audit": setup.AuditLogs.Add(new AuditLogEntity { Id = Guid.NewGuid(), EntityType = "Category", EntityId = "original" }); break;
				case "seed run": setup.SeedHistory.Add(new SeedHistoryEntry { SeedId = "user sample" }); break;
				case "history edit": (await setup.TaxonomyRevisions.SingleAsync()).Reason = "edited"; break;
			}
			await setup.SaveChangesAsync();
		}
		await using var db = new ApplicationDbContext(options);
		(await BackupImportService.HasPristineBootstrapAsync(db, default)).Should().Be(expected);
	}
}
