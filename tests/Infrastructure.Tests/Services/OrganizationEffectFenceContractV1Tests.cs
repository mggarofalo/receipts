using Application.Classification;
using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Services;
using static Infrastructure.Services.OrganizationEffectContractV1;
using static Infrastructure.Services.OrganizationEffectFenceContractV1;

namespace Infrastructure.Tests.Services;

public sealed class OrganizationEffectFenceContractV1Tests
{
	[Fact]
	public void MatchingSuppliedFactsAreOnlyASemanticMatch() => new Fixture().Check().Should().BeNull();

	[Fact]
	public void DisableAndReenableCannotReviveCapturedApply()
	{
		Fixture f = new();
		SettingsState disabled = f.Policy with { Settings = f.Policy.Settings with { Enabled = false, Revision = Guid.NewGuid() } };
		f.Check(f.Current with { Policy = disabled }).Should().Be("disabled");
		SettingsState reenabled = disabled with { Settings = disabled.Settings with { Enabled = true, Revision = Guid.NewGuid() } };
		f.Check(f.Current with { Policy = reenabled }).Should().Be("settings-revision-changed");
	}

	[Fact]
	public void RestoreGenerationFencesApplyEvenWithIdenticalBusinessRows()
	{
		Fixture f = new();
		f.Check(f.Current with { Policy = f.Policy with { RestoreGeneration = Guid.NewGuid() } })
			.Should().Be("restore-generation-changed");
	}

	[Fact]
	public void ChangedPolicyMappingAndSourceRevisionsReject()
	{
		Fixture f = new();
		f.Check(f.Current with { Policy = f.Policy with { Settings = f.Policy.Settings with { Revision = Guid.NewGuid() } } })
			.Should().Be("settings-revision-changed");
		f.Check(f.Current with { MappingRevisionId = Guid.NewGuid() }).Should().Be("mapping-revision-changed");
		f.Check(f.Current with { Source = f.Item.Source with { ReceiptRevision = Guid.NewGuid() } }).Should().Be("source-changed");
		// The text was changed and reverted; its revision must still fence the old capture.
		f.Check(f.Current with { Source = f.Item.Source with { ItemRevision = Guid.NewGuid() } }).Should().Be("source-changed");
		f.Check(f.Current with { Source = f.Item.Source with { ItemCode = "" } }).Should().Be("source-changed");
	}

	[Fact]
	public void ManualNullIsProtectedEvenWhenExpectedIdentityMatches()
	{
		Fixture f = new();
		PurchaseClassification manual = Assignment(f.Item.Source.ItemId, "manual");
		ItemRequest item = f.Item with { ExpectedAssignmentId = manual.Id };
		ManifestRequest manifest = f.Manifest with { Members = [item] };
		CaptureEvidence capture = f.Capture with { ManifestRequestHash = ManifestRequestHash(manifest) };
		PhaseCommand phase = f.Phase with { ItemRequestHash = RequestHash(item), ExpectedAssignmentId = manual.Id };
		Mismatch(capture, Evidence(capture, item, phase), manifest, item, phase, f.Policy,
			f.Current with { Assignment = manual }).Should().Be("purchase-protected");
	}

	[Fact]
	public void OwnedUndoDoesNotRequireEnabledProviderOrCurrentMappingButStillFencesRestore()
	{
		Fixture f = new();
		PurchaseClassification applied = Assignment(f.Item.Source.ItemId, "automation");
		PhaseCommand undo = new(1, "undo", RequestHash(f.Item), f.Item.UndoOperationId, applied.Id,
			new(null, f.Item.Target.TaxonomyRevisionId, []), f.Item.CapturedAt);
		CurrentState current = f.Current with
		{
			Assignment = applied,
			MappingRevisionId = Guid.NewGuid(),
			Policy = f.Policy with { Settings = new(false, null, null, 100, 3, Guid.NewGuid()) }
		};
		ExecutionEvidence evidence = Evidence(f.Capture, f.Item, undo);
		Mismatch(f.Capture, evidence, f.Manifest, f.Item, undo, f.Policy, current).Should().BeNull();
		Mismatch(f.Capture, evidence, f.Manifest, f.Item, undo, f.Policy,
			current with { Policy = current.Policy with { RestoreGeneration = Guid.NewGuid() } }).Should().Be("restore-generation-changed");
		Mismatch(f.Capture, evidence, f.Manifest, f.Item, undo, f.Policy,
			current with { Assignment = applied with { Id = Guid.NewGuid() } }).Should().Be("assignment-changed");
	}

	[Fact]
	public void CanonicalHashesBindEveryGenerationAndPhaseWithSeparateDomains()
	{
		Fixture f = new();
		string hash = CaptureHash(f.Capture);
		CaptureHash(f.Capture with { SettingsRevision = Guid.NewGuid() }).Should().NotBe(hash);
		CaptureHash(f.Capture with { RestoreGeneration = Guid.NewGuid() }).Should().NotBe(hash);
		CaptureHash(f.Capture with { ManifestRequestHash = new string('B', 64) }).Should().NotBe(hash);
		ExecutionHash(Evidence(f.Capture, f.Item, f.Phase)).Should().NotBe(hash);
		FluentActions.Invoking(() => CaptureHash(f.Capture with { Version = 2 })).Should().Throw<InvalidDataException>();
		ExecutionEvidence forged = Evidence(f.Capture, f.Item, f.Phase) with { PhaseCommandHash = new string('B', 64) };
		Mismatch(f.Capture, forged, f.Manifest, f.Item, f.Phase, f.Policy, f.Current).Should().Be("evidence-binding-changed");
	}

	private static PurchaseClassification Assignment(Guid item, string source) =>
		new(Guid.NewGuid(), item, 1, null, null, Guid.NewGuid(), DateTimeOffset.UtcNow,
			"synthetic-actor", source, "synthetic", Guid.NewGuid(), []);
	private static ExecutionEvidence Evidence(CaptureEvidence capture, ItemRequest item, PhaseCommand phase) =>
		new(1, CaptureHash(capture), RequestHash(item), PhaseHash(phase));

	private sealed class Fixture
	{
		internal SettingsState Policy { get; } = new(new(true, "jev", "jev-1.14.0", 100, 3, Guid.NewGuid()), Guid.NewGuid());
		internal ItemRequest Item { get; }
		internal ManifestRequest Manifest { get; }
		internal CaptureEvidence Capture { get; }
		internal PhaseCommand Phase { get; }
		internal CurrentState Current { get; }
		internal Fixture()
		{
			Guid category = Guid.NewGuid();
			Item = new(1, Guid.NewGuid(), "synthetic-actor", "candidate-policy", DateTimeOffset.Parse("2026-10-28T12:00:00.000000Z"),
				new(Guid.NewGuid(), Guid.NewGuid(), "Store", "Item", null, Guid.NewGuid(), Guid.NewGuid()), null,
				new(category, Guid.NewGuid(), [category]), Guid.NewGuid(), Guid.NewGuid(),
				new(1, "approved-exact-mapping", Guid.NewGuid(), Guid.NewGuid(), new string('A', 64)));
			Manifest = new(1, Item.ManifestId, Item.Actor, Item.PolicyVersion, Item.Target.TaxonomyRevisionId, Item.CapturedAt, [Item]);
			Capture = new(1, Item.ManifestId, ManifestRequestHash(Manifest), Policy.Settings.Revision, Policy.RestoreGeneration);
			Phase = new(1, "apply", RequestHash(Item), Item.ApplyOperationId, null, Item.Target, Item.CapturedAt);
			Current = new(Policy, Item.Source, null, Item.Target.TaxonomyRevisionId, Item.Decision.MappingRevisionId, true, false, false, true);
		}
		internal string? Check(CurrentState? current = null) => Mismatch(Capture, Evidence(Capture, Item, Phase), Manifest, Item, Phase, Policy, current ?? Current);
	}
}
