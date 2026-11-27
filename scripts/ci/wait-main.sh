#!/usr/bin/env bash
# Waits for CI on a main commit and exits 0 when its "CI OK" job passes. A push
# whose tree a green PR run already tested passes in about a minute (see
# scripts/ci/tested-tree-check.sh). On the first failed blocking job, prints
# its log tail and exits 1.
#
# Usage: scripts/ci/wait-main.sh <sha>
#   After `gh pr merge N`:
#   scripts/ci/wait-main.sh "$(gh pr view N --json mergeCommit -q .mergeCommit.oid)"
set -uo pipefail
# shellcheck source=scripts/ci/lib.sh
. "$(dirname "$0")/lib.sh"
[ $# -eq 1 ] || { echo "usage: $0 <sha>" >&2; exit 2; }
# `gh run list --commit` only matches a full SHA.
sha=$(gh api "repos/{owner}/{repo}/commits/$1" --jq .sha) || exit 2

idle=0
while :; do
  run=$(gh run list --workflow github-ci.yml --event push --commit "$sha" \
    --json databaseId --jq '.[0].databaseId // empty' 2>/dev/null)
  if [ -n "$run" ]; then
    view=$(gh run view "$run" --json status,conclusion,url,jobs 2>/dev/null)
    while IFS=$'\t' read -r job conclusion name; do
      case "$conclusion" in failure|cancelled|timed_out) non_blocking "$name" && continue ;; *) continue ;; esac
      echo "--- $name failed: $(jq -r .url <<<"$view") ---"
      log_tail "$job"
      exit 1
    done < <(jq -r '.jobs[] | "\(.databaseId)\t\(.conclusion)\t\(.name)"' <<<"$view")
    if jq -e '.jobs[] | select(.name == "CI OK" and .conclusion == "success")' <<<"$view" >/dev/null; then
      echo "CI OK passed on $sha"; exit 0
    fi
    # A completed run without a green CI OK (e.g. from before CI OK existed).
    if [ "$(jq -r .status <<<"$view")" = completed ]; then
      echo "Run finished without CI OK: $(jq -r '.conclusion + " " + .url' <<<"$view")" >&2; exit 2
    fi
  else
    # No push run after ~10 minutes: not a main push, or the workflow is off.
    idle=$((idle + 1))
    [ "$idle" -lt 60 ] || { echo "No CI push run for $sha" >&2; exit 2; }
  fi
  sleep 10
done
