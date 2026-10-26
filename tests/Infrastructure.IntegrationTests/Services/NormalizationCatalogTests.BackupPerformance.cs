using System.Data.Common;
using Application.Normalization;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Fact]
	public async Task PortableBackup_MappedPurchaseRestoreKeepsReferenceReadsAndSavesBoundedAsHistoryGrows()
	{
		List<(int Reads, int Saves)> emptyImports = [];
		List<(int Reads, int Saves)> repeatedImports = [];
		foreach (int count in new[] { 2, 220 })
		{
			await StartFreshDatabaseAsync();
			var target = await CanonicalAsync("Reviewed product restore scaling");
			var ids = await ItemsAsync(Source, count);
			var service = Service();
			var preview = await service.PreviewAsync(Actor, Request(target), CancellationToken.None);
			var approved = await service.ApproveAsync(Actor, preview.Id, preview.Revision, CancellationToken.None);
			while (approved.State == "applying")
			{ approved = await service.ResumeAsync(Actor, approved.Id, CancellationToken.None); }
			approved.Applied.Should().Be(count);
			var mapping = (await service.ListMappingsAsync(Actor, Source, CancellationToken.None)).Single();
			var facts = await FactsAsync(ids);
			var evidence = (await ReadAsync(ids)).Select(item => new
			{
				item.Id,
				item.NormalizationOrigin,
				item.NormalizationMappingId,
				item.NormalizationAssignmentId,
				item.NormalizationReviewState,
				item.NormalizationReviewReason,
				item.NormalizationReviewDecisionId,
				item.ReviewedSourceHash,
				item.ReviewedTargetId,
				item.ReviewedBy,
				item.ReviewedAt,
				item.ReviewMappingRevision,
			}).ToArray();
			string path = await new BackupService(new Factory(fixture), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
			try
			{
				await StartFreshDatabaseAsync();
				for (int attempt = 0; attempt < 2; attempt++)
				{
					RestoreReferenceReads reads = new();
					RestoreSaves saves = new();
					var options = new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions())
						.AddInterceptors(reads, saves).Options;
					await using (var stream = File.OpenRead(path))
					{
						await new BackupImportService(new MeasuredRestoreFactory(options), NullLogger<BackupImportService>.Instance)
							.ImportFromSqliteAsync(stream, CancellationToken.None);
					}
					(attempt == 0 ? emptyImports : repeatedImports).Add((reads.Count, saves.Count));
					(await FactsAsync(ids)).Should().BeEquivalentTo(facts);
					var restoredItems = await ReadAsync(ids);
					restoredItems.Should().HaveCount(count).And.OnlyContain(item =>
						item.NormalizedDescriptionId == target.Id && item.NormalizationMappingId == mapping.Id
						&& item.NormalizationOrigin == "mapped" && item.NormalizationReviewState == NormalizationReviewState.Verified);
					restoredItems.Select(item => new
					{
						item.Id,
						item.NormalizationOrigin,
						item.NormalizationMappingId,
						item.NormalizationAssignmentId,
						item.NormalizationReviewState,
						item.NormalizationReviewReason,
						item.NormalizationReviewDecisionId,
						item.ReviewedSourceHash,
						item.ReviewedTargetId,
						item.ReviewedBy,
						item.ReviewedAt,
						item.ReviewMappingRevision,
					}).Should().BeEquivalentTo(evidence);
					await using var verify = fixture.CreateDbContext();
					var restoredMapping = await verify.NormalizationSourceMappings.SingleAsync(row => row.Id == mapping.Id);
					restoredMapping.TargetId.Should().Be(target.Id);
					restoredMapping.State.Should().Be("active");
					(await verify.NormalizationCatalogOperations.SingleAsync(row => row.Id == preview.Id)).State.Should().Be("restored");
					(await verify.NormalizationCatalogMembers.Where(row => row.OperationId == preview.Id)
						.Select(row => row.ItemId).ToListAsync()).Should().BeEquivalentTo(ids);
				}
			}
			finally { File.Delete(path); }
		}

		// Compare database work, not elapsed time. Purchase fact upserts and historical
		// member writes are outside this bound; parent/mapping/target validation must
		// not add a round trip for each imported assignment or review record.
		emptyImports[1].Reads.Should().BeLessThanOrEqualTo(emptyImports[0].Reads + 4);
		repeatedImports[1].Reads.Should().BeLessThanOrEqualTo(repeatedImports[0].Reads + 4);
		emptyImports[1].Saves.Should().Be(emptyImports[0].Saves, "more purchases must not repeatedly save the growing tracked graph");
		repeatedImports[1].Saves.Should().Be(repeatedImports[0].Saves);
	}

	private sealed class MeasuredRestoreFactory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
	}

	private sealed class RestoreReferenceReads : DbCommandInterceptor
	{
		public int Count { get; private set; }
		private void Observe(DbCommand command)
		{
			string sql = command.CommandText;
			if (sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
				&& (sql.Contains("FROM receipts.\"Receipts\"", StringComparison.Ordinal)
					|| sql.Contains("FROM matching.\"NormalizationSourceMappings\"", StringComparison.Ordinal)
					|| sql.Contains("FROM matching.\"NormalizedDescriptions\"", StringComparison.Ordinal)))
			{ Count++; }
		}
		public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
		{ Observe(command); return result; }
		public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
			InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
		{ Observe(command); return ValueTask.FromResult(result); }
	}

	private sealed class RestoreSaves : SaveChangesInterceptor
	{
		public int Count { get; private set; }
		public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
			InterceptionResult<int> result, CancellationToken cancellationToken = default)
		{ Count++; return ValueTask.FromResult(result); }
	}
}
