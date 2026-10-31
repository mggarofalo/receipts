using System.Text.Json;
using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Services;

namespace Infrastructure.Tests.Services;

// Rejection coverage for the classification-history checks that validation resolves through lookups.
public sealed partial class PortableTaxonomyArchiveTests
{
	private static readonly string ClassificationHash = new('A', 64);

	private static PurchaseClassificationEntity ManualAssignment(PortableTaxonomyArchive.State state, Guid itemId, long sequence = 1,
		Guid? previous = null, Guid? categoryId = null) => new()
		{
			Id = Guid.NewGuid(),
			ReceiptItemId = itemId,
			Sequence = sequence,
			PreviousAssignmentId = previous,
			CategoryId = categoryId,
			TaxonomyRevisionId = state.Heads[0].RevisionId,
			RecordedAt = state.Revisions[0].RecordedAt.AddSeconds(sequence),
			Actor = "admin",
			Source = "manual",
			Reason = "manual",
			OperationId = Guid.NewGuid()
		};

	private static void Classify(PortableTaxonomyArchive.State state, PurchaseClassificationEntity assignment, bool authorize = true)
	{
		state.Assignments.Add(assignment);
		state.Current.RemoveAll(c => c.ReceiptItemId == assignment.ReceiptItemId);
		state.Current.Add(new() { ReceiptItemId = assignment.ReceiptItemId, AssignmentId = assignment.Id });
		if (authorize)
		{
			state.ClassificationOperations.Add(new() { Actor = assignment.Actor, OperationId = assignment.OperationId, RequestHash = ClassificationHash, AssignmentId = assignment.Id });
		}
	}

	[Fact]
	public void Validate_RejectsAssignmentWithoutAuthoritativeOperation()
	{
		// Arrange
		var state = EmptyState();
		Classify(state, ManualAssignment(state, Guid.NewGuid()));
		FluentActions.Invoking(() => PortableTaxonomyArchive.Validate(state)).Should().NotThrow();
		var unauthorized = EmptyState();
		Classify(unauthorized, ManualAssignment(unauthorized, Guid.NewGuid()), authorize: false);

		// Act
		Action validate = () => PortableTaxonomyArchive.Validate(unauthorized);

		// Assert
		validate.Should().Throw<InvalidDataException>().WithMessage("Assignment lacks its authoritative operation.");
	}

	[Fact]
	public void Validate_RejectsCurrentClassificationThatIsNotTheHistoryTip()
	{
		// Arrange
		var state = EmptyState();
		Guid itemId = Guid.NewGuid();
		var first = ManualAssignment(state, itemId);
		var second = ManualAssignment(state, itemId, sequence: 2, previous: first.Id);
		Classify(state, first);
		Classify(state, second);
		FluentActions.Invoking(() => PortableTaxonomyArchive.Validate(state)).Should().NotThrow();

		// Act
		state.Current.Single().AssignmentId = first.Id;

		// Assert
		FluentActions.Invoking(() => PortableTaxonomyArchive.Validate(state)).Should().Throw<InvalidDataException>()
			.WithMessage("Current classification does not match history.");
	}

	[Fact]
	public void Validate_RejectsHistoryWithoutCurrentClassification()
	{
		// Arrange
		var state = EmptyState();
		Classify(state, ManualAssignment(state, Guid.NewGuid()));

		// Act
		state.Current.Clear();

		// Assert
		FluentActions.Invoking(() => PortableTaxonomyArchive.Validate(state)).Should().Throw<InvalidDataException>()
			.WithMessage("Current classification does not match history.");
	}

	[Fact]
	public void Validate_RejectsAssignmentToCategoryOutsideItsRevisionSnapshot()
	{
		// Arrange
		var state = EmptyState();
		Guid identity = Guid.NewGuid();
		var revision = new TaxonomyRevisionEntity
		{
			Id = Guid.NewGuid(),
			Sequence = 1,
			PreviousRevisionId = state.Revisions[0].Id,
			Actor = "admin",
			Reason = "create",
			OperationId = Guid.NewGuid(),
			RecordedAt = state.Revisions[0].RecordedAt.AddSeconds(1),
			AffectedCount = 1
		};
		state.Identities.Add(new() { Id = identity });
		state.Revisions.Add(revision);
		state.Heads[0].RevisionId = revision.Id;
		state.Nodes.Add(new() { RevisionId = revision.Id, CategoryId = identity, Name = "Food" });
		state.Operations.Add(new()
		{
			Actor = revision.Actor,
			OperationId = revision.OperationId,
			RequestHash = ClassificationHash,
			RevisionId = revision.Id,
			Reason = revision.Reason,
			RecordedAt = revision.RecordedAt,
			AffectedCount = 1
		});
		var assignment = ManualAssignment(state, Guid.NewGuid(), categoryId: identity);
		Classify(state, assignment);
		FluentActions.Invoking(() => PortableTaxonomyArchive.Validate(state)).Should().NotThrow();

		// Act: the category exists in revision 1 but not in the genesis snapshot the row names.
		assignment.TaxonomyRevisionId = state.Revisions[0].Id;

		// Assert
		FluentActions.Invoking(() => PortableTaxonomyArchive.Validate(state)).Should().Throw<InvalidDataException>()
			.WithMessage("Invalid classification history.");
		assignment.TaxonomyRevisionId = revision.Id;
		assignment.CategoryId = Guid.NewGuid();
		FluentActions.Invoking(() => PortableTaxonomyArchive.Validate(state)).Should().Throw<InvalidDataException>()
			.WithMessage("Invalid classification history.");
	}

	[Fact]
	public void Validate_RejectsMigrationOutcomeThatDiffersFromPlan()
	{
		// Arrange
		var state = EmptyState();
		DateTimeOffset stored = state.Revisions[0].RecordedAt.AddSeconds(1);
		LegacyItemEvidence evidence = new(Guid.NewGuid(), Guid.NewGuid(), null, null, new(), new(), 3m, "USD");
		var plan = LegacyTaxonomyMigrationPlanner.Plan(new("dataset", Guid.NewGuid(),
			LegacyTaxonomyMigrationService.InitialOperationKind, "actor", stored, [], [], [evidence]));
		plan.CanCommit.Should().BeTrue();
		var item = plan.Items.Should().ContainSingle().Subject;
		state.Revisions.Add(new()
		{
			Id = plan.RevisionId,
			Sequence = 1,
			PreviousRevisionId = state.Revisions[0].Id,
			Actor = plan.Actor,
			OperationId = plan.OperationId,
			RecordedAt = stored,
			Reason = "observed",
			AffectedCount = plan.Nodes.Count
		});
		state.Heads[0].RevisionId = plan.RevisionId;
		state.Migrations.Add(new()
		{
			Actor = plan.Actor,
			OperationId = plan.OperationId,
			OperationKind = plan.OperationKind,
			SourceDataset = plan.SourceDataset,
			RequestHash = plan.RequestHash,
			RevisionId = plan.RevisionId,
			ObservedAt = stored,
			PlanJson = JsonSerializer.Serialize(plan)
		});
		var outcome = new LegacyMigrationItemEntity
		{
			Actor = plan.Actor,
			OperationId = plan.OperationId,
			ReceiptItemId = evidence.Id,
			AssignmentId = item.AssignmentId,
			Resolution = item.Kind.ToString(),
			EvidenceJson = JsonSerializer.Serialize(item)
		};
		state.MigrationItems.Add(outcome);
		Classify(state, new()
		{
			Id = item.AssignmentId,
			ReceiptItemId = evidence.Id,
			Sequence = 1,
			CategoryId = item.CategoryId,
			TaxonomyRevisionId = plan.RevisionId,
			RecordedAt = stored,
			Actor = plan.Actor,
			OperationId = plan.OperationId,
			Source = "legacy_import",
			Reason = item.Exception ?? item.Kind.ToString()
		}, authorize: false);
		FluentActions.Invoking(() => PortableTaxonomyArchive.Validate(state)).Should().NotThrow("migration evidence authorizes its own assignment");

		// Act
		outcome.Resolution = item.Kind == LegacyResolutionKind.Unresolved ? nameof(LegacyResolutionKind.Unclassified) : nameof(LegacyResolutionKind.Unresolved);

		// Assert
		FluentActions.Invoking(() => PortableTaxonomyArchive.Validate(state)).Should().Throw<InvalidDataException>()
			.WithMessage("Migration outcome differs from plan.");
		outcome.Resolution = item.Kind.ToString();
		outcome.ReceiptItemId = Guid.NewGuid();
		FluentActions.Invoking(() => PortableTaxonomyArchive.Validate(state)).Should().Throw<InvalidDataException>()
			.WithMessage("Migration outcome differs from plan.");
	}

	[Fact]
	public void DestinationPreflight_RejectsArchiveMissingALaterDestinationClassification()
	{
		// Arrange
		var existing = EmptyState();
		var incoming = EmptyState();
		Guid itemId = Guid.NewGuid();
		var first = ManualAssignment(existing, itemId);
		var later = ManualAssignment(existing, itemId, sequence: 2, previous: first.Id);
		Classify(existing, first);
		Classify(existing, later);
		Classify(incoming, first);
		PortableTaxonomyArchive.ValidateDestination(incoming, incoming);

		// Act
		Action restore = () => PortableTaxonomyArchive.ValidateDestination(existing, incoming);

		// Assert: immutable-prefix validation rejects this before the per-item overwrite check runs.
		restore.Should().Throw<InvalidDataException>().WithMessage("Restore conflicts with immutable destination history.");
		Classify(incoming, later);
		PortableTaxonomyArchive.ValidateDestination(existing, incoming);
	}
}
