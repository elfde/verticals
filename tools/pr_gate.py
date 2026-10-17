#!/usr/bin/env python3
"""Claude Code's PreToolUse hook for this repository (.claude/settings.json): a pull request opens only through
`tools/pr.py`, which checks it against .agents/skills/verticals-pr first. Every other command passes untouched."""

from __future__ import annotations

import json
import re
import sys

OPEN = re.compile(r"(?:^|[\s;&|(])gh\s+pr\s+create\b")


def main() -> int:
    try:
        event = json.load(sys.stdin)
    except ValueError:
        return 0
    command = (event.get("tool_input") or {}).get("command") or ""
    if event.get("tool_name") == "Bash" and OPEN.search(command):
        sys.stderr.write("Pull requests here open through `python3 tools/pr.py --title ... --body-file ...` "
                         "(skill verticals-pr): it checks the PR against the rules, then opens it.\n")
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
