---
description: Create (or update) a PR for the current branch with a human-scannable body and a collapsed AI-reviewer context section
argument-hint: [#issue-override] [--draft|--ready] [--review] [--skip-ci]
---

Create the PR for the current branch. If a PR already exists, update its body and labels instead.

Args (all optional, any order):
- `#<n>` or bare `<n>` — issue override (takes priority over branch/commit inference).
- `--draft` — force draft. **Default.**
- `--ready` — create as ready-for-review.
- `--review` — after the PR exists, invoke `/code-review` against it.
- `--skip-ci` — skip the local CI gate in step 2.

Raw args: `$ARGUMENTS`

## Steps

1. **Preconditions.** Run in order, abort on failure with a clear one-line message:
   - `git rev-parse --abbrev-ref HEAD` — refuse if `main`.
   - `git fetch origin main` — must succeed.
   - `git rev-list --count origin/main..HEAD` — refuse if `0` (no commits ahead).
   - `gh auth status` — must succeed.
   - Working tree must be clean (`git status --porcelain`). If dirty, abort and tell the user to commit/stash first — don't auto-commit.

2. **Local CI gate** (skip if `--skip-ci` passed). Mirror the pre-push hook so we don't create PRs that CI will reject:
   - Detect changed top-level dirs with `git diff --name-only origin/main...HEAD | awk -F/ '{print $1}' | sort -u`.
   - Run the scoped subset: `make ci-server` / `make ci-web` / `make ci-discord` for each touched area.
   - On failure: abort with the failing command and tell the user to fix or pass `--skip-ci`. Never create a PR on red.

3. **Push if needed.** If `git rev-parse @{u} 2>/dev/null` fails (no upstream) or `git rev-list --count @{u}..HEAD` > 0, run `git push -u origin HEAD`. If the push fails, abort.

4. **Resolve linked issue** (first match wins):
   1. `--`-stripped args contain `#<n>` or a bare number.
   2. Branch prefix matching `^([0-9]+)-` (e.g. `5-server-oauth-...` → `#5`).
   3. Commit-trailer scan: `git log origin/main..HEAD --format=%B` for `Closes #N` / `Fixes #N` / `Refs #N`.
   4. None found → omit the `Closes #N` line.

5. **Collect context:**
   - `git log origin/main..HEAD --format=%B` — commit messages for Summary / What's here.
   - `git diff --name-only origin/main...HEAD` — files changed on this branch (three-dot form ignores main-only commits).
   - `git diff --stat origin/main...HEAD` — for the file walkthrough.
   - `gh pr view --json number,title,body,labels,isDraft 2>/dev/null` — detects existing PR; on hit, switch to update mode.

6. **Detect breaking / high-blast-radius changes.** Scan the changed files. Flag any of:
   - New EF migration: `server/src/GankedTV.Api/Data/Migrations/*.cs` added.
   - Dependency churn: any of `package.json`, `bun.lock`, `*.csproj`, `Directory.Packages.props` modified.
   - Removed/renamed public API: removed/renamed endpoint methods in `server/src/GankedTV.Api/Endpoints/**` (heuristic: `git diff origin/main...HEAD -- server/src/GankedTV.Api/Endpoints/` shows deletions of `MapGet|MapPost|MapPut|MapDelete|MapPatch` lines).
   - Env/config surface: changes to `appsettings*.json`, `docker-compose*.yml`, `Makefile`, `.github/**`.
   If any fire, surface them as a `> [!IMPORTANT]` callout at the **top of the human body**, above Summary. If none, omit.

7. **Labels** — map paths and commit prefixes to existing repo labels only (never invent). Pull the repo's current label set with `gh label list --json name --jq '.[].name'` and intersect.
   - `server/**` → `area:server`
   - `web/**` → `area:web`
   - `discord/**` → `area:discord`
   - `docker-compose*.yml`, `Makefile`, `.github/**` → `area:infra`
   - `.claude/**`, `README.md`, `CLAUDE.md`, `*.md` at repo root → `documentation`
   - Conventional commit prefixes in the commit range: `feat:` → `enhancement`, `fix:` → `bug`, `docs:` → `documentation`.
   - Fallback: if a linked issue exists and no labels were derived above, inherit the issue's labels that are valid repo labels.
   Deduplicate. Drop any not in the repo's label set.

8. **Title.** Resolve in this order:
   - Existing PR title (update mode) — keep unless it's `wip`, `fix`, a single word, or missing a type prefix.
   - First conventional-commit subject in the range.
   - Branch name humanized.
   If the resolved title is vague (`wip`, `fix`, single word, no type prefix), generate a better one from the diff/commits and use it — don't just suggest. Print the chosen title in the final report so the user can spot anything wrong.

9. **Scale the body to the diff.** Tight by default. A 50-line docs PR should not read like a feature launch. If you catch yourself explaining what a file does when its name already does, cut it.

10. **Build the body** from this template. Drop entire sections that don't apply — no `<!-- placeholder -->` leftovers. Required: Summary; everything else earns its place.

    ````markdown
    > [!IMPORTANT]
    > <only if step 6 flagged something; one line per flag, e.g. "New EF migration: AddClipTags">

    ## Summary
    <1–3 sentences, why not what>

    ## What's here
    - <bullet>
    - <bullet>

    Closes #<N>

    ## How to test
    <numbered, copy-pasteable steps>

    <details>
    <summary><b>Context for reviewers</b> (file-by-file walkthrough, design notes, risk)</summary>

    ### Files changed
    - `path/to/file.ts` — what changed and why
    - `path/to/other.cs` — ...

    ### Design decisions
    - Chose X over Y because <constraint / tradeoff>.
    - <only include genuine decisions; skip if the diff is mechanical>

    ### Risk / blast radius
    - <what could break, who's affected, rollback path>
    - <skip section entirely if low-risk>

    ### Out of scope / follow-ups
    - <known gaps the reviewer might otherwise flag>

    </details>
    ````

    Rules for the collapsed section:
    - **Files changed** should group obviously-related files (e.g. "endpoint + its tests" as one line). Don't enumerate every test fixture.
    - Skip **Design decisions** if there are none — don't pad.
    - Skip **Risk** for low-blast-radius PRs (docs, internal refactors with full test coverage).
    - This is the section AI reviewers and deep-divers will read. It's allowed to be denser than the top.

11. **Apply.**
    - **Update mode** (PR exists):
      - Write body to a temp file, `gh pr edit <num> --body-file <tmpfile>`.
      - Add derived labels not already on the PR via `--add-label` flags (same `gh pr edit` call). Never remove labels.
      - If `--ready` was passed and PR is currently draft: `gh pr ready <num>`. If `--draft` was passed and PR is ready: `gh pr ready <num> --undo`.
    - **Create mode** (no PR):
      - Default to `--draft` unless `--ready` was passed.
      - `gh pr create --title "<title>" --body-file <tmpfile> [--draft] [--label "<l>"...]`.

12. **Post-create.**
    - Print the PR URL (`gh pr view --json url --jq .url`).
    - If `--review` was passed, invoke `/code-review` against the PR.

13. **Report back** (concise):
    - PR URL.
    - Title used.
    - Labels added.
    - Whether it was create or update, draft or ready.
    - Any breaking-change flags surfaced.
