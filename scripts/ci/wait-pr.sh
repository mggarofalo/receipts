#!/usr/bin/env bash
# Waits until a PR's "CI OK" check passes. On the first red blocking check,
# prints the failed jobs' log tails and exits 1.
#
# Usage: scripts/ci/wait-pr.sh [pr]   (default: the current branch's PR)
set -uo pipefail
# shellcheck source=scripts/ci/lib.sh
. "$(dirname "$0")/lib.sh"
pr=${1:-}

idle=0
while :; do
  rows=$(gh pr checks ${pr:+"$pr"} --json bucket,name,link \
    --jq '.[] | "\(.bucket)\t\(.name)\t\(.link)"' 2>/dev/null)
  pending=false
  while IFS=$'\t' read -r bucket name link; do
    [ "$bucket" = pending ] && pending=true
    case "$bucket" in fail|cancel) non_blocking "$name" && continue ;; *) continue ;; esac
    echo "--- $name failed: $link ---"
    job=${link##*/job/}
    [ "$job" != "$link" ] && log_tail "${job%%[!0-9]*}"
    exit 1
  done <<<"$rows"
  if grep -qx $'pass\tCI OK\t.*' <<<"$rows"; then echo "CI OK passed"; exit 0; fi
  # Nothing pending and no CI OK: checks not created yet, the run is between
  # jobs, or it never reaches CI OK. Give up after about two minutes.
  if [ "$pending" = false ]; then
    idle=$((idle + 1))
    [ "$idle" -lt 12 ] || { echo "CI OK never reported" >&2; exit 2; }
  else
    idle=0
  fi
  sleep 10
done
