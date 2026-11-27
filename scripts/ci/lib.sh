# shellcheck shell=bash
# Shared by scripts/ci/wait-*.sh. Source it; do not run it.

# Jobs with `continue-on-error: true` in github-ci.yml. Their check reports a
# failure that does not fail the run, so the wait scripts must not stop on it.
non_blocking() { [ "$1" = "Visual Regression" ]; }

# Prints the end of a failed job's log, without the post-job cleanup noise.
log_tail() {
  gh run view --job "$1" --log-failed 2>/dev/null |
    sed '/Post job cleanup/,$d' | cut -f3- | tail -n 40
}
