#!/usr/bin/env bash
# Decides whether a commit on main can reuse a green PR run instead of
# re-running CI.
#
# A commit matches when all of these hold:
#   1. A merged PR into main has this commit as its merge commit.
#   2. That PR's head commit carries a `tested-tree` status (written by the
#      CI OK job of a fully green PR run) whose description equals this
#      commit's tree. The PR run tested refs/pull/N/merge, so its tree is the
#      tree that ran, not the PR head's tree.
#   3. The first parent on main has a successful push run of this workflow.
#      The PR run skipped jobs whose paths it did not touch; those parts of the
#      tree are the parent's, so they inherit the parent's result.
#
# Prints "match" or "no-match: <reason>". Exits 0 on match, 1 otherwise, so
# any error also means "run everything".
#
# Usage: scripts/ci/tested-tree-check.sh [--pr N] [--tested-tree TREE]... <sha>
#   --pr N             Dry run: treat <sha> as PR N's merge commit (for example
#                      its refs/pull/N/merge commit) instead of looking it up.
#   --tested-tree T    Dry run: use T as a tested tree instead of reading the
#                      PR's `tested-tree` statuses. Repeatable.
# Requires gh (authenticated) and jq. GH_REPO selects the repository.
set -euo pipefail

workflow=github-ci.yml
pr=
trees=
sha=
while [ $# -gt 0 ]; do
  case "$1" in
    --pr) pr=$2; shift 2 ;;
    --tested-tree) trees="$trees$2"$'\n'; shift 2 ;;
    -h|--help) sed -n '2,23p' "$0"; exit 0 ;;
    *) sha=$1; shift ;;
  esac
done
[ -n "$sha" ] || { echo "usage: $0 [--pr N] [--tested-tree TREE]... <sha>" >&2; exit 2; }

no() { echo "no-match: $*"; exit 1; }
repo=${GH_REPO:-$(gh repo view --json nameWithOwner -q .nameWithOwner)} || no "cannot resolve repository"

commit=$(gh api "repos/$repo/commits/$sha") || no "cannot read commit $sha"
sha=$(jq -r '.sha' <<<"$commit")   # full SHA, whatever form was given
tree=$(jq -r '.commit.tree.sha' <<<"$commit")
parent=$(jq -r '.parents[0].sha // empty' <<<"$commit")
[ -n "$parent" ] || no "commit has no parent"

# 1. The PR this commit is the merge of.
if [ -n "$pr" ]; then
  head=$(gh api "repos/$repo/pulls/$pr" --jq '.head.sha') || no "cannot read PR #$pr"
else
  merged=$(gh api "repos/$repo/commits/$sha/pulls" |
    jq -r --arg sha "$sha" \
      '[.[] | select(.merged_at != null and .merge_commit_sha == $sha and .base.ref == "main")]
       | if length == 1 then "\(.[0].number) \(.[0].head.sha)" else empty end') ||
    no "cannot read PRs of $sha"
  [ -n "$merged" ] || no "no single merged PR has $sha as its merge commit"
  pr=${merged% *} head=${merged#* }
fi

# 2. A green PR run tested exactly this tree.
if [ -z "$trees" ]; then
  # All statuses, not the combined view: a PR run per push and per main move
  # each records its own tree, and any of them may be the one that landed.
  # Only this workflow's GITHUB_TOKEN may vouch for a tree.
  trees=$(gh api --paginate "repos/$repo/commits/$head/statuses" |
    jq -r '.[] | select(.context == "tested-tree" and .state == "success"
                        and .creator.login == "github-actions[bot]") | .description') ||
    no "cannot read statuses of PR #$pr head $head"
fi
printf '%s\n' "$trees" | grep -qxF "$tree" || no "no green run of PR #$pr tested tree $tree"

# 3. The parent on main is green.
green=$(gh api "repos/$repo/actions/workflows/$workflow/runs?head_sha=$parent&event=push&branch=main" |
  jq '[.workflow_runs[] | select(.conclusion == "success")] | length') ||
  no "cannot read CI runs of parent $parent"
[ "$green" -gt 0 ] || no "parent $parent has no green CI run on main"

echo "match: tree $tree was tested green by PR #$pr; parent $parent is green"
