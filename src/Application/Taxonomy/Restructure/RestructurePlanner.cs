using System.Text.Json;
using Application.Envelopes;
using Domain.Taxonomy;

namespace Application.Taxonomy.Restructure;

/// <summary>
/// Validates a restructure plan against one deployment and decides every change it makes. Pure: the dry
/// run and apply both call it on the same captured state, so what the dry run reports is what apply does.
/// </summary>
public static class RestructurePlanner
{
	public const int MaximumRuleLength = 64;
	public const int MaximumEvidenceLength = 2000;
	public const int MaximumOccasionNameLength = 200;
	public const int MaximumReasonLength = 4000;
	public const int MaximumTreeDepth = 2;

	/// <summary>Assignments these sources made after the plan's snapshot are someone's later decision.</summary>
	public static readonly IReadOnlySet<string> ProtectedSources = new HashSet<string>(StringComparer.Ordinal)
		{ "manual", "jev", "approved_mapping" };

	public static RestructureOutcome Plan(RestructurePlan plan, RestructureState state)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentNullException.ThrowIfNull(state);
		List<string> errors = [];
		List<string> warnings = [];
		ValidateHeader(plan, errors);
		if (errors.Count > 0) { return Invalid(errors, state); }

		if (state.BaseNodes is null)
		{ errors.Add("The plan's base category revision does not exist here. Generate the plan from a backup of this deployment."); }
		else if (!Unchanged(state.BaseNodes, state.CurrentNodes))
		{ errors.Add("Categories changed since the plan was generated. Generate a new plan from a current backup."); }
		if (errors.Count > 0) { return Invalid(errors, state); }

		IReadOnlyList<TaxonomyNode> result;
		try { result = TaxonomyTree.Apply(state.CurrentNodes, Edits(plan.TaxonomyChanges, state.CurrentNodes)); }
		catch (TaxonomyValidationException error) { return Invalid([$"Category changes are invalid: {error.Message}"], state); }

		Dictionary<Guid, TaxonomyNode> before = state.CurrentNodes.ToDictionary(node => node.Id);
		Dictionary<Guid, TaxonomyNode> after = result.ToDictionary(node => node.Id);
		ValidateTree(after, errors);

		HashSet<string> occasionNames = ValidateOccasions(plan.Occasions ?? [], state, errors);
		Dictionary<Guid, RestructureNodeMapping> nodeMappings = ValidateNodeMappings(plan, before, after, occasionNames, errors);
		Dictionary<Guid, RestructureItemReassignment> itemReassignments = ValidateItems(plan, after, occasionNames, errors);
		int onArchived = state.Items.Count(item => itemReassignments.ContainsKey(item.ItemId)
			&& item.CategoryId is Guid category && before.TryGetValue(category, out TaxonomyNode? node) && node.Archived);
		if (onArchived > 0)
		{ errors.Add($"{onArchived} listed purchases are on archived categories, so undo could not put them back. Leave them out of the plan."); }
		List<RestructureResolvedMerge> merges = ValidateMerges(plan, state, errors);
		Dictionary<Guid, Guid?> explicitDefaults = ValidateExplicitDefaults(plan, state, after, errors);
		if (errors.Count > 0) { return Invalid(errors, state); }

		List<RestructureDefaultChange> defaultChanges = PlanDefaults(state, after, nodeMappings, explicitDefaults, merges,
			errors, out RestructureDefaultsReport defaultsReport);
		if (errors.Count > 0) { return Invalid(errors, state); }

		List<RestructureMappingChange> mappingChanges = PlanMappings(state, before, after, nodeMappings, out RestructureMappingsReport mappingsReport);
		List<RestructureItemChange> itemChanges = PlanItems(plan, state, nodeMappings, itemReassignments,
			out RestructureItemReport itemReport);
		if (itemReport.MissingFromDeployment > 0)
		{ warnings.Add($"{itemReport.MissingFromDeployment} listed purchases no longer exist and are skipped."); }
		if (itemReport.Conflicts > 0)
		{ warnings.Add($"{itemReport.Conflicts} purchases were classified after the plan's snapshot and are left as they are."); }

		List<string> toCreate = (plan.Occasions ?? []).Where(name => !state.Occasions.Any(o => o.Name == name)).ToList();
		RestructureReport report = new(
			TaxonomyReport(before, after),
			itemReport,
			Transitions(itemChanges, before, after),
			defaultsReport,
			mappingsReport,
			MergeReports(merges, state),
			BudgetReport(merges, state),
			(plan.Occasions ?? []).Select(name => new RestructureOccasionReport(name, toCreate.Contains(name),
				itemChanges.Count(change => !change.Conflict && change.TargetOccasion == name))).ToList());
		return new(errors, warnings, result, itemChanges, defaultChanges, mappingChanges, merges, toCreate, report);
	}

	/// <summary>
	/// Edits that turn <paramref name="current"/> back into <paramref name="target"/>. Identities cannot be deleted,
	/// so categories that <paramref name="target"/> does not know are archived.
	/// </summary>
	public static IReadOnlyList<TaxonomyEdit> RevertChanges(IReadOnlyList<TaxonomyNode> current, IReadOnlyList<TaxonomyNode> target)
	{
		Dictionary<Guid, TaxonomyNode> now = current.ToDictionary(node => node.Id);
		List<TaxonomyEdit> reshape = [], archive = [], restore = [];
		foreach (TaxonomyNode wanted in target.OrderBy(node => node.Id))
		{
			TaxonomyNode node = now[wanted.Id];
			if (node.ParentId != wanted.ParentId) { reshape.Add(new("move", node.Id, ParentId: wanted.ParentId)); }
			if (node.Name != wanted.Name) { reshape.Add(new("rename", node.Id, Name: wanted.Name)); }
			if (node.Definition != wanted.Definition) { reshape.Add(new("define", node.Id, Definition: wanted.Definition)); }
			if (node.Archived && !wanted.Archived) { restore.Add(new("restore", node.Id)); }
			if (!node.Archived && wanted.Archived) { archive.Add(new("archive", node.Id)); }
		}
		HashSet<Guid> known = target.Select(node => node.Id).ToHashSet();
		archive.AddRange(current.Where(node => !known.Contains(node.Id) && !node.Archived).OrderBy(node => node.Id)
			.Select(node => new TaxonomyEdit("archive", node.Id)));
		// Moves first put kept categories back under their own parents, so archiving a created category cannot
		// cascade onto them; restores come last in case an archive cascade reached a category that must be active.
		return [.. reshape, .. archive, .. restore];
	}

	/// <summary>Names of a category's path in a tree, joined with " / ", or "(none)" for a cleared category.</summary>
	public static string PathOf(IReadOnlyDictionary<Guid, TaxonomyNode> nodes, Guid? categoryId)
	{
		if (categoryId is not Guid id) { return "(none)"; }
		List<string> names = [];
		HashSet<Guid> seen = [];
		for (Guid? cursor = id; cursor is Guid current && seen.Add(current) && nodes.TryGetValue(current, out TaxonomyNode? node); cursor = node.ParentId)
		{ names.Add(node.Name); }
		names.Reverse();
		return names.Count == 0 ? "(unknown)" : string.Join(" / ", names);
	}

	/// <summary>
	/// Whether <paramref name="current"/> still has the content of <paramref name="reference"/>. Revision identity does
	/// not matter, and archived categories the reference lacks are ignored: an undone restructure leaves the categories
	/// it created archived, and otherwise matches its base again.
	/// </summary>
	public static bool Unchanged(IReadOnlyList<TaxonomyNode> reference, IReadOnlyList<TaxonomyNode> current)
	{
		Dictionary<Guid, TaxonomyNode> nodes = current.ToDictionary(node => node.Id);
		HashSet<Guid> known = [.. reference.Select(node => node.Id)];
		return reference.All(node => nodes.TryGetValue(node.Id, out TaxonomyNode? match) && match == node)
			&& nodes.Values.All(node => known.Contains(node.Id) || node.Archived);
	}

	/// <summary>
	/// The plan's category changes as tree edits. Creating a category this deployment already holds archived (left by an
	/// undone restructure) restores it with the planned parent, name and definition, so the plan applies again.
	/// </summary>
	public static IReadOnlyList<TaxonomyEdit> Edits(IReadOnlyList<RestructureCategoryChange> changes, IReadOnlyList<TaxonomyNode> current)
	{
		Dictionary<Guid, TaxonomyNode> archived = current.Where(node => node.Archived).ToDictionary(node => node.Id);
		List<TaxonomyEdit> edits = [];
		foreach (RestructureCategoryChange change in changes)
		{
			if (change.Kind != "create" || !archived.TryGetValue(change.CategoryId, out TaxonomyNode? node))
			{
				edits.Add(change.ToEdit());
				continue;
			}
			edits.Add(new("restore", node.Id));
			if (node.ParentId != change.ParentId) { edits.Add(new("move", node.Id, ParentId: change.ParentId)); }
			if (node.Name != change.Name) { edits.Add(new("rename", node.Id, Name: change.Name)); }
			if (node.Definition != change.Definition) { edits.Add(new("define", node.Id, Definition: change.Definition)); }
		}
		return edits;
	}

	private static RestructureOutcome Invalid(List<string> errors, RestructureState state) => new(errors, [], state.CurrentNodes, [], [], [], [], [],
		new(new(0, 0, 0, 0, 0, 0, 0, 0), new(0, 0, 0, 0, 0, 0, new Dictionary<string, int>()), [], new(0, 0, 0, 0), new(0, 0, 0, 0), [], new(0, 0), []));

	private static void ValidateHeader(RestructurePlan plan, List<string> errors)
	{
		if (plan.Format != RestructurePlan.FormatName) { errors.Add($"Format must be \"{RestructurePlan.FormatName}\"."); }
		if (plan.Version != RestructurePlan.CurrentVersion) { errors.Add($"Version {plan.Version} is not supported; this server reads version {RestructurePlan.CurrentVersion}."); }
		if (plan.PlanId == Guid.Empty) { errors.Add("Plan ID is required."); }
		if (plan.BaseTaxonomyRevisionId == Guid.Empty) { errors.Add("Base category revision is required."); }
		if (plan.SnapshotAt == default) { errors.Add("Snapshot time is required."); }
		if (string.IsNullOrWhiteSpace(plan.Reason) || plan.Reason.Length > MaximumReasonLength || plan.Reason.Contains('\0'))
		{ errors.Add($"Reason is required, at most {MaximumReasonLength} characters and without NUL."); }
		if (plan.TaxonomyChanges is null || plan.NodeMappings is null || plan.TaxonomyChanges.Any(change => change is null)
			|| plan.NodeMappings.Any(mapping => mapping is null) || plan.Items?.Any(item => item is null) == true
			|| plan.Occasions?.Any(name => name is null) == true || plan.EnvelopeDefaults?.Any(row => row is null) == true
			|| plan.EnvelopeMerges?.Any(row => row is null) == true)
		{ errors.Add("Category changes and node mappings are required, and no list may contain null entries."); }
	}

	private static void ValidateTree(Dictionary<Guid, TaxonomyNode> after, List<string> errors)
	{
		List<TaxonomyNode> active = after.Values.Where(node => !node.Archived).ToList();
		List<string> undefined = active.Where(node => string.IsNullOrWhiteSpace(node.Definition))
			.Select(node => PathOf(after, node.Id)).Order(StringComparer.Ordinal).ToList();
		if (undefined.Count > 0)
		{ errors.Add($"Every active category needs a definition (\"includes … / not …\"); {undefined.Count} have none, e.g. {Sample(undefined)}."); }
		List<string> deep = active.Where(node => Depth(after, node) >= MaximumTreeDepth)
			.Select(node => PathOf(after, node.Id)).Order(StringComparer.Ordinal).ToList();
		if (deep.Count > 0)
		{ errors.Add($"Item types may have at most {MaximumTreeDepth} levels, which is what category assistance can ask about; {deep.Count} are deeper, e.g. {Sample(deep)}."); }
	}

	private static int Depth(Dictionary<Guid, TaxonomyNode> nodes, TaxonomyNode node)
	{
		int depth = 0;
		for (TaxonomyNode current = node; current.ParentId is Guid parent && depth <= nodes.Count; current = nodes[parent]) { depth++; }
		return depth;
	}

	private static HashSet<string> ValidateOccasions(IReadOnlyList<string> names, RestructureState state, List<string> errors)
	{
		HashSet<string> valid = new(StringComparer.Ordinal);
		foreach (string name in names)
		{
			if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.Length > MaximumOccasionNameLength || name.Contains('\0'))
			{ errors.Add($"Occasion names must be nonempty, trimmed and at most {MaximumOccasionNameLength} characters."); continue; }
			if (!valid.Add(name)) { errors.Add($"Occasion \"{name}\" is listed twice."); continue; }
			if (state.Occasions.Any(occasion => occasion.Name == name && !occasion.IsActive))
			{ errors.Add($"Occasion \"{name}\" exists but is archived. Restore it before applying the plan."); }
		}
		return valid;
	}

	private static Dictionary<Guid, RestructureNodeMapping> ValidateNodeMappings(RestructurePlan plan,
		Dictionary<Guid, TaxonomyNode> before, Dictionary<Guid, TaxonomyNode> after, HashSet<string> occasions, List<string> errors)
	{
		Dictionary<Guid, RestructureNodeMapping> mappings = [];
		foreach (RestructureNodeMapping mapping in plan.NodeMappings)
		{
			if (!before.ContainsKey(mapping.CategoryId))
			{ errors.Add($"Node mapping names category {mapping.CategoryId}, which does not exist."); continue; }
			if (!mappings.TryAdd(mapping.CategoryId, mapping))
			{ errors.Add($"Category {PathOf(before, mapping.CategoryId)} has more than one node mapping."); continue; }
			// Undo cannot write an archived category, so purchases moved off one could never go back.
			if (before[mapping.CategoryId].Archived)
			{ errors.Add($"Category {PathOf(before, mapping.CategoryId)} is already archived, so undo could not put its purchases back. Leave it out of the plan."); continue; }
			ValidateTarget($"Node mapping for {PathOf(before, mapping.CategoryId)}", mapping.TargetCategoryId, mapping.Clear,
				mapping.Occasion, after, occasions, errors);
			if (mapping.TargetCategoryId == mapping.CategoryId)
			{ errors.Add($"Node mapping for {PathOf(before, mapping.CategoryId)} maps a category to itself."); }
		}
		List<string> unmapped = before.Values
			.Where(node => !node.Archived && after[node.Id].Archived && !mappings.ContainsKey(node.Id))
			.Select(node => PathOf(before, node.Id)).Order(StringComparer.Ordinal).ToList();
		if (unmapped.Count > 0)
		{ errors.Add($"Every category the plan archives needs a node mapping (a target or clear), so purchases added after the plan was generated still move; {unmapped.Count} have none, e.g. {Sample(unmapped)}."); }
		return mappings;
	}

	private static Dictionary<Guid, RestructureItemReassignment> ValidateItems(RestructurePlan plan,
		Dictionary<Guid, TaxonomyNode> after, HashSet<string> occasions, List<string> errors)
	{
		Dictionary<Guid, RestructureItemReassignment> items = [];
		int invalid = 0;
		string? first = null;
		foreach (RestructureItemReassignment item in plan.Items ?? [])
		{
			List<string> problems = [];
			if (item.ItemId == Guid.Empty || !items.TryAdd(item.ItemId, item)) { problems.Add("purchase IDs must be nonempty and unique"); }
			if (string.IsNullOrWhiteSpace(item.Rule) || item.Rule.Length > MaximumRuleLength || item.Rule.Contains('\0'))
			{ problems.Add($"a rule ID of at most {MaximumRuleLength} characters is required"); }
			if (item.Evidence is JsonElement evidence && (evidence.ValueKind != JsonValueKind.Object || evidence.GetRawText().Length > MaximumEvidenceLength))
			{ problems.Add($"evidence must be a JSON object of at most {MaximumEvidenceLength} characters"); }
			ValidateTarget("", item.TargetCategoryId, item.Clear, item.Occasion, after, occasions, problems);
			if (problems.Count > 0)
			{
				invalid++;
				first ??= $"purchase {item.ItemId}: {string.Join("; ", problems)}";
			}
		}
		if (invalid > 0) { errors.Add($"{invalid} purchase reassignments are invalid, e.g. {first}."); }
		return items;
	}

	private static void ValidateTarget(string subject, Guid? target, bool clear, string? occasion,
		Dictionary<Guid, TaxonomyNode> after, HashSet<string> occasions, List<string> errors)
	{
		string prefix = subject.Length == 0 ? "" : subject + ": ";
		if ((target is null) == !clear) { errors.Add(prefix + "choose exactly one of a target category or clear"); }
		if (target is Guid id && (!after.TryGetValue(id, out TaxonomyNode? node) || node.Archived))
		{ errors.Add(prefix + "the target category must be active after the plan's category changes"); }
		if (occasion is not null && !occasions.Contains(occasion))
		{ errors.Add(prefix + $"occasion \"{occasion}\" must be listed in the plan's occasions"); }
	}

	private static RestructureEnvelopeState? Resolve(RestructureEnvelopeRef reference, RestructureState state,
		string subject, List<string> errors)
	{
		List<RestructureEnvelopeState> matches = state.Envelopes.Where(envelope => envelope.Name == reference.Name
			&& (reference.YnabCategoryId is null || envelope.Links.Any(link => link.YnabCategoryId == reference.YnabCategoryId))).ToList();
		if (matches.Count == 1) { return matches[0]; }
		errors.Add(matches.Count == 0
			? $"{subject}: no envelope named \"{reference.Name}\"{(reference.YnabCategoryId is null ? "" : " with that YNAB link")} exists."
			: $"{subject}: several envelopes are named \"{reference.Name}\"; add the YNAB category ID to choose one.");
		return null;
	}

	private static List<RestructureResolvedMerge> ValidateMerges(RestructurePlan plan, RestructureState state, List<string> errors)
	{
		List<RestructureResolvedMerge> merges = [];
		foreach (RestructureEnvelopeMerge merge in plan.EnvelopeMerges ?? [])
		{
			string subject = $"Envelope merge \"{merge.Source?.Name}\" into \"{merge.Target?.Name}\"";
			if (merge.Source is null || merge.Target is null) { errors.Add(subject + ": source and target are required."); continue; }
			RestructureEnvelopeState? source = Resolve(merge.Source, state, subject, errors);
			RestructureEnvelopeState? target = Resolve(merge.Target, state, subject, errors);
			if (source is null || target is null) { continue; }
			if (source.Id == target.Id) { errors.Add(subject + ": an envelope cannot merge into itself."); continue; }
			if (!target.IsActive) { errors.Add(subject + ": the target envelope is archived."); continue; }
			merges.Add(new(source.Id, target.Id));
		}
		if (merges.Select(merge => merge.SourceEnvelopeId).Distinct().Count() != merges.Count)
		{ errors.Add("An envelope can be merged away only once."); }
		if (merges.Any(merge => merges.Any(other => other.SourceEnvelopeId == merge.TargetEnvelopeId)))
		{ errors.Add("An envelope merge target cannot itself be merged away; merge straight into the final envelope."); }
		return merges;
	}

	private static Dictionary<Guid, Guid?> ValidateExplicitDefaults(RestructurePlan plan, RestructureState state,
		Dictionary<Guid, TaxonomyNode> after, List<string> errors)
	{
		Dictionary<Guid, Guid?> defaults = [];
		foreach (RestructureEnvelopeDefault row in plan.EnvelopeDefaults ?? [])
		{
			string subject = $"Default envelope for {PathOf(after, row.CategoryId)}";
			if (!after.TryGetValue(row.CategoryId, out TaxonomyNode? node) || node.Archived)
			{ errors.Add(subject + ": the item type must be active after the plan's category changes."); continue; }
			Guid? envelope = null;
			if (row.Envelope is not null)
			{
				RestructureEnvelopeState? resolved = Resolve(row.Envelope, state, subject, errors);
				if (resolved is null) { continue; }
				if (!resolved.IsActive) { errors.Add(subject + ": the envelope is archived."); continue; }
				envelope = resolved.Id;
			}
			if (!defaults.TryAdd(row.CategoryId, envelope)) { errors.Add(subject + " is listed twice."); }
		}
		return defaults;
	}

	private static List<RestructureDefaultChange> PlanDefaults(RestructureState state, Dictionary<Guid, TaxonomyNode> after,
		Dictionary<Guid, RestructureNodeMapping> nodeMappings, Dictionary<Guid, Guid?> explicitDefaults,
		List<RestructureResolvedMerge> merges, List<string> errors, out RestructureDefaultsReport report)
	{
		Dictionary<Guid, Guid> final = [];
		Dictionary<Guid, HashSet<Guid>> movedInto = [];
		int removed = 0;
		foreach ((Guid category, Guid envelope) in state.Defaults)
		{
			if (after.TryGetValue(category, out TaxonomyNode? node) && !node.Archived) { final[category] = envelope; continue; }
			if (nodeMappings.TryGetValue(category, out RestructureNodeMapping? mapping) && mapping.TargetCategoryId is Guid target)
			{
				if (!movedInto.TryGetValue(target, out HashSet<Guid>? set)) { movedInto[target] = set = []; }
				set.Add(envelope);
			}
		}
		int moved = 0;
		foreach ((Guid target, HashSet<Guid> envelopes) in movedInto.OrderBy(pair => pair.Key))
		{
			if (explicitDefaults.ContainsKey(target)) { continue; }
			// What the target resolves today, itself or through an ancestor, counts as a candidate too: moving another
			// envelope onto it would silently redirect the target's own future purchases.
			Guid? resolved = EnvelopeDerivationPolicy.ResolveDefault(target, after, final);
			HashSet<Guid> candidates = [.. envelopes];
			if (resolved is Guid current) { candidates.Add(current); }
			if (candidates.Count > 1)
			{
				errors.Add($"Merged categories bring a default envelope to {PathOf(after, target)} that differs from the one it has or inherits; add an envelope default for it to the plan.");
				continue;
			}
			if (resolved is null) { final[target] = candidates.Single(); moved++; }
		}
		foreach ((Guid category, Guid? envelope) in explicitDefaults)
		{
			if (envelope is Guid id) { final[category] = id; }
			else { final.Remove(category); }
		}
		foreach (RestructureResolvedMerge merge in merges)
		{
			foreach (Guid category in final.Where(pair => pair.Value == merge.SourceEnvelopeId).Select(pair => pair.Key).ToList())
			{ final[category] = merge.TargetEnvelopeId; }
		}
		List<RestructureDefaultChange> changes = [];
		foreach (Guid category in state.Defaults.Keys.Union(final.Keys).Order())
		{
			Guid? previous = state.Defaults.TryGetValue(category, out Guid p) ? p : null;
			Guid? next = final.TryGetValue(category, out Guid n) ? n : null;
			if (previous != next) { changes.Add(new(category, previous, next)); }
		}
		removed = changes.Count(change => change.PreviousEnvelopeId is not null && change.EnvelopeId is null);
		report = new(state.Defaults.Count(pair => final.TryGetValue(pair.Key, out Guid next) && next == pair.Value),
			moved, removed, explicitDefaults.Count);
		return changes;
	}

	private static List<RestructureMappingChange> PlanMappings(RestructureState state, Dictionary<Guid, TaxonomyNode> before,
		Dictionary<Guid, TaxonomyNode> after, Dictionary<Guid, RestructureNodeMapping> nodeMappings, out RestructureMappingsReport report)
	{
		List<RestructureMappingChange> changes = [];
		int reapproved = 0, repointed = 0, revoked = 0, stale = 0;
		TaxonomyNode[] beforeNodes = [.. before.Values];
		TaxonomyNode[] afterNodes = [.. after.Values];
		foreach (RestructureMappingState mapping in state.Mappings)
		{
			// A mapping that no longer resolves today stays as it is: the plan never revives an approval.
			if (CategoryMappingPolicy.SemanticFingerprint(beforeNodes, mapping.CategoryId) != mapping.SemanticFingerprint)
			{ stale++; continue; }
			Guid? target = mapping.CategoryId;
			if (after[mapping.CategoryId].Archived)
			{ target = nodeMappings.TryGetValue(mapping.CategoryId, out RestructureNodeMapping? node) ? node.TargetCategoryId : null; }
			if (target == mapping.CategoryId && CategoryMappingPolicy.SemanticFingerprint(afterNodes, mapping.CategoryId) == mapping.SemanticFingerprint)
			{ continue; }
			changes.Add(new(mapping, target));
			if (target is null) { revoked++; }
			else if (target == mapping.CategoryId) { reapproved++; }
			else { repointed++; }
		}
		report = new(reapproved, repointed, revoked, stale);
		return changes;
	}

	private static List<RestructureItemChange> PlanItems(RestructurePlan plan, RestructureState state,
		Dictionary<Guid, RestructureNodeMapping> nodeMappings, Dictionary<Guid, RestructureItemReassignment> itemReassignments,
		out RestructureItemReport report)
	{
		List<RestructureItemChange> changes = [];
		Dictionary<string, int> byRule = new(StringComparer.Ordinal);
		HashSet<Guid> present = [];
		Dictionary<string, Guid> occasionIds = state.Occasions.Where(o => o.IsActive).ToDictionary(o => o.Name, o => o.Id, StringComparer.Ordinal);
		int unchanged = 0, conflicts = 0;
		foreach (RestructureItemState item in state.Items.OrderBy(item => item.ItemId))
		{
			present.Add(item.ItemId);
			Guid? target;
			string? occasion;
			string rule;
			string evidence;
			if (itemReassignments.TryGetValue(item.ItemId, out RestructureItemReassignment? listed))
			{
				target = listed.TargetCategoryId;
				occasion = listed.Occasion;
				rule = listed.Rule;
				evidence = JsonSerializer.Serialize(new RestructureEvidence(plan.PlanId, rule, item.CategoryId, listed.Evidence));
			}
			else if (item.CategoryId is Guid category && nodeMappings.TryGetValue(category, out RestructureNodeMapping? mapping))
			{
				target = mapping.TargetCategoryId;
				occasion = mapping.Occasion;
				rule = "node";
				evidence = JsonSerializer.Serialize(new RestructureEvidence(plan.PlanId, rule, category));
			}
			else { unchanged++; continue; }
			// Occasion tags ride along with a type change; a plan that keeps the type changes nothing.
			if (target == item.CategoryId) { unchanged++; continue; }
			Guid? occasionId = occasion is null ? null : occasionIds.GetValueOrDefault(occasion);
			bool conflict = item.RecordedAt > plan.SnapshotAt && ProtectedSources.Contains(item.Source);
			if (conflict) { conflicts++; }
			else { byRule[rule] = byRule.GetValueOrDefault(rule) + 1; }
			changes.Add(new(item.ItemId, item.AssignmentId, item.CategoryId, target, item.OccasionId,
				occasion is not null && occasionId != item.OccasionId ? occasion : null, rule, evidence, conflict));
		}
		int missing = itemReassignments.Keys.Count(id => !present.Contains(id));
		int written = changes.Count(change => !change.Conflict);
		report = new(state.Items.Count, written, changes.Count(change => !change.Conflict && change.TargetCategoryId is null),
			unchanged, conflicts, missing, byRule);
		return changes;
	}

	private static RestructureTaxonomyReport TaxonomyReport(Dictionary<Guid, TaxonomyNode> before, Dictionary<Guid, TaxonomyNode> after)
	{
		int created = after.Keys.Count(id => !before.ContainsKey(id));
		IEnumerable<(TaxonomyNode Old, TaxonomyNode New)> kept = before.Values.Select(node => (node, after[node.Id]));
		return new(created,
			kept.Count(pair => pair.Old.Name != pair.New.Name),
			kept.Count(pair => pair.Old.ParentId != pair.New.ParentId),
			kept.Count(pair => pair.Old.Definition != pair.New.Definition) + after.Values.Count(node => !before.ContainsKey(node.Id) && node.Definition is not null),
			kept.Count(pair => !pair.Old.Archived && pair.New.Archived),
			kept.Count(pair => pair.Old.Archived && !pair.New.Archived),
			before.Values.Count(node => !node.Archived),
			after.Values.Count(node => !node.Archived));
	}

	private static List<RestructureTransition> Transitions(List<RestructureItemChange> changes,
		Dictionary<Guid, TaxonomyNode> before, Dictionary<Guid, TaxonomyNode> after) =>
		changes.Where(change => !change.Conflict)
			.GroupBy(change => (change.PreviousCategoryId, change.TargetCategoryId))
			.Select(group => new RestructureTransition(group.Key.PreviousCategoryId, PathOf(before, group.Key.PreviousCategoryId),
				group.Key.TargetCategoryId, PathOf(after, group.Key.TargetCategoryId), group.Count()))
			.OrderByDescending(row => row.Count).ThenBy(row => row.FromPath, StringComparer.Ordinal)
			.ThenBy(row => row.ToPath, StringComparer.Ordinal).ToList();

	private static List<RestructureMergeReport> MergeReports(List<RestructureResolvedMerge> merges, RestructureState state)
	{
		Dictionary<Guid, RestructureEnvelopeState> envelopes = state.Envelopes.ToDictionary(e => e.Id);
		return merges.Select(merge =>
		{
			RestructureEnvelopeUse use = state.EnvelopeUse.GetValueOrDefault(merge.SourceEnvelopeId) ?? new(0, 0);
			int items = use.Items;
			return new RestructureMergeReport(envelopes[merge.SourceEnvelopeId].Name, envelopes[merge.TargetEnvelopeId].Name, items,
				use.Receipts,
				state.Defaults.Count(pair => pair.Value == merge.SourceEnvelopeId),
				LinksDiffer(envelopes[merge.SourceEnvelopeId], envelopes[merge.TargetEnvelopeId]) ? items : 0);
		}).ToList();
	}

	private static RestructureBudgetReport BudgetReport(List<RestructureResolvedMerge> merges, RestructureState state)
	{
		List<RestructureMergeReport> reports = MergeReports(merges, state);
		return new(reports.Sum(report => report.Items), reports.Sum(report => report.ItemsChangingYnabCategory));
	}

	/// <summary>True when some budget would push a merged purchase to a different YNAB category than before.</summary>
	private static bool LinksDiffer(RestructureEnvelopeState source, RestructureEnvelopeState target)
	{
		IEnumerable<string> budgets = source.Links.Select(link => link.BudgetId).Union(target.Links.Select(link => link.BudgetId));
		return budgets.Any(budget => source.Links.FirstOrDefault(link => link.BudgetId == budget)?.YnabCategoryId
			!= target.Links.FirstOrDefault(link => link.BudgetId == budget)?.YnabCategoryId);
	}

	private static string Sample(List<string> values) => string.Join("; ", values.Take(5)) + (values.Count > 5 ? "; …" : "");
}
