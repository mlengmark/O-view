# Run a command under a timeout, sampling the process tree while it runs, and print the
# samples only if it times out. Sourced by the packaging workflow; see #131.
#
# Why this is a file rather than two copies inline: it is needed by two steps now (the apt
# install and lintian itself), and it carries a correctness detail that is easy to get wrong
# a second time. The first version of the sampler piped `ps` straight into the output file,
# which reports the exit status of the trailing `tail` — that succeeds on empty input, so the
# "ps is unavailable" guard could never fire and a degraded runner produced a file of
# timestamps with nothing under them while looking perfectly healthy. `ps` output is captured
# into a variable first, and it is captured in exactly one place.
#
# The question all of this answers is the one the 2026-08-19 stall left open. If the command
# is WEDGED, the same child sits at the same wchan across every sample with its elapsed time
# climbing. If it is merely SLOW, the children churn between samples. Those need completely
# different fixes and look identical from outside the step.

_WATCHED_TREE=/tmp/watched-tree.txt

# Most-recently-started 25 processes rather than the whole tree: whatever we are watching and
# its helpers are always the newest things on a runner that has just booted, and it bounds
# the output to something readable.
_watched_sample() {
  local snapshot
  snapshot="$(ps -eo pid,ppid,etime,stat,wchan:20,args --sort=start_time)" || return 1
  {
    echo "--- $(date -u +%H:%M:%SZ) ---"
    printf '%s\n' "$snapshot" | { read -r header; echo "$header"; tail -n 25; }
  } >> "$_WATCHED_TREE"
}

# watched_run <label> <timeout-seconds> <command...>
#
# Returns the command's exit status, or 124 when the timeout fired. On 124 the sampled trees
# are printed; on every other outcome nothing is, because the healthy path normally finishes
# inside the first sampling interval and the file stays empty.
watched_run() {
  local label="$1" limit="$2"
  shift 2

  # One sample before the command starts. It is the "before" to compare a wedged tree
  # against, and — the reason it is not optional — it is the only thing that exercises the ps
  # invocation on a normal run. Without it the sampler's first call happens 120 s in, so on
  # every healthy build this code never executes and an unsupported ps option would be
  # discovered during the incident it exists to diagnose. Non-fatal: a missing diagnostic
  # must not fail a good package.
  #
  # Reported rather than left silent. A diagnostic whose only evidence of working is the
  # absence of a warning is one nobody can trust at the moment they need it — "ps exited 0"
  # is not "ps produced a tree". The count says so on every run, and costs one line.
  # A count of zero is treated as degraded, not as armed. A `ps` that exits 0 while printing
  # nothing but a header satisfies the capture above and produces a baseline of no processes
  # — which looks healthy in the log and is worth exactly nothing during an incident. Checked
  # rather than left to a human noticing the number is 0.
  : > "$_WATCHED_TREE"
  local sampled=0
  if _watched_sample; then
    sampled=$(( $(wc -l < "$_WATCHED_TREE") - 2 ))
  fi

  if [ "$sampled" -gt 0 ]; then
    echo "process sampling armed for $label: baseline captured $sampled processes"
  else
    echo "note: process sampling unavailable on this runner — #131 diagnostics degraded"
  fi

  # stdout redirected to /dev/null, not because it prints anything, but because a background
  # subshell holding the script's stdout open outlives it: anything reading that pipe blocks
  # until the loop is reaped, whether or not the loop has anything to say. Found by running
  # this rather than reasoning about it — it hung a local test harness for three minutes.
  ( while sleep 120; do _watched_sample; done ) >/dev/null 2>&1 &
  local sampler=$!

  # `|| status=$?` rather than `set +e` around the call. errexit is a SHELL-wide flag, not a
  # function-scoped one, so a `set -e` in here silently re-enables it for the caller — and
  # the callers deliberately turn it off so they can inspect the status. With that leak, a
  # non-zero return killed the step before it could read $?, taking the "lintian reported an
  # error" and timeout branches with it. The left-hand side of `||` is exempt from errexit,
  # so no flag has to be touched at all.
  #
  # Not `if ! timeout ...; then status=$?; fi`, which was the first attempt and looks
  # identical: inside the branch, $? is the status of the `!` negation — always 0 — so every
  # failure was reported as success. Both the real-error and the timeout case were silently
  # swallowed until the stub run showed status=0 where 2 and 124 were expected.
  local status=0
  timeout "$limit" "$@" || status=$?

  kill "$sampler" 2>/dev/null || true
  wait "$sampler" 2>/dev/null || true

  if [ "$status" -eq 124 ]; then
    echo "$label did not finish within ${limit}s and was killed — this is the stall the bound exists for (#131), not a package defect"
    echo "Process tree sampled while it ran:"
    cat "$_WATCHED_TREE"
  fi

  return "$status"
}
