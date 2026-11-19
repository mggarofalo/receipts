# Migration: AddYnabOAuthConnection

**Migration id:** `20261119121648_AddYnabOAuthConnection`
**Date:** 2026-11-19

## What it does

Creates one table, `ynab."YnabOAuthConnections"`. It holds at most one row: the deployment's
[YNAB OAuth connection](../ynab-connection.md), with its tokens encrypted by ASP.NET Data Protection.

No existing table or row changes, and nothing is backfilled. The table is empty after the upgrade and stays
empty until an Admin chooses **Connect** on `/settings/ynab`. A deployment that uses `YNAB_PAT` keeps working
exactly as before.

Backups keep their format. The new table is deliberately left out of the
[portable backup](../backup-restore.md), and a restore does not touch it.

## Deploying

No step is required. To use OAuth afterwards, follow [YNAB connection](../ynab-connection.md).

The connection can only be read with the Data Protection keys under `/data/DataProtection-Keys` on the
`app-data` volume. Keep that volume with the database; if the keys are lost, the app asks for a reconnect.

## Rolling back

`Down` drops the table. Any stored connection is lost; the deployment falls back to `YNAB_PAT` if it is set,
and is otherwise not connected until the newer version is deployed and an Admin reconnects.

The previous release never reads the table, so rolling back the application image alone, without running
`Down`, is safe.
