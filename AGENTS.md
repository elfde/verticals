# Agents

Pull requests here follow [.agents/skills/verticals-pr](.agents/skills/verticals-pr/SKILL.md). Read it before you open or rework one: one task, one PR, and a title and text in the shape it gives. Push the branch, then open the PR with

```sh
python3 tools/pr.py --title "type(scope): what changes" --body-file /path/to/pr.md
```

It checks the PR against the skill and opens it only when it holds; otherwise it says what to fix. It needs Claude Code; without it, open the PR with `gh pr create`, and GitHub checks its form.

The other skills are in `.agents/skills/`, and the README says where code, schema and UI live.
