namespace Domain.Envelopes;

/// <summary>Why an item carries its envelope. Only <see cref="Default"/> follows the item type.</summary>
public enum EnvelopeSource
{
	/// <summary>Derived from the item type's default envelope.</summary>
	Default,
	/// <summary>Applied from the receipt's "paid from" envelope.</summary>
	Receipt,
	/// <summary>An explicit per-item override.</summary>
	Item,
	/// <summary>Recorded by the historical backfill from the item's YNAB resolution.</summary>
	Historical
}

/// <summary>The envelope recorded on an item at save time. A null envelope is unresolved.</summary>
public sealed record EnvelopeAssignment(Guid? EnvelopeId, EnvelopeSource Source);
