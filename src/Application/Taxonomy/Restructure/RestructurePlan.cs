using System.Text.Json;
using Domain.Taxonomy;

namespace Application.Taxonomy.Restructure;

/// <summary>
/// A reviewed, data-driven restructure of the category tree: one new taxonomy revision plus the
/// reassignments, defaults and envelope merges that go with it. Generated outside the app from a
/// backup, so it references categories and items by their stable IDs and envelopes by name.
/// </summary>
public sealed record RestructurePlan(
	string Format,
	int Version,
	Guid PlanId,
	string Reason,
	Guid BaseTaxonomyRevisionId,
	DateTimeOffset SnapshotAt,
	IReadOnlyList<RestructureCategoryChange> TaxonomyChanges,
	IReadOnlyList<RestructureNodeMapping> NodeMappings,
	IReadOnlyList<RestructureItemReassignment>? Items = null,
	IReadOnlyList<string>? Occasions = null,
	IReadOnlyList<RestructureEnvelopeDefault>? EnvelopeDefaults = null,
	IReadOnlyList<RestructureEnvelopeMerge>? EnvelopeMerges = null)
{
	public const string FormatName = "receipts-restructure-plan";
	public const int CurrentVersion = 1;
}

/// <summary>A category tree edit, as the category API takes it: create, rename, move, define, archive or restore.</summary>
public sealed record RestructureCategoryChange(string Kind, Guid CategoryId, Guid? ParentId = null, string? Name = null,
	string? Definition = null)
{
	public TaxonomyEdit ToEdit() => new(Kind, CategoryId, ParentId, Name, Definition);
}

/// <summary>
/// Every purchase on <see cref="CategoryId"/> that the plan does not list by item follows this
/// mapping: to <see cref="TargetCategoryId"/>, or cleared when <see cref="Clear"/> is set.
/// </summary>
public sealed record RestructureNodeMapping(Guid CategoryId, Guid? TargetCategoryId = null, bool Clear = false,
	string? Occasion = null);

/// <summary>An explicit reassignment of one purchase, with the rule that decided it and its inputs.</summary>
public sealed record RestructureItemReassignment(Guid ItemId, Guid? TargetCategoryId = null, bool Clear = false,
	string? Occasion = null, string Rule = "", JsonElement? Evidence = null);

/// <summary>Envelope IDs differ per deployment, so a plan names envelopes; a YNAB category ID narrows the match.</summary>
public sealed record RestructureEnvelopeRef(string Name, string? YnabCategoryId = null);

/// <summary>Sets the item type's default envelope, or removes it when <see cref="Envelope"/> is null.</summary>
public sealed record RestructureEnvelopeDefault(Guid CategoryId, RestructureEnvelopeRef? Envelope);

/// <summary>Moves every purchase, receipt and default from one envelope to another and archives the source.</summary>
public sealed record RestructureEnvelopeMerge(RestructureEnvelopeRef Source, RestructureEnvelopeRef Target);
