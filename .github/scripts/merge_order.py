"""merge-order: a PR lands only in its planned order, and parallel branches never collide.

Fails while:
  - a PR named in "Depends on" has not merged;
  - this PR changes a file outside its declared Touches list;
  - another open PR changes one of the same files;
  - this PR is stacked (targets another PR's branch rather than main). Stacking is allowed,
    one level deep, but a stacked PR never merges into its parent: it waits until the parent
    has merged and GitHub has retargeted it to main. While stacked it must also name its
    parent ("Stacked on") and change none of the parent's files, so a change to the parent
    can never conflict with it.

Required on main, so auto-merge waits on it. A PR held back by a dependency or a parent passes
once that PR merges and this branch is brought up to date (which re-runs this check, and the
build, on top of main).
"""
import json
import os
import re
import sys
import urllib.request

sys.path.insert(0, ".github/scripts")
from pr_common import event, field, finish, gate, matches, sections, touches_patterns  # noqa: E402

REPO = os.environ["GITHUB_REPOSITORY"]
TOKEN = os.environ["GITHUB_TOKEN"]


def api(path):
    request = urllib.request.Request(
        f"https://api.github.com/repos/{REPO}/{path}",
        headers={"Authorization": f"Bearer {TOKEN}", "Accept": "application/vnd.github+json"})
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def files_of(number):
    files, page = [], 1
    while True:
        batch = api(f"pulls/{number}/files?per_page=100&page={page}")
        files.extend(f["filename"] for f in batch)
        if len(batch) < 100:
            return files
        page += 1


pr = gate("merge order not applicable")

problems = []
merge = sections(pr.get("body")).get("Merge order", "")
default = event()["repository"]["default_branch"]
open_prs = [p for p in api("pulls?state=open&per_page=100") if p["user"]["login"] != "dependabot[bot]"]
mine = files_of(pr["number"])

# 0. Stacking: one level deep, named, never sharing the parent's files, never merged early.
parent = None
stacked_on = field(merge, "Stacked on") or ""
if pr["base"]["ref"] != default:
    parents = [p for p in open_prs if p["head"]["ref"] == pr["base"]["ref"]]
    if not parents:
        problems.append(f'targets branch {pr["base"]["ref"]}, which is not an open PR\'s branch: '
                        f"stack only on an open PR, or target {default}")
    else:
        parent = parents[0]
        problems.append(f'stacked on PR #{parent["number"]} "{parent["title"]}": it can merge only after that '
                        f"PR has merged and GitHub has retargeted this one to {default}")
        if parent["base"]["ref"] != default:
            problems.append(f'PR #{parent["number"]} is itself stacked: stacking is one level deep')
        if f'#{parent["number"]}' not in stacked_on:
            problems.append(f'Merge order must say "- Stacked on: PR #{parent["number"]}"')
        shared = sorted(set(mine) & set(files_of(parent["number"])))
        if shared:
            problems.append(f'changes {", ".join(shared)}, which its parent PR #{parent["number"]} also changes: '
                            "a stacked PR may use its parent's code but never edit its files")
else:
    for number in re.findall(r"#(\d+)", stacked_on):
        other = next((p for p in open_prs if p["number"] == int(number)), None)
        if other is not None:
            problems.append(f'says it is stacked on PR #{number}, which is still open, but targets {default}: '
                            f'set its base to {other["head"]["ref"]}')

# 1. Everything it depends on has merged (a stacked PR's parent is covered above).
depends = field(merge, "Depends on") or ""
for number in sorted({int(n) for n in re.findall(r"#(\d+)", depends)}):
    if parent is not None and number == parent["number"]:
        continue
    other = api(f"pulls/{number}")
    if not other.get("merged_at"):
        problems.append(f'waits for PR #{number} "{other["title"]}" to merge first')

# 2. It changes only the files it declared.
patterns = touches_patterns(merge)
if not patterns:
    problems.append('Merge order needs a "- Touches:" line listing the files this PR may change')
else:
    outside = [f for f in mine if not any(matches(f, p) for p in patterns)]
    for f in outside:
        problems.append(f"changes {f}, which is not in its Touches list")

# 3. No other open PR changes the same files. (A stacked PR's own diff is measured against its
#    parent's branch, so the parent's changes aren't counted as its own; the parent itself was
#    checked above with the stricter rule.)
mine_set = set(mine)
for other in open_prs:
    if other["number"] == pr["number"] or (parent is not None and other["number"] == parent["number"]):
        continue
    shared = sorted(mine_set & set(files_of(other["number"])))
    if shared:
        problems.append(f'shares {", ".join(shared)} with open PR #{other["number"]} "{other["title"]}"')

finish(problems, f"Merge order: ok ({len(mine)} file(s), all declared; no dependency or overlap holding it back)")
