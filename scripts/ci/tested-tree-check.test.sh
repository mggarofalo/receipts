#!/usr/bin/env bash
# Tests scripts/ci/tested-tree-check.sh step 3 (waiting on the parent's run)
# against a fake `gh` put first on PATH. Self-contained: needs bash and jq.
#
# Usage: scripts/ci/tested-tree-check.test.sh
set -uo pipefail

script="$(cd "$(dirname "$0")" && pwd)/tested-tree-check.sh"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

sha=1111111111111111111111111111111111111111
tree=2222222222222222222222222222222222222222
parent=3333333333333333333333333333333333333333
head=4444444444444444444444444444444444444444

mkdir -p "$work/bin"
# The fake answers the three calls the script makes with --pr and
# --tested-tree. Runs responses come from $FAKE_DIR/runs.1, runs.2, ... one
# per call, repeating the last; $FAKE_DIR/runs.calls counts the calls.
cat >"$work/bin/gh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
[ "${1:-}" = api ] || { echo "fake gh: unexpected call: $*" >&2; exit 99; }
case "$2" in
  repos/R/commits/*)
    printf '{"sha":"%s","commit":{"tree":{"sha":"%s"}},"parents":[{"sha":"%s"}]}\n' \
      "$FAKE_SHA" "$FAKE_TREE" "$FAKE_PARENT" ;;
  repos/R/pulls/*)
    echo "$FAKE_HEAD" ;;
  repos/R/actions/workflows/github-ci.yml/runs\?*)
    n=$(( $(cat "$FAKE_DIR/runs.calls" 2>/dev/null || echo 0) + 1 ))
    echo "$n" >"$FAKE_DIR/runs.calls"
    while [ "$n" -gt 1 ] && [ ! -f "$FAKE_DIR/runs.$n" ]; do n=$((n - 1)); done
    cat "$FAKE_DIR/runs.$n" ;;
  *) echo "fake gh: unexpected path: $2" >&2; exit 99 ;;
esac
EOF
chmod +x "$work/bin/gh"

# Run JSON helpers.
run() { printf '{"status":"%s","conclusion":%s}' "$1" "$2"; }
runs() { local IFS=,; printf '{"workflow_runs":[%s]}\n' "$*"; }
running=$(runs "$(run in_progress null)")
success=$(runs "$(run completed '"success"')")
failure=$(runs "$(run completed '"failure"')")
none=$(runs)
rerun=$(runs "$(run completed '"cancelled"')" "$(run in_progress null)")

failures=0
n_case=0

# check NAME WANT_EXIT WANT_STDOUT [extra assertions as "kind:value"...] -- RESPONSES... -- ARGS...
# Extra assertions: stderr:<substring>, nostderr:<substring>, maxsecs:<n>,
# mincalls:<n>, calls:<n>.
check() {
  local name=$1 want_exit=$2 want_out=$3; shift 3
  local asserts=() responses=() args=()
  while [ "$1" != -- ]; do asserts+=("$1"); shift; done; shift
  while [ "$1" != -- ]; do responses+=("$1"); shift; done; shift
  args=("$@")

  n_case=$((n_case + 1))
  local dir="$work/case$n_case" i=1 r
  mkdir -p "$dir"
  for r in "${responses[@]+"${responses[@]}"}"; do printf '%s\n' "$r" >"$dir/runs.$i"; i=$((i + 1)); done

  local start=$SECONDS code out err
  out=$(PATH="$work/bin:$PATH" GH_REPO=R FAKE_DIR="$dir" FAKE_SHA=$sha FAKE_TREE=$tree \
        FAKE_PARENT=$parent FAKE_HEAD=$head \
        "$script" --pr 1 --tested-tree "$tree" "${args[@]+"${args[@]}"}" "$sha" 2>"$dir/stderr")
  code=$?
  err=$(cat "$dir/stderr")
  local elapsed=$((SECONDS - start)) calls
  calls=$(cat "$dir/runs.calls" 2>/dev/null || echo 0)

  local why=()
  [ "$code" = "$want_exit" ] || why+=("exit $code, want $want_exit")
  [[ "$out" == *"$want_out"* ]] || why+=("stdout lacks '$want_out'")
  local a
  for a in "${asserts[@]+"${asserts[@]}"}"; do
    case "$a" in
      stderr:*) [[ "$err" == *"${a#stderr:}"* ]] || why+=("stderr lacks '${a#stderr:}'") ;;
      nostderr:*) [[ "$err" != *"${a#nostderr:}"* ]] || why+=("stderr has '${a#nostderr:}'") ;;
      maxsecs:*) [ "$elapsed" -le "${a#maxsecs:}" ] || why+=("took ${elapsed}s, want <= ${a#maxsecs:}s") ;;
      mincalls:*) [ "$calls" -ge "${a#mincalls:}" ] || why+=("$calls runs calls, want >= ${a#mincalls:}") ;;
      calls:*) [ "$calls" -eq "${a#calls:}" ] || why+=("$calls runs calls, want ${a#calls:}") ;;
    esac
  done

  if [ ${#why[@]} -eq 0 ]; then
    echo "PASS $name (${elapsed}s, $calls runs calls)"
  else
    failures=$((failures + 1))
    echo "FAIL $name"
    printf '  - %s\n' "${why[@]}"
    printf '  stdout: %s\n  stderr: %s\n' "$out" "$err"
  fi
}

check "parent green immediately" 0 "match: tree $tree" \
  calls:1 nostderr:"still running" maxsecs:1 \
  -- "$success" -- --poll 1

check "running then success" 0 "parent $parent is green" \
  stderr:"parent $parent CI run still running; checking again in 1s" mincalls:3 \
  -- "$running" "$running" "$success" -- --poll 1 --max-wait 30

check "running then failure" 1 "no-match: parent $parent has no green CI run on main" \
  stderr:"still running" calls:2 \
  -- "$running" "$failure" -- --poll 1 --max-wait 30

check "always running hits the cap" 1 "no-match: parent $parent CI run still running after 2s" \
  stderr:"still running" maxsecs:5 \
  -- "$running" -- --max-wait 2 --poll 1

check "max-wait 0 does not sleep" 1 "still running after 0s" \
  calls:1 nostderr:"checking again" maxsecs:1 \
  -- "$running" -- --max-wait 0

check "no runs" 1 "no-match: parent $parent has no CI run on main" \
  calls:1 \
  -- "$none" -- --poll 1

check "cancelled then rerun in progress keeps waiting" 0 "parent $parent is green" \
  stderr:"still running" mincalls:2 \
  -- "$rerun" "$(runs "$(run completed '"cancelled"')" "$(run completed '"success"')")" -- --poll 1 --max-wait 30

check "non-numeric max-wait" 2 "" \
  stderr:"whole seconds" calls:0 \
  -- -- --max-wait soon

echo
if [ "$failures" -eq 0 ]; then
  echo "All $n_case cases passed"
else
  echo "$failures of $n_case cases failed"
  exit 1
fi
