#!/usr/bin/env bash
#
# Tear down a worktree created by scripts/new-worktree.sh: stop containers,
# remove named volumes (data is throwaway), and remove the worktree.
#
# Usage:
#   ./scripts/remove-worktree.sh <issue-number> [--force]   single-shot teardown
#   ./scripts/remove-worktree.sh --merged [--yes]           scan .worktrees/ and
#                                                            tear down every one
#                                                            whose PR is merged
#
# Extra flags after the issue number pass through to `git worktree remove`
# (e.g. --force to discard uncommitted changes).

set -euo pipefail

# ---- batch mode: --merged ----------------------------------------------------
# Scans .worktrees/issue-*/, checks each branch's PR state via `gh`, previews
# the set of merged ones, prompts, then recurses into single-shot mode for each.
if [[ "${1:-}" == "--merged" ]]; then
  shift
  assume_yes=0
  if [[ "${1:-}" == "--yes" || "${1:-}" == "-y" ]]; then
    assume_yes=1
    shift
  fi

  repo_root=$(git rev-parse --show-toplevel)
  cd "$repo_root"

  if [[ ! -d .worktrees ]]; then
    echo "no .worktrees/ directory — nothing to clean up"
    exit 0
  fi

  if ! command -v gh >/dev/null 2>&1; then
    echo "error: gh CLI required for --merged mode" >&2
    exit 1
  fi

  to_remove=()
  printf '\nWorktree status:\n'
  printf '%-8s %-12s %-50s %s\n' 'ISSUE' 'PR STATE' 'BRANCH' 'DECISION'
  printf '%-8s %-12s %-50s %s\n' '-----' '--------' '------' '--------'
  shopt -s nullglob
  for d in .worktrees/issue-*/; do
    n="${d#.worktrees/issue-}"; n="${n%/}"
    branch=$(git -C "$d" branch --show-current 2>/dev/null || true)
    if [[ -z "$branch" ]]; then
      printf '%-8s %-12s %-50s %s\n' "#$n" 'NO-BRANCH' '(detached)' 'skip'
      continue
    fi
    pr_state=$(gh pr list --head "$branch" --state all --json state --jq '.[0].state // "NONE"' 2>/dev/null || echo 'GH-FAIL')
    if [[ "$pr_state" == "MERGED" ]]; then
      printf '%-8s %-12s %-50s %s\n' "#$n" "$pr_state" "$branch" 'REMOVE'
      to_remove+=("$n")
    else
      printf '%-8s %-12s %-50s %s\n' "#$n" "$pr_state" "$branch" 'skip'
    fi
  done
  shopt -u nullglob

  if (( ${#to_remove[@]} == 0 )); then
    printf '\nNothing to remove.\n'
    exit 0
  fi

  printf '\nAbout to remove %d worktree(s): %s\n' "${#to_remove[@]}" "${to_remove[*]}"
  printf 'This stops + removes docker containers, drops postgres/minio volumes,\n'
  printf 'removes the .worktrees/issue-<n>/ dir, and deletes the local branch.\n'
  if (( ! assume_yes )); then
    printf 'Proceed? [y/N] '
    read -r confirm
    if [[ "$confirm" != "y" && "$confirm" != "Y" ]]; then
      echo "aborted"
      exit 0
    fi
  fi

  for n in "${to_remove[@]}"; do
    printf '\n=== removing #%s ===\n' "$n"
    "$0" "$n" || echo "warning: removal of #$n failed, continuing"
  done

  printf '\n✓ cleanup complete\n'
  exit 0
fi

# ---- single-shot mode --------------------------------------------------------
issue="${1:-}"
if [[ -z "$issue" ]]; then
  echo "usage: $0 <issue-number> [--force]" >&2
  echo "       $0 --merged [--yes]" >&2
  exit 1
fi
issue="${issue#\#}"
if ! [[ "$issue" =~ ^[0-9]+$ ]]; then
  echo "error: issue must be numeric (got '$issue')" >&2
  exit 1
fi
# Canonicalize to match scripts/new-worktree.sh: collapse "042" → "42" so the
# constructed path matches whichever form was used at create time. 10# forces
# base-10 interpretation (avoids bash treating leading-zero strings as octal).
issue=$((10#$issue))
if (( issue < 1 )); then
  echo "error: issue number must be positive (got '$issue')" >&2
  exit 1
fi
shift

# Anchor to repo root so the script works regardless of cwd, matching the path
# layout produced by scripts/new-worktree.sh.
repo_root=$(git rev-parse --show-toplevel)
rel_dir=".worktrees/issue-${issue}"
dir="${repo_root}/${rel_dir}"
if [[ ! -d "$dir" ]]; then
  echo "error: no worktree at ${rel_dir}" >&2
  exit 1
fi

# Capture the worktree's branch before removal so we can offer to delete it
# afterwards. Without this, the local branch sticks around after teardown and
# the next `new-worktree.sh <same-issue>` would pick it up via the script's
# refs/heads/* check — which bit us during testing when the stale branch
# pointed at an older commit than origin.
branch=$(git -C "$dir" branch --show-current 2>/dev/null || true)

# Detect --force in passthrough args (also forwarded to git worktree remove).
# When set, escalate the branch deletion from -d (safe, refuses unmerged) to
# -D (force).
force_branch_delete=0
for arg in "$@"; do
  if [[ "$arg" == "--force" ]]; then force_branch_delete=1; fi
done

# Tear down with the same --env-file flag the worktree's Makefile uses, so
# COMPOSE_PROJECT_NAME (now set explicitly in .env.worktree.local) and the port
# vars resolve identically to the `up`. -v removes the named volumes
# (gankedtv-issue-<n>_postgres_data, _minio_data) — dev data is intentionally
# throwaway.
if [[ -f "$dir/.env.worktree.local" ]]; then
  (cd "$dir" && docker-compose -f docker-compose.dev.yml --env-file .env.worktree.local down -v)
else
  echo "warning: ${rel_dir}/.env.worktree.local missing — falling back to bare compose down" >&2
  (cd "$dir" && docker-compose -f docker-compose.dev.yml down -v)
fi

git worktree remove "$dir" "$@"

# `git worktree remove` only deletes git-tracked files — gitignored build
# artifacts (server/*/obj/, server/*/bin/, web/node_modules/, dist/) survive and
# leave a half-empty directory behind. We just ran `docker compose down -v` so
# we're already in destructive mode; finish the cleanup so .worktrees/ doesn't
# accumulate stale tree skeletons across iterations.
rm -rf "$dir"

# Tidy up the empty .worktrees/ root once the last worktree is gone, so the
# main checkout doesn't carry an empty stub directory around.
rmdir "${repo_root}/.worktrees" 2>/dev/null || true

echo "✓ removed ${rel_dir}"

# Delete the local branch — without this, the next `new-worktree.sh <same-issue>`
# would check out the stale ref instead of taking the latest from origin/main.
# Use -d by default (safe, refuses unmerged). --force on the script call
# escalates to -D for the case where the user genuinely wants to drop the
# branch including unpushed work.
if [[ -n "$branch" ]] && git show-ref --verify --quiet "refs/heads/${branch}"; then
  if (( force_branch_delete )); then
    git branch -D "$branch"
    echo "✓ branch $branch deleted (forced)"
  elif git branch -d "$branch" 2>/dev/null; then
    echo "✓ branch $branch deleted"
  else
    echo "  branch $branch retained — it has unmerged commits"
    echo "  delete manually:  git branch -D $branch"
  fi
fi
