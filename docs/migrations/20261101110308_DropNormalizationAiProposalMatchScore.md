# Migration: DropNormalizationAiProposalMatchScore (RECEIPTS-1110)

**Migration id:** `20261101110308_DropNormalizationAiProposalMatchScore`
**Date:** 2026-11-01

Take a portable backup before deploying this migration.

## What it does

- Drops `matching."NormalizationAiProposals"."MatchScore"`. The ChatGPT worker copied the chosen
  candidate's similarity into it, and nothing ever read it. The candidates themselves, and their
  scores, stay in `CandidatesJson`.
- No database function, trigger or view references the column, so nothing else changes.

Backups keep their format. ChatGPT proposals are not part of a backup, so older backups restore
unchanged.

## Rolling back

`Down` re-adds the column as nullable. The dropped scores are not restored; nothing reads them.
