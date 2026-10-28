namespace Infrastructure.Services;

internal static partial class PortableTaxonomyArchive
{
	// Candidate only; the v13 restore path does not call this method. Ordinary destination
	// preflight must validate history independently, under the restore coordination locks.
	// Null incoming effects means an older format, not permission to discard evidence.
	internal static EffectEvidence ValidateEffectDestination(State existingHistory, EffectEvidence existing,
		State incomingHistory, EffectEvidence? incoming)
	{
		ValidateEffectEvidence(existingHistory, existing);
		if (incoming != null) { ValidateEffectEvidence(incomingHistory, incoming); }
		incoming ??= new([], [], []);
		var incomingManifests = incoming.Manifests.ToDictionary(m => m.Manifest.Id);
		var incomingOutcomes = incoming.Outcomes.ToDictionary(o => (o.ManifestId, o.ItemId, o.Phase));
		foreach (var manifest in existing.Manifests)
		{
			if (!incomingManifests.TryGetValue(manifest.Manifest.Id, out var archived)) { continue; }
			// Both sides have already verified their exact membership digest. Comparing the
			// envelope preserves all frozen intent, while accepting jsonb decision rendering.
			Require(Equal(manifest, archived), "Restore conflicts with immutable effect manifest or membership.");
			foreach (var outcome in existing.Outcomes.Where(o => o.ManifestId == manifest.Manifest.Id))
			{
				Require(incomingOutcomes.TryGetValue((outcome.ManifestId, outcome.ItemId, outcome.Phase), out var restored)
					&& Equal(outcome, restored), "Restore would replace or discard a terminal effect outcome.");
			}
		}

		// Match historical archive retention: a manifest omitted entirely stays local. A
		// shared manifest must contain all retained outcomes and may append later results.
		var omittedIds = existing.Manifests.Where(m => !incomingManifests.ContainsKey(m.Manifest.Id))
			.Select(m => m.Manifest.Id).ToHashSet();
		EffectEvidence retained = new(
			[.. incoming.Manifests, .. existing.Manifests.Where(m => omittedIds.Contains(m.Manifest.Id))],
			[.. incoming.Items, .. existing.Items.Where(i => omittedIds.Contains(i.ManifestId))],
			[.. incoming.Outcomes, .. existing.Outcomes.Where(o => omittedIds.Contains(o.ManifestId))]);
		// Incoming assignments must close over retained effects, including older formats.
		// Check reservations against both historical sets: either side can omit history
		// which the ordinary restore retains. Repeated IDs for identical history are harmless
		// here; ordinary historical validation owns duplicate/intent/progress validation.
		ValidateEffectEvidence(incomingHistory with
		{
			HistoricalItems = [.. existingHistory.HistoricalItems, .. incomingHistory.HistoricalItems]
		}, retained);
		return retained;
	}
}
