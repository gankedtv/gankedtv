# Claude Code slash commands — design

> Design + initial v1 implementation landed together for [issue #20](https://github.com/Turbootzz/gankedtv/issues/20). This doc is the permanent reference for how slash commands work in this repo; the five v1 commands live in [../commands/](../commands/).

## Goal

Remove the repetitive busywork contributors do around every PR — rebasing, writing PR descriptions, starting issue branches, refreshing the knowledge graph — by codifying each workflow as a project-scoped Claude Code slash command.

## Conventions

- **Location:** `.claude/commands/<name>.md`. Project-scoped, auto-discovered by Claude Code, ships to every contributor that clones the repo.
- **Naming:** kebab-case (`create-pr`, `rebasemaster`, `issue`, `graph-refresh`).
- **File shape:** YAML frontmatter (`description`, `argument-hint`) + a markdown body with the instructions Claude should follow. Any shell work lives in fenced bash blocks inside the body — no external scripts. Each command file is self-contained.
- **AI tooling:** the team uses Claude Code exclusively, so no parallel Copilot / Gemini command files. Revisit if another tool gets adopted.

## v1 commands

### `/create-pr`

Create — or update — the PR for the current branch end-to-end. Supersedes the old `/pr-description`, which only drafted a body; `/create-pr` runs the precondition + CI gate, pushes, and opens (or edits) the PR itself.

- **Args** (all optional, any order): `#<n>`/bare `<n>` issue override; `--draft` (the **default**); `--ready` to open ready-for-review; `--review` to chain `/code-review` once the PR exists; `--skip-ci` to bypass the local CI gate.
- **Preconditions:** on a feature branch (not `main`), `git fetch origin main` succeeds, commits ahead of `origin/main`, `gh` authenticated, clean working tree (never auto-commits).
- **Local CI gate:** before pushing, mirror the pre-push hook scoped to changed top-level dirs (`make ci-server` / `make ci-web` / `make ci-discord`). Abort on red — never open a PR CI will reject. Skippable with `--skip-ci`.
- **Scale-to-complexity rule:** body length should match the change's actual complexity. Small docs/config PRs get a 1–2 sentence Summary + a few bullets + a one-line test note, and drop sections that don't apply. Only the full template for genuinely medium/large PRs.
- **Two-tier body:** a tight top half humans skim — `Summary` (the only **required** section) · `What's here` · `Closes #N` · `How to test` — over a collapsed `<details>` block for AI reviewers and deep-divers (file-by-file walkthrough, design decisions, risk / blast radius, out of scope). Drop any section that doesn't apply; no `<!-- placeholder -->` leftovers.
- **Breaking-change detector:** scans the diff for high-blast-radius changes (new EF migration, dependency churn, removed/renamed public endpoints, env/config surface) and surfaces them as a `> [!IMPORTANT]` callout at the top of the body. Omitted when nothing fires.
- **Labels** — map paths and commit prefixes to existing repo labels only (intersected with `gh label list` — never invent):
  - `server/**` → `area:server`
  - `web/**` → `area:web`
  - `discord/**` → `area:discord`
  - `docker-compose*.yml`, `Makefile`, `.github/**` → `area:infra`
  - `.claude/**`, `CLAUDE.md`, `README.md`, other root `*.md` → `documentation`
  - Conventional commit `feat:` → `enhancement`, `fix:` → `bug`, `docs:` → `documentation`
  - Fallback: if nothing mapped and a linked issue exists, inherit the linked issue's labels.
- **Title:** keep an existing PR title in update mode unless it's vague (`wip`, `fix`, single word, no type prefix); otherwise derive from the first conventional-commit subject or the humanized branch name. A vague resolved title is **regenerated from the diff/commits and used** — not merely suggested — and the chosen title is printed in the final report so the operator can spot anything wrong.
- **Side effects:** update mode → `gh pr edit --body-file …` plus `--add-label` for each derived label not already present (never removes labels), and toggles draft/ready to match the flag. Create mode → `gh pr create` (draft unless `--ready`) with the derived `--label` flags. Prints the PR URL and, with `--review`, invokes `/code-review`.
- **Failure modes:** no commits ahead, detached `HEAD`, `gh` unauthenticated, dirty tree, failed CI gate, failed push — each exits with a clear message and no partial PR.

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

- Rewriting the PR description template the team already uses wholesale — `/create-pr` builds on the established format; sweeping template changes are a separate conversation.
- Parallel Copilot / Gemini command files. Noted in "Mirroring" above.
