---
description: Rebase the current branch onto the latest origin/main safely
---

Rebase the current branch onto latest `origin/main`. Takes no arguments.

## Steps

1. **Preconditions** — refuse early, one-line message each, before touching git state:
   - `git rev-parse --abbrev-ref HEAD` — refuse if `main`. Message: `/rebasemaster is for feature branches; already on main`.
   - `git status --porcelain --untracked-files=no` — refuse if non-empty (staged or unstaged changes). Rebase won't touch untracked files, so ignore them. Print the offending lines and tell the user to commit or stash first.

2. **Fetch:** `git fetch origin main`. On failure, abort and print the underlying error.

3. **Rebase:** `git rebase origin/main`.

4. **Handle conflicts.** If `git rebase` exits non-zero:
   - Do **not** run `git rebase --abort`. Leave the rebase in progress.
   - Print the conflicted paths: `git diff --name-only --diff-filter=U`.
   - Print `git status --short` so the user sees their options.
   - Stop. The user will resolve and `git rebase --continue` themselves.

5. **On clean rebase.** Print:
   - A one-line confirmation including how many commits were rebased (`git rev-list --count origin/main..HEAD`).
   - The exact next-step command, do **not** run it:
     ```
     git push --force-with-lease
     ```

## Notes

- Never force-push from this command. The suggestion in step 5 is printed, not executed.
- Never abort mid-rebase on the user's behalf — conflict resolution is their call.
