---
name: resolve-issue
description: Take a GitHub issue in mlengmark/O-view from "open" to a PR that passes the repository's nine required checks. Use this whenever the user asks to fix, resolve, close, work through, triage or "run through" O-view issues, names an issue number or URL, or says "resolve the open issues", even when they only paste a link or say "fix 192". Also use it when the user asks for a change that should be tracked as an issue first, and when a session is about to open any PR against O-view, because the PR format and merge-order rules here apply to every PR, not only issue fixes.
---

# Resolve an O-view issue

`main` is protected: PRs only, nine required checks, branch up to date with `main`, merge
commits only. Three of those checks read the PR body itself (`pr-format`, `merge-order`,
`guard-ratchet`), so a PR opened with a half-written body goes red the moment it opens, and a
PR that touches a file it did not declare stays red. Everything below exists to get a PR
green on the first run rather than to add ceremony.

Read `CLAUDE.md` first if it is not already in context. Its hard rules (layering, no fabricated
numbers, status-paired types, idempotent ingestion, no credentials) decide where code goes and
what a fix may look like. Nothing here repeats them.

## 1. Triage before touching code

- `gh issue list -R mlengmark/O-view --state open --json number,title,labels,updatedAt`
- `gh issue view <n> -R mlengmark/O-view --comments` for each issue in scope. Comments often
  carry a later decision that supersedes the description.
- Check whether it is already fixed: search the code and `git log --grep "#<n>"`. Issues in
  this repo outlive their fixes fairly often. If it is fixed, say so and offer to close it with
  a comment naming the commit, rather than re-fixing it.
- For several issues, group by the files they touch. One branch and one PR per issue is the
  default. Two issues share a PR only when they cannot be separated without one breaking the
  other; two PRs that change the same file cannot both be open, because `merge-order` blocks
  the second. Order the work so that nothing waits on a file another open PR holds.
- With more than three issues in scope, post the plan (order, grouping, anything you will not
  do and why) before writing code. The user usually wants to adjust the order.

## 2. Branch

```bash
git fetch origin
git switch -c s/<n>-<short-name> origin/main
```

The `s/` prefix is what the repository's history uses for every slice branch. Keep the issue
number in the branch name so `gh pr list` reads as a map of the open issues.

## 3. Build the fix

- Behaviour you add or change ships with a test. The suite is the proof the PR cites.
- **Guarded paths**: `.github/**`, `tools/check.ps1`, `docs/feature-checklist.md`, `*.csproj`,
  `*.slnx`, `Directory.*.props`, package versions, `installer/**`, `packaging/**`,
  `**/Updates/**`. These are approved when planned, not when merged. Outside a Paperclip phase
  there is no brief, so the user's explicit go-ahead in this session is the approval. Ask
  before changing one; never weaken a check (`guard-ratchet` fails a PR that removes a required
  check, a checklist item or a test, or lowers the test count).
- Golden images (`tests/golden/**`) change only when the issue is about something visible.
  Decide which states should change before running the check, so an unexpected diff reads as
  a bug rather than a thing to accept.
- Python is not installed on this machine. Scripts are PowerShell, node or C#.
- No em dashes anywhere in the repository, no co-author trailer on commits, no issue keys or
  phase names in code comments. These are `CLAUDE.md` conventions that no check enforces, so
  the reviewer is the only thing that catches them. Grep the diff for an em dash before pushing.

## 4. Run the gate

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/check.ps1
```

The bypass is needed because this machine's execution policy refuses repository scripts
otherwise; CI needs none. It builds, runs every test including the smoke test, renders both panels' sample states and
compares them with the goldens. Read the diff images in `artifacts/diff/` with the Read tool
when anything changed. Accept a change with `-UpdateGolden` only for the states the issue
asked for; if other states moved, that is a finding, not noise. CI's `visual` job is the
authority when local and CI renders disagree.

## 5. Open the PR

Write the whole body to a file first, then open the PR with it. The checks run as soon as the
PR exists.

- Title: `<what changes, in plain English> (#<n>)`, at most 70 characters.
- Body: every section of `.github/pull_request_template.md`, in that order: Merge order,
  Summary, Changes, Screenshots, Checks, Not verified, Noticed. `pr-format` fails on a missing
  or reordered section.
- Merge order needs four lines, `- Position:`, `- Depends on:`, `- Stacked on:`, `- Touches:`.
  For a standalone issue fix write `Position: 1 of 1 (issue #<n>)`, `Depends on: none`,
  `Stacked on: none`, and a Touches list that names every file the diff changes, exactly.
  Check it with `git diff --name-only origin/main...HEAD`; `merge-order` compares the two.
- Summary: two or three plain sentences and `Closes #<n>`, so the issue closes on merge.
- Screenshots: before and after for each changed golden, linked by SHA from
  `https://raw.githubusercontent.com/mlengmark/O-view/<sha>/tests/golden/<file>`. Before is
  the `origin/main` SHA you branched from, after is your head SHA, so push before opening the
  PR and redo the SHAs with `gh pr edit --body-file` after any later push. No visual change:
  write "No visual change, all <n> golden states unchanged."
- Checks: the test count and golden status from your own run of the gate.
- Not verified: say what nobody has run, for example "not run on a real Linux desktop".

```bash
git push -u origin HEAD
gh pr create -R mlengmark/O-view --base main --title "<title>" --body-file <file>
gh pr checks <n> -R mlengmark/O-view --watch
```

## 6. Finish

- A red check is yours to fix before reporting. Read the failing job's log, not its name.
- Do not merge unless the user asked for that in this session. Auto-merge is on for the
  repository; `gh pr merge <n> --auto --merge --delete-branch` is the whole step when they do.
- Report the PR link, what it changes, the Not verified list, and anything you noticed and
  left alone. Those are the parts a reader cannot get from the diff.
