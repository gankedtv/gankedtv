---
description: Draft or update the PR description for the current branch
argument-hint: [#issue-override]
---

Generate or update the PR body for the current branch. Arg (optional): `$ARGUMENTS` — an issue override like `#42` or `42` that takes priority over branch/commit inference.

## Steps

1. **Preconditions.** Run in order, abort on failure with a clear one-line message:
   - `git rev-parse --abbrev-ref HEAD` — refuse if `main`.
   - `git fetch origin main` — must succeed.
   - `git rev-list --count origin/main..HEAD` — refuse if `0` (no commits ahead).
   - `gh auth status` — must succeed.

2. **Resolve linked issue** (first match wins):
   1. `$ARGUMENTS` contains `#<n>` or a bare number.
   2. Branch prefix matching `^([0-9]+)-` (e.g. `5-server-oauth-...` → `#5`).
   3. Commit-trailer scan: `git log origin/main..HEAD --format=%B` for `Closes #N` / `Fixes #N` / `Refs #N`.
   4. None found → omit the `Closes #N` line; note it at the end of the output.

3. **Collect context:**
   - `git log origin/main..HEAD --format=%B` — commit messages for Summary/What's here.
   - `git diff --name-only origin/main...HEAD` — note the three-dot form: files changed on this branch since it diverged from `main`, ignoring any unrelated main-only commits.
   - `gh pr view --json number,title,body 2>/dev/null` — detects if a PR already exists.

4. **Label suggestions** — map paths to existing repo labels only (never invent):
   - `server/**` → `area:server`
   - `web/**` → `area:web`
   - `docker-compose*.yml`, `Makefile`, `.github/**` → `area:infra`
   - Conventional commit subject prefixes across the commit range: `feat:` → `enhancement`, `fix:` → `bug`, `docs:` → `documentation`.
   Deduplicate. Do not apply labels automatically.

5. **Title hygiene.** If the current PR title (or branch name if no PR) is `wip`, `fix`, a single word, or missing a type prefix, suggest a better one. Do not rewrite silently.

6. **Build body** with these sections exactly (skip any that would be empty except the placeholders):

   ```
   ## Summary
   <1–3 sentences, why not what>

   ## What's here
   - <bullet>
   - <bullet>

   Closes #<N>

   ## How to test manually
   <copy-pasteable steps>

   ## Screenshots / recordings
   <!-- attach here -->

   ## Checklist
   - [ ] Tests updated
   - [ ] Docs updated (CLAUDE.md / AGENTS.md if relevant)
   - [ ] Verified manually
   ```

7. **Apply:**
   - If a PR exists: write the body to a temp file and run `gh pr edit <num> --body-file <tmpfile>`.
   - If no PR: print the body in a fenced block and tell the user the body is ready for `gh pr create` — do **not** create the PR yourself.

8. **Report back:** suggested title (if any), suggested labels, and the exact command to apply them:
   `gh pr edit <num> --add-label "<label>" --add-label "<label>"`.
