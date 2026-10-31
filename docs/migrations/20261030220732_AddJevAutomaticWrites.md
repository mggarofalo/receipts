# Migration: AddJevAutomaticWrites (RECEIPTS-1083)

**Migration id:** `20261030220732_AddJevAutomaticWrites`
**Date:** 2026-10-30

Take a portable backup before deploying this migration.

## What it does

- Adds `category_assistance."Settings"."AutoApply"` (default **true**). With consent given, strong
  Jev matches are written automatically from the first poll after deployment. An administrator can
  switch it off under Jev settings ("Apply strong matches automatically").
- Adds `category_assistance."Attempts"."AutoWriteCheckedAt"`, the point up to which an attempt's
  save observations were considered for an automatic write.
- Adds `SecondStageInputTokens` and `SecondStageOutputTokens` to `category_assistance."Attempts"`,
  next to the existing first-stage columns, so the billed usage of both requests is recorded.
  Existing rows keep `NULL`.
- Adds `SettingsRevision` to the unique attempt identity
  (`IX_Attempts_RestoreGeneration_SourceHash_TaxonomyRevision_Prov~`, dropped and recreated under the
  same name). After a settings save, a source saved again gets a fresh request instead of attaching to
  a result decided under the earlier settings. Existing rows already satisfy the wider index.
- Allows the attempt state `applied`: a strong match written automatically leaves the review
  queue and the pending limit.
- Allows the historical operation kind `JevSuggestionV1`. Automatic Jev writes appear in the
  existing automatic classification activity and use its conditional undo. The header identity
  check (`historical_capture_mapping_identity`) is replaced, still `NOT VALID`, so a Jev header
  carries no mapping revision. The item guard (`guard_historical_provenance_item`) rejects mapping
  provenance on Jev items just as it does on manual ones.

No existing row is rewritten, apart from setting `AutoApply` on the settings singleton.

## Rolling back

`Down` takes an exclusive lock on the historical tables. It refuses (SQLSTATE 23514, "Automatic
Jev history is retained") while any `JevSuggestionV1` operation or `applied` attempt exists,
because the earlier schema cannot represent them. It also refuses ("Category assistance attempts from
several settings revisions are retained") while one source has attempts under more than one settings
revision, which the narrower earlier identity cannot hold. Otherwise it restores the previous constraints,
item guard and state list, and drops the new columns. Second-stage usage recorded since the
upgrade is lost on rollback.
