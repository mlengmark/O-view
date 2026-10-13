"""Shared by the pull-request gate checks (pr_format, merge_order, guard_ratchet).

Reads the pull_request event GitHub hands every job, and parses the standard PR format
(.github/pull_request_template.md) into its sections.
"""
import json
import os
import re
import sys

SECTIONS = [
    "Merge order",
    "Summary",
    "Changes",
    "Screenshots",
    "Checks",
    "Not verified",
    "Noticed (out of scope)",
]

# The rules agents are judged by. Changing one needs the phase brief's Guarded changes line.
GUARDED = [
    ".github/**",
    "tools/check.ps1",
    "tools/ImageCompare.cs",
    "docs/feature-checklist.md",
    "**/*.csproj",
    "*.slnx",
    "**/Directory.*.props",
    "installer/**",
    "packaging/**",
    "**/Updates/**",
]


def event():
    with open(os.environ["GITHUB_EVENT_PATH"], encoding="utf-8") as handle:
        return json.load(handle)


def pull_request():
    return event()["pull_request"]


def is_dependabot(pr):
    """Dependabot writes its own PR format; its updates are judged by the test jobs alone."""
    return pr["user"]["login"] == "dependabot[bot]"


def sections(body):
    """The body's '## ' sections, as {heading: text}, in order of appearance."""
    found = {}
    current = None
    for line in (body or "").replace("\r\n", "\n").split("\n"):
        heading = re.match(r"^##\s+(.+?)\s*$", line)
        if heading:
            current = heading.group(1)
            found[current] = []
        elif current is not None:
            found[current].append(line)
    return {k: "\n".join(v).strip() for k, v in found.items()}


def field(text, name):
    """The value after '- <name>:' in a section, or None."""
    match = re.search(r"^\s*-\s*" + re.escape(name) + r"\s*:\s*(.+)$", text or "", re.M | re.I)
    return match.group(1).strip() if match else None


def matches(path, pattern):
    """Glob match where ** spans folders and * stays inside one."""
    regex = ""
    i = 0
    while i < len(pattern):
        if pattern.startswith("**/", i):
            regex += "(?:.*/)?"
            i += 3
        elif pattern.startswith("**", i):
            regex += ".*"
            i += 2
        elif pattern[i] == "*":
            regex += "[^/]*"
            i += 1
        elif pattern[i] == "?":
            regex += "[^/]"
            i += 1
        else:
            regex += re.escape(pattern[i])
            i += 1
    return re.fullmatch(regex, path) is not None


def expand_braces(pattern):
    """'a/{b,c}.cs' -> ['a/b.cs', 'a/c.cs'] (one level, which is all a Touches line needs)."""
    match = re.search(r"\{([^{}]*)\}", pattern)
    if not match:
        return [pattern]
    head, tail = pattern[:match.start()], pattern[match.end():]
    return [p for option in match.group(1).split(",") for p in expand_braces(head + option.strip() + tail)]


def touches_patterns(text):
    """The Touches line, split into glob patterns.

    Entries are separated by commas, but '|', ';' and '·' are accepted too: the template uses
    '|' for "or", builders copied it into Touches, and the first run of every such PR failed on
    punctuation alone. A trailing note in brackets, such as "(that job's steps only)", is dropped.
    """
    value = field(text, "Touches")
    if not value:
        return []
    patterns = []
    for part in re.split(r"\s*[,;|·]\s*(?![^{]*\})", value):  # separators inside {a,b} belong to the brace
        part = re.sub(r"\s*\([^)]*\)\s*$", "", part.strip()).strip().strip("`").strip()
        if part and part.lower() != "none":
            patterns.extend(expand_braces(part))
    return patterns


def is_guarded(path):
    return any(matches(path, pattern) for pattern in GUARDED)


def finish(problems, passed_message):
    if problems:
        for problem in problems:
            print(f"FAIL: {problem}")
        sys.exit(1)
    print(passed_message)
    sys.exit(0)
