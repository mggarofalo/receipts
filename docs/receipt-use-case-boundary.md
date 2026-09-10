# Receipt use-case boundary

Complete receipt creation is the pilot for application-owned receipt workflows. The
supported entry point is `CreateCompleteReceiptCommand`; HTTP endpoints and future
workers send that command through Mediator instead of calling Infrastructure.

## Ownership

| Concern | Owner |
| --- | --- |
| Request mapping and HTTP response/notification | Presentation |
| Balance policy and decision to persist | `CreateCompleteReceiptCommandHandler` in Application |
| Atomic persistence port | `ICompleteReceiptWriter` in Application |
| Domain-to-EF mapping, generated identities, and one durable save | `CompleteReceiptWriter` in Infrastructure |
| Committed receipt notification | Presentation, after the command succeeds |
| Affected client projections | Receipt lists/details and dependent aggregates through the receipt notification contract |

The handler validates the complete receipt before it invokes the writer. The writer
contract represents one atomic commit: receipt, transactions, items, adjustments,
and mandatory audit rows either persist together or do not persist. Infrastructure
does not decide balance policy, and Application does not reference EF or transport
types.

Code that needs the same behavior must send `CreateCompleteReceiptCommand`. It must
not duplicate the balance check or call `CompleteReceiptWriter` directly. The HTTP
controller remains responsible for translating generated wire models and publishing
the client-facing receipt notification after the command succeeds.

## Receipt-list read projection

`GetAllReceiptsQueryHandler` uses the `IReceiptListReader` application port. Its
Infrastructure adapter projects `ReceiptListItem` values in the database and loads
only the category and payment summaries for the selected page. It does not hydrate a
partial `ReceiptEntity` graph or rely on global navigation includes.

The projection owns its aggregate totals, balance state, concise summaries, filtering,
sorting, and page metadata. Other receipt reads keep their existing contracts; this
pilot is not a repository-wide rewrite.
