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

4. **Labels** — map paths and commit prefixes to existing repo labels only (never invent). Pull the repo's current label set with `gh label list --json name --jq '.[].name'` and intersect — if a mapped label doesn't exist in the repo, drop it.
   - `server/**` → `area:server`
   - `web/**` → `area:web`
   - `docker-compose*.yml`, `Makefile`, `.github/**` → `area:infra`
   - `.claude/**`, `README.md`, `CLAUDE.md`, `*.md` at repo root → `documentation`
   - Conventional commit subject prefixes across the commit range: `feat:` → `enhancement`, `fix:` → `bug`, `docs:` → `documentation`.
   - Fallback: if a linked issue exists and no labels were derived above, inherit the linked issue's labels that are also valid repo labels.
   Deduplicate.

5. **Title hygiene.** If the current PR title (or branch name if no PR) is `wip`, `fix`, a single word, or missing a type prefix, suggest a better one. Do not rewrite silently.

6. **Scale the body to the PR.** Match the body's length to the change's actual complexity. A 50-line docs-only PR should not read like a feature launch.
   - Small / simple PRs (single-area doc or config change, <~200 lines, no new behavior to exercise): 1–2 sentence Summary, 2–4 What's here bullets, 1–3 line test plan, drop Screenshots if nothing visual changed.
   - Medium PRs: the full template, but keep each section tight.
   - Large / cross-cutting PRs: full template; add extra subsections only if they earn their place.
   - If you catch yourself explaining what a file does when its name already does, cut it. The reader can open the file.

7. **Build body** from this template. Drop entire sections that don't apply — don't leave placeholders like `<!-- attach here -->` behind. The only always-required sections are Summary and (if an issue is linked) `Closes #<N>`.

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
   <attach if there's a visual change; otherwise omit this section entirely>

   ## Checklist
   - [ ] Verified manually
   - [ ] <add item only if it's genuinely pending, not a reflex>
   ```

8. **Apply:**
   - If a PR exists:
     - Write the body to a temp file and run `gh pr edit <num> --body-file <tmpfile>`.
     - Apply the derived labels in the same `gh pr edit` call with `--add-label "<label>"` for each (only labels not already on the PR — fetch current with `gh pr view <num> --json labels`). Never remove labels; only add.
   - If no PR: print the body in a fenced block and print the labels plus the exact `gh pr create --label "<label>"` flags to pass. Do **not** create the PR yourself.

9. **Report back:** suggested title (if any) and the labels that were applied (or, if no PR, the flags the user should pass to `gh pr create`).
