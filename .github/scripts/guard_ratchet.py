"""guard-ratchet: no PR weakens the checks the project is judged by.

Compares the PR's result with the base branch and fails if it:
  - removes a line from .github/required-checks.txt (the list only grows);
  - removes a feature from docs/feature-checklist.md, empties its proof, makes it cite fewer
    tests, or cites a test that doesn't exist under tests/;
  - lowers the number of test methods ([Fact] and [Theory]) under tests/;
  - changes golden images whose state the PR body doesn't name;
  - changes a guarded path while its Checks section says "Guarded changes: none".

It can't stop a deliberate edit to itself; that is what the brief's Guarded changes line and
the after-merge review are for. It stops the quiet, accidental and convenient weakening.
"""
import os
import re
import subprocess
import sys

sys.path.insert(0, ".github/scripts")
from pr_common import changed_files, field, finish, gate, is_guarded, sections  # noqa: E402

pr = gate("ratchet not applicable")

base = pr["base"]["sha"]
body = pr.get("body") or ""
problems = []


def at_base(path):
    result = subprocess.run(["git", "show", f"{base}:{path}"], capture_output=True, text=True)
    return result.stdout if result.returncode == 0 else None


def now(path):
    try:
        with open(path, encoding="utf-8") as handle:
            return handle.read()
    except FileNotFoundError:
        return None


# Required checks only grow.
def check_lines(text):
    return {line.strip() for line in (text or "").splitlines() if line.strip() and not line.strip().startswith("#")}


before, after = check_lines(at_base(".github/required-checks.txt")), check_lines(now(".github/required-checks.txt"))
for removed in sorted(before - after):
    problems.append(f'removes required check "{removed}" from .github/required-checks.txt')


# Feature checklist: no feature removed, no proof emptied or thinned, every cited test real.
def features(text):
    rows = {}
    for line in (text or "").splitlines():
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if cells and re.fullmatch(r"F\d{2,}", cells[0]):
            rows[cells[0]] = cells[-1] if len(cells) > 1 else ""
    return rows


def cited(proof):
    # Tests are cited as `Class.Method` (or `Class.Nested.Method`).
    return set(re.findall(r"`([A-Z][A-Za-z0-9]+(?:\.[A-Za-z0-9_]+)+)`", proof))


def test_files(ref=None):
    """Every .cs file under tests/: in the working tree, or at `ref` when one is given."""
    listing = ["git", "ls-files", "tests"] if ref is None else ["git", "ls-tree", "-r", "--name-only", ref, "tests"]
    return [p for p in subprocess.run(listing, capture_output=True, text=True, check=True).stdout.split()
            if p.endswith(".cs")]


test_sources = [now(p) or "" for p in test_files()]


def test_exists(name):
    parts = name.split(".")
    return any(re.search(rf"\bclass\s+{parts[0]}\b", s) and re.search(rf"(?:void|Task)\s+{parts[-1]}\s*\(", s)
               for s in test_sources)


before, after = features(at_base("docs/feature-checklist.md")), features(now("docs/feature-checklist.md"))
for fid in sorted(before):
    if fid not in after:
        problems.append(f"removes feature {fid} from docs/feature-checklist.md")
    elif before[fid] and not after[fid]:
        problems.append(f"empties the proof of feature {fid}")
    elif len(cited(after[fid])) < len(cited(before[fid])):
        problems.append(f"feature {fid} now cites {len(cited(after[fid]))} test(s) as proof, down from "
                        f"{len(cited(before[fid]))}")
for fid, proof in sorted(after.items()):
    for name in sorted(cited(proof)):
        if not test_exists(name):
            problems.append(f"feature {fid} cites {name}, but no test by that name exists under tests/")


# Test methods don't decrease.
def test_methods(paths, read):
    return sum(len(re.findall(r"\[(?:Fact|Theory)\b", read(p) or "")) for p in paths)


tests_before, tests_after = test_methods(test_files(base), at_base), test_methods(test_files(), now)
if tests_after < tests_before:
    problems.append(f"lowers the test count from {tests_before} to {tests_after}")

# Golden images change only where the PR says so, and guarded paths only where it says so.
changed = changed_files(base)
for path in changed:
    if path.startswith("tests/golden/") and path.endswith(".png"):
        state = re.sub(r"^panel-|-(?:dark|light)\.png$", "", os.path.basename(path))
        if state not in body:
            problems.append(f"changes {path}, but the PR body never names state \"{state}\"")

guarded = [p for p in changed if is_guarded(p)]
declared = (field(sections(body).get("Checks", ""), "Guarded changes") or "").lower()
if guarded and declared.startswith("none"):
    problems.append("changes guarded paths (" + ", ".join(guarded) + ') but says "Guarded changes: none"')

finish(problems, f"Ratchet: ok ({tests_after} test methods, was {tests_before}; "
                 f"{len(after)} features; {len(check_lines(now('.github/required-checks.txt')))} required checks)")
