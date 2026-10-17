#!/usr/bin/env python3
"""The form a pull request keeps (.agents/skills/verticals-pr): its title and its sections. `tools/pr.py` checks it before
a PR opens; `.github/workflows/pr-rules.yml` checks it on GitHub with the title and text in PR_TITLE and PR_BODY."""

from __future__ import annotations

import os
import re
import sys

TYPES = ("feat", "fix", "docs", "ci", "chore", "refactor", "test", "perf", "build", "style")
TITLE = re.compile(r"^(%s)(\([a-z0-9][a-z0-9/-]*\))?!?: \S" % "|".join(TYPES))
MAX_TITLE = 72
CYRILLIC = re.compile(r"[Ѐ-ӿ]")
HEADING = re.compile(r"^##\s+(.+?)\s*$", re.MULTILINE)


def problems(title: str, body: str) -> list[str]:
    """What breaks the form, one line each; nothing when it holds."""
    found = []
    if not TITLE.match(title):
        found.append(f"the title reads `type(scope): what changes`, type one of {', '.join(TYPES)}: {title!r}")
    if len(title) > MAX_TITLE:
        found.append(f"the title has {len(title)} characters, at most {MAX_TITLE}")
    if CYRILLIC.search(title):
        found.append("the title is in English")
    sections = {name.lower() for name in HEADING.findall(body)}
    if "testing" not in sections:
        found.append("the text has a `## Testing` section")
    if title.startswith("fix") and not {"bug", "fix"} <= sections:
        found.append("a fix has `## Bug` and `## Fix` sections")
    elif not {"bug", "fix"} <= sections and "what" not in sections:
        found.append("the text has `## What`, or `## Bug` and `## Fix`")
    return found


def main() -> int:
    found = problems(os.environ.get("PR_TITLE", ""), os.environ.get("PR_BODY") or "")
    for problem in found:
        print(f"::error::{problem}")
    return 1 if found else 0


if __name__ == "__main__":
    sys.exit(main())
