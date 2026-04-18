---
description: Start work on a GitHub issue — fetch, summarize, and create a matching branch
argument-hint: <number>
---

Start work on issue `$ARGUMENTS`. Required arg: the issue number (accepts `42` or `#42`).

## Steps

1. **Parse the argument.** Strip a leading `#` if present. If what remains is not a positive integer, abort with `usage: /issue <number>`.

2. **Preconditions:**
   - `gh auth status` must succeed.
   - `git status --porcelain` must be empty (clean working tree). If dirty, refuse and tell the user to commit or stash first.

3. **Fetch the issue:** `gh issue view <n> --json number,title,state,body,labels`.
   - If the call fails (404 etc.), abort with the underlying error.
   - If `state` is `CLOSED`, warn but continue — the user may be intentionally reopening work.

4. **Summarize** for the user before branching:
   - Title, state, labels.
   - A 2–4 sentence summary of the body (not a full paste).

5. **Derive the branch slug** from the issue title:
   - Lowercase.
   - Replace any run of non-alphanumerics with a single `-`.
   - Trim leading/trailing `-`.
   - Cap at 60 characters (cut on a `-` boundary if possible).
   - Branch name is `<n>-<slug>`. Example: issue 5 titled "Server OAuth: Discord + Google, JWT, refresh tokens, /me" → `5-server-oauth-discord-google-jwt-refresh-tokens-me`.

6. **Create the branch, linked to the issue:**
   - `gh issue develop <n> --name <n>-<slug> --base main --checkout`
   - One command: creates the branch on origin, links it to the issue (shows in the issue's "Development" section), and checks it out locally with correct upstream tracking.
   - If it fails because the branch already exists, stop and suggest `git switch <n>-<slug>` — do not overwrite.

7. **Confirm** with one line: the new branch name and a pointer back to the issue URL (`gh issue view <n> --json url --jq .url`).
