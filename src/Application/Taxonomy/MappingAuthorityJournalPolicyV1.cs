using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Normalization;

namespace Application.Taxonomy;

public enum MappingAuthorityEventKind { Transition = 1, Capture = 2 }
public sealed record MappingAuthorityHead(NormalizationSourceKey Source, Guid RevisionId);
public sealed record MappingAuthorityBaseline(int Version, Guid StreamId, Guid Id, IReadOnlyList<MappingAuthorityHead> Heads);
// Supplied by an independent trusted store, never accepted from an untrusted journal itself.
public sealed record MappingAuthoritySeal(Guid StreamId, Guid BaselineId, string BaselineHash);
public sealed record MappingAuthorityCheckpoint(long Sequence, Guid EventId);
public sealed record MappingAuthorityEvent(int Version, long Sequence, Guid Id, Guid PreviousEventId,
	MappingAuthorityEventKind Kind, NormalizationSourceKey Source, Guid MappingRevisionId, Guid? CaptureId);
public sealed record MappingAuthorityCapture(Guid CaptureId, long Sequence, NormalizationSourceKey Source, Guid MappingRevisionId);
public sealed record MappingAuthorityReplay(string? Error, IReadOnlyList<MappingAuthorityCapture> Captures);

/// <summary>
/// Inactive v1 ordering policy. A trusted sealed baseline anchors future events only. This policy cannot
/// authenticate its seal, prove transactional persistence, or grant assignment authority. Revision evidence
/// must separately pass ordinary mapping history/command/taxonomy validation. Hashes are not signatures.
/// </summary>
public static class MappingAuthorityJournalPolicyV1
{
	public const int Version = 1;

	// Property and list ordering here define this candidate v1 baseline digest, not an archive format.
	public static string BaselineHash(MappingAuthorityBaseline baseline)
	{
		byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
		{
			baseline.Version,
			baseline.StreamId,
			baseline.Id,
			Heads = baseline.Heads.OrderBy(head => head.Source.Location, StringComparer.Ordinal)
				.ThenBy(head => head.Source.Description, StringComparer.Ordinal)
				.ThenBy(head => head.Source.ItemCode, StringComparer.Ordinal)
				.Select(head => new { head.Source, head.RevisionId })
		});
		byte[] domain = Encoding.UTF8.GetBytes("receipts.mapping-authority.baseline.v1\0");
		return Convert.ToHexString(SHA256.HashData([.. domain, .. payload]));
	}

	/// <summary>
	/// Replays the complete ordered prefix through an independently trusted terminal checkpoint. Events are
	/// never sorted by wall clock. Failure discards every intermediate capture. No pre-baseline capture can
	/// be inferred from a baseline head. A matching capture proves head ordering only, not category eligibility.
	/// </summary>
	public static MappingAuthorityReplay Replay(MappingAuthoritySeal? seal, MappingAuthorityBaseline? baseline,
		IReadOnlyList<CategoryMappingRevision>? revisions, IReadOnlyList<MappingAuthorityEvent>? events,
		MappingAuthorityCheckpoint? expectedTail)
	{
		static MappingAuthorityReplay Fail(string error) => new(error, []);
		if (seal == null || baseline?.Heads == null || revisions == null || events == null || expectedTail == null)
		{ return Fail("missing-evidence"); }
		if (baseline.Version != Version) { return Fail("unknown-version"); }
		if (baseline.Id == Guid.Empty || baseline.StreamId == Guid.Empty || expectedTail.Sequence < 0 || expectedTail.EventId == Guid.Empty)
		{ return Fail("invalid-identity"); }
		Dictionary<Guid, CategoryMappingRevision> byId = [];
		foreach (CategoryMappingRevision revision in revisions)
		{
			if (revision == null || revision.Id == Guid.Empty || revision.PreviousRevisionId == Guid.Empty
				|| revision.TaxonomyRevisionId == Guid.Empty || revision.CategoryId == Guid.Empty
				|| !ValidSource(revision.Source) || revision.SourceFingerprint != NormalizationCatalogPolicy.Hash(revision.Source)
				|| (revision.CategoryId == null ? revision.SemanticFingerprint != null : string.IsNullOrWhiteSpace(revision.SemanticFingerprint))
				|| !byId.TryAdd(revision.Id, revision))
			{ return Fail("invalid-revision-evidence"); }
		}
		Dictionary<NormalizationSourceKey, CategoryMappingRevision> heads = [];
		HashSet<Guid> usedRevisions = [];
		foreach (MappingAuthorityHead head in baseline.Heads)
		{
			if (head == null || !ValidSource(head.Source) || !byId.TryGetValue(head.RevisionId, out CategoryMappingRevision? revision)
				|| revision.Source != head.Source || !heads.TryAdd(head.Source, revision) || !usedRevisions.Add(revision.Id))
			{ return Fail("invalid-baseline-head"); }
		}
		if (seal.StreamId != baseline.StreamId || seal.BaselineId != baseline.Id || seal.BaselineHash != BaselineHash(baseline))
		{ return Fail("baseline-seal-mismatch"); }

		long sequence = 0;
		Guid previous = baseline.Id;
		HashSet<Guid> eventIds = [baseline.Id];
		HashSet<Guid> captureIds = [];
		List<MappingAuthorityCapture> captures = [];
		foreach (MappingAuthorityEvent entry in events)
		{
			if (entry == null) { return Fail("missing-evidence"); }
			if (entry.Version != Version) { return Fail("unknown-version"); }
			if (sequence == long.MaxValue || entry.Sequence != sequence + 1 || entry.PreviousEventId != previous
				|| entry.Id == Guid.Empty || !eventIds.Add(entry.Id))
			{ return Fail("journal-order-mismatch"); }
			if (!ValidSource(entry.Source) || !byId.TryGetValue(entry.MappingRevisionId, out CategoryMappingRevision? revision)
				|| revision.Source != entry.Source)
			{ return Fail("missing-exact-mapping-evidence"); }
			heads.TryGetValue(entry.Source, out CategoryMappingRevision? current);
			switch (entry.Kind)
			{
				case MappingAuthorityEventKind.Transition:
					if (entry.CaptureId != null || !usedRevisions.Add(revision.Id)) { return Fail("invalid-transition"); }
					if (revision.PreviousRevisionId != current?.Id || (current == null && revision.CategoryId == null))
					{ return Fail("mapping-predecessor-mismatch"); }
					heads[entry.Source] = revision;
					break;
				case MappingAuthorityEventKind.Capture:
					if (entry.CaptureId is not Guid captureId || captureId == Guid.Empty || !captureIds.Add(captureId))
					{ return Fail("invalid-capture"); }
					if (current?.Id != revision.Id || current.CategoryId == null) { return Fail("capture-head-mismatch"); }
					captures.Add(new(captureId, entry.Sequence, entry.Source, revision.Id));
					break;
				default:
					return Fail("unknown-event-kind");
			}
			sequence = entry.Sequence;
			previous = entry.Id;
		}
		return sequence != expectedTail.Sequence || previous != expectedTail.EventId
			? Fail("terminal-checkpoint-mismatch") : new(null, captures.AsReadOnly());
	}

	private static bool ValidSource(NormalizationSourceKey? source)
	{
		if (source?.Location == null || source.Description == null || source.Location.Contains('\0')
			|| source.Description.Contains('\0') || source.ItemCode?.Contains('\0') == true) { return false; }
		try { return source == NormalizationCatalogPolicy.Normalize(source); }
		catch (NormalizationCatalogException) { return false; }
	}
}
