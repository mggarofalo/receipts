using System.Security.Cryptography;
using System.Text.Json;
using Application.Classification;
using Application.Normalization;
using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.Entities.CategoryAssistance;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	[Fact]
	public async Task CandidateRestore_RoundTripsDetachedReviewOperationsAndRollsBack()
	{
		await using var source = await fixture.CreateIsolatedAsync();
		await using var destination = await fixture.CreateIsolatedAsync();
		var subject = new ClassificationTests(source);
		var seed = await subject.HistoricalSeedAsync();
		var group = (await subject.HistoricalRequest(seed.Item, seed.Revision, seed.Root)).Groups.Single().Group;
		var sourceKey = NormalizationCatalogPolicy.Normalize(new(group.Store, group.RawDescription, group.ItemCode));
		Guid purgedItem;
		await using (var db = source.CreateDbContext())
		{
			var receiptId = (await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == seed.Item)).ReceiptId;
			var item = ReceiptItemEntityGenerator.Generate(receiptId);
			db.ReceiptItems.Add(item);
			await db.SaveChangesAsync();
			purgedItem = item.Id;
			await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM receipts.\"ReceiptItems\" WHERE \"Id\" = {purgedItem}");
		}
		var attempt = Guid.NewGuid();
		CategoryAssistanceReviewPreferenceEntity first = new()
		{
			Actor = "absent-owner",
			AttemptId = attempt,
			Location = sourceKey.Location,
			Description = sourceKey.Description,
			ItemCodeKey = sourceKey.ItemCode ?? ""
		};
		CategoryAssistanceReviewPreferenceEntity second = new()
		{
			Actor = "other-owner",
			AttemptId = attempt,
			Location = first.Location,
			Description = first.Description,
			ItemCodeKey = first.ItemCodeKey
		};
		CategoryAssistanceReviewPreferenceEntity otherSource = new()
		{
			Actor = "absent-owner",
			AttemptId = Guid.NewGuid(),
			Location = "another store",
			Description = "another source"
		};
		var operations = new[] { Advance(first, "later", [purgedItem]), Advance(first, "active", [purgedItem]),
			Advance(second, "left_as_is", []), Advance(otherSource, "later", []) };
		await using (var db = source.CreateDbContext())
		{
			db.CategoryAssistanceReviewPreferences.AddRange(first, second, otherSource);
			db.CategoryAssistanceReviewOperations.AddRange(operations);
			await db.SaveChangesAsync();
		}
		var unrelated = new CategoryAssistanceReviewPreferenceEntity
		{
			Actor = "local-owner",
			AttemptId = Guid.NewGuid(),
			Location = "local",
			Description = "unrelated"
		};
		var unrelatedOperation = Advance(unrelated, "later", []);
		await using (var db = destination.CreateDbContext())
		{
			db.CategoryAssistanceReviewPreferences.Add(unrelated);
			db.CategoryAssistanceReviewOperations.Add(unrelatedOperation);
			db.Users.Add(new ApplicationUser { Id = first.Actor, UserName = first.Actor });
			db.CategoryAssistanceAttempts.Add(new CategoryAssistanceAttemptEntity
			{
				Id = attempt,
				RequestId = Guid.NewGuid(),
				Location = first.Location,
				Description = first.Description,
				ItemCodeKey = first.ItemCodeKey,
				SourceHash = NormalizationCatalogPolicy.Hash(sourceKey),
				State = "suggested",
				CreatedAt = DateTimeOffset.UtcNow
			});
			await db.SaveChangesAsync();
		}
		string path = await new BackupService(new Factory(source), NullLogger<BackupService>.Instance).ExportCandidateToSqliteAsync();
		try
		{
			var failure = new BackupImportService(new FailAfterPreferenceFactory(destination), NullLogger<BackupImportService>.Instance);
			await using (var input = File.OpenRead(path))
			{
				Func<Task> import = () => failure.ImportFromSqliteAsync(input, default);
				await import.Should().ThrowAsync<InvalidOperationException>().WithMessage("Injected after preference insert");
			}
			await using (var db = destination.CreateDbContext())
			{
				(await db.CategoryAssistanceReviewPreferences.CountAsync()).Should().Be(1);
				(await db.CategoryAssistanceReviewOperations.CountAsync()).Should().Be(1);
				(await db.ReceiptItems.AnyAsync(x => x.Id == seed.Item)).Should().BeFalse();
			}
			var importer = new BackupImportService(new Factory(destination), NullLogger<BackupImportService>.Instance);
			for (int n = 0; n < 2; n++)
			{
				await using var input = File.OpenRead(path);
				await importer.ImportFromSqliteAsync(input, default);
			}
			var store = new CategoryAssistanceReviewPreferenceStore(new Factory(destination), TimeProvider.System);
			var replayRequest = new CategoryAssistanceReviewPreferenceRequest(operations[0].OperationId, Guid.Empty, "later", [purgedItem]);
			var replay = await store.UpdateAsync(first.Actor, attempt, replayRequest, default);
			replay.Should().BeEquivalentTo(JsonSerializer.Deserialize<CategoryAssistanceReviewPreference>(operations[0].ResultJson));
			Func<Task> changedReplay = () => store.UpdateAsync(first.Actor, attempt, replayRequest with { State = "left_as_is" }, default);
			await changedReplay.Should().ThrowAsync<TaxonomyException>().Where(x => x.Status == 409);
			await using (var db = destination.CreateDbContext())
			{
				var preferences = await db.CategoryAssistanceReviewPreferences.AsNoTracking().ToListAsync();
				preferences.Should().BeEquivalentTo(new[] { first, second, otherSource, unrelated });
				var recorded = await db.CategoryAssistanceReviewOperations.AsNoTracking().ToListAsync();
				recorded.Should().HaveCount(5);
				recorded.Select(x => x.ResultJson).Should().BeEquivalentTo(operations.Select(x => x.ResultJson).Append(unrelatedOperation.ResultJson));
				(await db.CategoryAssistanceAttempts.CountAsync()).Should().Be(1);
				(await db.CategoryAssistanceAttempts.AnyAsync(x => x.Id == otherSource.AttemptId)).Should().BeFalse();
				(await db.Users.AnyAsync(x => x.Id == "other-owner")).Should().BeFalse();
				(await db.ReceiptItems.IgnoreQueryFilters().AnyAsync(x => x.Id == purgedItem)).Should().BeFalse();
				var changed = await db.CategoryAssistanceReviewPreferences.SingleAsync(x => x.Actor == first.Actor && x.AttemptId == attempt);
				db.CategoryAssistanceReviewOperations.Add(Advance(changed, "active", []));
				await db.SaveChangesAsync();
			}
			await using (var stale = File.OpenRead(path))
			{
				Func<Task> restore = () => importer.ImportFromSqliteAsync(stale, default);
				await restore.Should().ThrowAsync<InvalidOperationException>();
			}
			await using (var db = destination.CreateDbContext())
			{
				(await db.CategoryAssistanceReviewPreferences.SingleAsync(x => x.Actor == first.Actor && x.AttemptId == attempt)).ExcludedItemIdsJson.Should().Be("[]");
				(await db.CategoryAssistanceReviewOperations.CountAsync()).Should().Be(6);
			}
		}
		finally { File.Delete(path); }
	}

	private static CategoryAssistanceReviewOperationEntity Advance(CategoryAssistanceReviewPreferenceEntity row, string state, Guid[] excluded)
	{
		Array.Sort(excluded);
		var expected = row.Revision;
		string hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
		{ attemptId = row.AttemptId, ExpectedRevision = expected, State = state, excludedItemIds = excluded })));
		row.State = state; row.Revision = Guid.NewGuid(); row.ExcludedItemIdsJson = JsonSerializer.Serialize(excluded);
		return new()
		{
			Actor = row.Actor,
			AttemptId = row.AttemptId,
			OperationId = Guid.NewGuid(),
			RequestHash = hash,
			ResultJson = JsonSerializer.Serialize(new CategoryAssistanceReviewPreference(row.AttemptId, state, row.Revision, excluded)),
			RecordedAt = DateTimeOffset.UtcNow
		};
	}

	private sealed class FailAfterPreferenceDb(DbContextOptions<ApplicationDbContext> options) : ApplicationDbContext(options)
	{
		private bool inserted;
		public override async Task<int> SaveChangesAsync(CancellationToken ct = default)
		{
			if (inserted) { throw new InvalidOperationException("Injected after preference insert"); }
			bool adding = ChangeTracker.Entries<CategoryAssistanceReviewPreferenceEntity>().Any(x => x.State == EntityState.Added);
			int result = await base.SaveChangesAsync(ct);
			inserted = adding;
			return result;
		}
	}
	private sealed class FailAfterPreferenceFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => new FailAfterPreferenceDb(fixture.CreateOptions()); }
}

