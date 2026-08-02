using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Tests;

/// <summary>
/// Makes the coverage claim in <c>PurgeTrashServiceTests</c> enforceable.
///
/// That test seeds a hand-written list of entities, and a hand-written list silently rots: add a
/// new <see cref="ISoftDeletable"/> entity, forget to teach <c>TrashService.PurgeAllDeletedAsync</c>
/// about it, and the suite stays green while its tombstones accumulate with no way to clear them —
/// nothing surfaces them in the recycle bin. That is exactly what happened to
/// <see cref="AcceptedDuplicatePairEntity"/> in RECEIPTS-834.
///
/// This is a UNIT test on purpose. The guard it replaces lived in Infrastructure.IntegrationTests,
/// which CI and the pre-commit hook both skip via <c>--filter "Category!=Integration"</c>, so it
/// never gated anything. Building the EF model needs no database, so there is no reason for it to
/// sit behind Docker.
/// </summary>
public class SoftDeletePurgeCoverageTests
{
	/// <summary>
	/// Entities that <c>PurgeTrashServiceTests.PurgeAllDeletedAsync_RemovesSoftDeletedRowsFromEverySoftDeletableTable_AndPreservesActiveRows</c>
	/// seeds (one active row + one soft-deleted row) and asserts on.
	/// </summary>
	private static readonly string[] CoveredByPurgeTrashServiceTests =
	[
		nameof(AcceptedDuplicatePairEntity),
		nameof(AdjustmentEntity),
		nameof(CategoryEntity),
		nameof(ItemTemplateEntity),
		nameof(ReceiptEntity),
		nameof(ReceiptItemEntity),
		nameof(SubcategoryEntity),
		nameof(TransactionEntity),
		nameof(YnabSyncRecordEntity),
	];

	[Fact]
	public void EverySoftDeletableEntity_IsCoveredByThePurgeTest()
	{
		// Arrange / Act
		string[] softDeletable = SoftDeletableEntityNames();

		// Assert
		softDeletable.Should().NotBeEmpty("the model should map at least one soft-deletable entity");
		softDeletable.Should().BeSubsetOf(
			CoveredByPurgeTrashServiceTests,
			"""
			every ISoftDeletable entity must be seeded (one active row + one soft-deleted row) and
			asserted in PurgeTrashServiceTests.

			If this failed because you ADDED a soft-deletable entity: add an ExecuteDeleteAsync step
			for it in TrashService.PurgeAllDeletedAsync (in FK dependency order, children first),
			then seed and assert it in that test and list it here. Without the purge step its
			tombstones are unreachable — nothing surfaces them in the recycle bin, so they accumulate
			with no way to clear them.
			""");
	}

	[Fact]
	public void CoverageList_HasNoStaleEntries()
	{
		// Catches the reverse drift: an entity removed from the model, or one that stopped being
		// soft-deletable, leaving a name here that no longer means anything.
		string[] softDeletable = SoftDeletableEntityNames();

		CoveredByPurgeTrashServiceTests.Should().BeSubsetOf(
			softDeletable,
			"every name in the coverage list should still map to a soft-deletable entity in the model");
	}

	/// <summary>Builds the EF model only — it does not connect.</summary>
	private static string[] SoftDeletableEntityNames()
	{
		DbContextOptionsBuilder<ApplicationDbContext> builder = new();
		builder.UseNpgsql(
			"Host=model-build-only;Database=model-build-only",
			b =>
			{
				b.UseVector();
				b.UsePublicMigrationsHistory();
			});

		using ApplicationDbContext context = new(builder.Options);
		return [.. context.Model
			.GetEntityTypes()
			.Select(entityType => entityType.ClrType)
			.Where(clrType => typeof(ISoftDeletable).IsAssignableFrom(clrType))
			.Select(clrType => clrType.Name)
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)];
	}
}
