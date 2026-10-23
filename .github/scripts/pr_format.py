"""pr-format: the PR follows the standard format (.github/pull_request_template.md).

Every section present and in order; Merge order names Position, Depends on and Touches;
Checks says what guarded changes were made; a PR that changes golden images shows them.
"""
import re
import sys

sys.path.insert(0, ".github/scripts")
from pr_common import SECTIONS, changed_files, field, finish, gate, sections  # noqa: E402

pr = gate("format check not applicable")

problems = []
found = sections(pr.get("body"))
present = [s for s in SECTIONS if s in found]

for name in SECTIONS:
    if name not in found:
        problems.append(f'missing section "## {name}"')
order = [s for s in found if s in SECTIONS]
if order != present:
    problems.append("sections are out of order; expected: " + " → ".join(SECTIONS))

merge = found.get("Merge order", "")
for name in ("Position", "Depends on", "Stacked on", "Touches"):
    if not field(merge, name):
        problems.append(f'Merge order needs a "- {name}:" line')

if not field(found.get("Checks", ""), "Guarded changes"):
    problems.append('Checks needs a "- Guarded changes:" line (none, or the change and the brief that lists it)')

if not re.search(r"\((?:[A-Z]+-\d+|Phase [0-9A-Za-z]+)\)\s*$", pr.get("title", "")):
    problems.append('title must end with the issue key, e.g. "(OVIAA-14)"')

goldens = [p for p in changed_files(pr["base"]["sha"]) if p.startswith("tests/golden/")]
if goldens and "![" not in found.get("Screenshots", ""):
    problems.append(f"{len(goldens)} golden image(s) changed but Screenshots shows none")

finish(problems, "PR format: ok")
