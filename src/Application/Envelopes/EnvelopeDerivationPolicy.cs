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
	/// Choosing a per-item envelope overrides whatever was recorded; no choice keeps the recorded envelope,
	/// whatever its source. Only <see cref="Automatic"/> returns an item to automatic.
	/// </summary>
	public static EnvelopeAssignment AfterOverrideChange(EnvelopeAssignment stored, Guid? itemOverride)
		=> ForEditedItem(stored, itemOverride);

	/// <summary>
	/// A person's Automatic re-derives once, whatever the stored source: the receipt's paid-from, else the item
	/// type's default. An unresolved result is for the caller to refuse, never to record.
	/// </summary>
	public static EnvelopeAssignment Automatic(Guid? receiptPaidFrom, Guid? itemTypeDefault)
		=> ForNewItem(null, receiptPaidFrom, itemTypeDefault);

	/// <summary>
	/// An item-type change only fills: an unresolved envelope takes the new type's default. A recorded envelope,
	/// whatever its source, is kept, and so is the stored source when there is no default to fill from.
	/// </summary>
	public static EnvelopeAssignment AfterItemTypeChange(EnvelopeAssignment stored, Guid? itemTypeDefault)
		=> IsUnresolved(stored) && itemTypeDefault is Guid fill ? new(fill, EnvelopeSource.Default) : stored;

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

	public static bool IsUnresolved(EnvelopeAssignment stored) => stored.EnvelopeId is null;

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
