---
name: cleanup-worktrees
description: Use when the user wants to tear down GankedTV worktrees after their PRs have merged — removes the worktree dir, stops + removes docker containers and named volumes, and deletes the local branch. Triggers on phrases like "clean up merged worktrees", "remove finished worktrees", "tear down old worktrees", "prune worktrees", or after a PR merge when the user wants to free disk + docker resources.
---

# Cleanup worktrees

## What it does

For each issue number (auto-detected or explicit), wraps `scripts/remove-worktree.sh <n>` to:

1. `docker compose down -v` against the worktree's stack — stops postgres + minio containers and drops their named volumes (dev data is throwaway).
2. `git worktree remove` then `rm -rf .worktrees/issue-<n>/` — removes git-tracked files AND gitignored build artifacts (obj/, bin/, node_modules/, dist/) that the bare `git worktree remove` leaves behind.
3. `git branch -d <branch>` — deletes the local branch (refuses if unmerged unless `--force`).

The single-issue script is idempotent and self-contained; this skill just orchestrates it across multiple worktrees with a safety preview.

## When NOT to use

- PR not yet merged → leave the worktree alone (or the user will lose unpushed work).
- The user is actively working in the worktree (`make web`/`make server`/`claude` panes open for it) → close those first.

## Default behavior: auto-detect merged worktrees

When invoked without explicit issue numbers, the skill scans `.worktrees/issue-*/`, looks up each branch's PR state via `gh pr list --head <branch> --state merged`, and proposes removal only for branches whose PR is merged. Unmerged or branchless worktrees are listed but skipped by default.

**Always show the preview list and wait for explicit user confirmation before destroying anything.** This is irreversible (volumes dropped, branch deleted).

## Explicit invocation

If the user names specific issue numbers ("clean up 87 and 89"), skip the gh detection and operate on exactly that list — still show the preview and confirm.

## Implementation

```bash
#!/usr/bin/env bash
set -euo pipefail

# Optional explicit issue list as args; if empty, auto-detect merged ones.
EXPLICIT_ISSUES=("$@")

REPO_ROOT="$(git rev-parse --show-toplevel)" || { echo "not in a git repo" >&2; exit 1; }
cd "$REPO_ROOT"

if [[ ! -d .worktrees ]]; then
  echo "no .worktrees/ directory — nothing to clean up"
  exit 0
fi

# Build candidate list.
candidates=()
if (( ${#EXPLICIT_ISSUES[@]} > 0 )); then
  for n in "${EXPLICIT_ISSUES[@]}"; do
    n="${n#\#}"
    candidates+=("$n")
  done
else
  shopt -s nullglob
  for dir in .worktrees/issue-*/; do
    n="${dir#.worktrees/issue-}"
    n="${n%/}"
    candidates+=("$n")
  done
  shopt -u nullglob
fi

if (( ${#candidates[@]} == 0 )); then
  echo "no worktrees found"
  exit 0
fi

# Classify each candidate: merged / open / closed / no-pr / no-branch / missing.
declare -a TO_REMOVE=()
declare -a SKIP=()
printf '\nWorktree status:\n'
printf '%-8s %-12s %-50s %s\n' 'ISSUE' 'PR STATE' 'BRANCH' 'DECISION'
printf '%-8s %-12s %-50s %s\n' '-----' '--------' '------' '--------'
for n in "${candidates[@]}"; do
  dir=".worktrees/issue-$n"
  if [[ ! -d "$dir" ]]; then
    printf '%-8s %-12s %-50s %s\n' "#$n" 'MISSING' '(no dir)' 'skip'
    SKIP+=("$n")
    continue
  fi
  branch=$(git -C "$dir" branch --show-current 2>/dev/null || true)
  if [[ -z "$branch" ]]; then
    printf '%-8s %-12s %-50s %s\n' "#$n" 'NO-BRANCH' '(detached)' 'skip'
    SKIP+=("$n")
    continue
  fi

  # gh pr list returns [] when no PR exists; we check for merged state.
  pr_state=$(gh pr list --head "$branch" --state all --json state --jq '.[0].state // "NONE"' 2>/dev/null || echo 'GH-FAIL')

  decision='skip'
  if (( ${#EXPLICIT_ISSUES[@]} > 0 )); then
    # User explicitly named this issue — trust them.
    decision='REMOVE'
    TO_REMOVE+=("$n")
  elif [[ "$pr_state" == "MERGED" ]]; then
    decision='REMOVE'
    TO_REMOVE+=("$n")
  else
    SKIP+=("$n")
  fi
  printf '%-8s %-12s %-50s %s\n' "#$n" "$pr_state" "$branch" "$decision"
done

if (( ${#TO_REMOVE[@]} == 0 )); then
  printf '\nNothing to remove.\n'
  exit 0
fi

printf '\nAbout to remove %d worktree(s): %s\n' "${#TO_REMOVE[@]}" "${TO_REMOVE[*]}"
printf 'This stops + removes docker containers, drops postgres/minio volumes,\n'
printf 'removes the .worktrees/issue-<n>/ dir, and deletes the local branch.\n'
printf 'Proceed? [y/N] '
read -r confirm
if [[ "$confirm" != "y" && "$confirm" != "Y" ]]; then
  echo "aborted"
  exit 0
fi

for n in "${TO_REMOVE[@]}"; do
  printf '\n=== removing #%s ===\n' "$n"
  ./scripts/remove-worktree.sh "$n" || echo "warning: removal of #$n failed, continuing"
done

printf '\n✓ cleanup complete\n'
```

## Notes

- The skill calls `scripts/remove-worktree.sh` per issue — that script handles the docker, git worktree, and branch teardown in the correct order. Don't reimplement that logic here.
- For merged-but-squashed PRs, `git branch -d` will refuse (commits aren't reachable from main). The user can re-run with `scripts/remove-worktree.sh <n> --force` for those, or rely on `commit-commands:clean_gone` to sweep them up afterwards.
- The skill does NOT prune merged worktrees automatically on schedule — it's manual-trigger only, by design.

## Example invocations

- `cleanup-worktrees` — auto-detect merged PRs and propose removal.
- `cleanup-worktrees 87 89` — explicitly remove these two, regardless of PR state.
