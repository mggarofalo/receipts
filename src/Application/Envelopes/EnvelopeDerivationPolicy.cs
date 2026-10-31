using Domain.Envelopes;
using Domain.Taxonomy;

namespace Application.Envelopes;

/// <summary>
/// The one owner of save-time envelope derivation:
/// item override ?? receipt paid-from ?? default(item type) ?? unresolved.
/// Persistence records the result; reads never recompute it, so history does not move
/// when defaults change.
/// </summary>
public static class EnvelopeDerivationPolicy
{
	public static EnvelopeAssignment ForNewItem(Guid? itemOverride, Guid? receiptPaidFrom, Guid? itemTypeDefault)
	{
		if (itemOverride is Guid item)
		{
			return new(item, EnvelopeSource.Item);
		}
		if (receiptPaidFrom is Guid receipt)
		{
			return new(receipt, EnvelopeSource.Receipt);
		}
		return new(itemTypeDefault, EnvelopeSource.Default);
	}

	/// <summary>An ordinary edit keeps the recorded envelope unless it asks for an override.</summary>
	public static EnvelopeAssignment ForEditedItem(EnvelopeAssignment stored, Guid? itemOverride)
		=> itemOverride is Guid item ? new(item, EnvelopeSource.Item) : stored;

	/// <summary>
	/// Choosing a per-item envelope overrides whatever was recorded. Returning to automatic re-derives an
	/// overridden item from the receipt's paid-from or its item-type default; any other item keeps its envelope.
	/// </summary>
	public static EnvelopeAssignment AfterOverrideChange(EnvelopeAssignment stored, Guid? itemOverride, Guid? receiptPaidFrom,
		Guid? itemTypeDefault)
		=> itemOverride is not null || stored.Source == EnvelopeSource.Item
			? ForNewItem(itemOverride, receiptPaidFrom, itemTypeDefault)
			: stored;

	/// <summary>Only default-sourced or unresolved envelopes follow an item-type change.</summary>
	public static EnvelopeAssignment AfterItemTypeChange(EnvelopeAssignment stored, Guid? itemTypeDefault)
		=> FollowsItemType(stored) ? new(itemTypeDefault, EnvelopeSource.Default) : stored;

	/// <summary>
	/// Choosing a receipt's paid-from envelope applies it to every item except per-item overrides.
	/// Clearing it returns receipt-sourced items to their item-type default.
	/// </summary>
	public static EnvelopeAssignment AfterPaidFromChange(EnvelopeAssignment stored, Guid? paidFrom, Guid? itemTypeDefault)
	{
		if (stored.Source == EnvelopeSource.Item)
		{
			return stored;
		}
		if (paidFrom is Guid receipt)
		{
			return new(receipt, EnvelopeSource.Receipt);
		}
		return stored.Source == EnvelopeSource.Receipt ? new(itemTypeDefault, EnvelopeSource.Default) : stored;
	}

	public static bool FollowsItemType(EnvelopeAssignment stored)
		=> stored.Source == EnvelopeSource.Default || stored.EnvelopeId is null;

	/// <summary>Default for an item type located in a revision's nodes; unknown or cyclic ancestry stops the walk.</summary>
	public static Guid? ResolveDefault(Guid? categoryId, IReadOnlyDictionary<Guid, TaxonomyNode> nodes,
		IReadOnlyDictionary<Guid, Guid> defaults)
	{
		List<TaxonomyNode> path = [];
		HashSet<Guid> visited = [];
		for (Guid? cursor = categoryId; cursor is Guid id && visited.Add(id) && nodes.TryGetValue(id, out TaxonomyNode? node); cursor = node.ParentId)
		{
			path.Add(node);
		}
		path.Reverse();
		return ResolveDefault(path, defaults);
	}

	/// <summary>Nearest mapped ancestor-or-self of a root-to-leaf item-type path.</summary>
	public static Guid? ResolveDefault(IReadOnlyList<TaxonomyNode> path, IReadOnlyDictionary<Guid, Guid> defaults)
	{
		for (int index = path.Count - 1; index >= 0; index--)
		{
			if (defaults.TryGetValue(path[index].Id, out Guid envelopeId))
			{
				return envelopeId;
			}
		}
		return null;
	}
}
