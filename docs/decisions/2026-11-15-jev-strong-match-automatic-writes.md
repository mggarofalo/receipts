# Jev strong matches are written automatically

Date: 2026-11-15. Status: accepted. Tracking: RECEIPTS-1118. Supersedes the automatic-Jev-action gate in
[Historical automatic classification and portable trust](2026-10-28-historical-automatic-classification.md).

## Context

The 2026-10-28 decision kept Jev suggestions user-confirmed. It said any automatic Jev action needed its own
policy, evaluated on held-out real sources against predeclared precision, coverage and safety gates.

Since then, category assistance has written strong matches automatically, on by default once consent exists (#845,
[Jev suggestion adapter](../jev-suggestion-adapter.md)). The owner asked to "automatically apply strong matches and
show exceptions". The thresholds rest on pilot evidence: the round-4 cohort of 60 cases, in which the root was right
in 21 of the 22 item cases that cleared it. That is not a validated operating point, and no superseding decision was
recorded, so the docs contradicted each other.

## Decision

Automatic writes of strong Jev matches are intended. With consent given and **Apply strong matches automatically**
on:

- **Root writes:** a fit of at least 0.7 and a stage-1 root of at least 0.90 writes the root, with source `jev` and
  JSON evidence.
- **Leaf writes:** off by default. A child is written only when they are enabled and the child clears its own
  threshold under the same root.
- **Eligibility:** writes fill only items that have no type (see the
  [purchase-classification](../../openspec/specs/purchase-classification/spec.md) eligibility rule). They never
  overwrite a manual choice, a clear or any existing category.
- **Undo:** each write appears in the saving user's automatic classification activity, with conditional undo. Undo
  writes a `manual` value, and that is the suppression. A person choosing a type by hand is the only way to reopen
  an item.

The owner accepts the pilot thresholds instead of the predeclared gates the 2026-10-28 decision required. The rest
of that decision, including its restore and provenance protections and the rules for approved deterministic
mappings, still stands.

## When to reconsider

Re-evaluate the thresholds and leaf writes on the restructured item-type tree with a fresh cohort. Reconsider
sooner if testing or the activity list shows wrong automatic roots. An administrator can switch automatic writes off
without a deployment.

## Consequences

- The [category-assistance](../../openspec/specs/category-assistance/spec.md) spec states this behaviour as intended.
- The 2026-10-28 decision is marked as superseded on this point.
