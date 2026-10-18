# Duplicate acceptance after edits

A duplicate acceptance acknowledges the receipt's date, exact location text and
payment total. Changing any of these facts invalidates every active accepted pair
that includes the receipt, regardless of the report mode or tolerance used when
accepting it. Restoring the previous values does not restore the acceptance; the
user must review and accept the pair again.

`DuplicateReceiptFacts` in Application defines the immutable acknowledged facts.
The PostgreSQL save pipeline captures narrow persisted snapshots before and after
the write. It compares database values rather than potentially stale tracked
original values. Receipt-item details, tax, images, account/card identity and
payment dates are not duplicate-detection facts. A net-zero change to the payment
total does not invalidate acceptance.

Receipt trash and restore retain their existing behavior. For a trashed receipt,
the fact snapshot includes payments cascade-deleted by that receipt, so the trash
cycle alone does not change the acknowledged total. Independently deleted payments
remain excluded. Editing a cascade-deleted payment while its receipt is trashed
does change that receipt's logical total and invalidates acceptance.

The save pipeline and duplicate acceptance writer acquire the same ordered
receipt-row locks. Locks, the business write, mandatory audits and pair tombstones
participate in the existing transaction or caller savepoint. An acceptance that
commits before an edit is invalidated by that edit; one that acquires the locks
after the edit acknowledges the new facts. Rejected or rolled-back writes do not
invalidate acceptance. Pair tombstones retain the existing intentional audit
exclusion and record the current user/API-key attribution when available.

This guarantee applies to the PostgreSQL application write path, including all
synchronous and asynchronous `SaveChanges` overloads. InMemory fixtures do not
implement this transactional reconciliation. Any future raw SQL write to these
facts must join the same locking and invalidation protocol. Database migrations
or external maintenance writes must reconcile affected acceptances explicitly.

No schema or backup-format migration is needed. Existing acceptances remain until
a subsequent fact change: historical acceptance-time facts were never stored, so
the application cannot reconstruct whether an older acceptance was already stale.
The wire contracts and pairwise grouping semantics are unchanged.
