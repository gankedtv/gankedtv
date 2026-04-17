# Claude Code slash commands — design

> Design + initial v1 implementation landed together for [issue #20](https://github.com/Turbootzz/gankedtv/issues/20). This doc is the permanent reference for how slash commands work in this repo; the five v1 commands live in [../commands/](../commands/).

## Goal

Remove the repetitive busywork contributors (human or AI) do around every PR — rebasing, writing PR descriptions, starting issue branches, keeping `CLAUDE.md` / `AGENTS.md` in sync, refreshing the knowledge graph — by codifying each workflow as a project-scoped Claude Code slash command.

## Conventions

- **Location:** `.claude/commands/<name>.md`. Project-scoped, auto-discovered by Claude Code, ships to every contributor that clones the repo.
- **Naming:** kebab-case, matching the literal command name agreed in issue #20 (`pr-description`, `rebasemaster`, `issue`, `sync-agents`, `graph-refresh`).
- **File shape:** YAML frontmatter (`description`, `argument-hint`) + a markdown body with the instructions Claude should follow. Any shell work lives in fenced bash blocks inside the body — no external scripts. Each command file is self-contained.
- **Mirroring:** Claude-only for now. A short paragraph in [AGENTS.md](../../AGENTS.md) will point contributors using other AI tooling to the bash-equivalent workflow embedded in each command file. Revisit if/when a non-Claude tool gets regular use in the project.

## v1 commands

### `/pr-description`

Draft or update the PR body for the current branch.

- **Args:** optional `#<issue>` override; otherwise infer from branch prefix `<num>-<slug>` (e.g. `5-server-oauth-…` → #5) or commit trailers.
- **Preconditions:** on a feature branch (not `main`), commits ahead of `origin/main`, `gh` authenticated.
- **Scale-to-complexity rule:** body length should match the change's actual complexity. Small docs/config PRs get 1–2 sentence Summary + 2–4 bullets + a one-line test note, and drop sections that don't apply (no Screenshots placeholder if nothing visual changed, no reflexive checklist items). Only the full template for genuinely medium/large PRs.
- **Template sections** (drop any that don't apply):
  - Summary · What's here · `Closes #N` · How to test manually · Screenshots / recordings · Checklist
  - Mirrors the format already established on [PR #18](https://github.com/Turbootzz/gankedtv/pull/18).
- **Labels** — map paths and commit prefixes to existing repo labels only (intersected with `gh label list` — never invent):
  - `server/**` → `area:server`
  - `web/**` → `area:web`
  - `docker-compose*.yml`, `Makefile`, `.github/**` → `area:infra`
  - `.claude/**`, `CLAUDE.md`, `AGENTS.md`, `README.md`, other root `*.md` → `documentation`
  - Conventional commit `feat:` → `enhancement`, `fix:` → `bug`, `docs:` → `documentation`
  - Fallback: if nothing mapped and a linked issue exists, inherit the linked issue's labels.
- **Title hygiene:** flag generic titles (`wip`, `fix`, single-word) and suggest a replacement; never rewrite silently.
- **Side effects:** if a PR exists → `gh pr edit --body-file …` plus `--add-label` for each derived label not already present (never removes labels). Otherwise print the body and the `--label` flags for a future `gh pr create`; never auto-create.
- **Failure modes:** no upstream, no commits ahead, detached `HEAD`, `gh` unauthenticated — each exits with a clear message and no partial writes.

### `/rebasemaster`

Rebase the current branch onto the latest `main` safely.

- **Args:** none.
- **Preconditions:** not on `main`; clean working tree (no staged/unstaged changes, no untracked files that would be clobbered).
- **Steps:** `git fetch origin main` → `git rebase origin/main` → on conflict, list conflicted paths and stop (do **not** abort); on clean rebase, suggest `git push --force-with-lease` but do not run it.
- **Failure modes:**
  - On `main` or dirty tree → refuse upfront with a remediation hint.
  - Mid-rebase conflict → surface `git status` output and stop, leaving the contributor to resolve.

### `/issue <number>`

Start work on an issue.

- **Args:** required issue number (`42` or `#42`).
- **Steps:** `gh issue view <n>` → summarize → derive slug from the issue title (lowercase, hyphenated, non-alphanumerics stripped) → `git checkout -b <n>-<slug>` off latest `main`. Matches the existing `<num>-<slug>` branch convention visible across the repo's merged PRs.
- **Preconditions:** clean working tree; branch name not already in use locally.
- **Failure modes:** issue not found, branch already exists (suggest `git switch` instead), dirty tree.

### `/sync-agents`

Keep [CLAUDE.md](../../CLAUDE.md) and [AGENTS.md](../../AGENTS.md) in sync.

- **Synced-pair invariant:** the two files are byte-identical from line 2 onward. Line 1 is the file's own H1 title (`# CLAUDE.md` / `# AGENTS.md`). This is the only intentional difference; the command reconciles any other drift.
- **Args:** optional `--check` flag. With `--check`, exit non-zero on drift instead of editing (for future CI use).
- **Steps:** diff the two files from line 2 onward. If identical, report a no-op. Otherwise pick the side modified most recently (`git log -1 --format=%ct -- <file>`, fall back to on-disk mtime) as source of truth, present a unified diff of the proposed update, and ask before writing. The write preserves the stale file's line 1 and mirrors lines 2..N from the source.
- **Side effects:** at most one file is rewritten (the stale side). Never edits both at once.
- **Failure modes:** timestamps tied in both git log and on-disk mtime with divergent content → refuse and print the diff for manual reconciliation.

### `/graph-refresh`

Run `graphify update .` per the graphify rule in [CLAUDE.md](../../CLAUDE.md), so the knowledge graph at `graphify-out/` doesn't drift after a coding session.

- **Args:** none.
- **Preconditions:** `graphify-out/` exists. If not, print a hint to run `graphify init` and stop — do not initialize silently.
- **Steps:** run `graphify update .`; on success, print a one-line summary (added / updated / removed node counts parsed from the command's output).
- **Failure modes:** `graphify` binary missing on `PATH` → clear error with install hint.

## Deferred commands

Captured so the decision is recorded and doesn't get re-litigated each time:

- **`/new-migration <Name>`** — the documented `dotnet ef migrations add` flow in CLAUDE.md is two shell lines. Not enough friction to justify a command yet. Revisit if the migration cadence picks up or the flow grows additional steps.
- **`/run-checks`** — depends on a pre-push hook suite that doesn't exist in this repo yet (the companion quality issue). Land that first; this command then becomes a thin client of it.

## Rollout

- v1 (this PR): all five commands shipped together, since each is small and they share conventions — reviewing them as a set is cheaper than five separate PRs.
- Iteration: future changes to a single command ship as their own PR. Edge cases that surface during use get added to a "Known limitations" section below as they come up.

## Skills vs. slash commands

This project uses slash commands, not skills (`.claude/skills/<name>/SKILL.md`). Rationale:

- Our commands are thin workflow wrappers with no supporting templates, examples, or scripts. Skills earn their directory layout when they have that.
- We don't currently need model auto-invocation for any of these — they're explicitly user-triggered. Skills shine when you want Claude to discover and apply them automatically.
- Automation-on-event (e.g. "run graphify after every code edit") belongs in hooks, not skills.

Revisit if any one command grows a set of supporting files, or if we want a workflow Claude applies without being asked.

## Non-goals

- Rewriting the PR description template the team already uses — `/pr-description` mirrors what's there; template changes are a separate conversation.
- Parallel Copilot / Gemini command files. Noted in "Mirroring" above.
