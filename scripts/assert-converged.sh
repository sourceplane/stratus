#!/usr/bin/env sh
#
# Assert that a run actually executed every job in its plan.
#
# `orun run` exits 0 when jobs finish in the WAITING state — dependencies that
# were never satisfied. That is correct for a partial run and catastrophic as a
# CI signal: run 21 of this repo reported success having executed 6 of 18 jobs
# and compiled none of the six services.
#
# This asserts on the sealed execution state — machine-readable, per job —
# rather than on the run summary's prose. The first attempt at this gate did
# grep the summary, for `^[0-9]+ waiting`, and could never have matched: the
# stats are joined onto one ANSI-dimmed line ("7 succeeded · 1 failed"), so the
# count is never at the start of a line. A gate that silently matches nothing
# is worse than no gate, which is the whole lesson of run 21.
#
# Usage: assert-converged.sh <plan.json> <state.json>
#   state.json is the output of: orun status --exec-id <id> --json
#
set -eu

plan="${1:?usage: assert-converged.sh <plan.json> <state.json>}"
state="${2:?usage: assert-converged.sh <plan.json> <state.json>}"

test -f "$plan" || { echo "assert-converged: no plan at $plan" >&2; exit 1; }
test -f "$state" || { echo "assert-converged: no state at $state" >&2; exit 1; }

# Fail CLOSED. `orun status` prints "No runs yet." and exits 0 when it cannot
# find the execution, and a jq filter over that text would fail in a way easy to
# mistake for a passing check. Assert the envelope first, explicitly.
if ! jq -e 'has("state") and (.state | has("jobs"))' "$state" >/dev/null 2>&1; then
    echo "assert-converged: $state is not an execution-state envelope — orun status found no run." >&2
    echo "assert-converged: contents follow:" >&2
    head -c 2000 "$state" >&2
    echo >&2
    exit 1
fi

planned="$(jq '.jobs | length' "$plan")"
test "$planned" -gt 0 || { echo "assert-converged: the plan contains no jobs" >&2; exit 1; }

unfinished="$(jq -r --slurpfile s "$state" '
    ($s[0].state.jobs // {}) as $actual
    | [ .jobs[].id
        | . as $id
        | ($actual[$id].status // "never ran") as $status
        | select($status != "completed")
        | "  \($id) → \($status)" ]
    | .[]' "$plan")"

if [ -n "$unfinished" ]; then
    echo "assert-converged: ${planned} job(s) planned, and these did not complete:" >&2
    printf '%s\n' "$unfinished" >&2
    echo "assert-converged: a job that never ran must not report success." >&2
    exit 1
fi

echo "assert-converged: all ${planned} planned job(s) completed"
