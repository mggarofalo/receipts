using FluentAssertions;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class OrganizationEffectManifestTests(PostgresFixture server) : IsolatedPostgresTest(server)
{
	[Fact]
	public async Task CapturedIntent_IsSealedAndCannotBeChangedOrRemoved()
	{
		var (manifest, item) = await CaptureAsync();
		await using ApplicationDbContext db = fixture.CreateDbContext();
		foreach (string sql in new[]
		{
			"UPDATE taxonomy.\"OrganizationEffectManifests\" SET \"PolicyVersion\" = 'changed'",
			"DELETE FROM taxonomy.\"OrganizationEffectItems\"",
			"TRUNCATE taxonomy.\"OrganizationEffectOutcomes\"",
			"UPDATE taxonomy.\"OrganizationEffectItems\" SET \"ItemRevision\" = gen_random_uuid()"
		})
		{
			Func<Task> mutate = () => db.Database.ExecuteSqlRawAsync(sql);
			await mutate.Should().ThrowAsync<PostgresException>();
		}
		db.Set<OrganizationEffectItemEntity>().Add(new()
		{
			ManifestId = manifest,
			ItemId = Guid.NewGuid(),
			ReceiptId = Guid.NewGuid(),
			TaxonomyRevisionId = item.TaxonomyRevisionId,
			TargetCategoryId = item.TargetCategoryId,
			ApplyOperationId = Guid.NewGuid(),
			UndoOperationId = Guid.NewGuid()
		});
		Func<Task> extend = () => db.SaveChangesAsync();
		(await extend.Should().ThrowAsync<DbUpdateException>()).WithInnerException<PostgresException>();
		db.ChangeTracker.Clear();
		(await db.Set<OrganizationEffectItemEntity>().SingleAsync()).ItemRevision.Should().Be(item.ItemRevision);
	}

	[Fact]
	public async Task TerminalOutcome_IsIdempotentImmutableAndCannotUndoASkippedEffect()
	{
		var (manifest, item) = await CaptureAsync();
		await using ApplicationDbContext db = fixture.CreateDbContext();
		OrganizationEffectOutcomeEntity outcome = new()
		{
			ManifestId = manifest,
			ItemId = item.ItemId,
			Phase = "apply",
			Disposition = "skipped",
			Reason = "source_changed",
			RecordedAt = DateTimeOffset.UtcNow
		};
		db.Add(outcome);
		await db.SaveChangesAsync();
		db.ChangeTracker.Clear();
		db.Add(outcome);
		Func<Task> duplicate = () => db.SaveChangesAsync();
		(await duplicate.Should().ThrowAsync<DbUpdateException>()).WithInnerException<PostgresException>();
		db.ChangeTracker.Clear();
		db.Add(new OrganizationEffectOutcomeEntity
		{
			ManifestId = manifest,
			ItemId = item.ItemId,
			Phase = "undo",
			Disposition = "skipped",
			Reason = "attempted_undo",
			RecordedAt = DateTimeOffset.UtcNow
		});
		Func<Task> undo = () => db.SaveChangesAsync();
		(await undo.Should().ThrowAsync<DbUpdateException>()).WithInnerException<PostgresException>();
		db.ChangeTracker.Clear();
		Func<Task> mutate = () => db.Database.ExecuteSqlRawAsync("UPDATE taxonomy.\"OrganizationEffectOutcomes\" SET \"Reason\" = 'changed'");
		await mutate.Should().ThrowAsync<PostgresException>();
		(await db.Set<OrganizationEffectOutcomeEntity>().SingleAsync()).Reason.Should().Be("source_changed");
	}

	[Fact]
	public async Task AppliedOutcome_RejectsUnrelatedAssignmentReference()
	{
		var (manifest, item) = await CaptureAsync();
		await using ApplicationDbContext db = fixture.CreateDbContext();
		db.Add(new OrganizationEffectOutcomeEntity
		{
			ManifestId = manifest,
			ItemId = item.ItemId,
			Phase = "apply",
			Disposition = "applied",
			AssignmentId = Guid.NewGuid(),
			ItemRevision = item.ItemRevision,
			ReceiptRevision = item.ReceiptRevision,
			RecordedAt = DateTimeOffset.UtcNow
		});
		Func<Task> unrelated = () => db.SaveChangesAsync();
		(await unrelated.Should().ThrowAsync<DbUpdateException>()).WithInnerException<PostgresException>();
		db.ChangeTracker.Clear();
		(await db.Set<OrganizationEffectOutcomeEntity>().CountAsync()).Should().Be(0);
	}

	[Fact]
	public async Task AppliedAndUndoneOutcomes_RetainSeparateAssignmentsAndFrozenEvidence()
	{
		var (manifest, item) = await CaptureAsync();
		await using ApplicationDbContext db = fixture.CreateDbContext();
		var receipt = SampleData.Entities.ReceiptEntityGenerator.Generate();
		receipt.Id = item.ReceiptId;
		var source = SampleData.Entities.ReceiptItemEntityGenerator.Generate(receipt.Id);
		source.Id = item.ItemId;
		source.Category = "";
		source.Subcategory = null;
		db.AddRange(receipt, source);
		await db.SaveChangesAsync();
		PurchaseClassificationEntity applied = new()
		{
			Id = Guid.NewGuid(),
			ReceiptItemId = item.ItemId,
			Sequence = 1,
			TaxonomyRevisionId = item.TaxonomyRevisionId,
			CategoryId = item.TargetCategoryId,
			OperationId = item.ApplyOperationId,
			RecordedAt = DateTimeOffset.UtcNow,
			Actor = "test"
		};
		db.Classifications.Add(applied);
		await db.SaveChangesAsync();
		db.Add(new OrganizationEffectOutcomeEntity
		{
			ManifestId = manifest,
			ItemId = item.ItemId,
			Phase = "apply",
			Disposition = "applied",
			AssignmentId = applied.Id,
			ItemRevision = item.ItemRevision,
			ReceiptRevision = item.ReceiptRevision,
			RecordedAt = DateTimeOffset.UtcNow
		});
		await db.SaveChangesAsync();
		PurchaseClassificationEntity undone = new()
		{
			Id = Guid.NewGuid(),
			ReceiptItemId = item.ItemId,
			Sequence = 2,
			PreviousAssignmentId = applied.Id,
			TaxonomyRevisionId = item.TaxonomyRevisionId,
			CategoryId = null,
			OperationId = item.UndoOperationId,
			RecordedAt = DateTimeOffset.UtcNow,
			Actor = "test"
		};
		db.Classifications.Add(undone);
		await db.SaveChangesAsync();
		db.Add(new OrganizationEffectOutcomeEntity
		{
			ManifestId = manifest,
			ItemId = item.ItemId,
			Phase = "undo",
			Disposition = "applied",
			AssignmentId = undone.Id,
			ItemRevision = item.ItemRevision,
			ReceiptRevision = item.ReceiptRevision,
			RecordedAt = DateTimeOffset.UtcNow
		});
		await db.SaveChangesAsync();
		db.ChangeTracker.Clear();
		(await db.Set<OrganizationEffectOutcomeEntity>().Select(x => x.AssignmentId).ToListAsync())
			.Should().BeEquivalentTo(new Guid?[] { applied.Id, undone.Id });
		(await db.Set<OrganizationEffectItemEntity>().SingleAsync()).ExpectedAssignmentId.Should().BeNull();
	}

	[Theory]
	[InlineData("apply", "undo")]
	[InlineData("undo", "apply")]
	public async Task CrossPhaseReservation_RejectsCollisionOutsideRepeatableReadSnapshot(string firstPhase, string secondPhase)
	{
		var (_, baseline) = await CaptureAsync();
		await using ApplicationDbContext first = fixture.CreateDbContext();
		await using ApplicationDbContext second = fixture.CreateDbContext();
		await using var firstTransaction = await first.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
		await using var secondTransaction = await second.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
		// Freeze the second transaction's view before the first inserts its reservation.
		(await second.Set<OrganizationEffectOperationEntity>().CountAsync()).Should().Be(2);
		Guid shared = Guid.NewGuid();
		OrganizationEffectItemEntity firstItem = await AddManifestAndItemAsync(first, baseline,
			firstPhase == "apply" ? shared : Guid.NewGuid(), firstPhase == "undo" ? shared : Guid.NewGuid());
		await firstTransaction.CommitAsync();
		// Repeatable Read still cannot see the winning reservation. The PK must arbitrate.
		(await second.Set<OrganizationEffectOperationEntity>().AnyAsync(x => x.OperationId == shared)).Should().BeFalse();
		Func<Task> collide = async () => await AddManifestAndItemAsync(second, baseline,
			secondPhase == "apply" ? shared : Guid.NewGuid(), secondPhase == "undo" ? shared : Guid.NewGuid());
		var failure = await collide.Should().ThrowAsync<DbUpdateException>();
		failure.Which.InnerException.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
		await secondTransaction.RollbackAsync();
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		var reservation = await verify.Set<OrganizationEffectOperationEntity>().SingleAsync(x => x.OperationId == shared);
		(reservation.ManifestId, reservation.ItemId, reservation.Phase).Should().Be((firstItem.ManifestId, firstItem.ItemId, firstPhase));
		(await verify.Set<OrganizationEffectOperationEntity>().CountAsync()).Should().Be(4);
		foreach (string sql in new[]
		{
			"UPDATE taxonomy.\"OrganizationEffectOperations\" SET \"Phase\" = 'undo'",
			"DELETE FROM taxonomy.\"OrganizationEffectOperations\"",
			"TRUNCATE taxonomy.\"OrganizationEffectOperations\""
		})
		{
			Func<Task> mutate = () => verify.Database.ExecuteSqlRawAsync(sql);
			await mutate.Should().ThrowAsync<PostgresException>();
		}
	}

	[Fact]
	public async Task ManifestStamp_IsDatabaseOwnedAndCannotBeForgedOrReopened()
	{
		var (_, baseline) = await CaptureAsync();
		Guid manifest = Guid.NewGuid();
		await using ApplicationDbContext db = fixture.CreateDbContext();
		await using (var transaction = await db.Database.BeginTransactionAsync())
		{
			await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO taxonomy."OrganizationEffectManifests"
                    ("Id", "Actor", "RequestHash", "PolicyVersion", "TaxonomyRevisionId", "CapturedAt", "WriteTransaction")
                VALUES ({manifest}, 'test', 'hash', 'v1', {baseline.TaxonomyRevisionId}, now(), 0)
                """);
			long stamp = await db.Set<OrganizationEffectManifestEntity>().Where(x => x.Id == manifest)
				.Select(x => EF.Property<long>(x, "WriteTransaction")).SingleAsync();
			stamp.Should().NotBe(0);
			await transaction.CommitAsync();
		}
		Func<Task> reopen = () => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE taxonomy."OrganizationEffectManifests" SET "WriteTransaction" = txid_current() WHERE "Id" = {manifest}
            """);
		await reopen.Should().ThrowAsync<PostgresException>();
		Func<Task> forgedReservation = () => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO taxonomy."OrganizationEffectOperations" ("OperationId", "ManifestId", "ItemId", "Phase")
            VALUES ({Guid.NewGuid()}, {baseline.ManifestId}, {baseline.ItemId}, 'apply')
            """);
		(await forgedReservation.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
	}

	[Theory]
	[InlineData("operation")]
	[InlineData("category")]
	[InlineData("lineage")]
	public async Task AppliedOutcome_RejectsSameItemAssignmentDifferingFromIntent(string difference)
	{
		var (manifest, item) = await CaptureAsync();
		await using ApplicationDbContext db = fixture.CreateDbContext();
		var receipt = SampleData.Entities.ReceiptEntityGenerator.Generate();
		receipt.Id = item.ReceiptId;
		var source = SampleData.Entities.ReceiptItemEntityGenerator.Generate(receipt.Id);
		source.Id = item.ItemId;
		source.Category = "";
		source.Subcategory = null;
		db.AddRange(receipt, source);
		await db.SaveChangesAsync();
		Guid? previous = null;
		if (difference == "lineage")
		{
			previous = Guid.NewGuid();
			db.Classifications.Add(new()
			{
				Id = previous.Value,
				ReceiptItemId = item.ItemId,
				Sequence = 1,
				TaxonomyRevisionId = item.TaxonomyRevisionId,
				OperationId = Guid.NewGuid(),
				RecordedAt = DateTimeOffset.UtcNow,
				Actor = "test"
			});
			await db.SaveChangesAsync();
		}
		PurchaseClassificationEntity candidate = new()
		{
			Id = Guid.NewGuid(),
			ReceiptItemId = item.ItemId,
			Sequence = previous is null ? 1 : 2,
			PreviousAssignmentId = previous,
			TaxonomyRevisionId = item.TaxonomyRevisionId,
			CategoryId = difference == "category" ? null : item.TargetCategoryId,
			OperationId = difference == "operation" ? Guid.NewGuid() : item.ApplyOperationId,
			RecordedAt = DateTimeOffset.UtcNow,
			Actor = "test"
		};
		db.Classifications.Add(candidate);
		await db.SaveChangesAsync();
		db.Add(new OrganizationEffectOutcomeEntity
		{
			ManifestId = manifest,
			ItemId = item.ItemId,
			Phase = "apply",
			Disposition = "applied",
			AssignmentId = candidate.Id,
			ItemRevision = item.ItemRevision,
			ReceiptRevision = item.ReceiptRevision,
			RecordedAt = DateTimeOffset.UtcNow
		});
		Func<Task> mismatch = () => db.SaveChangesAsync();
		var failure = await mismatch.Should().ThrowAsync<DbUpdateException>();
		failure.Which.InnerException.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
		db.ChangeTracker.Clear();
		(await db.Set<OrganizationEffectOutcomeEntity>().CountAsync()).Should().Be(0);
	}

	private static async Task<OrganizationEffectItemEntity> AddManifestAndItemAsync(ApplicationDbContext db,
		OrganizationEffectItemEntity baseline, Guid apply, Guid undo)
	{
		Guid manifest = Guid.NewGuid();
		db.Add(new OrganizationEffectManifestEntity
		{
			Id = manifest,
			Actor = "test",
			RequestHash = "concurrency",
			PolicyVersion = "v1",
			TaxonomyRevisionId = baseline.TaxonomyRevisionId,
			CapturedAt = DateTimeOffset.UtcNow
		});
		await db.SaveChangesAsync();
		OrganizationEffectItemEntity item = new()
		{
			ManifestId = manifest,
			ItemId = Guid.NewGuid(),
			ReceiptId = Guid.NewGuid(),
			TaxonomyRevisionId = baseline.TaxonomyRevisionId,
			TargetCategoryId = baseline.TargetCategoryId,
			ItemRevision = Guid.NewGuid(),
			ReceiptRevision = Guid.NewGuid(),
			ApplyOperationId = apply,
			UndoOperationId = undo
		};
		db.Add(item);
		await db.SaveChangesAsync();
		return item;
	}

	[Fact]
	public async Task NonUtcEvidenceTimestamps_PersistAsUtcWithoutChangingTheirInstants()
	{
		DateTimeOffset capturedAt = new(2026, 10, 28, 7, 15, 0, TimeSpan.FromHours(-5));
		DateTimeOffset recordedAt = new(2026, 10, 28, 22, 30, 0, TimeSpan.FromHours(9));
		var (manifest, item) = await CaptureAsync(capturedAt);
		await using ApplicationDbContext db = fixture.CreateDbContext();
		db.OrganizationEffectOutcomes.Add(new()
		{
			ManifestId = manifest,
			ItemId = item.ItemId,
			Phase = "apply",
			Disposition = "skipped",
			Reason = "source_changed",
			RecordedAt = recordedAt
		});
		await db.SaveChangesAsync();
		db.ChangeTracker.Clear();
		DateTimeOffset actualCapture = (await db.OrganizationEffectManifests.SingleAsync()).CapturedAt;
		DateTimeOffset actualOutcome = (await db.OrganizationEffectOutcomes.SingleAsync()).RecordedAt;
		actualCapture.Should().Be(capturedAt.ToUniversalTime());
		actualCapture.Offset.Should().Be(TimeSpan.Zero);
		actualOutcome.Should().Be(recordedAt.ToUniversalTime());
		actualOutcome.Offset.Should().Be(TimeSpan.Zero);
	}

	[Fact]
	public async Task PortableMembershipHash_SurvivesActualJsonbNumericRoundTrip()
	{
		const string decision = """
			{"nested":{"exponent":1e2,"decimal":1.230000,"tiny":1.23456789012345678901234567890123456789e-50},
			 "values":[-0.000,1e100,123456789012345678901234567890123456789.012345678901234567890,{"x":-12.3400e+5}]}
			""";
		var (manifestId, original) = await CaptureAsync(decisionEvidence: decision);
		await using ApplicationDbContext db = fixture.CreateDbContext();
		var manifest = await db.OrganizationEffectManifests.AsNoTracking().SingleAsync(m => m.Id == manifestId);
		var stored = await db.OrganizationEffectItems.AsNoTracking().SingleAsync();
		stored.DecisionEvidenceJson.Should().NotBe(decision);
		stored.DecisionEvidenceJson.Should().NotContain("1e2");
		PortableTaxonomyArchive.SealEffectManifest(manifest, [stored]).ItemsHash
			.Should().Be(PortableTaxonomyArchive.SealEffectManifest(manifest, [original]).ItemsHash);
		PortableTaxonomyArchive.CanonicalEffectDecision(stored.DecisionEvidenceJson)
			.Should().Be(PortableTaxonomyArchive.CanonicalEffectDecision(decision));
	}

	private async Task<(Guid Manifest, OrganizationEffectItemEntity Item)> CaptureAsync(DateTimeOffset? capturedAt = null,
		string decisionEvidence = "{\"policy\":\"v1\"}")
	{
		await using ApplicationDbContext db = fixture.CreateDbContext();
		await using var transaction = await db.Database.BeginTransactionAsync();
		Guid revision = Guid.NewGuid(), category = Guid.NewGuid(), manifest = Guid.NewGuid();
		db.TaxonomyIdentities.Add(new() { Id = category });
		db.TaxonomyRevisions.Add(new()
		{
			Id = revision,
			PreviousRevisionId = Infrastructure.Configurations.TaxonomyConfiguration.GenesisId,
			Sequence = 1,
			Actor = "test",
			Reason = "effect test",
			RecordedAt = DateTimeOffset.UtcNow,
			OperationId = Guid.NewGuid()
		});
		await db.SaveChangesAsync();
		db.TaxonomyNodes.Add(new() { RevisionId = revision, CategoryId = category, Name = "Test" });
		db.Add(new OrganizationEffectManifestEntity
		{
			Id = manifest,
			Actor = "test",
			PolicyVersion = "v1",
			RequestHash = "frozen-request",
			TaxonomyRevisionId = revision,
			CapturedAt = capturedAt ?? DateTimeOffset.UtcNow
		});
		await db.SaveChangesAsync();
		OrganizationEffectItemEntity item = new()
		{
			ManifestId = manifest,
			ItemId = Guid.NewGuid(),
			ReceiptId = Guid.NewGuid(),
			Store = "Store",
			RawDescription = "Item",
			ItemRevision = Guid.NewGuid(),
			ReceiptRevision = Guid.NewGuid(),
			TaxonomyRevisionId = revision,
			TargetCategoryId = category,
			ApplyOperationId = Guid.NewGuid(),
			UndoOperationId = Guid.NewGuid(),
			DecisionEvidenceJson = decisionEvidence
		};
		db.Add(item);
		await db.SaveChangesAsync();
		await transaction.CommitAsync();
		return (manifest, item);
	}
}
