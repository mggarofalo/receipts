namespace Infrastructure.Entities.Audit;

public enum AuditAction
{
	Create,
	Update,
	Delete,
	Restore,
	Merge,
	// Split is the inverse of Merge: one row detached out of another. Recorded explicitly because
	// the mechanical trail (a Create plus an Update) does not say what was split out of what
	// (RECEIPTS-890). Persisted as a string via the global EnumToStringConverter, so appending a
	// value needs no migration and does not renumber the existing ones.
	Split,
}
