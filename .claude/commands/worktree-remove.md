---
description: Tear down per-issue worktree(s) — stops containers, removes named volumes, removes the worktree. With no arg, scans for merged PRs.
argument-hint: [<number> [--force] | --merged [--yes]]
---

Remove worktree(s) under `.worktrees/issue-*/`. Wraps `scripts/remove-worktree.sh`.

Two modes, chosen by `$ARGUMENTS`:

## Single-shot: explicit issue number

`/worktree-remove <n> [--force]` — tear down exactly that worktree.

1. **Parse the argument.** Strip a leading `#` if present. If what remains is not a positive integer, abort with `usage: /worktree-remove <number> [--force]`.
2. **Preconditions:**
   - `docker info` must succeed (the script calls `docker-compose down`).
   - If the worktree has uncommitted changes, `git worktree remove` will refuse unless `--force` is passed. Surface that error to the user rather than silently forcing.
3. **Run:** `./scripts/remove-worktree.sh <n> [--force]`. Pass `--force` only if the user included it.
4. **On success**, confirm: containers and volumes for issue `<n>` are gone, the worktree directory is removed.
5. **On failure**, surface stderr. Most common: uncommitted changes blocking `git worktree remove` — quote the error and ask whether to retry with `--force`.

## Batch: scan merged PRs

`/worktree-remove` (no args) or `/worktree-remove --merged` — scan `.worktrees/issue-*/`, look up each branch's PR state via `gh pr view`, propose removal only for those whose PR is **MERGED**.

1. Run `./scripts/remove-worktree.sh --merged`. The script prints a status table and interactively prompts before destroying anything. Surface its output verbatim.
2. The user types `y` at the script's prompt to proceed, or anything else to abort.
3. For squash-merged branches the per-issue `git branch -d` may refuse (commits aren't reachable from main). The script prints a retain message and continues; suggest `commit-commands:clean_gone` or `/worktree-remove <n> --force` to finish those.

**Do NOT** auto-pass `--yes` to the script — the confirmation prompt is the safety net for an irreversible destructive batch. Only pass `--yes` if the user explicitly asks to skip confirmation.

## When NOT to use

- PR not yet merged → leave the worktree alone unless the user explicitly names it (single-shot mode trusts them).
- The user is actively working in the worktree (panes/editors open) → close those first.
