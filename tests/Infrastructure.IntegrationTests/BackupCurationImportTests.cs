using System.Data.Common;
using Application.Interfaces.Services;
using Application.Models;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Audit;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.IntegrationTests;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class BackupCurationImportTests(PostgresFixture target) : IClassFixture<PostgresFixture>
{
	[Fact]
	public async Task ImportCanonicalRename_WinsRaceWithOldNameWorker_AndQueuesRenamedTextForRebuild()
	{
		await using (ApplicationDbContext reset = target.CreateDbContext())
		{
			await reset.Database.ExecuteSqlRawAsync(
				"""TRUNCATE matching."NormalizedDescriptions" RESTART IDENTITY CASCADE;""");
		}
		Guid canonicalId = Guid.NewGuid();
		await using (ApplicationDbContext seed = target.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = canonicalId,
				CanonicalName = "Old canonical name",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await seed.SaveChangesAsync();
		}
		using Artifact artifact = await Artifact.Create(5);
		artifact.Execute(
			"INSERT INTO normalized_descriptions (id, canonical_name, status, created_at, display_label, nearest_neighbour_id, nearest_neighbour_similarity) VALUES ($id, 'Imported canonical name', 'Active', '2024-01-01T00:00:00Z', 'Imported label', NULL, NULL)",
			("$id", canonicalId.ToString()));

		HeldEmbeddingService heldEmbedding = new();
		CanonicalLockBarrier workerBarrier = new();
		using ServiceProvider workerProvider = BuildWorkerProvider(heldEmbedding, workerBarrier);
		using EmbeddingGenerationService worker = new(
			workerProvider.GetRequiredService<IServiceScopeFactory>(),
			NullLogger<EmbeddingGenerationService>.Instance);
		Task<int> generation = worker.ProcessPendingEmbeddingsAsync(CancellationToken.None);
		(await heldEmbedding.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)))
			.Should().ContainSingle().Which.Should().Be("Old canonical name");
		heldEmbedding.Release.TrySetResult();
		await workerBarrier.LockHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));

		ImportReadBarrier importBarrier = new();
		DbContextOptions<ApplicationDbContext> importOptions =
			new DbContextOptionsBuilder<ApplicationDbContext>(target.CreateOptions())
				.AddInterceptors(importBarrier)
				.Options;
		BackupImportService importer = new(new Factory(importOptions), NullLogger<BackupImportService>.Instance);
		artifact.CloseForRead();
		FileStream stream = File.OpenRead(artifact.Path);
		await using (stream)
		{
			Task<BackupImportResult> import = importer.ImportFromSqliteAsync(stream, CancellationToken.None);
			await importBarrier.CanonicalRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
			workerBarrier.Release.TrySetResult();
			await Task.WhenAll(generation, import).WaitAsync(TimeSpan.FromSeconds(15));
		}
		(await generation).Should().Be(1);

		await using (ApplicationDbContext afterImport = target.CreateDbContext())
		{
			NormalizedDescriptionEntity restored = await afterImport.NormalizedDescriptions
				.AsNoTracking().SingleAsync(row => row.Id == canonicalId);
			restored.CanonicalName.Should().Be("Imported canonical name");
			restored.DisplayLabel.Should().Be("Imported label");
			restored.Embedding.Should().BeNull();
			restored.EmbeddingModelVersion.Should().BeNull(
				"the import must invalidate the old-name vector even after waiting on the worker lock");
		}

		using ServiceProvider rebuildProvider = BuildWorkerProvider(new ImmediateEmbeddingService());
		using EmbeddingGenerationService rebuild = new(
			rebuildProvider.GetRequiredService<IServiceScopeFactory>(),
			NullLogger<EmbeddingGenerationService>.Instance);
		(await rebuild.ProcessPendingEmbeddingsAsync(CancellationToken.None)).Should().Be(1);
		await using ApplicationDbContext verify = target.CreateDbContext();
		NormalizedDescriptionEntity rebuilt = await verify.NormalizedDescriptions
			.AsNoTracking().SingleAsync(row => row.Id == canonicalId);
		rebuilt.CanonicalName.Should().Be("Imported canonical name");
		rebuilt.Embedding.Should().NotBeNull();
		rebuilt.EmbeddingModelVersion.Should().Be(OnnxEmbeddingService.EmbeddingSpaceFingerprint);
	}

	[Theory]
	[InlineData("same-active")]
	[InlineData("different-active")]
	[InlineData("same-tombstone")]
	[InlineData("tombstone-and-active")]
	public async Task ImportPairs_UsesSemanticIdentity_AndRepeatsWithoutDuplicateActives(string existing)
	{
		Guid[] receipts = [.. new[] { Guid.NewGuid(), Guid.NewGuid() }.Order()];
		Guid sourceId = Guid.NewGuid();
		Guid activeId = existing is "different-active" or "tombstone-and-active" ? Guid.NewGuid() : sourceId;
		DateTimeOffset accepted = new(2024, 2, 1, 0, 0, 0, TimeSpan.Zero);
		await using (ApplicationDbContext seed = target.CreateDbContext())
		{
			seed.Receipts.AddRange(receipts.Select(Receipt));
			if (existing is "same-tombstone" or "tombstone-and-active")
			{
				seed.AcceptedDuplicatePairs.Add(new() { Id = sourceId, ReceiptIdA = receipts[0], ReceiptIdB = receipts[1], AcceptedAt = accepted.AddDays(-1), DeletedAt = accepted });
			}
			if (existing != "same-tombstone")
			{
				seed.AcceptedDuplicatePairs.Add(new() { Id = activeId, ReceiptIdA = receipts[0], ReceiptIdB = receipts[1], AcceptedAt = accepted.AddDays(-1) });
			}
			await seed.SaveChangesAsync();
		}
		using Artifact artifact = await Artifact.Create(5);
		artifact.AddReceipts(receipts);
		artifact.AddPair(sourceId, receipts[0], receipts[1], accepted);

		for (int pass = 0; pass < 2; pass++)
		{
			BackupImportResult result = await Import(artifact);
			result.AcceptedDuplicatePairsCreated.Should().Be(0);
			result.AcceptedDuplicatePairsUpdated.Should().Be(1);
		}

		await using ApplicationDbContext read = target.CreateDbContext();
		List<AcceptedDuplicatePairEntity> active = await read.AcceptedDuplicatePairs.Where(row => row.ReceiptIdA == receipts[0] && row.ReceiptIdB == receipts[1]).ToListAsync();
		active.Should().ContainSingle();
		active[0].Id.Should().Be(activeId);
		active[0].AcceptedAt.Should().Be(accepted);
		if (existing == "tombstone-and-active")
		{
			(await read.AcceptedDuplicatePairs.IgnoreQueryFilters().SingleAsync(row => row.Id == sourceId)).DeletedAt.Should().NotBeNull();
		}
	}

	[Fact]
	public async Task ImportPairs_SourceIdBelongsToAnotherPair_RejectsAndRollsBackEarlierReceiptUpdates()
	{
		Guid[] ids = [.. new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() }.Order()];
		Guid pairId = Guid.NewGuid();
		await using (ApplicationDbContext seed = target.CreateDbContext())
		{
			seed.Receipts.AddRange(ids.Select(Receipt));
			seed.AcceptedDuplicatePairs.Add(new() { Id = pairId, ReceiptIdA = ids[0], ReceiptIdB = ids[2], AcceptedAt = DateTimeOffset.UtcNow });
			await seed.SaveChangesAsync();
		}
		using Artifact artifact = await Artifact.Create(5);
		artifact.AddReceipts(ids);
		artifact.AddPair(pairId, ids[0], ids[1], DateTimeOffset.UtcNow);

		Func<Task> import = () => Import(artifact);
		await import.Should().ThrowAsync<InvalidOperationException>();

		await using ApplicationDbContext read = target.CreateDbContext();
		(await read.Receipts.Where(row => ids.Contains(row.Id)).ToListAsync()).Should().OnlyContain(row => row.Location == "Before import");
		(await read.AcceptedDuplicatePairs.SingleAsync(row => row.Id == pairId)).ReceiptIdB.Should().Be(ids[2]);
		string[] auditIds = [.. ids.Select(id => id.ToString())];
		(await read.AuditLogs.CountAsync(row => auditIds.Contains(row.EntityId))).Should().Be(3, "failed import must roll back earlier row audits too");
	}

	[Fact]
	public async Task ImportPairs_MissingEndpoint_RollsBackEntireImport()
	{
		Guid[] ids = [.. new[] { Guid.NewGuid(), Guid.NewGuid() }.Order()];
		using Artifact artifact = await Artifact.Create(5);
		artifact.AddReceipts([ids[0]]);
		artifact.AddPair(Guid.NewGuid(), ids[0], ids[1], DateTimeOffset.UtcNow);

		Func<Task> import = () => Import(artifact);
		await import.Should().ThrowAsync<InvalidOperationException>();

		await using ApplicationDbContext read = target.CreateDbContext();
		(await read.Receipts.AnyAsync(row => row.Id == ids[0])).Should().BeFalse();
		(await read.AuditLogs.AnyAsync(row => row.EntityId == ids[0].ToString())).Should().BeFalse();
		(await read.AcceptedDuplicatePairs.AnyAsync(row => row.ReceiptIdA == ids[0])).Should().BeFalse();
	}

	[Fact]
	public async Task ImportCanonicalForwardReference_PreservesNeighbourEvidenceBeforeLinkingItems()
	{
		Guid pending = Guid.NewGuid(), neighbour = Guid.NewGuid(), receipt = Guid.NewGuid(), item = Guid.NewGuid();
		using Artifact artifact = await Artifact.Create(5);
		artifact.AddReceipts([receipt]);
		artifact.Execute("INSERT INTO normalized_descriptions (id, canonical_name, status, created_at, display_label, nearest_neighbour_id, nearest_neighbour_similarity) VALUES ($id, $name, 'PendingReview', '2024-01-01T00:00:00Z', NULL, $neighbour, '0.78')", ("$id", pending.ToString()), ("$name", $"Pending {pending}"), ("$neighbour", neighbour.ToString()));
		artifact.Execute("INSERT INTO normalized_descriptions (id, canonical_name, status, created_at, display_label, nearest_neighbour_id, nearest_neighbour_similarity) VALUES ($id, $name, 'Active', '2024-01-01T00:00:00Z', NULL, NULL, NULL)", ("$id", neighbour.ToString()), ("$name", $"Neighbour {neighbour}"));
		artifact.AddItem(item, receipt, "Pending item", pending, 0.78);

		await Import(artifact);

		await using ApplicationDbContext read = target.CreateDbContext();
		NormalizedDescriptionEntity restored = await read.NormalizedDescriptions.SingleAsync(row => row.Id == pending);
		restored.NearestNeighbourId.Should().Be(neighbour);
		restored.NearestNeighbourSimilarity.Should().Be(0.78);
		(await read.ReceiptItems.SingleAsync(row => row.Id == item)).NormalizedDescriptionId.Should().Be(pending);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	[InlineData(4)]
	public async Task LegacyFreshImport_DefaultsAbsentCuration_AndLeavesExistingDecisionsAlone(int version)
	{
		Guid receipt = Guid.NewGuid(), item = Guid.NewGuid(), template = Guid.NewGuid(), canonical = Guid.NewGuid();
		Guid[] existingReceipts = [.. new[] { Guid.NewGuid(), Guid.NewGuid() }.Order()];
		Guid existingPair = Guid.NewGuid();
		await using (ApplicationDbContext seed = target.CreateDbContext())
		{
			seed.Receipts.AddRange(existingReceipts.Select(Receipt));
			seed.AcceptedDuplicatePairs.Add(new() { Id = existingPair, ReceiptIdA = existingReceipts[0], ReceiptIdB = existingReceipts[1], AcceptedAt = DateTimeOffset.UtcNow });
			await seed.SaveChangesAsync();
		}
		using Artifact artifact = await Artifact.Create(version);
		artifact.AddReceipts([receipt]);
		artifact.AddItem(item, receipt, "Legacy item");
		artifact.Execute("CREATE TABLE item_templates (id TEXT PRIMARY KEY, name TEXT, default_category TEXT, default_subcategory TEXT, default_unit_price TEXT, default_unit_price_currency TEXT, default_item_code TEXT, description TEXT)");
		artifact.Execute("INSERT INTO item_templates VALUES ($id, $name, NULL, NULL, NULL, NULL, NULL, NULL)", ("$id", template.ToString()), ("$name", $"Template {template}"));
		if (version == 4)
		{
			artifact.Execute("CREATE TABLE normalized_descriptions (id TEXT PRIMARY KEY, canonical_name TEXT, status TEXT, created_at TEXT)");
			artifact.Execute("INSERT INTO normalized_descriptions VALUES ($id, $name, 'Rejected', '2024-01-01T00:00:00Z')", ("$id", canonical.ToString()), ("$name", $"Rejected {canonical}"));
		}

		BackupImportResult result = await Import(artifact);

		result.AcceptedDuplicatePairsCreated.Should().Be(0);
		result.AcceptedDuplicatePairsUpdated.Should().Be(0);
		await using ApplicationDbContext read = target.CreateDbContext();
		ReceiptItemEntity stored = await read.ReceiptItems.SingleAsync(row => row.Id == item);
		stored.NormalizedDescriptionId.Should().BeNull();
		stored.NormalizedDescriptionMatchScore.Should().BeNull();
		(await read.ItemTemplates.SingleAsync(row => row.Id == template)).NormalizedDescriptionId.Should().BeNull();
		(await read.AcceptedDuplicatePairs.AnyAsync(row => row.Id == existingPair)).Should().BeTrue();
		if (version == 4)
		{
			NormalizedDescriptionEntity description = await read.NormalizedDescriptions.SingleAsync(row => row.Id == canonical);
			description.Status.Should().Be(NormalizedDescriptionStatus.Rejected);
			description.DisplayLabel.Should().BeNull();
			description.NearestNeighbourId.Should().BeNull();
			description.NearestNeighbourSimilarity.Should().BeNull();
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task LegacyV4Upsert_PreservesAbsentCanonicalEvidence_AndInvalidatesOnlyChangedItemText(bool changed)
	{
		Guid receipt = Guid.NewGuid(), item = Guid.NewGuid(), canonical = Guid.NewGuid(), neighbour = Guid.NewGuid(), template = Guid.NewGuid();
		string canonicalName = $"Milk canonical {canonical}";
		await using (ApplicationDbContext seed = target.CreateDbContext())
		{
			seed.Receipts.Add(Receipt(receipt));
			seed.NormalizedDescriptions.AddRange(
				new() { Id = neighbour, CanonicalName = $"Neighbour {neighbour}", Status = NormalizedDescriptionStatus.Active, CreatedAt = DateTimeOffset.UtcNow },
				new() { Id = canonical, CanonicalName = canonicalName, DisplayLabel = $"Human label {canonical}", Status = NormalizedDescriptionStatus.Active, CreatedAt = DateTimeOffset.UtcNow, NearestNeighbourId = neighbour, NearestNeighbourSimilarity = 0.86 });
			seed.ReceiptItems.Add(new() { Id = item, ReceiptId = receipt, Description = "Milk", Quantity = 1, UnitPrice = 3, TotalAmount = 3, Category = "Food", NormalizedDescriptionId = canonical, NormalizedDescriptionMatchScore = 0.79 });
			seed.ItemTemplates.Add(new() { Id = template, Name = $"Template {template}", NormalizedDescriptionId = canonical });
			await seed.SaveChangesAsync();
		}
		using Artifact artifact = await Artifact.Create(4);
		artifact.AddReceipts([receipt]);
		artifact.AddItem(item, receipt, changed ? "Bread" : "Milk");
		artifact.Execute("CREATE TABLE normalized_descriptions (id TEXT PRIMARY KEY, canonical_name TEXT, status TEXT, created_at TEXT)");
		artifact.Execute("INSERT INTO normalized_descriptions VALUES ($id, $name, 'PendingReview', '2024-01-01T00:00:00Z')", ("$id", canonical.ToString()), ("$name", canonicalName));
		artifact.Execute("CREATE TABLE item_templates (id TEXT PRIMARY KEY, name TEXT, default_category TEXT, default_subcategory TEXT, default_unit_price TEXT, default_unit_price_currency TEXT, default_item_code TEXT, description TEXT)");
		artifact.Execute("INSERT INTO item_templates VALUES ($id, $name, NULL, NULL, NULL, NULL, NULL, NULL)", ("$id", template.ToString()), ("$name", $"Updated template {template}"));

		await Import(artifact);

		await using ApplicationDbContext read = target.CreateDbContext();
		ReceiptItemEntity stored = await read.ReceiptItems.SingleAsync(row => row.Id == item);
		stored.NormalizedDescriptionId.Should().Be(changed ? null : canonical);
		stored.NormalizedDescriptionMatchScore.Should().Be(changed ? null : 0.79);
		(await read.ItemTemplates.SingleAsync(row => row.Id == template)).NormalizedDescriptionId.Should().Be(canonical);
		NormalizedDescriptionEntity row = await read.NormalizedDescriptions.SingleAsync(row => row.Id == canonical);
		row.DisplayLabel.Should().Be($"Human label {canonical}");
		row.NearestNeighbourId.Should().Be(neighbour);
		row.NearestNeighbourSimilarity.Should().Be(0.86);
		row.Status.Should().Be(NormalizedDescriptionStatus.PendingReview);
	}

	[Fact]
	public async Task ImportLabelSwap_RestoresValidFinalNamesAcrossExistingRows()
	{
		Guid a = Guid.NewGuid(), b = Guid.NewGuid();
		string x = $"Label X {a}", y = $"Label Y {b}";
		await using (ApplicationDbContext seed = target.CreateDbContext())
		{
			seed.NormalizedDescriptions.AddRange(
				new() { Id = a, CanonicalName = $"Raw A {a}", DisplayLabel = y, CreatedAt = DateTimeOffset.UtcNow },
				new() { Id = b, CanonicalName = $"Raw B {b}", DisplayLabel = x, CreatedAt = DateTimeOffset.UtcNow });
			await seed.SaveChangesAsync();
		}
		using Artifact artifact = await Artifact.Create(5);
		artifact.Execute("INSERT INTO normalized_descriptions (id, canonical_name, status, created_at, display_label) VALUES ($id, $name, 'Active', '2024-01-01T00:00:00Z', $label)", ("$id", a.ToString()), ("$name", $"Raw A {a}"), ("$label", x));
		artifact.Execute("INSERT INTO normalized_descriptions (id, canonical_name, status, created_at, display_label) VALUES ($id, $name, 'Active', '2024-01-01T00:00:00Z', $label)", ("$id", b.ToString()), ("$name", $"Raw B {b}"), ("$label", y));

		await Import(artifact);

		await using ApplicationDbContext read = target.CreateDbContext();
		(await read.NormalizedDescriptions.SingleAsync(row => row.Id == a)).DisplayLabel.Should().Be(x);
		(await read.NormalizedDescriptions.SingleAsync(row => row.Id == b)).DisplayLabel.Should().Be(y);
		AuditLogEntity auditA = await read.AuditLogs.SingleAsync(row => row.EntityId == a.ToString() && row.Action == AuditAction.Update);
		AuditLogEntity auditB = await read.AuditLogs.SingleAsync(row => row.EntityId == b.ToString() && row.Action == AuditAction.Update);
		auditA.GetChanges().Should().ContainSingle(change => change.FieldName == "DisplayLabel" && change.OldValue == y && change.NewValue == x);
		auditB.GetChanges().Should().ContainSingle(change => change.FieldName == "DisplayLabel" && change.OldValue == x && change.NewValue == y);
		auditA.GetChanges().Should().NotContain(change => change.FieldName == "CanonicalName");
		auditB.GetChanges().Should().NotContain(change => change.FieldName == "CanonicalName");
	}

	[Fact]
	public async Task ImportLabelCollision_WithRowOutsideArtifact_RejectsWithoutChangingAnyImportedRows()
	{
		Guid a = Guid.NewGuid(), outside = Guid.NewGuid(), account = Guid.NewGuid();
		string taken = $"Occupied {outside}";
		await using (ApplicationDbContext seed = target.CreateDbContext())
		{
			seed.NormalizedDescriptions.AddRange(
				new() { Id = a, CanonicalName = $"Raw {a}", DisplayLabel = $"Before {a}", CreatedAt = DateTimeOffset.UtcNow },
				new() { Id = outside, CanonicalName = $"Outside {outside}", DisplayLabel = taken, CreatedAt = DateTimeOffset.UtcNow });
			await seed.SaveChangesAsync();
		}
		using Artifact artifact = await Artifact.Create(5);
		artifact.Execute("INSERT INTO accounts VALUES ($id, 'Earlier imported account', 1)", ("$id", account.ToString()));
		artifact.Execute("INSERT INTO normalized_descriptions (id, canonical_name, status, created_at, display_label) VALUES ($id, $name, 'Active', '2024-01-01T00:00:00Z', $label)", ("$id", a.ToString()), ("$name", $"Raw {a}"), ("$label", taken));

		Func<Task> import = () => Import(artifact);
		await import.Should().ThrowAsync<InvalidOperationException>();

		await using ApplicationDbContext read = target.CreateDbContext();
		(await read.NormalizedDescriptions.SingleAsync(row => row.Id == a)).DisplayLabel.Should().Be($"Before {a}");
		(await read.NormalizedDescriptions.SingleAsync(row => row.Id == outside)).DisplayLabel.Should().Be(taken);
		(await read.Accounts.AnyAsync(row => row.Id == account)).Should().BeFalse();
		(await read.AuditLogs.CountAsync(row => row.EntityId == a.ToString())).Should().Be(1);
	}

	[Theory]
	[InlineData("item")]
	[InlineData("template")]
	[InlineData("neighbour")]
	[InlineData("pair")]
	public async Task ImportCurationReference_ExistingOnlyInTarget_IsRejectedAsIncompleteArtifact(string relation)
	{
		Guid externalCanonical = Guid.NewGuid(), externalReceipt = Guid.NewGuid(), receipt = Guid.NewGuid(), dependent = Guid.NewGuid(), account = Guid.NewGuid();
		await using (ApplicationDbContext seed = target.CreateDbContext())
		{
			seed.NormalizedDescriptions.Add(new() { Id = externalCanonical, CanonicalName = $"Target only {externalCanonical}", CreatedAt = DateTimeOffset.UtcNow });
			seed.Receipts.Add(Receipt(externalReceipt));
			await seed.SaveChangesAsync();
		}
		using Artifact artifact = await Artifact.Create(5);
		artifact.AddReceipts([receipt]);
		artifact.Execute("INSERT INTO accounts VALUES ($id, 'Earlier imported account', 1)", ("$id", account.ToString()));
		switch (relation)
		{
			case "item":
				artifact.AddItem(dependent, receipt, "Incomplete item", externalCanonical, 0.8);
				break;
			case "template":
				artifact.Execute("INSERT INTO item_templates (id, name, normalized_description_id) VALUES ($id, $name, $canonical)", ("$id", dependent.ToString()), ("$name", $"Incomplete template {dependent}"), ("$canonical", externalCanonical.ToString()));
				break;
			case "neighbour":
				artifact.Execute("INSERT INTO normalized_descriptions (id, canonical_name, status, created_at, nearest_neighbour_id, nearest_neighbour_similarity) VALUES ($id, $name, 'PendingReview', '2024-01-01T00:00:00Z', $canonical, '0.8')", ("$id", dependent.ToString()), ("$name", $"Incomplete neighbour {dependent}"), ("$canonical", externalCanonical.ToString()));
				break;
			case "pair":
				Guid[] endpoints = [.. new[] { receipt, externalReceipt }.Order()];
				artifact.AddPair(dependent, endpoints[0], endpoints[1], DateTimeOffset.UtcNow);
				break;
		}

		Func<Task> import = () => Import(artifact);
		await import.Should().ThrowAsync<InvalidOperationException>();

		await using ApplicationDbContext read = target.CreateDbContext();
		(await read.Receipts.AnyAsync(row => row.Id == receipt)).Should().BeFalse();
		(await read.Accounts.AnyAsync(row => row.Id == account)).Should().BeFalse();
		(await read.AuditLogs.AnyAsync(row => row.EntityId == dependent.ToString())).Should().BeFalse();
	}

	private async Task<BackupImportResult> Import(Artifact artifact)
	{
		BackupImportService importer = new(new Factory(target.CreateOptions()), NullLogger<BackupImportService>.Instance);
		artifact.CloseForRead();
		await using FileStream stream = File.OpenRead(artifact.Path);
		return await importer.ImportFromSqliteAsync(stream, CancellationToken.None);
	}

	private ServiceProvider BuildWorkerProvider(IEmbeddingService embeddings, IInterceptor? interceptor = null)
	{
		DbContextOptionsBuilder<ApplicationDbContext> options =
			new(target.CreateOptions());
		if (interceptor is not null)
		{
			options.AddInterceptors(interceptor);
		}
		ServiceCollection services = new();
		services.AddSingleton(embeddings);
		services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new Factory(options.Options));
		return services.BuildServiceProvider();
	}

	private sealed class HeldEmbeddingService : IEmbeddingService
	{
		public bool IsConfigured => true;
		public TaskCompletionSource<IReadOnlyList<string>> Started { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken) =>
			throw new NotSupportedException();

		public async Task<List<float[]>> GenerateEmbeddingsAsync(
			List<string> texts,
			CancellationToken cancellationToken)
		{
			Started.TrySetResult(texts);
			await Release.Task.WaitAsync(cancellationToken);
			return texts.Select(_ => new float[OnnxEmbeddingService.EmbeddingDimension]).ToList();
		}
	}

	private sealed class ImmediateEmbeddingService : IEmbeddingService
	{
		public bool IsConfigured => true;
		public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken) =>
			Task.FromResult(new float[OnnxEmbeddingService.EmbeddingDimension]);
		public Task<List<float[]>> GenerateEmbeddingsAsync(List<string> texts, CancellationToken cancellationToken) =>
			Task.FromResult(texts.Select(_ => new float[OnnxEmbeddingService.EmbeddingDimension]).ToList());
	}

	private sealed class CanonicalLockBarrier : DbCommandInterceptor
	{
		public TaskCompletionSource LockHeld { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public override async ValueTask<DbDataReader> ReaderExecutedAsync(
			DbCommand command,
			CommandExecutedEventData eventData,
			DbDataReader result,
			CancellationToken cancellationToken = default)
		{
			if (command.CommandText.Contains("matching.\"NormalizedDescriptions\"", StringComparison.Ordinal)
				&& command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
			{
				LockHeld.TrySetResult();
				await Release.Task.WaitAsync(cancellationToken);
			}
			return result;
		}
	}

	private sealed class ImportReadBarrier : DbCommandInterceptor
	{
		public TaskCompletionSource CanonicalRead { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public override ValueTask<DbDataReader> ReaderExecutedAsync(
			DbCommand command,
			CommandExecutedEventData eventData,
			DbDataReader result,
			CancellationToken cancellationToken = default)
		{
			if (command.CommandText.Contains("matching.\"NormalizedDescriptions\"", StringComparison.Ordinal)
				&& !command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
			{
				CanonicalRead.TrySetResult();
			}
			return ValueTask.FromResult(result);
		}
	}

	private static ReceiptEntity Receipt(Guid id) => new() { Id = id, Location = "Before import", Date = new(2024, 1, 1) };
	private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
		public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
	}

	// Handwritten versioned artifacts pin old schema absence independently of the current exporter.
	private sealed class Artifact : IDisposable
	{
		private readonly int _version;
		private readonly SqliteConnection _sqlite;
		public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"curation-import-{Guid.NewGuid():N}.db");
		private Artifact(int version)
		{
			_version = version;
			_sqlite = new(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false, ForeignKeys = false }.ToString());
			_sqlite.Open();
		}
		public static async Task<Artifact> Create(int version)
		{
			Artifact artifact = new(version);
			if (version >= 5)
			{
				await BackupService.CreateSchemaAsync(artifact._sqlite, CancellationToken.None);
			}
			else if (version > 0)
			{
				artifact.Execute("CREATE TABLE backup_metadata (key TEXT PRIMARY KEY, value TEXT)");
			}
			if (version > 0)
			{
				artifact.Execute("INSERT INTO backup_metadata VALUES ('export_version', $version)", ("$version", version.ToString()));
			}
			if (version is 3 or 4)
			{
				artifact.Execute("CREATE TABLE accounts (id TEXT PRIMARY KEY, name TEXT, is_active INTEGER)");
			}
			return artifact;
		}
		public void AddReceipts(Guid[] ids)
		{
			string imageColumns = _version >= 4 ? ", original_image_path TEXT, processed_image_path TEXT" : "";
			if (_version < 5)
			{
				Execute($"CREATE TABLE receipts (id TEXT PRIMARY KEY, location TEXT, date TEXT, tax_amount TEXT, tax_amount_currency TEXT{imageColumns})");
			}
			foreach (Guid id in ids)
			{
				Execute($"INSERT INTO receipts VALUES ($id, 'Imported', '2024-01-01', '0', 'USD'{(_version >= 4 ? ", NULL, NULL" : "")})", ("$id", id.ToString()));
			}
		}
		public void AddPair(Guid id, Guid a, Guid b, DateTimeOffset accepted)
		{
			if (_version < 5)
			{
				Execute("CREATE TABLE accepted_duplicate_pairs (id TEXT PRIMARY KEY, receipt_id_a TEXT, receipt_id_b TEXT, accepted_at TEXT)");
			}
			Execute("INSERT INTO accepted_duplicate_pairs VALUES ($id, $a, $b, $accepted)", ("$id", id.ToString()), ("$a", a.ToString()), ("$b", b.ToString()), ("$accepted", accepted.ToString("O")));
		}
		public void AddItem(Guid id, Guid receipt, string description, Guid? canonical = null, double? score = null)
		{
			string curationColumns = _version >= 5 ? ", normalized_description_id TEXT, normalized_description_match_score TEXT" : "";
			if (_version < 5)
			{
				Execute($"CREATE TABLE receipt_items (id TEXT PRIMARY KEY, receipt_id TEXT, receipt_item_code TEXT, description TEXT, quantity TEXT, unit_price TEXT, unit_price_currency TEXT, total_amount TEXT, total_amount_currency TEXT, category TEXT, subcategory TEXT{curationColumns})");
			}
			Execute($"INSERT INTO receipt_items VALUES ($id, $receipt, NULL, $description, '1', '3', 'USD', '3', 'USD', 'Food', NULL{(_version >= 5 ? ", $canonical, $score" : "")})", ("$id", id.ToString()), ("$receipt", receipt.ToString()), ("$description", description), ("$canonical", canonical?.ToString() ?? (object)DBNull.Value), ("$score", score ?? (object)DBNull.Value));
		}
		public void Execute(string sql, params (string Name, object Value)[] parameters)
		{
			using SqliteCommand command = _sqlite.CreateCommand();
			command.CommandText = sql;
			foreach ((string name, object value) in parameters)
			{
				command.Parameters.AddWithValue(name, value);
			}
			command.ExecuteNonQuery();
		}
		public void CloseForRead() => _sqlite.Close();
		public void Dispose()
		{
			_sqlite.Dispose();
			File.Delete(Path);
		}
	}
}
