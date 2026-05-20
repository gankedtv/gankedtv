---
description: Create an isolated git worktree for a GitHub issue with deterministic per-worktree ports + bootstrapped dev stack
argument-hint: <number>
---

Create an isolated worktree for issue `$ARGUMENTS`. Required arg: the issue number (accepts `42` or `#42`).

This wraps `scripts/new-worktree.sh`. The worktree is created at `.worktrees/issue-<n>/` (gitignored) and gets its own dev stack (postgres + minio + .NET API + Vite) on offset ports derived from the issue number, plus a generated `.env.worktree.local`. The stack is fully bootstrapped (compose up + migrate + seed) so the user can `cd` in and `make dev-all` immediately. If the `code` CLI is available, a new VS Code window opens on the worktree automatically.

## Steps

1. **Parse the argument.** Strip a leading `#` if present. If what remains is not a positive integer, abort with `usage: /worktree <number>`.

2. **Preconditions:**
   - `gh auth status` must succeed (the script calls `gh issue view`).
   - `docker info` must succeed (the script runs `make up`).

3. **Run the script:** `./scripts/new-worktree.sh <n>`. Stream its output so the user sees progress (compose pull, migrate, seed all take time).

4. **On success**, repeat the summary line back to the user verbatim (`postgres :…   minio :…/…   api :…   web :…`) and the next-step command (`cd .worktrees/issue-<n> && make dev-all`).

5. **On failure**, surface stderr and stop. Common failures:
   - Worktree directory already exists → tell the user to run `/worktree-remove <n>` first.
   - Port already in use → another stack is on the same offset; suggest tearing it down or using a different issue number.
   - `gh` not authenticated → tell the user to `gh auth login`.

Do not start `make dev-all` — that runs in the foreground and blocks. Leave it for the user.
