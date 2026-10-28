using Application.Normalization;
using Application.Taxonomy;
using FluentAssertions;

namespace Application.Tests.Taxonomy;

public sealed class MappingAuthorityJournalPolicyTests
{
	private static readonly NormalizationSourceKey Source = new("Store", "Milk", null);
	private static readonly DateTimeOffset SameTime = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
	private readonly CategoryMappingRevision approval = Revision(Guid.NewGuid(), null, Guid.NewGuid());
	private readonly Guid baselineId = Guid.NewGuid();
	private readonly Guid streamId = Guid.NewGuid();

	[Fact]
	public void ApprovalCaptureRevocationPreservesTheEarlierCaptureEvenWithEqualTimestamps()
	{
		CategoryMappingRevision revoke = Revision(Guid.NewGuid(), approval.Id, null);
		MappingAuthorityEvent[] events = Events(revoke);
		MappingAuthorityReplay actual = Replay(events, [approval, revoke]);

		actual.Error.Should().BeNull();
		actual.Captures.Should().ContainSingle().Which.MappingRevisionId.Should().Be(approval.Id);
	}

	[Fact]
	public void RevocationBeforeCaptureRejectsOldApproval()
	{
		CategoryMappingRevision revoke = Revision(Guid.NewGuid(), approval.Id, null);
		MappingAuthorityEvent[] events = Events(revoke);
		events[1] = events[1] with { Kind = MappingAuthorityEventKind.Transition, MappingRevisionId = revoke.Id, CaptureId = null };
		events[2] = events[2] with { Kind = MappingAuthorityEventKind.Capture, MappingRevisionId = approval.Id, CaptureId = Guid.NewGuid() };

		Replay(events, [approval, revoke]).Error.Should().Be("capture-head-mismatch");
	}

	[Fact]
	public void TransitionMustExtendTheReplayedHead()
	{
		CategoryMappingRevision revoke = Revision(Guid.NewGuid(), Guid.NewGuid(), null);
		Replay(Events(revoke), [approval, revoke]).Error.Should().Be("mapping-predecessor-mismatch");
	}

	[Theory]
	[InlineData("gap")]
	[InlineData("fork")]
	[InlineData("reused-id")]
	[InlineData("truncated")]
	public void GapsForksAndMissingSuffixFailClosed(string mutation)
	{
		CategoryMappingRevision revoke = Revision(Guid.NewGuid(), approval.Id, null);
		MappingAuthorityEvent[] events = Events(revoke);
		MappingAuthorityCheckpoint tail = new(3, events[2].Id);
		events = mutation switch
		{
			"gap" => [events[0], events[1] with { Sequence = 3 }, events[2]],
			"fork" => [events[0], events[1] with { PreviousEventId = baselineId }, events[2]],
			"reused-id" => [events[0], events[1] with { Id = events[0].Id }, events[2]],
			_ => events[..2]
		};

		MappingAuthorityReplay actual = Replay(events, [approval, revoke], tail: tail);
		actual.Error.Should().NotBeNull();
		actual.Captures.Should().BeEmpty();
	}

	[Theory]
	[InlineData("version")]
	[InlineData("kind")]
	[InlineData("revision")]
	[InlineData("source")]
	[InlineData("fingerprint")]
	[InlineData("capture-id")]
	public void UnknownOrMissingEvidenceFailsClosed(string mutation)
	{
		CategoryMappingRevision revoke = Revision(Guid.NewGuid(), approval.Id, null);
		MappingAuthorityEvent[] events = Events(revoke);
		events[1] = mutation switch
		{
			"version" => events[1] with { Version = 99 },
			"kind" => events[1] with { Kind = (MappingAuthorityEventKind)99 },
			"revision" => events[1] with { MappingRevisionId = Guid.NewGuid() },
			"source" => events[1] with { Source = Source with { ItemCode = "other" } },
			"capture-id" => events[1] with { CaptureId = null },
			_ => events[1]
		};
		CategoryMappingRevision evidence = mutation == "fingerprint" ? approval with { SourceFingerprint = "untrusted" } : approval;

		MappingAuthorityReplay actual = Replay(events, [evidence, revoke]);
		actual.Error.Should().NotBeNull();
		actual.Captures.Should().BeEmpty();
	}

	[Fact]
	public void TrustedSealedBaselineCanStartWithAnExistingHead()
	{
		MappingAuthorityBaseline baseline = Baseline([new(Source, approval.Id)]);
		MappingAuthorityEvent capture = new(1, 1, Guid.NewGuid(), baselineId, MappingAuthorityEventKind.Capture,
			Source, approval.Id, Guid.NewGuid());

		Replay([capture], [approval], baseline).Error.Should().BeNull();
	}

	[Fact]
	public void ModifiedOrMissingSealCannotAuthenticateBaseline()
	{
		MappingAuthorityBaseline original = Baseline([]);
		MappingAuthoritySeal seal = new(streamId, baselineId, MappingAuthorityJournalPolicyV1.BaselineHash(original));
		MappingAuthorityBaseline changed = original with { Heads = [new(Source, approval.Id)] };

		MappingAuthorityJournalPolicyV1.Replay(seal, changed, [approval], [], new(0, baselineId)).Error.Should().Be("baseline-seal-mismatch");
		MappingAuthorityJournalPolicyV1.Replay(null, original, [], [], new(0, baselineId)).Error.Should().Be("missing-evidence");
	}

	[Fact]
	public void BaselineDoesNotAuthorizeAnUnjournaledHistoricalCapture()
	{
		MappingAuthorityReplay actual = Replay([], [approval], Baseline([new(Source, approval.Id)]));
		actual.Error.Should().BeNull();
		actual.Captures.Should().BeEmpty();
	}

	[Fact]
	public void CaptureReferencingTheRevocationHeadIsAlsoRejected()
	{
		CategoryMappingRevision revoke = Revision(Guid.NewGuid(), approval.Id, null);
		MappingAuthorityBaseline baseline = Baseline([new(Source, revoke.Id)]);
		MappingAuthorityEvent capture = new(1, 1, Guid.NewGuid(), baselineId, MappingAuthorityEventKind.Capture,
			Source, revoke.Id, Guid.NewGuid());

		Replay([capture], [revoke], baseline).Error.Should().Be("capture-head-mismatch");
	}

	[Fact]
	public void AnotherSourcesHeadCannotAuthorizeCapture()
	{
		NormalizationSourceKey other = Source with { Description = "milk" };
		CategoryMappingRevision otherApproval = approval with { Source = other, SourceFingerprint = NormalizationCatalogPolicy.Hash(other) };
		MappingAuthorityBaseline baseline = Baseline([new(other, approval.Id)]);
		MappingAuthorityEvent capture = new(1, 1, Guid.NewGuid(), baselineId, MappingAuthorityEventKind.Capture,
			Source, approval.Id, Guid.NewGuid());

		Replay([capture], [otherApproval], baseline).Error.Should().Be("missing-exact-mapping-evidence");
	}

	[Fact]
	public void ReapprovalInvalidatesTheEarlierHeadEvenForTheSameCategory()
	{
		CategoryMappingRevision renewed = Revision(Guid.NewGuid(), approval.Id, approval.CategoryId);
		MappingAuthorityEvent[] events = Events(renewed);
		events[1] = events[1] with { Kind = MappingAuthorityEventKind.Transition, MappingRevisionId = renewed.Id, CaptureId = null };
		events[2] = events[2] with { Kind = MappingAuthorityEventKind.Capture, MappingRevisionId = approval.Id, CaptureId = Guid.NewGuid() };

		Replay(events, [approval, renewed]).Error.Should().Be("capture-head-mismatch");
	}

	[Fact]
	public void DuplicateCaptureIdentityDoesNotProduceTwoClaims()
	{
		MappingAuthorityBaseline baseline = Baseline([new(Source, approval.Id)]);
		MappingAuthorityEvent first = new(1, 1, Guid.NewGuid(), baselineId, MappingAuthorityEventKind.Capture,
			Source, approval.Id, Guid.NewGuid());
		MappingAuthorityEvent second = first with { Sequence = 2, Id = Guid.NewGuid(), PreviousEventId = first.Id };

		MappingAuthorityReplay actual = Replay([first, second], [approval], baseline);
		actual.Error.Should().Be("invalid-capture");
		actual.Captures.Should().BeEmpty();
	}

	[Fact]
	public void DuplicateBaselineHeadsAndMissingRevisionEvidenceFailClosed()
	{
		MappingAuthorityBaseline duplicate = Baseline([new(Source, approval.Id), new(Source, approval.Id)]);
		Replay([], [approval], duplicate).Error.Should().Be("invalid-baseline-head");
		Replay([], [], Baseline([new(Source, approval.Id)])).Error.Should().Be("invalid-baseline-head");
	}

	[Fact]
	public void UnknownBaselineVersionAndMissingTailFailClosed()
	{
		MappingAuthorityBaseline baseline = Baseline([]) with { Version = 2 };
		Replay([], [], baseline).Error.Should().Be("unknown-version");
		MappingAuthoritySeal seal = new(streamId, baselineId, "untrusted");
		MappingAuthorityJournalPolicyV1.Replay(seal, Baseline([]), [], [], null).Error.Should().Be("missing-evidence");
	}

	private MappingAuthorityBaseline Baseline(IReadOnlyList<MappingAuthorityHead> heads) => new(1, streamId, baselineId, heads);

	private MappingAuthorityReplay Replay(MappingAuthorityEvent[] events, CategoryMappingRevision[] revisions,
		MappingAuthorityBaseline? baseline = null, MappingAuthorityCheckpoint? tail = null)
	{
		baseline ??= Baseline([]);
		MappingAuthoritySeal seal = new(streamId, baselineId, MappingAuthorityJournalPolicyV1.BaselineHash(baseline));
		return MappingAuthorityJournalPolicyV1.Replay(seal, baseline, revisions, events,
			tail ?? new(events.Length, events.LastOrDefault()?.Id ?? baselineId));
	}

	private MappingAuthorityEvent[] Events(CategoryMappingRevision revoke)
	{
		Guid first = Guid.NewGuid();
		Guid second = Guid.NewGuid();
		return [new(1, 1, first, baselineId, MappingAuthorityEventKind.Transition, Source, approval.Id, null),
			new(1, 2, second, first, MappingAuthorityEventKind.Capture, Source, approval.Id, Guid.NewGuid()),
			new(1, 3, Guid.NewGuid(), second, MappingAuthorityEventKind.Transition, Source, revoke.Id, null)];
	}

	private static CategoryMappingRevision Revision(Guid id, Guid? predecessor, Guid? category) => new(id, predecessor,
		Source, NormalizationCatalogPolicy.Hash(Source), category, Guid.NewGuid(), category == null ? null : "semantics",
		"actor", Guid.NewGuid(), "reviewed", SameTime);
}
