---
name: verticals-pr
description: Open, rework or describe a pull request in the Vertical repository. Use whenever a change here is ready to become a pull request, when an open PR's title, text, commits or pictures change, and before any `gh pr create`. PRs open only through `tools/pr.py`, which checks the form and has a second agent read the PR against these rules.
---

# Vertical pull requests

One task is one pull request, squashed into one commit when it merges. Its title is a line of the release notes; its text says what changed and how it was checked, in English.

## The change

- **One task, one PR.** The refactors, test helpers and document updates the change needs go into the same PR. Pieces that only make sense together are one PR, not a stack.
- **Squashed on merge.** GitHub squashes the PR into one commit named by its title, so a rework (a review note, a typo, a dropped comment) is a new commit pushed on top.
- **Docs move with the code.** A change to behaviour that `docs/` or a README describes updates that document in the same PR.
- **Comments are short and rare,** only where the code is not obvious. No names, dates or history of attempts in them. The reasons go in the PR text.

## The title

`type(scope): what changes`: imperative, English, at most 72 characters. The types are feat, fix, docs, ci, chore, refactor, test, perf, build and style.

The release workflow strips `type(scope):` and puts merged titles into the release notes the app shows. So the title says what changes for the person using the app: "fix(web): keep the subtask caret beside its title", not "fix(web): adjust CardRow padding". When a rework changes what the PR does, rename the PR.

## The text

- **A fix** has `## Bug`, `## Fix` and `## Testing`. Bug says what goes wrong, for whom and when, as the program's behaviour in general terms. Fix gives the mechanism and where it lives, briefly.
- **Anything else** has `## What` (what changes and why) and `## Testing`.
- **A big PR** (over 400 changed lines, not counting `web/package-lock.json`) adds `## How to read`: the order to read the files in and what to check in each.
- **The program, never someone's data.** No board contents, personal names, mail, accounts or a person's local paths.
- **Testing is two or three bullets:** the tests run (files and counts), one real check (a stand, a recording, a real command) and what was not run. A failure that also fails on `main` is named as such.
- **Pictures.** A change people can see gets before/after pictures in a one-column table. Motion gets before/after videos on their own lines with bold labels. Use only synthetic data. Pictures may follow right after the PR opens: leave `<!-- shots -->` where they go.

## Opening it

Push the branch, write the text to a file, then run:

```sh
python3 tools/pr.py --title "fix(web): keep the subtask caret beside its title" --body-file /path/to/pr.md
```

In Claude Code, run it in the background: the second reading takes longer than a Bash call waits.

It runs in two steps:

1. It checks the title and the sections. `.github/workflows/pr-rules.yml` checks the same rules on GitHub.
2. A second agent (Claude Code, with no tool that writes) reads the diff and the text against this skill.

On a pass it opens the PR and prints the link; otherwise it prints what to fix. In Claude Code, the hook in `.claude/settings.json` refuses a bare `gh pr create`, so every PR goes through this.

A rework is pushed on top. When it changes what the PR does, update the title and text to match.
