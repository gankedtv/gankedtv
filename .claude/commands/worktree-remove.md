---
description: Tear down a per-issue worktree — stops containers, removes its named volumes, removes the worktree
argument-hint: <number> [--force]
---

Remove the worktree previously created for issue `$ARGUMENTS`. Required arg: the issue number. Optional second arg: `--force` (passed through to `git worktree remove` to discard uncommitted changes).

This wraps `scripts/remove-worktree.sh`. It stops the worktree's containers and removes its named volumes (dev data is throwaway), then removes the worktree directory.

## Steps

1. **Parse the argument.** Strip a leading `#` if present. If what remains is not a positive integer, abort with `usage: /worktree-remove <number> [--force]`.

2. **Preconditions:**
   - `docker info` must succeed (the script calls `docker-compose down`).
   - If the worktree has uncommitted changes, `git worktree remove` will refuse unless `--force` is passed. Surface that error to the user rather than silently forcing.

3. **Run the script:** `./scripts/remove-worktree.sh <n> [--force]`. Pass through `--force` only if the user explicitly included it in their `$ARGUMENTS`.

4. **On success**, confirm: containers and volumes for issue `<n>` are gone, the worktree directory is removed.

5. **On failure**, surface stderr. The most common failure is uncommitted changes blocking `git worktree remove` — quote the error and ask the user whether to retry with `--force`.
