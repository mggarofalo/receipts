# Historical automatic classification and portable trust

Date: 2026-10-28. Status: design decision; implementation and enablement remain future work. Tracking: RECEIPTS-1078.

## Context and decision

Historical automatic classification needs bounded, recoverable changes that respect later human decisions. The owner selects a backup for restore as trusted business data. Its contents must still pass strict structural, referential and semantic consistency validation before any destination mutation. They are not authenticated by an independent external authority: a malicious, internally consistent archive cannot be detected as such by these checks. Hashes and internal chains detect inconsistency; they do not prove origin or intent.

This decision supersedes the proposed attestation architecture for automatic historical effects, including a requirement for an independently trusted checkpoint. No automatic historical effect writer or candidate effect archive path exists. Public import accepts validated v14 automatic historical manifests through the candidate restore path; public export remains v13. Unsupported effect records still reject during archive validation before destination mutation. Existing archive validation remains in force; any future effect implementation must establish its own safety and restore validation before activation.

## Execution and correction

Extend the existing historical reclassification operations rather than introducing a parallel effects engine. Freeze explicit scope, item and receipt revisions, prior assignments, taxonomy and stable operation identities. Record typed automatic provenance that distinguishes the policy and the approved mapping or model decision that produced each assignment. Persist assignment and progress atomically, with idempotent retries and durable outcomes; a crash cannot leave one committed without the other.

An exact approved mapping is eligible only while its source values, mapping revision, target and taxonomy semantics remain valid. Recheck these facts and the frozen item, receipt and current assignment under the established locks in the write transaction. A preview or hash alone is not authority. Changed, deleted, unavailable or otherwise ineligible items become visible conflicts or skips rather than overwrites. Preserve explicit manual classifications and clears, including a later manual change to the same category.

Undo appends a correction only while the exact assignment made by the operation remains current; it cannot undo later work. An undo or explicit rejection must also create durable suppression sufficient to prevent the same automatic decision from immediately reapplying. Define its scope, lifecycle and explicit reopening behavior as part of implementation; preserve it across backup and restore. Cancellation stops future work and does not silently reverse committed changes.

## Backup, restore and rollout

Fence backup capture and restore against participating classification, mapping, taxonomy and historical-operation writes so the archive represents a consistent boundary and in-flight work cannot resume against incompatible restored state. Validate operation identities, typed provenance, source and mapping evidence, taxonomy and assignment references, outcome/undo lineage, suppression and progress consistency before writes. Reject malformed, contradictory or divergent immutable evidence atomically; do not infer missing authority or silently repair it. Preserve compatible evidence and define revision preservation or a validated translation before allowing pending work to resume after restore. Legacy archives without the required evidence must not acquire automatic authority by inference.

Jev suggestions remain user-confirmed under the current policy. Any automatic Jev action needs a separate explicit policy, independently evaluated on appropriately authorized held-out real sources with predeclared precision, coverage and safety gates. Provider confidence or the existing suggestion evaluation alone does not authorize automatic writes. Keep automatic Jev actions disabled until that policy and the persistence, race, crash, undo, suppression and backup/restore protections are verified. Automatic use of approved deterministic mappings requires its own verification of those operational protections and the mapping checks above; it does not depend on Jev policy or availability. Manual classification remains independent of provider availability.

This direction trades external origin authentication for simpler, portable owner-controlled backup recovery. It protects against malformed archives, inconsistent evidence, crashes and races, but cannot prove that a deliberately forged, internally consistent owner-selected backup reflects real past actions.
